// E2E 探针 v6 —— 用**正确 API** 复测自有光，并走组件真实流程
//   v2~v4 我用 Material.SetInt 写 SC_uint 属性 → 静默无效，导致误判"自有光坏了"。
//   隔离实验已证明：Integer 属性要用 SetInteger（SerializedObject 路径也通），SetInt/SetFloat 无效。
//   我们的产品代码用的就是 SetInteger，所以要用同样方式复测才算公平。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTE2EProbe6
{
    const string OUT = "e2e-report6.txt";
    static readonly StringBuilder sb = new StringBuilder();
    static int pass, fail;
    static void L(string s) { sb.AppendLine(s); Debug.Log("[E2E6] " + s); }
    static void Ok(string s) { pass++; L("  [OK]   " + s); }
    static void Bad(string s) { fail++; L("  [FAIL] " + s); }
    static void Check(bool c, string s) { if (c) Ok(s); else Bad(s); }
    static void Info(string s) { L("  [info] " + s); }

    static Type FindType(string n)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var t in ts) if (t.Name == n) return t;
        }
        return null;
    }

    class S { public Color32 center; public double mean, nonBlack; public int distinct; public Color32[] px; public byte[] png; }

    static S Shot(Material mat, float lightIntensity)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        var lg = new GameObject("L");
        var li = lg.AddComponent<Light>();
        li.type = LightType.Directional; li.intensity = lightIntensity;
        li.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
        var cg = new GameObject("Cam");
        var cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.fieldOfView = 40f; cam.transform.position = new Vector3(0, 0, -3.2f); cam.transform.LookAt(Vector3.zero);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.20f, 0.20f, 0.26f, 1f);

        const int Z = 128;
        var rt = new RenderTexture(Z, Z, 24, RenderTextureFormat.ARGB32); rt.Create();
        var prev = RenderTexture.active;
        cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
        var tex = new Texture2D(Z, Z, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, Z, Z), 0, 0); tex.Apply();
        RenderTexture.active = prev;
        var px = tex.GetPixels32();
        var st = new S { px = px, png = tex.EncodeToPNG(), center = px[(Z / 2) * Z + Z / 2] };
        double sum = 0; int nb = 0; var set = new HashSet<int>();
        foreach (var p in px)
        {
            double l = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0;
            sum += l; if (l > 0.02) nb++; set.Add((p.r >> 3) << 10 | (p.g >> 3) << 5 | (p.b >> 3));
        }
        st.mean = sum / px.Length; st.nonBlack = (double)nb / px.Length; st.distinct = set.Count;
        UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(lg); UnityEngine.Object.DestroyImmediate(cg);
        return st;
    }
    static double Diff(S a, S b)
    {
        double d = 0; for (int i = 0; i < a.px.Length; i++)
            d += Math.Abs(a.px[i].r - b.px[i].r) + Math.Abs(a.px[i].g - b.px[i].g) + Math.Abs(a.px[i].b - b.px[i].b);
        return d / (a.px.Length * 3.0 * 255.0);
    }

    public static void Run()
    {
        try { Body(); } catch (Exception e) { Bad("探针异常 " + e); }
        L("");
        L(fail == 0 ? "==== v6 全部通过（" + pass + "）====" : "==== v6 失败 " + fail + " 项（通过 " + pass + "）====");
        try { File.WriteAllText(OUT, sb.ToString()); } catch { }
        EditorApplication.Exit(fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        L("=== E2E 探针 v6 === " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var sh = Shader.Find("NonToon");
        Check(sh != null, "Shader.Find(\"NonToon\") 非 null");
        if (sh == null) return;

        // ---------- 1. 属性类型普查 ----------
        L("");
        L("[1] 属性在 Unity 眼里的真实类型（决定该用哪个写入 API）");
        var typeCount = new Dictionary<string, int>();
        foreach (var pn in new[] { "_UseSelfLight", "_SelfLightOnly", "_SelfLightId", "_RenderingMode",
            "_LightMinLimit", "_AsUnlit", "_SelfLightIntensity", "_SelfLightColor", "_jp_lilxyzw_nontoon_details_Enable" })
        {
            var idx = sh.FindPropertyIndex(pn);
            if (idx < 0) { Info($"  {pn,-46} 不存在"); continue; }
            var t = sh.GetPropertyType(idx);
            Info($"  {pn,-46} {t}");
            typeCount[t.ToString()] = typeCount.TryGetValue(t.ToString(), out var c) ? c + 1 : 1;
        }
        // 全部 165 条的类型分布
        var all = new Dictionary<string, int>();
        for (int i = 0; i < sh.GetPropertyCount(); i++)
        {
            var t = sh.GetPropertyType(i).ToString();
            all[t] = all.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        Info("NonToon 165 条属性类型分布：" + string.Join(", ", all.Select(kv => kv.Key + "=" + kv.Value)));

        // ---------- 2. 用 SetInteger（正确 API）开自有光 ----------
        L("");
        L("[2] 用 SetInteger 开自有光（正确 API，产品代码用的就是它）");
        var m = new Material(sh);
        var b0 = Shot(m, 1f);
        Info($"基准：中心 {b0.center} 均值 {b0.mean:F4} 颜色数 {b0.distinct}");
        File.WriteAllBytes("e2e6-selflight-off.png", b0.png);

        m.SetInteger("_UseSelfLight", 1);
        m.SetInteger("_SelfLightOnly", 1);
        m.SetFloat("_SelfLightIntensity", 4f);
        m.SetFloat("_SelfLightShadowStrength", 0f);
        m.SetColor("_SelfLightColor", new Color(1f, 0f, 0f, 1f));
        m.SetVector("_SelfLightDirection", new Vector4(0f, 0f, -1f, 0f));
        // 顺便确认"读回来"和"着色器看到"是两件事
        Info($"_UseSelfLight: GetInteger={m.GetInteger("_UseSelfLight")} GetInt={m.GetInt("_UseSelfLight")} GetFloat={m.GetFloat("_UseSelfLight")}");
        var b1 = Shot(m, 1f);
        Info($"开「只用自有光」后：中心 {b1.center} 均值 {b1.mean:F4} 颜色数 {b1.distinct}  差 {Diff(b0, b1):F4}");
        File.WriteAllBytes("e2e6-selflight-on.png", b1.png);
        Check(Diff(b0, b1) > 0.05, "【关键】自有光排他通路真的改变了画面（差 " + Diff(b0, b1).ToString("F4") + "）");
        Check(b1.center.r > 150 && b1.center.g < 100, "球体中心确实被自有光染红了（" + b1.center + "）");
        UnityEngine.Object.DestroyImmediate(m);

        // ---------- 3. 组件真实流程（② 自有光源 → 只同步参数 不烘焙）----------
        L("");
        L("[3] 走 ② 组件的真实写入流程（反射调用它「只同步参数（不烘焙）」那条路）");
        var compType = FindType("NTSelfLight");
        var edType = FindType("NTSelfLightEditor");
        Check(compType != null && edType != null, "找到 NTSelfLight / NTSelfLightEditor");
        if (compType != null && edType != null)
        {
            var mat = new Material(sh);
            var go = new GameObject("selflight-probe");
            var comp = go.AddComponent(compType);
            // 设置组件字段（都走公开字段）
            void F(string n, object v) { var f = compType.GetField(n); if (f != null) f.SetValue(comp, v); }
            F("targetRoot", go.transform);
            F("includeChildren", true);
            F("onlyThisLight", true);
            F("color", new Color(0f, 1f, 0f, 1f));
            F("intensity", 4f);
            F("shadowStrength", 0f);
            F("matchWorldColor", 0f);
            F("matchWorldDirection", 0f);
            F("lightId", 0);
            F("stampId", false);

            var before = Shot(mat, 1f);
            var apply = edType.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static);
            Check(apply != null, "找到 NTSelfLightEditor.Apply（组件的写入实现）");
            if (apply != null)
            {
                var pars = apply.GetParameters();
                Info("Apply 签名：" + string.Join(", ", pars.Select(p => p.ParameterType.Name + " " + p.Name)));
                var args = new object[pars.Length];
                for (int i = 0; i < pars.Length; i++)
                {
                    if (pars[i].ParameterType == compType) args[i] = comp;
                    else if (pars[i].ParameterType == typeof(List<Material>)) args[i] = new List<Material> { mat };
                    else args[i] = null;
                }
                try { apply.Invoke(null, args); Ok("Apply 调用成功（组件把参数写进了材质）"); }
                catch (Exception e) { Bad("Apply 抛异常：" + (e.InnerException?.Message ?? e.Message)); }

                var after = Shot(mat, 1f);
                Info($"组件写入后：中心 {after.center} 均值 {after.mean:F4}  差 {Diff(before, after):F4}");
                File.WriteAllBytes("e2e6-selflight-component.png", after.png);
                Check(Diff(before, after) > 0.05, "② 组件「自动写入材质」真的生效（差 " + Diff(before, after).ToString("F4") + "）");
                Check(after.center.g > 150, "球体中心被组件的绿色自有光染绿（" + after.center + "）");
                Check(mat.GetInteger("_UseSelfLight") == 1, "材质上 _UseSelfLight 已被组件置 1");
            }
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(mat);
        }
        L("");
        Info("Graphics: " + SystemInfo.graphicsDeviceType);
    }
}
