// E2E 探针 v3 —— 三个待钉死的问题
//   A. 自有光为什么完全没效果？→ 直接读出**生成的 ShaderLab 源码**，看相位代码在不在里面
//   B. 面板本地化到底覆盖多少？→ 调 ShaderCore 真正的 API `L10n.L()`（v2 数 displayName 是我错了）
//   C. 我的渲染架子到底对不对？→ 用内置 Standard 着色器做参考基准
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class NTE2EProbe3
{
    const string OUT = "e2e-report3.txt";
    static readonly StringBuilder sb = new StringBuilder();
    static int pass, fail;
    static void L(string s) { sb.AppendLine(s); Debug.Log("[E2E3] " + s); }
    static void Ok(string s) { pass++; L("  [OK]   " + s); }
    static void Bad(string s) { fail++; L("  [FAIL] " + s); }
    static void Check(bool c, string s) { if (c) Ok(s); else Bad(s); }
    static void Info(string s) { L("  [info] " + s); }

    static bool HasCJK(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
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

    class Shot { public byte[] png; public Color32[] px; public double mean, std, nonBlack; public Color32 center; public int distinct; }

    static Shot Render(Shader sh, float lightIntensity, string label)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var mr = go.GetComponent<MeshRenderer>();
        var mat = new Material(sh);
        mr.sharedMaterial = mat;

        var lg = new GameObject("L");
        var li = lg.AddComponent<Light>();
        li.type = LightType.Directional; li.intensity = lightIntensity; li.color = Color.white;
        li.transform.rotation = Quaternion.Euler(25f, 175f, 0f);

        var cg = new GameObject("Cam");
        var cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.fieldOfView = 40f; cam.transform.position = new Vector3(0, 0, -3.2f); cam.transform.LookAt(Vector3.zero);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.20f, 0.20f, 0.26f, 1f);
        RenderSettings.fog = false;

        const int S = 256;
        var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32); rt.Create();
        var prev = RenderTexture.active;
        cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, S, S), 0, 0); tex.Apply();
        RenderTexture.active = prev; cam.targetTexture = null;

        var px = tex.GetPixels32();
        var st = new Shot { px = px, png = tex.EncodeToPNG() };
        double sum = 0, s2 = 0; int nb = 0; var set = new HashSet<int>();
        foreach (var p in px)
        {
            double l = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0;
            sum += l; s2 += l * l; if (l > 0.02) nb++;
            set.Add((p.r >> 3) << 10 | (p.g >> 3) << 5 | (p.b >> 3));
        }
        int n = px.Length;
        st.mean = sum / n; st.std = Math.Sqrt(Math.Max(0, s2 / n - st.mean * st.mean));
        st.nonBlack = (double)nb / n; st.distinct = set.Count; st.center = px[(S / 2) * S + S / 2];

        UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(lg); UnityEngine.Object.DestroyImmediate(cg);
        UnityEngine.Object.DestroyImmediate(mat);
        return st;
    }

    static double Diff(Shot a, Shot b)
    {
        double d = 0;
        for (int i = 0; i < a.px.Length; i++) d += Math.Abs(a.px[i].r - b.px[i].r) + Math.Abs(a.px[i].g - b.px[i].g) + Math.Abs(a.px[i].b - b.px[i].b);
        return d / (a.px.Length * 3.0 * 255.0);
    }

    public static void Run()
    {
        try { Body(); } catch (Exception e) { Bad("探针异常 " + e); }
        L("");
        L(fail == 0 ? "==== v3 全部通过（" + pass + "）====" : "==== v3 失败 " + fail + " 项（通过 " + pass + "）====");
        try { File.WriteAllText(OUT, sb.ToString()); } catch { }
        EditorApplication.Exit(fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        L("=== E2E 探针 v3 === " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var sh = Shader.Find("nontoon-fork");
        if (sh == null)
        {
            AssetDatabase.ImportAsset("Packages/com.catandling.nontoon/Shaders/NonToon.scshader", ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(); sh = Shader.Find("nontoon-fork");
        }
        Check(sh != null, "Shader.Find(\"nontoon-fork\") 非 null");
        if (sh == null) return;

        // ================= A. 生成的 ShaderLab 源码里到底有什么 =================
        L("");
        L("[A] 生成着色器源码审计（这是真正被编译的东西，不是我们的 .hlsl 源文件）");
        foreach (var name in new[] { "nontoon-fork", "nontoon-fork-fur" })
        {
            var fileName = name == "nontoon-fork" ? "NonToon" : "NonToonFur";
            var path = "Packages/com.catandling.nontoon/Shaders/" + fileName + ".scshader";
            var objs = AssetDatabase.LoadAllAssetsAtPath(path);
            var src = objs.OfType<TextAsset>().FirstOrDefault();
            if (src == null) { Bad(name + " 没有名为 Shader Source 的子资源 TextAsset"); continue; }
            var text = src.text;
            File.WriteAllText("e2e-shader-source-" + name + ".txt", text);
            Info(name + " 生成源码 " + text.Length + " 字符（已存 e2e-shader-source-" + name + ".txt）");

            // 标记检查：这些字符串出现 = 对应模块的相位代码**真的被拼进去了**
            var markers = new (string marker, string what)[]
            {
                ("_SelfLightIntensity", "SelfLight 自有光"),
                ("NTSELFLIGHT", "SelfLight 宏展开"),
                ("__SC_PHASE_", "未替换的相位占位符（出现=注入失败）"),
                ("_ShadowColor", "ShadowColor 阴影色"),
                ("_jp_lilxyzw_nontoon_details", "Details 细节模块"),
                ("_OutlineColor", "主着色器自身属性"),
            };
            foreach (var (m, what) in markers)
            {
                int c = text.Split(new[] { m }, StringSplitOptions.None).Length - 1;
                if (m == "__SC_PHASE_") Check(c == 0, "没有残留相位占位符（" + c + " 个）");
                else Info($"  {what,-28} 出现 {c} 次  ({m})");
            }
            // 关键：SelfLight 相位代码是否在编译单元里
            Check(text.Contains("_SelfLightIntensity"), name + "：SelfLight 相位代码**在**生成源码里");
            Check(text.Contains("saturate(dot(sd.N, "), name + "：方向点积代码在生成源码里");
            // 数一下 Pass 数量（相位是按 pass 展开的）
            Info("  Pass 数量 " + (text.Split(new[] { "Pass" }, StringSplitOptions.None).Length - 1));
            Info("  #pragma 行数 " + text.Split('\n').Count(l => l.TrimStart().StartsWith("#pragma")));
        }

        // ================= B. 本地化：调真正的 API =================
        L("");
        L("[B] 面板本地化（调 ShaderCore 真正的 API L10n.L，不是 displayName）");
        var l10nType = FindType("SCL10n");
        if (l10nType == null) Bad("找不到 SCL10n");
        else
        {
            var loadM = l10nType.GetMethod("Load", new[] { typeof(string) });
            var Lm = l10nType.GetMethod("L", new[] { typeof(string) });
            var settingsType = FindType("Settings");
            string lang = "?";
            if (settingsType != null)
            {
                var inst = settingsType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                var f = settingsType.GetField("language", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (inst != null && f != null) lang = f.GetValue(inst) as string;
            }
            Info("ShaderCore 当前语言 Settings.language = " + lang);

            var mat = new Material(sh);
            var props = MaterialEditor.GetMaterialProperties(new UnityEngine.Object[] { mat });
            Info("面板属性 " + props.Length + " 条");

            // 按 ShaderCore 的实际做法：先 Load(着色器路径)，模块属性由各自的 moduleID 提供
            var raw = props.Select(p => p.displayName).ToArray();
            var best = new Dictionary<string, string>();
            var contexts = new List<string> { "Packages/com.catandling.nontoon/Shaders/NonToon.scshader" };
            // 注册表里所有模块 id 也各当一个上下文
            var regPath = "ProjectSettings/jp.lilxyzw.shadercore.asset";
            if (File.Exists(regPath))
            {
                var ids = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(regPath), "jp\\.lilxyzw\\.nontoon\\.[a-z0-9]+")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct();
                contexts.AddRange(ids);
            }

            // 先看看 core 键单独能不能翻出来
            loadM.Invoke(null, new object[] { "Packages/com.catandling.nontoon/Shaders/NonToon.scshader" });
            foreach (var k in new[] { "__Texture", "__SharedMask", "__MaskChannel", "__RenderingMode", "__SelectModules", "Shadow Bias", "Outline Color", "Light Min Limit" })
            {
                var v = (string)Lm.Invoke(null, new object[] { k });
                Info($"  L(\"{k}\") = \"{v}\"" + (HasCJK(v) ? "   ← 中文" : "   ← 没翻译"));
            }

            foreach (var ctx in contexts)
            {
                try { loadM.Invoke(null, new object[] { ctx }); } catch { continue; }
                foreach (var d in raw)
                {
                    var v = (string)Lm.Invoke(null, new object[] { d });
                    if (v != d) best[d] = v;
                }
            }

            int cjk = raw.Count(d => best.ContainsKey(d) && HasCJK(best[d]));
            int translated = raw.Count(d => best.ContainsKey(d));
            Info($"结论：165 条属性里，L10n 能翻出**非原文**的 {translated} 条，其中**真正是中文**的 {cjk} 条，" +
                 $"剩 {raw.Length - cjk} 条显示时仍是原文");
            // 全量导出，供人工核对
            var dump = new StringBuilder();
            foreach (var p in props)
            {
                var d = p.displayName;
                dump.AppendLine($"{p.name,-60} | 原文: {d,-32} | 显示: {(best.TryGetValue(d, out var v) ? v : d)}");
            }
            File.WriteAllText("e2e-l10n.txt", dump.ToString());
            Info("全量对照已存 e2e-l10n.txt");
            var stillEn = raw.Where(d => !best.ContainsKey(d)).Distinct().Take(30).ToArray();
            if (stillEn.Length > 0) Info("仍未翻译（前 30）：" + string.Join(" / ", stillEn));
            UnityEngine.Object.DestroyImmediate(mat);
        }

        // ================= C. 渲染架子自检 + 参考基准 =================
        L("");
        L("[C] 渲染与光照：先用内置 Standard 证明场景本身是好的");
        var std = Shader.Find("Standard");
        var unlit = Shader.Find("Unlit/Color");
        Check(std != null, "内置 Standard 着色器可用");
        if (std != null)
        {
            var s1 = Render(std, 1f, "Standard-光强1");
            var s0 = Render(std, 0f, "Standard-光强0");
            Info(string.Format("Standard 光强1：均值 {0:F4} 中心 {1} 非黑 {2:F1}% ; 光强0：均值 {3:F4} 中心 {4}",
                s1.mean, s1.center, s1.nonBlack * 100, s0.mean, s0.center));
            var d = Diff(s1, s0);
            Info("Standard 开/关光的像素差 " + d.ToString("F4"));
            Check(d > 0.05, "【场景基准】内置 Standard 对平行光有强烈响应（差 " + d.ToString("F4") + "）→ 场景光照设置是好的");
            File.WriteAllBytes("e2e-std-light.png", s1.png);
        }

        L("");
        L("[C2] NonToon 在同一场景下");
        var n1 = Render(sh, 1f, "NonToon-光强1");
        var n0 = Render(sh, 0f, "NonToon-光强0");
        Info(string.Format("NonToon 光强1：均值 {0:F4} 中心 {1} 非黑 {2:F1}% ; 光强0：均值 {3:F4} 中心 {4}",
            n1.mean, n1.center, n1.nonBlack * 100, n0.mean, n0.center));
        Info("NonToon 开/关光的像素差 " + Diff(n1, n0).ToString("F4"));
        File.WriteAllBytes("e2e-nt-light1.png", n1.png);

        // ================= D. 自有光：用排他通路做最严苛的判定 =================
        L("");
        L("[D] 自有光排他通路（若能生效，世界环境光必须被掐掉 → 画面必然变化）");
        var matL = new Material(sh);
        var baseShot = Render(sh, 1f, "基准");
        // 用材质直连：造一个带参数的材质
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.GetComponent<MeshRenderer>().sharedMaterial = matL;
        var lg = new GameObject("L"); var li = lg.AddComponent<Light>();
        li.type = LightType.Directional; li.intensity = 1f; li.transform.rotation = Quaternion.Euler(25f, 175f, 0f);
        var cg = new GameObject("Cam"); var cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.fieldOfView = 40f; cam.transform.position = new Vector3(0, 0, -3.2f); cam.transform.LookAt(Vector3.zero);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.20f, 0.20f, 0.26f, 1f);
        const int S = 256;
        var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32); rt.Create();
        Func<Shot> shot = () =>
        {
            var prev = RenderTexture.active; cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, S, S), 0, 0); tex.Apply(); RenderTexture.active = prev; cam.targetTexture = null;
            var px = tex.GetPixels32();
            var st = new Shot { px = px, png = tex.EncodeToPNG() };
            double sum = 0, s2 = 0; int nb = 0; var set = new HashSet<int>();
            foreach (var p in px) { double l = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0; sum += l; s2 += l * l; if (l > 0.02) nb++; set.Add((p.r >> 3) << 10 | (p.g >> 3) << 5 | (p.b >> 3)); }
            int n2 = px.Length; st.mean = sum / n2; st.std = Math.Sqrt(Math.Max(0, s2 / n2 - st.mean * st.mean)); st.nonBlack = (double)nb / n2; st.distinct = set.Count; st.center = px[(S / 2) * S + S / 2];
            UnityEngine.Object.DestroyImmediate(tex);
            return st;
        };
        var b0 = shot();
        matL.SetInt("_UseSelfLight", 1);
        matL.SetInt("_SelfLightOnly", 1);
        if (matL.HasProperty("_SelfLightIntensity")) matL.SetFloat("_SelfLightIntensity", 8f);
        if (matL.HasProperty("_SelfLightShadowStrength")) matL.SetFloat("_SelfLightShadowStrength", 0f);
        if (matL.HasProperty("_SelfLightColor")) matL.SetColor("_SelfLightColor", new Color(1f, 0f, 0f, 1f));
        if (matL.HasProperty("_SelfLightDirection")) matL.SetVector("_SelfLightDirection", new Vector4(0f, 0f, -1f, 0f));
        Info("  _UseSelfLight=" + matL.GetInt("_UseSelfLight") + " _SelfLightOnly=" + matL.GetInt("_SelfLightOnly")
            + " _SelfLightIntensity=" + matL.GetFloat("_SelfLightIntensity"));
        var b1 = shot();
        double dSelf = Diff(b0, b1);
        Info("排他自有光前后：差 " + dSelf.ToString("F4") + "  均值 " + b0.mean.ToString("F4") + " → " + b1.mean.ToString("F4")
            + "  中心 " + b0.center + " → " + b1.center);
        File.WriteAllBytes("e2e-nt-selflight-only.png", b1.png);
        Check(dSelf > 0.002, "自有光排他通路改变了画面（差 " + dSelf.ToString("F4") + "）");

        UnityEngine.Object.DestroyImmediate(cam); UnityEngine.Object.DestroyImmediate(lg);
        UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(matL);
    }
}
