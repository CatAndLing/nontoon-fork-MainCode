// 装置 A：强制 D3D11 bundle 编译 + 精确负向对照 + 从 bundle 加载后的浮点像素门禁。
// 只覆盖下列代表性变体及 SelfLight 合成深度图；不证明所有平台、实时灯阴影或零开销。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class NTBundleProbe
{
    const string Dir = "Assets/NTProbe";
    const string BundleDir = Dir + "/Bundles";
    const string BrokenShaderPath = Dir + "/NTBrokenProbe.shader";
    const string BrokenMatPath = Dir + "/NTBrokenProbe.mat";
    const string BundleName = "ntshaderprobe";
    const string Matcap = "_JP_LILXYZW_NONTOON_MATCAPS_ENABLE_";
    const string Details = "_JP_LILXYZW_NONTOON_DETAILS_ENABLE_";
    const int Size = 64;
    // 固定门槛来自受控几何/混合公式：alpha=.4 => T=.6；白面×光强2 => HDR=2。
    const float Epsilon = 0.005f;
    static readonly StringBuilder Sb = new StringBuilder();
    static readonly List<string> Errors = new List<string>();
    static readonly List<string> BuildErrors = new List<string>();
    static int Fail, Pass;
    static bool Building;
    static string Out { get { return Path.Combine(Directory.GetCurrentDirectory(), "ntshadercompile.txt"); } }

    // 赋值时引用未声明符号、随后正常 return：避免旧夹具同时报“未声明”与“缺少返回值”。
    const string BrokenSource = @"Shader ""NTBrokenProbe""
{
    SubShader { Pass {
        CGPROGRAM
        #pragma only_renderers d3d11
        #pragma vertex vert
        #pragma fragment frag
        #include ""UnityCG.cginc""
        float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }
        float4 frag() : SV_Target {
            float4 result = 0;
            result += undeclaredIdentifier_probe;
            return result;
        }
        ENDCG
    } }
}";
    static readonly Regex ExpectedError = new Regex(
        @"\AShader error in 'NTBrokenProbe': undeclared identifier 'undeclaredIdentifier_probe' at line \d+ \(on d3d11\)(?:\r?\n[\s\S]*)?\z");

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Sb.AppendLine("  " + (ok ? "[OK] " : "[FAIL] ") + what);
    }
    static void Capture(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert &&
            !message.StartsWith("Shader error", StringComparison.Ordinal)) return;
        Errors.Add(message);
        if (Building) BuildErrors.Add(message);
    }

    public static void Run()
    {
        Sb.Clear(); Errors.Clear(); BuildErrors.Clear(); Fail = Pass = 0; Building = false;
        Application.logMessageReceived += Capture;
        try
        {
            if (File.Exists(Out)) File.Delete(Out);
            Sb.AppendLine("== 运行信息 ==");
            Sb.AppendLine("时间 = " + DateTime.UtcNow.ToString("O") + " / probe-id = " + Guid.NewGuid());
            Sb.AppendLine("工程 = " + Directory.GetCurrentDirectory());
            Sb.AppendLine("Unity = " + Application.unityVersion + " / GPU = " + SystemInfo.graphicsDeviceType);
            Body();
        }
        catch (Exception e) { Check(false, "PROBE EXCEPTION: " + e); }
        finally { Application.logMessageReceived -= Capture; Building = false; }

        Sb.AppendLine("== 编译负向对照（仅统计本次强制 Build 的回调，不接受导入阶段冒充） ==");
        Check(BuildErrors.Count(ExpectedError.IsMatch) == 1, "负向对照恰好 1 条预期的未声明标识符错误");
        Check(BuildErrors.Count == 1, "构建只报这 1 条错误，无其它 shader/构建错误（实际 " + BuildErrors.Count + "）");
        Check(Errors.All(ExpectedError.IsMatch), "包括导入/加载/渲染在内，无非预期错误");
        foreach (var e in Errors) Sb.AppendLine("  捕获: " + e);
        Sb.AppendLine(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + " 项）====");
        try
        {
            File.WriteAllText(Out, Sb.ToString());
            // B 启动会删除工程根 *.txt；保留本轮 A 的原始报告供交付核对。
            var evidence = Path.GetFullPath("../_notes/gate-fixes-evidence");
            if (Directory.Exists(evidence)) File.WriteAllText(Path.Combine(evidence, "ntshadercompile.txt"), Sb.ToString());
        }
        catch (Exception e) { Debug.LogError("报告写入失败: " + e); Fail++; }
        Debug.Log("[NTBundleProbe] done -> " + Out + " (fail=" + Fail + ", pass=" + Pass + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    sealed class Case
    {
        public string Path, Shader;
        public int Profile;
        public bool Pixels, Keywords;
    }

    static void Body()
    {
        Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "渲染设备为 D3D11（非 Null）");
        Check(QualitySettings.activeColorSpace == ColorSpace.Linear, "Linear 色彩空间");
        Check(GraphicsSettings.currentRenderPipeline == null, "Built-in RP");
        Check(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) &&
              SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat), "支持线性浮点渲染与读回");
        if (Fail != 0) return;
        Sb.AppendLine("== Shader rename: forced import / exact name resolution ==");
        foreach (var pair in new[] {
            new[] { "NonToon", "nontoon-fork" },
            new[] { "NonToonFur", "nontoon-fork-fur" },
            new[] { "NonToonTwoPass", "nontoon-fork-twopass" }
        })
        {
            var path = "Packages/com.catandling.nontoon/Shaders/" + pair[0] + ".scshader";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var imported = AssetDatabase.LoadAssetAtPath<Shader>(path);
            var found = Shader.Find(pair[1]);
            Check(imported != null && imported.name == pair[1] && found == imported,
                "Shader.Find(\"" + pair[1] + "\") != null and resolves to " + path);
            Check(Shader.Find(pair[0]) == null, "Shader.Find(\"" + pair[0] + "\") == null (fork-only project)");
        }
        if (Fail != 0) return;
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "NTProbe");
        if (!AssetDatabase.IsValidFolder(BundleDir)) AssetDatabase.CreateFolder(Dir, "Bundles");
        File.WriteAllText(BrokenShaderPath, BrokenSource);
        AssetDatabase.ImportAsset(BrokenShaderPath, ImportAssetOptions.ForceUpdate);
        var broken = AssetDatabase.LoadAssetAtPath<Shader>(BrokenShaderPath);
        Check(broken != null && broken.name == "NTBrokenProbe", "负向 shader 正确导入");
        if (broken == null) return;
        SaveMaterial(new Material(broken), BrokenMatPath);

        var alpha = SaveTexture("Alpha", new Color(1, 1, 1, 0.4f));
        SaveTexture("Occluded", Color.black);
        var cases = new List<Case>();
        foreach (var name in new[] { "nontoon-fork", "nontoon-fork-fur" })
        {
            var shader = Shader.Find(name);
            if (shader == null)
            {
                var fileName = name == "nontoon-fork" ? "NonToon" : "NonToonFur";
                AssetDatabase.ImportAsset("Packages/com.catandling.nontoon/Shaders/" + fileName + ".scshader", ImportAssetOptions.ForceUpdate);
                shader = Shader.Find(name);
            }
            Check(shader != null && shader.name == name, "找到产品 shader " + name);
            if (shader == null) continue;
            foreach (var profile in new[] { 0, 2 })
            {
                var m = new Material(shader);
                Configure(m, profile, alpha, true);
                SetNumber(m, "_SelfLightOnly", profile == 2 ? 1 : 0);
                SetNumber(m, "_ShadowColorEnable", 1);
                SetNumber(m, "_ShadowMaskEnable", 1);
                SetNumber(m, "_UseEmission", 1);
                SetNumber(m, "_SelfLightReceiveMaskStrength", 1);
                SetNumber(m, "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex", 0);
                SetNumber(m, "_jp_lilxyzw_nontoon_nearer_Enable", 1);
                SetNumber(m, "_jp_lilxyzw_nontoon_distancefade_DistanceFadeStrength", 0.5f);
                var c = new Case { Path = Dir + "/NTCompile_" + name + "_" + profile + ".mat", Shader = name, Profile = profile, Keywords = true };
                SaveMaterial(m, c.Path); cases.Add(c);
            }
            // Fur 的源码固定 _RenderingMode=0，不伪造透明模式覆盖。
            foreach (var mode in name == "nontoon-fork" ? new[] { 0, 2 } : new[] { 0 })
            foreach (var keywords in new[] { false, true })
            {
                var m = new Material(shader);
                Configure(m, mode, alpha, keywords);
                var c = new Case { Path = Dir + "/NTRender_" + name + "_" + mode + "_" + keywords + ".mat", Shader = name, Profile = mode, Pixels = true, Keywords = keywords };
                SaveMaterial(m, c.Path); cases.Add(c);
            }
        }
        AssetDatabase.SaveAssets();
        Check(cases.Count(c => !c.Pixels) == 4, "恰好四个原始产品材质（Fur 第二项是光照配置，实际仍为模式0）");
        Check(cases.Count(c => c.Pixels) == 6, "六个渲染材质覆盖合法模式及 Details/MatCaps keyword 开/关");
        var inputs = cases.Select(c => c.Path).Concat(new[] { BrokenMatPath, Dir + "/Occluded.asset" }).ToArray();
        Check(inputs.Distinct().Count() == inputs.Length && inputs.All(p => !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p))), "bundle 输入逐项存在且路径唯一");
        foreach (var c in cases) Validate(AssetDatabase.LoadAssetAtPath<Material>(c.Path), c, "输入");
        Sb.AppendLine("== 强制构建 Windows64 / D3D11 ==");
        Check(EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64, "活动目标 Windows64");
        Check(PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64).SequenceEqual(new[] { GraphicsDeviceType.Direct3D11 }), "目标图形 API 恰为 D3D11");
        if (Fail != 0) return;
        AssetBundleManifest manifest;
        Building = true;
        try
        {
            manifest = BuildPipeline.BuildAssetBundles(BundleDir,
                new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = inputs } },
                BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
        }
        finally { Building = false; }
        Check(manifest != null && manifest.GetAllAssetBundles().SequenceEqual(new[] { BundleName }), "本轮 manifest 恰含目标 bundle");
        if (manifest == null) return;
        var bundle = AssetBundle.LoadFromFile(Path.Combine(BundleDir, BundleName));
        Check(bundle != null, "从本轮编译产物加载 AssetBundle");
        if (bundle == null) return;
        try
        {
            var names = new HashSet<string>(bundle.GetAllAssetNames(), StringComparer.OrdinalIgnoreCase);
            Check(names.SetEquals(inputs), "bundle 实际资产清单与输入完全一致");
            var occluded = bundle.LoadAsset<Texture2D>(Dir + "/Occluded.asset");
            Check(occluded != null, "加载全遮挡深度图");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
            RenderSettings.fog = false; RenderSettings.skybox = null;
            foreach (var c in cases)
            {
                var m = bundle.LoadAsset<Material>(c.Path);
                Validate(m, c, "产物");
                if (m == null) continue;
                Check(m != AssetDatabase.LoadAssetAtPath<Material>(c.Path), "材质来自 bundle 而非 AssetDatabase: " + m.name);
                foreach (var p in c.Shader == "nontoon-fork" ? new[] { "Forward", "ForwardAdd", "ShadowCaster", "Outline" } : new[] { "Forward", "ForwardAdd", "ShadowCaster", "Fur" })
                {
                    var index = m.FindPass(p);
                    Check(index >= 0 && m.SetPass(index), "产物 Pass 可用: " + m.name + "/" + p);
                }
                if (c.Pixels && occluded != null) PixelGate(m, c, occluded);
            }
        }
        finally { bundle.Unload(true); }
    }

    static void Validate(Material m, Case c, string stage)
    {
        var label = stage + " " + Path.GetFileName(c.Path);
        Check(m != null && m.shader != null && m.shader.name == c.Shader, label + " shader 匹配");
        if (m == null) return;
        Check(m.shader.isSupported, label + " shader 支持当前设备");
        if (c.Shader == "nontoon-fork") Check(m.GetInteger("_RenderingMode") == c.Profile, label + " RenderingMode=" + c.Profile);
        else Check(!m.HasProperty("_RenderingMode"), label + " Fur 无 RenderingMode 属性，源码固定0");
        Check(m.IsKeywordEnabled(Matcap + "1") == c.Keywords && m.IsKeywordEnabled(Details + "1") == c.Keywords &&
              m.IsKeywordEnabled(Matcap + "0") == !c.Keywords && m.IsKeywordEnabled(Details + "0") == !c.Keywords, label + " keyword 实态匹配");
        Check(m.GetInteger("_SelfLightOnly") == (c.Pixels || c.Profile == 2 ? 1 : 0), label + " SelfLight 配置匹配");
    }

    static void Configure(Material m, int mode, Texture2D alpha, bool keywords)
    {
        if (m.HasProperty("_RenderingMode")) SetNumber(m, "_RenderingMode", mode);
        if (m.shader.name == "nontoon-fork")
        {
            SetNumber(m, "_SrcBlend", mode == 2 ? (int)BlendMode.SrcAlpha : (int)BlendMode.One);
            SetNumber(m, "_DstBlend", mode == 2 ? (int)BlendMode.OneMinusSrcAlpha : (int)BlendMode.Zero);
            SetNumber(m, "_OutlineWidth", 0);
            m.renderQueue = mode == 2 ? 3000 : 2000;
        }
        else m.SetVector("_FurVector", Vector4.zero);
        m.SetTexture("_BaseTexture", alpha);
        SetNumber(m, "_UseSelfLight", 1); SetNumber(m, "_SelfLightOnly", 1);
        SetNumber(m, "_SelfLightIntensity", 2); SetNumber(m, "_LightMaxLimit", 4);
        SetNumber(m, "_SelfLightDistance", 0); SetNumber(m, "_SelfLightPCSS", 1);
        SetNumber(m, "_SelfLightPCSSQuality", 3);
        m.SetVector("_SelfLightDirection", new Vector4(0, 0, -1, 0));
        m.SetVector("_SelfLightOrigin", new Vector4(0, 0, -0.5f, 0));
        // ShaderLab Color 在 Linear 工程上传时解码；夹具指定的是线性 .5。
        m.SetColor("_jp_lilxyzw_nontoon_matcaps_MatCapMultiplyColor", new Color(0.5f, 1, 1, 1).gamma);
        foreach (var k in new[] { Matcap, Details })
        {
            // 生成指令是二选一 _0/_1，没有空 keyword 变体。必须显式选择 _0。
            m.DisableKeyword(k + (keywords ? "0" : "1"));
            m.EnableKeyword(k + (keywords ? "1" : "0"));
        }
    }

    static void PixelGate(Material m, Case c, Texture2D occluded)
    {
        Sb.AppendLine("== 像素 " + m.name + "（中心16x16固定ROI，不按亮度挑像素） ==");
        var alpha = c.Shader == "nontoon-fork" && c.Profile == 2 ? 0.4f : 1f;
        var expectedT = 1 - alpha;
        var black = Render(m, Color.black);
        var white = Render(m, Color.white);
        // .5 的线性背景编码成 sRGB 后交给 Camera；避免项目历史上的 gamma 假失败。
        var middle = Render(m, new Color(0.5f, 0.5f, 0.5f, 1).gamma);
        m.SetTexture("_SelfLightShadowMap", occluded);
        var dark = Render(m, Color.black);
        var darkWhite = Render(m, Color.white);
        SetNumber(m, "_SelfLightShadowStrength", 0);
        var bypass = Render(m, Color.black);
        SetNumber(m, "_SelfLightShadowStrength", 1); m.SetTexture("_SelfLightShadowMap", null);
        bool finite = new[] { black, white, middle, dark, darkWhite, bypass }.All(a => a.All(p =>
            Enumerable.Range(0, 4).All(k => !float.IsNaN(p[k]) && !float.IsInfinity(p[k]))));
        Check(finite, "全部读回像素有限，无 NaN/Inf");
        float tError = 0, litError = 0, darkError = 0, affine = 0, restore = 0, minSignal = float.PositiveInfinity, peak = 0;
        for (int y = 24; y < 40; y++) for (int x = 24; x < 40; x++)
        {
            int i = y * Size + x;
            for (int k = 0; k < 3; k++)
            {
                float expectedLit = 2 * alpha * (c.Keywords && k == 0 ? 0.5f : 1);
                tError = Mathf.Max(tError, Mathf.Abs(white[i][k] - black[i][k] - expectedT), Mathf.Abs(darkWhite[i][k] - dark[i][k] - expectedT));
                litError = Mathf.Max(litError, Mathf.Abs(black[i][k] - expectedLit));
                darkError = Mathf.Max(darkError, Mathf.Abs(dark[i][k]));
                affine = Mathf.Max(affine, Mathf.Abs(middle[i][k] - (black[i][k] + white[i][k]) * 0.5f));
                restore = Mathf.Max(restore, Mathf.Abs(bypass[i][k] - black[i][k]));
                minSignal = Mathf.Min(minSignal, black[i][k] - dark[i][k]);
                peak = Mathf.Max(peak, white[i][k]);
            }
        }
        Sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  T期望={0:F3}; T误差={1:F6}; 受光误差={2:F6}; 遮挡误差={3:F6}; 仿射残差={4:F6}; 恢复误差={5:F6}; 最小阴影信号={6:F6}; HDR峰值={7:F6}", expectedT, tError, litError, darkError, affine, restore, minSignal, peak));
        Check(tError <= Epsilon, "无阴影/全遮挡 T 均匹配 0 或 .6（最大误差 <= .005）");
        Check(litError <= Epsilon, "受光值匹配 2×alpha×MatCap（最大误差 <= .005）");
        Check(darkError <= Epsilon && minSignal > 0.35f, "全遮挡接近黑且每个ROI通道有阴影信号（误差 <= .005，信号 > .35）");
        Check(affine <= Epsilon && peak > 1.1f, "黑/灰/白背景仿射且 HDR > 1.1（未夹取）");
        Check(restore <= Epsilon, "阴影强度0恢复无阴影像素（最大差 <= .005）");
    }

    static Color[] Render(Material m, Color background)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var camGo = new GameObject("NTBundlePixelCamera");
        var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
        var previous = RenderTexture.active;
        try
        {
            go.GetComponent<Renderer>().sharedMaterial = m;
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false; cam.orthographic = true; cam.orthographicSize = 0.75f;
            cam.transform.position = new Vector3(0, 0, -2);
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = background;
            cam.allowHDR = true; cam.allowMSAA = false; cam.renderingPath = RenderingPath.Forward;
            rt.Create(); cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); tex.Apply();
            return tex.GetPixels();
        }
        finally
        {
            RenderTexture.active = previous;
            Object.DestroyImmediate(camGo); Object.DestroyImmediate(go);
            Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt);
        }
    }

    static void SaveMaterial(Material source, string path)
    {
        var old = AssetDatabase.LoadAssetAtPath<Material>(path);
        source.name = Path.GetFileNameWithoutExtension(path);
        if (old == null) AssetDatabase.CreateAsset(source, path);
        else { EditorUtility.CopySerialized(source, old); EditorUtility.SetDirty(old); Object.DestroyImmediate(source); }
    }
    static Texture2D SaveTexture(string name, Color color)
    {
        var path = Dir + "/" + name + ".asset";
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex == null) { tex = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true); AssetDatabase.CreateAsset(tex, path); }
        tex.SetPixels(Enumerable.Repeat(color, 16).ToArray()); tex.Apply();
        tex.filterMode = FilterMode.Point; tex.wrapMode = TextureWrapMode.Clamp; EditorUtility.SetDirty(tex);
        return tex;
    }
    static void SetNumber(Material m, string p, float v)
    {
        if (!m.HasProperty(p)) throw new InvalidOperationException(m.shader.name + " 缺少属性 " + p);
        var index = m.shader.FindPropertyIndex(p);
        if (m.shader.GetPropertyType(index) == ShaderPropertyType.Int) m.SetInteger(p, Mathf.RoundToInt(v));
        else m.SetFloat(p, v);
    }
}
