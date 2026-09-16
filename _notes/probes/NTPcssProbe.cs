// [NT-DIAG] PCSS（SelfLight v3 软阴影）实测装置。
//
// 为什么需要它：PCSS 的数学在代码里可以读，但有四件事**只有真 GPU 才知道**：
//   1) ShaderCore 的 SC_SamplerState 展开成裸 `SamplerState`（没有 filter / address 声明），
//      深度图采样到底用点采样还是双线性、Clamp 还是 Repeat，取决于 Unity 的绑定 —— 只能实测。
//      （若实际是 Repeat，PCSS 的取样点越过包围盒边界就会"绕到对面去"，读到对面的深度。）
//   2) 深度图的基（origin/right/up/halfX/near/far 与 u/v/depth 的约定）与球面解析解是否一致
//      —— 用左右半张 / 上下半张贴图做负向控制。
//   3) PCSS 在**真实烘焙出来的深度分布**下半影有多宽（= 这个功能到底有没有用）。
//   4) 半影宽度是不是随烘焙分辨率变化（代码里半径以"纹素"为单位 ⇒ 分辨率越高世界尺度越细）。
//
// 方法：球体接收面 + 正交相机 + 逐像素解析世界坐标（球面），把渲染结果按
//   occlusion = (P_无阴影 - P) / (P_无阴影 - P_全遮挡)  归一化成 0..1 的**遮挡量**
//   （0 = 完全受光，1 = 完全在阴影里），再在扫描线上量"10%→90% 过渡宽度"。
// 所有对照只改一个变量；扫描线上过暗的像素（球体轮廓 / 背景）一律用**亮度掩码**剔除，
// 否则会把背景的 0 当成"完全阴影"（第一版就是这么误报的）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTPcssProbe
{
    const string OUT = "ntpcss.txt";
    const string BakeFolder = "Assets/NTPcssProbeBake";
    const int S = 256;      // 合成测试渲染边长：影子图 256 时 1 纹素 = 1 像素
    const int SB = 512;     // 真实烘焙测试渲染边长：512 时 1 纹素 = 2 像素
    const float RAD = 0.5f;
    const float OZ = -0.75f;   // 光源空间原点 z（深度 0；球最近点在 z=-0.5 ⇒ 归一化深度最小 0.25）

    static readonly StringBuilder Sb = new StringBuilder();
    static int Fail, Pass;
    static void L(string s) { Sb.AppendLine(s); Debug.Log("[NT-PCSS] " + s); }
    static void Ok(string s) { Pass++; L("  ✅ " + s); }
    static void Bad(string s) { Fail++; L("  ❌ " + s); }
    static void Info(string s) { L("  ·  " + s); }
    static void Check(bool c, string what, string detail = "")
    {
        if (c) Ok(what + (detail.Length > 0 ? " → " + detail : ""));
        else Bad(what + (detail.Length > 0 ? " → " + detail : ""));
    }

    static Type FindType(string n)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var t in ts) if (t.Name == n) return t;
        }
        return null;
    }

    public static void Run()
    {
        try { if (File.Exists(OUT)) File.Delete(OUT); } catch { }
        L("== 运行信息 ==");
        L("时间  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        L("工程  = " + Directory.GetCurrentDirectory());
        L("Unity = " + Application.unityVersion);
        L("GPU   = " + SystemInfo.graphicsDeviceType + " / shaderLevel " + SystemInfo.graphicsShaderLevel);
        L("");
        try { Body(); }
        catch (Exception e) { Bad("探针异常：" + e); }
        L("");
        L(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + "）====");
        File.WriteAllText(OUT, Sb.ToString());
        Debug.Log("[NTPcssProbe] done -> " + OUT + " (fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- 数据与工装
    class Shot { public Color32[] px; public int size; public byte[] png; }
    class BakeOut { public Texture2D map; public Vector4 origin, right, up, forward; public float halfX, halfY, near, far; public int resolution; public int hits; public int meshCount; }

    static Shot Render(Material m, int size, string pngName, float camSize = RAD, float goScale = 1f)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "NTPcssReceiver";
        go.transform.position = Vector3.zero;
        go.transform.localScale = Vector3.one * goScale;   // 必须与烘焙时接收面的缩放一致，否则 UV 与深度图对不上
        go.GetComponent<MeshRenderer>().sharedMaterial = m;

        var camGo = new GameObject("NTPcssCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = camSize;
        cam.nearClipPlane = 0.01f; cam.farClipPlane = 100f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.transform.position = new Vector3(0, 0, -2f);
        cam.transform.rotation = Quaternion.identity;   // 看向 +Z

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;

        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32); rt.Create();
        var prev = RenderTexture.active;
        cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply();
        RenderTexture.active = prev;

        var shot = new Shot { px = tex.GetPixels32(), size = size, png = tex.EncodeToPNG() };
        if (!string.IsNullOrEmpty(pngName)) { try { File.WriteAllBytes(pngName, shot.png); } catch { } }

        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(go);
        UnityEngine.Object.DestroyImmediate(camGo);
        return shot;
    }

    static Texture2D MakeMap(int res, Func<float, float, float> f, TextureWrapMode wrap, FilterMode filter, string name)
    {
        var t = new Texture2D(res, res, TextureFormat.RGBA32, false, true);   // linear=true（不做 sRGB 解码）
        t.name = name;
        var c = new Color[res * res];
        for (var y = 0; y < res; y++)
            for (var x = 0; x < res; x++)
            {
                var v = Mathf.Clamp01(f((x + 0.5f) / res, (y + 0.5f) / res));
                c[y * res + x] = new Color(v, v, v, 1f);
            }
        t.SetPixels(c); t.Apply();
        t.wrapMode = wrap; t.filterMode = filter;
        return t;
    }

    static void SetNum(Material m, string p, float v)
    {
        if (!m.HasProperty(p)) return;
        var i = m.shader.FindPropertyIndex(p);
        if (i >= 0 && m.shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Int)
            m.SetInteger(p, Mathf.RoundToInt(v));
        else m.SetFloat(p, v);
    }

    // 「只由自有光照亮」⇒ 像素 ∝ dot(N,L)·shadow，便于归一化测量
    // bias 可传：诊断"分辨率依赖到底是不是归一化深度的 bias 造成的"（见 E 节的 bias 对照）
    static Material MakeMat(Texture2D map, int res, bool pcss, int quality, float softness, float strength,
                            float halfX = RAD, float bias = 0.02f)
    {
        var m = new Material(Shader.Find("NonToon"));
        m.name = "NTPcssProbeMat";
        SetNum(m, "_UseSelfLight", 1);
        SetNum(m, "_SelfLightOnly", 1);
        m.SetColor("_SelfLightColor", Color.white);
        SetNum(m, "_SelfLightIntensity", 1f);
        m.SetVector("_SelfLightDirection", new Vector4(0, 0, -1, 0));   // 朝光源（相机一侧）
        SetNum(m, "_SelfLightUseTemperature", 0);
        SetNum(m, "_SelfLightMatchAmbient", 0);
        SetNum(m, "_SelfLightMatchDirection", 0);
        SetNum(m, "_SelfLightBlockAmbient", 0);
        m.SetColor("_SelfLightShadowColor", Color.black);
        SetNum(m, "_SelfLightShadowStrength", strength);
        SetNum(m, "_SelfLightPCSS", pcss ? 1 : 0);
        SetNum(m, "_SelfLightPCSSQuality", quality);
        SetNum(m, "_SelfLightSoftness", softness);
        SetNum(m, "_SelfLightDensity", 1f);
        SetNum(m, "_SelfLightClamp", 0f);
        SetNum(m, "_SelfLightDistance", 0f);          // 0 = 不限制距离
        SetNum(m, "_SelfLightShadowBias", bias);
        SetNum(m, "_SelfLightShadowMaskStrength", 0f);
        SetNum(m, "_SelfLightReceiveMaskStrength", 0f);
        SetNum(m, "_LightMinLimit", 0f);
        SetNum(m, "_LightMaxLimit", 1f);
        SetNum(m, "_MonochromeLighting", 0f);
        SetNum(m, "_AsUnlit", 0f);

        m.SetTexture("_SelfLightShadowMap", map);
        SetNum(m, "_SelfLightShadowTexels", res);
        m.SetVector("_SelfLightOrigin", new Vector4(0, 0, OZ, 0));
        m.SetVector("_SelfLightRight", new Vector4(1, 0, 0, 0));
        m.SetVector("_SelfLightUp", new Vector4(0, 1, 0, 0));
        m.SetVector("_SelfLightForward", new Vector4(0, 0, 1, 0));
        SetNum(m, "_SelfLightHalfX", halfX);
        SetNum(m, "_SelfLightHalfY", halfX);
        SetNum(m, "_SelfLightNear", 0f);
        SetNum(m, "_SelfLightFar", 1f);
        return m;
    }

    static Material MakeMatBaked(BakeOut b, bool pcss, int quality, float softness, float strength,
                                 float bias = 0.02f, int texelsOverride = 0)
    {
        var m = MakeMat(b.map, texelsOverride > 0 ? texelsOverride : b.resolution, pcss, quality, softness, strength, RAD, bias);
        m.SetVector("_SelfLightOrigin", new Vector4(b.origin.x, b.origin.y, b.origin.z, 0));
        m.SetVector("_SelfLightRight", new Vector4(b.right.x, b.right.y, b.right.z, 0));
        m.SetVector("_SelfLightUp", new Vector4(b.up.x, b.up.y, b.up.z, 0));
        m.SetVector("_SelfLightForward", new Vector4(b.forward.x, b.forward.y, b.forward.z, 0));
        SetNum(m, "_SelfLightHalfX", b.halfX);
        SetNum(m, "_SelfLightHalfY", b.halfY);
        SetNum(m, "_SelfLightNear", b.near);
        SetNum(m, "_SelfLightFar", b.far);
        return m;
    }

    static float Luma(Color32 c) { return (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f; }

    // 逐像素遮挡量：0 = 完全受光，1 = 完全在阴影里
    static float[] OcclRow(Shot test, Shot lit, Shot dark, int py)
    {
        var size = test.size;
        var outp = new float[size];
        for (var px = 0; px < size; px++)
        {
            var i = py * size + px;
            float l = Luma(lit.px[i]), d = Luma(dark.px[i]), t = Luma(test.px[i]);
            outp[px] = Mathf.Clamp01((l - t) / Mathf.Max(1e-4f, l - d));
        }
        return outp;
    }

    // 亮度掩码：无阴影参考图上太暗的像素（球体轮廓/背景/掠射角）不参与统计
    static bool[] MaskRow(Shot lit, Shot dark, int py, float frac = 0.25f)
    {
        var size = lit.size;
        var m = new bool[size];
        double sum = 0; int n = 0;
        for (var px = 0; px < size; px++) { sum += Luma(lit.px[py * size + px]); n++; }
        var mean = sum / Mathf.Max(1, n);
        for (var px = 0; px < size; px++)
        {
            var i = py * size + px;
            m[px] = Luma(lit.px[i]) > frac * mean && Luma(dark.px[i]) < Luma(lit.px[i]) * 0.9;
        }
        return m;
    }

    static float Mean(float[] v, bool[] m, int i0, int i1)
    {
        i0 = Mathf.Max(0, i0); i1 = Mathf.Min(v.Length - 1, i1);
        double s = 0; int n = 0;
        for (var i = i0; i <= i1; i++) if (m == null || m[i]) { s += v[i]; n++; }
        return n == 0 ? -1f : (float)(s / n);
    }
    static float Max(float[] v, bool[] m, int i0, int i1, out int idx)
    {
        i0 = Mathf.Max(0, i0); i1 = Mathf.Min(v.Length - 1, i1);
        var best = -1f; idx = -1;
        for (var i = i0; i <= i1; i++) if ((m == null || m[i]) && v[i] > best) { best = v[i]; idx = i; }
        return best;
    }

    // 10%→90% 过渡宽度（像素）。自动判方向；mask 之外的像素忽略。
    static float Width1090(float[] v, bool[] m, int i0, int i1)
    {
        i0 = Mathf.Max(0, i0); i1 = Mathf.Min(v.Length - 1, i1);
        var win = new List<float>();
        for (var i = i0; i <= i1; i++) if (m == null || m[i]) win.Add(v[i]);
        if (win.Count < 5) return -1f;
        var sorted = win.OrderBy(x => x).ToArray();
        var lo = sorted[Mathf.FloorToInt(sorted.Length * 0.05f)];
        var hi = sorted[Mathf.FloorToInt(sorted.Length * 0.95f)];
        if (hi - lo < 0.15f) return -1f;                     // 窗口里没有明显过渡
        var rising = v[i1] > v[i0];
        var t10 = rising ? lo + 0.10f * (hi - lo) : hi - 0.10f * (hi - lo);
        var t90 = rising ? lo + 0.90f * (hi - lo) : hi - 0.90f * (hi - lo);
        int a = -1, b = -1;
        for (var i = i0; i <= i1; i++)
        {
            if (m != null && !m[i]) continue;
            var hit10 = rising ? v[i] >= t10 : v[i] <= t10;
            var hit90 = rising ? v[i] >= t90 : v[i] <= t90;
            if (a < 0 && hit10) a = i;
            if (b < 0 && hit90) b = i;
        }
        if (a < 0 || b < 0) return -1f;
        return b - a + 1;
    }

    static float DiskMean(Shot s)
    {
        double sum = 0; int n = 0;
        for (var py = 0; py < s.size; py++)
            for (var px = 0; px < s.size; px++)
            {
                var x = (px + 0.5f) / s.size - 0.5f; var y = (py + 0.5f) / s.size - 0.5f;
                if (x * x + y * y < (RAD * 0.9f) * (RAD * 0.9f)) { sum += Luma(s.px[py * s.size + px]); n++; }
            }
        return n == 0 ? 0f : (float)(sum / n);
    }

    static BakeOut DoBake(Light light, List<Renderer> rs, int res)
    {
        var t = FindType("NTSelfShadowBaker");
        if (t == null) { Bad("找不到 NTSelfShadowBaker"); return null; }
        // 必须写全参数类型：0.5.0 给烘焙器加了「虚拟光方向」重载后，只按名字取会 AmbiguousMatchException
        var m = t.GetMethod("Bake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(Light), typeof(IEnumerable<Renderer>), typeof(int), typeof(string) }, null);
        if (m == null) { Bad("找不到 NTSelfShadowBaker.Bake"); return null; }
        object r;
        try { r = m.Invoke(null, new object[] { light, rs, res, BakeFolder }); }
        catch (Exception e) { Bad("Bake 抛异常：" + (e.InnerException ?? e).Message); return null; }
        if (r == null) { Bad("Bake 返回 null"); return null; }
        var rt = t.GetNestedType("Result", BindingFlags.Public | BindingFlags.NonPublic);
        Func<string, object> F = n => rt.GetField(n).GetValue(r);
        return new BakeOut
        {
            map = F("map") as Texture2D,
            origin = (Vector3)F("origin"),
            right = (Vector3)F("right"),
            up = (Vector3)F("up"),
            forward = (Vector3)F("forward"),
            halfX = (float)F("halfX"),
            halfY = (float)F("halfY"),
            near = (float)F("near"),
            far = (float)F("far"),
            resolution = (int)F("resolution"),
            hits = (int)F("hits"),
            meshCount = (int)F("meshCount"),
        };
    }

    // ---------------------------------------------------------------- 主流程
    static void Body()
    {
        var shader = Shader.Find("NonToon");
        Check(shader != null, "找到 NonToon 着色器");
        if (shader == null) return;
        if (AssetDatabase.IsValidFolder(BakeFolder)) AssetDatabase.DeleteAsset(BakeFolder);

        // ============ A. 深度约定自检（基 + 球面解析解）============
        L("== A. 深度约定自检 ==");
        var mapAllNear = MakeMap(256, (u, v) => 0.005f, TextureWrapMode.Clamp, FilterMode.Bilinear, "allNear");
        var mapAllFar = MakeMap(256, (u, v) => 0.995f, TextureWrapMode.Clamp, FilterMode.Bilinear, "allFar");
        var lit = Render(MakeMat(mapAllFar, 256, false, 0, 0.5f, 0f), S, "ntpcss-A-lit.png");      // strength 0 ⇒ 无阴影
        var dark = Render(MakeMat(mapAllNear, 256, false, 0, 0.5f, 1f), S, "ntpcss-A-dark.png");   // 全遮挡 ⇒ 全阴影
        var lumLit = DiskMean(lit);
        var occDark = DiskMean(dark) / Mathf.Max(1e-4f, lumLit);
        Info(string.Format("无阴影渲染的球面内侧平均亮度 = {0:F4}", lumLit));
        Check(lumLit > 0.05f, "有光信号（自有光排他通路在出像素）", lumLit.ToString("F4"));
        Check(occDark < 0.05f, "全遮挡贴图 ⇒ 渲染为黑（深度比较方向正确）", occDark.ToString("F3"));

        var mapLeft = MakeMap(256, (u, v) => u < 0.5f ? 0.005f : 0.995f, TextureWrapMode.Clamp, FilterMode.Point, "leftHalf");
        var sl = Rendered(mapLeft, false, 0, 0.5f, 1f, "ntpcss-A-left.png", lit, dark, S / 2, out var ml);
        var leftMean = Mean(sl, ml, 0, (int)(S * 0.42f));
        var rightMean = Mean(sl, ml, (int)(S * 0.58f), S - 1);
        Check(leftMean > 0.75f && rightMean < 0.25f, "u 轴方向正确（贴图左半遮挡 ⇒ 屏幕左侧是阴影）",
            string.Format("左 {0:F3} / 右 {1:F3}", leftMean, rightMean));

        var mapBottom = MakeMap(256, (u, v) => v < 0.5f ? 0.005f : 0.995f, TextureWrapMode.Clamp, FilterMode.Point, "bottomHalf");
        var bot = MakeMapRender(mapBottom, "ntpcss-A-bottom.png");
        var botProf = OcclRow(bot, lit, dark, (int)(S * 0.25f));
        var topProf = OcclRow(bot, lit, dark, (int)(S * 0.75f));
        Check(botProf[S / 2] > 0.75f && topProf[S / 2] < 0.25f, "v 轴方向正确（贴图下半遮挡 ⇒ 屏幕下方是阴影）",
            string.Format("下 {0:F3} / 上 {1:F3}", botProf[S / 2], topProf[S / 2]));

        // ============ B. 采样器 filter：棋盘贴图 ============
        L("");
        L("== B. 深度图的采样 filter（棋盘贴图：点采样出条纹，双线性糊成均匀） ==");
        var mapCheck = MakeMap(256, (u, v) =>
        {
            var tx = (int)(u * 256f); var ty = (int)(v * 256f);
            return ((tx + ty) & 1) == 0 ? 0.005f : 0.995f;
        }, TextureWrapMode.Clamp, FilterMode.Point, "checker");
        var mk = MaskRow(lit, dark, S / 2);
        var sCheck = OcclRow(MakeMapRender(mapCheck, "ntpcss-B-checker.png"), lit, dark, S / 2);
        double vsum = 0; int vn = 0;
        for (var px = S / 2 - 40; px < S / 2 + 37; px++)
            for (var d = 0; d < 3; d++)
            {
                if (!mk[px + d] || !mk[px + d + 1]) continue;
                var s = sCheck[px + d] - sCheck[px + d + 1];
                vsum += s * s; vn++;
            }
        var diffVar = (float)(vsum / Mathf.Max(1, vn));
        Info(string.Format("相邻列遮挡量之差的均方 = {0:F4}（点采样 ≈1.0；双线性 ⇒ 相邻纹素被平均 ⇒ ≈0）", diffVar));
        Check(diffVar > 0.3f, "深度图走的是点采样（纹素可辨，不被相邻纹素平均）", diffVar.ToString("F4"));

        // ============ C. 采样器 address：包围盒边界 ============
        // 关键设计：把包围盒缩到 halfX=0.25（球半径 0.5），这样"贴边"的像素落在球面**中间**，
        // 不用碰轮廓（第一版把窗口放在 |x|≈0.49，那里混进了背景像素 ⇒ 误报"漏光 23%"）。
        L("");
        L("== C. 深度图的 address 模式（决定 PCSS 越界取样会不会绕到对面去） ==");
        const float HC = 0.25f;
        Func<float, float, float> band = (u, v) => u >= 0.6f ? 0.02f : 0.995f;
        float cInt = -1, cEdgeClamp = -1, cEdgeRepeat = -1;
        foreach (var wrap in new[] { TextureWrapMode.Clamp, TextureWrapMode.Repeat })
        {
            var mp = MakeMap(256, band, wrap, FilterMode.Bilinear, "band_" + wrap);
            var sh = Rendered(mp, true, 3, 1.0f, 1f, "ntpcss-C-" + wrap + ".png", lit, dark, S / 2, out var mc, HC);
            var mInt = Mean(sh, mc, 160, 168);     // u≈0.75~0.81（盒内深处）
            var mEdge = Mean(sh, mc, 190, 191);    // u≈0.988~0.996（离右边界 1~3 纹素 ⇒ 越界取样比例最大）
            Info(string.Format("wrap={0,-7}  内部 u≈0.78 遮挡={1:F3}   贴边 u≈0.99 遮挡={2:F3}", wrap, mInt, mEdge));
            if (wrap == TextureWrapMode.Clamp) { cInt = mInt; cEdgeClamp = mEdge; } else cEdgeRepeat = mEdge;
        }
        Check(cInt > 0.9f, "遮挡物覆盖处阴影满值（PCSS 通路有效）", cInt.ToString("F3"));
        Check(cEdgeClamp > 0.9f, "贴边像素不被越界取样污染（产品路径安全）", cEdgeClamp.ToString("F3"));
        if (cEdgeRepeat < 0.85f)
            Info(string.Format("对照：同一张图按 Repeat 导入 ⇒ 贴边遮挡掉到 {0:F3}（越界取样读到了对面的背景）", cEdgeRepeat));
        else
            Info(string.Format("对照：Repeat 贴图与 Clamp 贴图结果一致（{0:F3}）⇒ 采样器地址模式不受贴图导入设置影响", cEdgeRepeat));

        // C2：判别实验 —— 把"对面"（u<0.05）也铺成同样的遮挡物。
        //   · 若地址模式是 Repeat/不受贴图控制：越界取样会读到这个遮挡物 ⇒ 贴边遮挡回升到 ~1
        //   · 若已 clamp：两种情况完全一致（对照）
        var mpWrap = MakeMap(256, (u, v) => (u >= 0.6f || u < 0.05f) ? 0.02f : 0.995f, TextureWrapMode.Clamp, FilterMode.Bilinear, "band_wrapped");
        var shWrap = Rendered(mpWrap, true, 3, 1.0f, 1f, "ntpcss-C2-wrapped.png", lit, dark, S / 2, out var mc2, HC);
        var c2Edge = Mean(shWrap, mc2, 190, 191);
        var c2Int = Mean(shWrap, mc2, 160, 168);
        Info(string.Format("判别 C2：把 u<0.05 也铺成遮挡物 ⇒ 内部 {0:F3}（对照）、贴边 {1:F3}（原 {2:F3}）", c2Int, c2Edge, cEdgeClamp));
        if (c2Edge > cEdgeClamp + 0.15f)
            Info("⇒ 判定：越界取样确实读到了**对面的纹素**（地址模式是 Repeat 一类，且不受贴图导入设置控制）");
        else if (c2Edge > cEdgeClamp - 0.15f && c2Edge < cEdgeClamp + 0.15f)
            Info("⇒ 判定：贴边处的损失与对面无关（不是地址模式问题）");

        // ============ D. 合成遮挡物：PCSS 的有效半影 ============
        L("");
        L("== D. 合成遮挡物：PCSS 到底柔不柔（10%→90% 过渡宽度；S=256 ⇒ 1 纹素 = 1 像素） ==");
        var W = new Dictionary<string, float>();
        Action<string, Func<float, float, float>, bool, int, float> measure = (tag, fn, pcss, q, soft) =>
        {
            var mp = MakeMap(256, fn, TextureWrapMode.Clamp, FilterMode.Bilinear, tag);
            var sh = Rendered(mp, pcss, q, soft, 1f, "ntpcss-D-" + tag.Replace(' ', '_') + ".png", lit, dark, S / 2, out var md);
            var w = Width1090(sh, md, (int)(S * 0.50f), (int)(S * 0.74f));
            W[tag] = w;
            var core = Max(sh, md, (int)(S * 0.68f), (int)(S * 0.80f), out var _);
            Info(string.Format("{0,-32} → 过渡宽度 {1:F1} 像素（核心遮挡 {2:F3}）", tag, w, core));
        };
        // 强 ratio：遮挡物归一化深度 0.005（半影比 ≈52 ⇒ 半径 13~26 纹素）
        measure("硬阴影 强ratio", (u, v) => u >= 0.6f ? 0.005f : 0.995f, false, 0, 0.5f);
        measure("PCSS低 s0.5 强ratio", (u, v) => u >= 0.6f ? 0.005f : 0.995f, true, 0, 0.5f);
        measure("PCSS极高 s0.5 强ratio", (u, v) => u >= 0.6f ? 0.005f : 0.995f, true, 3, 0.5f);
        // 现实 ratio：遮挡物归一化深度 0.05（半影比 ≈4.3 ⇒ 半径 1.5~2.2 纹素）
        measure("硬阴影 现实ratio", (u, v) => u >= 0.6f ? 0.05f : 0.995f, false, 0, 0.5f);
        measure("PCSS低 s0.5 现实ratio", (u, v) => u >= 0.6f ? 0.05f : 0.995f, true, 0, 0.5f);
        measure("PCSS极高 s1.0 现实ratio", (u, v) => u >= 0.6f ? 0.05f : 0.995f, true, 3, 1.0f);

        Check(W["PCSS极高 s0.5 强ratio"] > W["硬阴影 强ratio"] * 2f,
            "强 ratio 下 PCSS 明显比硬阴影柔（功能存在）",
            string.Format("{0:F1} → {1:F1} 像素", W["硬阴影 强ratio"], W["PCSS极高 s0.5 强ratio"]));
        Info(string.Format("现实 ratio：硬 {0:F1} → PCSS 低 {1:F1} / 极高 {2:F1} 像素",
            W["硬阴影 现实ratio"], W["PCSS低 s0.5 现实ratio"], W["PCSS极高 s1.0 现实ratio"]));

        // ============ E. 真实烘焙：半影宽度与分辨率依赖 ============
        L("");
        L("== E. 真实烘焙（converter 的 NTSelfShadowBaker）半影与分辨率依赖 ==");
        var lightGo = new GameObject("NTPcssProbeLight");
        lightGo.transform.position = new Vector3(0, 0, -2f);
        lightGo.transform.rotation = Quaternion.identity;    // forward = +Z
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;

        var receiver = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        receiver.name = "NTBakeReceiver";
        receiver.transform.position = Vector3.zero;
        var receiverMr = receiver.GetComponent<MeshRenderer>();
        var occluder = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        occluder.name = "NTBakeOccluder";

        var nearRef = MakeMap(8, (u, v) => 0.005f, TextureWrapMode.Clamp, FilterMode.Point, "near8");
        var hard = new Dictionary<string, float>();
        var lowQ = new Dictionary<string, float>();
        var ultraQ = new Dictionary<string, float>();

        try
        {
            foreach (var cfg in new[] {
                new { tag = "远遮挡", z = -1.5f, dia = 0.12f, res = 256, scale = 1.0f, cam = RAD },
                new { tag = "远遮挡", z = -1.5f, dia = 0.12f, res = 512, scale = 1.0f, cam = RAD },
                // [NT-VERIFY 0.3.9] 4× 分辨率：用来暴露"半径修好了、但 blocker 搜索窗口仍是 8 纹素"
                // 残留的世界尺度依赖（窗口不随分辨率换算 ⇒ 高分辨率下搜的世界范围变小）
                new { tag = "远遮挡", z = -1.5f, dia = 0.12f, res = 1024, scale = 1.0f, cam = RAD },
                new { tag = "近遮挡", z = -0.62f, dia = 0.04f, res = 256, scale = 1.0f, cam = RAD },
                // 头尺度：接收球直径 0.30 m（≈ 一颗头），遮挡物（≈ 一撮头发）在它前面 5 cm
                new { tag = "头尺度", z = -0.20f, dia = 0.04f, res = 256, scale = 0.30f, cam = 0.16f },
            })
            {
                receiver.transform.localScale = Vector3.one * cfg.scale;
                occluder.transform.localScale = Vector3.one * cfg.dia;
                occluder.transform.position = new Vector3(0, 0, cfg.z);
                occluder.SetActive(true);
                var b = DoBake(light, new List<Renderer> { receiverMr, occluder.GetComponent<MeshRenderer>() }, cfg.res);
                if (b == null) continue;
                occluder.SetActive(false);       // 影子已在贴图里，渲染时别挡镜头
                receiver.SetActive(false);       // 渲染用的是 Render() 里临时建的同尺寸球，避免两个球重叠

                var occDepth = (cfg.z - b.origin.z) / Mathf.Max(1e-4f, b.far);
                var recvDepth = (0f - b.origin.z) / Mathf.Max(1e-4f, b.far);
                Info(string.Format("{0} @{1}²：命中 {2} 像素 / {3} 网格；halfX={4:F3} far={5:F3}；"
                    + "遮挡物归一化深度≈{6:F3}、接收面≈{7:F3} ⇒ 半影比≈{8:F1}",
                    cfg.tag, cfg.res, b.hits, b.meshCount, b.halfX, b.far, occDepth, recvDepth,
                    (recvDepth - occDepth) / Mathf.Max(1e-4f, occDepth)));

                var size = SB;
                var refLit = Render(MakeMatBaked(b, false, 0, 0.5f, 0f), size, null, cfg.cam, cfg.scale);
                var bDark = new BakeOut { map = nearRef, origin = b.origin, right = b.right, up = b.up, forward = b.forward, halfX = b.halfX, halfY = b.halfY, near = b.near, far = b.far, resolution = 8 };
                var refDark = Render(MakeMatBaked(bDark, false, 0, 0.5f, 1f), size, null, cfg.cam, cfg.scale);
                var mb = MaskRow(refLit, refDark, size / 2);

                var key = cfg.tag + " @" + cfg.res;
                var sHard = OcclRow(Render(MakeMatBaked(b, false, 0, 0.5f, 1f), size, "ntpcss-E-" + cfg.res + "-" + cfg.tag + "-hard.png", cfg.cam, cfg.scale), refLit, refDark, size / 2);
                var sLow = OcclRow(Render(MakeMatBaked(b, true, 0, 0.5f, 1f), size, "ntpcss-E-" + cfg.res + "-" + cfg.tag + "-low.png", cfg.cam, cfg.scale), refLit, refDark, size / 2);
                var sUltra = OcclRow(Render(MakeMatBaked(b, true, 3, 0.5f, 1f), size, "ntpcss-E-" + cfg.res + "-" + cfg.tag + "-ultra.png", cfg.cam, cfg.scale), refLit, refDark, size / 2);

                // 自动定位阴影核心（不再假定它在某一列 —— 第一版把窗口开在 x∈[-0.4,-0.1]，整个窗口都在阴影外）
                var core = Max(sHard, mb, (int)(size * 0.15f), (int)(size * 0.5f), out var iPeak);
                Check(core > 0.75f, string.Format("{0}@{1}²：烘焙出的阴影真的落在接收面上", cfg.tag, cfg.res),
                    string.Format("核心遮挡 {0:F3} @ 第 {1} 列", core, iPeak));

                var tpp = cfg.res / (float)size;     // 每个像素等于多少纹素
                // 测量窗口必须**同时包含亮平台与暗平台**（第一版把窗口右端压在过渡起点上，
                // 窗口里 95% 都是平台 ⇒ 量不出过渡 ⇒ 返回 -1）
                var w0 = (int)(size * 0.15f);
                var w1 = (int)(size * 0.52f);
                hard[key] = Width1090(sHard, mb, w0, w1);
                lowQ[key] = Width1090(sLow, mb, w0, w1);
                ultraQ[key] = Width1090(sUltra, mb, w0, w1);
                Info(string.Format("  硬阴影 {0:F1} px={1:F1}纹素 / PCSS低 {2:F1} px={3:F1}纹素 / PCSS极高 {4:F1} px={5:F1}纹素",
                    hard[key], hard[key] * tpp, lowQ[key], lowQ[key] * tpp, ultraQ[key], ultraQ[key] * tpp));

                // 【判别实验】把"半径随分辨率变大"与"深度图内容随分辨率变化"分开：
                //   用**同一张 256² 贴图**，只把 `_SelfLightShadowTexels` 谎报成 512/1024。
                //   半径公式里的纹素换算会因此按谎报值放大（贴图内容一点没变）。
                //   · 若半影随之明显变大 ⇒ 漂移由**半径**驱动（那就有希望靠改半径公式修）
                //   · 若几乎不变 ⇒ 漂移由**贴图内容/分辨率**驱动（改半径公式修不了）
                if (cfg.tag == "远遮挡" && cfg.res == 256)
                {
                    L("  【判别实验】同一张 256² 贴图，只改 _SelfLightShadowTexels（纹素换算基准）：");
                    foreach (var fake in new[] { 128, 64 })
                    {
                        var wFake = Width1090(OcclRow(Render(MakeMatBaked(b, true, 0, 0.5f, 1f, 0.02f, fake), size,
                            "ntpcss-E-faketexels-" + fake + ".png", cfg.cam, cfg.scale), refLit, refDark, size / 2), mb, w0, w1);
                        L(string.Format("     谎报 texels={0}（贴图仍是 256²；取样足迹 = 4.4×256/{0} = {1:F1} 真纹素）→ 半影 {2:F1} px（真值 256 时 {3:F1} px）",
                            fake, 4.4f * 256f / fake, wFake, lowQ[key]));
                    }
                }
            }

            L("");
            var k256 = "远遮挡 @256";
            var k512 = "远遮挡 @512";
            var k1024 = "远遮挡 @1024";
            if (hard.ContainsKey(k256) && lowQ.ContainsKey(k512))
            {
                Info("分辨率依赖（同一场景/同一包围盒，只有烘焙分辨率与 Shadow Texels 变了）：");
                Info(string.Format("  硬阴影边缘：256² = {0:F1} px / 512² = {1:F1} px（对照：世界尺度上应≈不变）",
                    hard[k256], hard[k512]));
                Info(string.Format("  PCSS低 半影：256² = {0:F1} px / 512² = {1:F1} px ⇒ 世界尺度上 {2:F0}%",
                    lowQ[k256], lowQ[k512], 100f * lowQ[k512] / Mathf.Max(0.1f, lowQ[k256])));
                if (hard.ContainsKey(k1024) && lowQ.ContainsKey(k1024))
                    Info(string.Format("  PCSS低 半影：1024² = {0:F1} px ⇒ 世界尺度上 {1:F0}%（4× 分辨率，用来看副作用）",
                        lowQ[k1024], 100f * lowQ[k1024] / Mathf.Max(0.1f, lowQ[k256])));

                // 【P2 回归门禁】把"半影世界尺度与分辨率无关"钉成机器可判的断言。
                //   修前实测：256² = 7.0 px → 512² = 5.0 px（世界尺度 71%）。
                //   半影在**渲染像素**里度量，而相机/场景/接收球都没变 ⇒ 同样世界宽度就该是同样像素数。
                var ratio512 = lowQ[k512] / Mathf.Max(0.1f, lowQ[k256]);
                Check(ratio512 > 0.85f && ratio512 < 1.20f,
                    "【P2】PCSS 半影的世界尺度与烘焙分辨率无关（256² vs 512²）",
                    string.Format("256²={0:F1} px、512²={1:F1} px ⇒ 世界尺度 {2:F0}%（修前为 71%）",
                        lowQ[k256], lowQ[k512], 100f * ratio512));

                // 1024² 也必须一起判：只把 256/512 判绿就宣称"分辨率无关"是假绿
                // （实测 12 个 tap 在 1024² 会饱和 ⇒ 必须同时补 tap 数，见 includes.hlsl 的 0.3.9 注释）
                if (lowQ.ContainsKey(k1024))
                {
                    var ratio1024 = lowQ[k1024] / Mathf.Max(0.1f, lowQ[k256]);
                    Check(ratio1024 > 0.85f && ratio1024 < 1.20f,
                        "【P2】同上，4× 分辨率（1024²）也要成立",
                        string.Format("256²={0:F1} px、1024²={1:F1} px ⇒ 世界尺度 {2:F0}%（修前为 57%）",
                            lowQ[k256], lowQ[k1024], 100f * ratio1024));
                }
            }
            var kn = "近遮挡 @256";
            if (hard.ContainsKey(kn))
                Info(string.Format("近遮挡（遮挡物离接收面 0.12 m、包围盒 far=1.19 ⇒ 半影比 13.8）：硬 {0:F1} px → PCSS低 {1:F1} px ⇒ 多柔化 {2:F1} 像素",
                    hard[kn], lowQ[kn], lowQ[kn] - hard[kn]));
            var kh = "头尺度 @256";
            if (hard.ContainsKey(kh))
                Info(string.Format("头尺度（接收球直径 0.30 m、遮挡物在前 5 cm；包围盒 halfX≈{0:F3} m、1 纹素≈{1:F2} mm）："
                    + "硬 {2:F1} px → PCSS低 {3:F1} px ⇒ 多柔化 {4:F1} 像素 ≈ {5:F1} mm",
                    0.15f * 1.05f, 1000f * (0.15f * 1.05f) / 256f, hard[kh], lowQ[kh], lowQ[kh] - hard[kh],
                    (lowQ[kh] - hard[kh]) * (0.15f * 1.05f) / 256f * 1000f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(occluder);
            UnityEngine.Object.DestroyImmediate(receiver);
            UnityEngine.Object.DestroyImmediate(lightGo);
        }
    }

    // 小包装：合成贴图 → 渲染 → 归一化成遮挡量
    static Shot MakeMapRender(Texture2D map, string png)
    {
        return Render(MakeMat(map, map.width, false, 0, 0.5f, 1f), S, png);
    }
    static float[] Rendered(Texture2D map, bool pcss, int q, float soft, float strength, string png,
                            Shot lit, Shot dark, int py, out bool[] mask, float halfX = RAD)
    {
        var shot = Render(MakeMat(map, map.width, pcss, q, soft, strength, halfX), lit.size, png);
        mask = MaskRow(lit, dark, py);
        return OcclRow(shot, lit, dark, py);
    }
}
