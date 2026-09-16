// 用真实的 AAO(Avatar Optimizer) + NDMF 在批处理里跑一次 avatar 构建，
// 实证回答两个问题：
//   Q1（E11）AAO 的 Trace and Optimize 会不会干掉 ③ 插件生成的 FX 层 / 参数 / 材质属性动画？
//   Q2（E9）AAO 对未注册 ShaderInformation 的着色器（NonToon）是否真的走"保守路径"——
//           也就是**不会**乱删贴图 / 乱改材质属性？
//
// 依赖：_verify-proj/Packages 下有 MA + AAO + NDMF（本批验证 1.18.7 / 1.9.19 / 1.14.8）
//       + com.unity.burst + com.unity.nuget.newtonsoft-json（从用户工程拷来，见交接文档）
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class NTAaoProbe
{
    const string OUT = "ntaao.txt";
    const string Dir = "Assets/NTAaoProbe";
    static readonly StringBuilder Sb = new StringBuilder();
    static int Fail, Pass;
    static readonly List<string> Logs = new List<string>();

    static void L(string s) { Sb.AppendLine(s); Debug.Log("[NTAO] " + s); }
    static void Ok(string s) { Pass++; L("  ✅ " + s); }
    static void Bad(string s) { Fail++; L("  ❌ " + s); }
    static void Info(string s) { L("  ·  " + s); }
    static void Check(bool c, string what, string detail = "")
    {
        // ⛔ `detail` 必须是 null 安全的：调用方常写 `x?.Something`，x 为 null 时 detail 就是 null，
        //    以前这里直接 `detail.Length` ⇒ `NullReferenceException`（探针自己崩，看起来像产品缺陷）。
        var d = detail != null && detail.Length > 0 ? " → " + detail : "";
        if (c) Pass++; else Fail++;
        L((c ? "  ✅ " : "  ❌ ") + what + d);
    }

    static Type T(string full)
    {
        var t = Type.GetType(full);
        if (t != null) return t;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        { var x = a.GetType(full, false); if (x != null) return x; }
        return null;
    }
    static Type TName(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var x in ts) if (x.Name == name) return x;
        }
        return null;
    }
    static void SetField(object o, string n, object v)
    {
        var f = o.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) f.SetValue(o, v);
    }
    static object GetField(object o, string n)
    {
        var f = o?.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(o);
    }

    public static void Run()
    {
        try { if (File.Exists(OUT)) File.Delete(OUT); } catch { }
        Application.logMessageReceived += (m, s, t) =>
        {
            if (t == LogType.Warning || t == LogType.Error || t == LogType.Exception || m.StartsWith("[NonToon NDMF]")) Logs.Add(t + " | " + m);
        };

        L("== 运行信息 ==");
        L("时间  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        L("工程  = " + Directory.GetCurrentDirectory());
        L("Unity = " + Application.unityVersion);
        L("");

        try { Body(); }
        catch (Exception e) { Bad("探针异常：" + e); }

        L("");
        L(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + "）====");
        File.WriteAllText(OUT, Sb.ToString());
        Debug.Log("[NTAaoProbe] done -> " + OUT + " (fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        // ---------------- A. 环境 ----------------
        L("== A. AAO / NDMF 环境 ==");
        var aaoType = T("Anatawa12.AvatarOptimizer.TraceAndOptimize") ?? TName("TraceAndOptimize");
        var mergeType = T("Anatawa12.AvatarOptimizer.MergeSkinnedMesh") ?? TName("MergeSkinnedMesh");
        var procType = T("nadena.dev.ndmf.AvatarProcessor");
        // VRChatPlatform 是 **internal**，且命名空间是小写的 nadena.dev.ndmf.vrchat
        // ⇒ 用 Type.GetType 拿不到，也可能因程序集未加载而找不到。这里按程序集强加载来取，并打印诊断。
        Type platType = null; object platInst = null;
        foreach (var an in new[] { "nadena.dev.ndmf.vrchat", "nadena.dev.ndmf" })
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == an);
                if (asm == null) asm = Assembly.Load(an);
                var t = asm?.GetType("nadena.dev.ndmf.vrchat.VRChatPlatform", false);
                if (t != null) { platType = t; Info("VRChatPlatform 来自 " + an + "（" + (t.IsPublic ? "public" : "internal") + "）"); break; }
            }
            catch (Exception e) { Info("加载 " + an + " 失败：" + e.Message); }
        }
        if (platType == null) platType = TName("VRChatPlatform");
        if (platType != null)
            platInst = platType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? platType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (platInst == null)
        {
            var apType = T("nadena.dev.ndmf.AmbientPlatform");
            platInst = apType?.GetProperty("CurrentPlatform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? apType?.GetProperty("DefaultPlatform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (platInst != null) Info("改用 AmbientPlatform：" + platInst.GetType().FullName);
        }
        Info("已加载的 ndmf 程序集：" + string.Join(", ",
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n.Contains("ndmf")).ToArray()));
        var siType = T("Anatawa12.AvatarOptimizer.API.ShaderInformation");
        var siRegType = T("Anatawa12.AvatarOptimizer.API.ShaderInformationRegistry");
        // F is now a required real-build check. Missing dependencies must fail closed.
        if (aaoType == null || procType == null)
        {
            Bad("本批 F 必须跑真实 AAO/NDMF 构建：缺依赖不能作为通过证据");
            return;
        }

        Check(true, "找到 AAO 的 TraceAndOptimize 组件", aaoType.Assembly.GetName().Name);
        Check(mergeType != null, "找到 AAO 的 MergeSkinnedMesh 组件");
        Check(procType != null, "找到 NDMF 的 AvatarProcessor");
        Check(platType != null, "找到 NDMF 的 VRChatPlatform 类型");
        Check(platInst != null, "拿到平台实例", platInst?.GetType().FullName);
        Check(siType != null && siRegType != null, "找到 AAO 的 ShaderInformation API（E9 相关）");

        // ---------------- B. 搭一个最小 avatar ----------------
        L("");
        L("== B. 搭最小 avatar（含 ③ 插件真实产物）==");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets", "NTAaoProbe");

        var shader = Shader.Find("nontoon-fork");
        Check(shader != null, "找到 NonToon 着色器");
        if (shader == null) return;

        // 材质资产（AAO 处理的是资产，所以要落盘）
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        tex.name = "NTAaoProbeTex";
        File.WriteAllBytes(Dir + "/NTAaoProbeTex.png", tex.EncodeToPNG());
        AssetDatabase.ImportAsset(Dir + "/NTAaoProbeTex.png", ImportAssetOptions.ForceUpdate);
        var texAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/NTAaoProbeTex.png");

        var matPath = Dir + "/NTAaoMat.mat";
        if (File.Exists(matPath)) AssetDatabase.DeleteAsset(matPath);
        var mat = new Material(shader);
        mat.name = "NTAaoMat";
        mat.SetTexture("_BaseTexture", texAsset);
        mat.SetFloat("_LightMinLimit", 0f);
        AssetDatabase.CreateAsset(mat, matPath);
        var mat2Path = Dir + "/NTAaoMat2.mat";
        if (File.Exists(mat2Path)) AssetDatabase.DeleteAsset(mat2Path);
        var mat2 = new Material(shader);
        mat2.name = "NTAaoMat2";
        mat2.SetTexture("_BaseTexture", texAsset);
        AssetDatabase.CreateAsset(mat2, mat2Path);

        var root = new GameObject("NTAAOAvatar");
        var descType = TName("VRCAvatarDescriptor");
        var desc = root.AddComponent(descType);
        root.AddComponent<Animator>();
        root.AddComponent(aaoType);                    // Trace and Optimize
        // 真实 avatar 上都有 VRCPipelineManager（SDK 加的）；NDMF 构建时也会用到它。
        // 缺了它构建里会出现 "ArgumentNullException: first"（实测）。
        var pmType = T("VRC.SDKBase.VRCPipelineManager") ?? TName("VRCPipelineManager");
        if (pmType != null) { root.AddComponent(pmType); Info("已加 " + pmType.Name); }
        else Info("没找到 VRCPipelineManager（com.vrchat.base 里的类型）");

        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());
        var accessory = GameObject.CreatePrimitive(PrimitiveType.Cube);
        accessory.name = "Accessory";
        accessory.transform.SetParent(root.transform, false);
        accessory.transform.localPosition = new Vector3(0.5f, 0, 0);
        UnityEngine.Object.DestroyImmediate(accessory.GetComponent<Collider>());
        body.GetComponent<MeshRenderer>().sharedMaterial = mat;
        accessory.GetComponent<MeshRenderer>().sharedMaterial = mat2;

        // ③ 插件生成真实产物
        var builderType = TName("NTVrcParameterBuilder");
        Check(builderType != null, "找到 NTVrcParameterBuilder");
        var build = builderType?.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object res = null;
        try
        {
            res = build.Invoke(null, new object[]
            {
                root, new List<Renderer> { body.GetComponent<MeshRenderer>(), accessory.GetComponent<MeshRenderer>() },
                "NT_Light", 0.05f, 0.6f, "Assets/NonToonLightAdjuster", "_LightMinLimit"
            });
        }
        catch (Exception e) { Bad("生成 ③ 产物失败：" + (e.InnerException ?? e).Message); }
        if (res != null)
        {
            var ok = (bool)(res.GetType().GetProperty("Ok")?.GetValue(res) ?? false);
            Check(ok, "③ 产物生成无错误");
            L((res.GetType().GetMethod("Summary")?.Invoke(res, null) as string ?? "").TrimEnd());
        }

        // 同时保留旧版直接 FX 挂载作为残留夹具，并用当前 MA Attach 路径安装。
        // 因此输出可能有同名层；这是检查旧残留报警/不擅自删除的有意输入。
        // Use THIS invocation's result; the former fixed path can contain stale assets.
        var fx = GetField(res, "Controller") as AnimatorController;
        Check(fx != null, "拿到生成的 FX 控制器");
        if (fx == null) return;
        var attach = T("NonToonTools.NTModularAvatarBridge").GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { root, fx, "NT_Light", true, 1f, "亮度", true });
        Check((bool)attach.GetType().GetProperty("Ok").GetValue(attach), "③ 产物按当前窗口路径挂上真实 MA 组件");
        bool assigned = AssignFxLayer(desc, descType, fx);
        Check(assigned, "已把 FX 控制器写进 VRCAvatarDescriptor 的 FX 层");

        // F owns the real build: deliberately stale inputs must change in the BUILD output only.
        var pluginType = T("NonToonTools.NTBuildPlugin");
        Check(pluginType != null, "【NDMF】工具包插件已加载");
        mat.SetInteger("_RenderingMode", 2); mat.renderQueue = 2000;
        mat2.SetInteger("_RenderingMode", 0); mat2.renderQueue = 3000;
        mat2.SetFloat("_LightMaxLimit", 0.12f);
        var adjustType = T("NonToonTools.NTLightAdjust");
        var adjust = accessory.AddComponent(adjustType);
        SetField(adjust, "mode", Enum.ToObject(adjustType.GetField("mode").FieldType, 0));
        SetField(adjust, "propertyName", "_LightMaxLimit");
        SetField(adjust, "fixedValue", 0.73f);
        var lightGo = new GameObject("BuildLight"); lightGo.transform.SetParent(root.transform, false);
        var light = lightGo.AddComponent<Light>(); light.intensity = 0.11f;
        var lightSettings = lightGo.AddComponent(T("NonToonTools.NTAvatarLight"));
        SetField(lightSettings, "target", light); SetField(lightSettings, "intensity", 1.23f);
        var custom = AddQueueFixture(root, shader, "CustomQueue", 2, 3005);
        var cutout = AddQueueFixture(root, shader, "CutoutQueue", 1, 2000);
        var unchanged = AddQueueFixture(root, shader, "UnchangedQueue", 2, 3000);
        // A material which appears ONLY in an animation must also be fixed through NDMF's virtual clips.
        var swap = new Material(shader) { name = "AnimatedQueue", renderQueue = 2000 };
        swap.SetInteger("_RenderingMode", 2);
        if (File.Exists(Dir + "/AnimatedQueue.mat")) AssetDatabase.DeleteAsset(Dir + "/AnimatedQueue.mat");
        AssetDatabase.CreateAsset(swap, Dir + "/AnimatedQueue.mat");
        var swapClip = new AnimationClip { name = "QueueSwap" };
        AnimationUtility.SetObjectReferenceCurve(swapClip,
            EditorCurveBinding.PPtrCurve("Body", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
            new[] { new ObjectReferenceKeyframe { time = 0, value = mat }, new ObjectReferenceKeyframe { time = 1, value = swap } });
        if (File.Exists(Dir + "/QueueSwap.anim")) AssetDatabase.DeleteAsset(Dir + "/QueueSwap.anim");
        AssetDatabase.CreateAsset(swapClip, Dir + "/QueueSwap.anim");
        fx.AddLayer("QueueSwap");
        var swapLayer = fx.layers.Last(); swapLayer.stateMachine.AddState("Swap").motion = swapClip;
        var fxLayers = fx.layers; fxLayers[fxLayers.Length - 1].defaultWeight = 1; fx.layers = fxLayers;
        AssetDatabase.SaveAssets();
        var sourcePaths = new[] { matPath, mat2Path, Dir + "/AnimatedQueue.mat", Dir + "/QueueSwap.anim", AssetDatabase.GetAssetPath(fx) };
        var sourceBytes = sourcePaths.ToDictionary(p => p, p => File.ReadAllBytes(p));

        // ---------------- C. 处理前基线 ----------------
        L("");
        L("== C. 处理前基线 ==");
        var clipPath = "Assets/NonToonLightAdjuster/NT_Light_LightMinLimit_低.anim";
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        int bindBefore = clip == null ? -1 : AnimationUtility.GetCurveBindings(clip).Count(b => b.propertyName == "material._LightMinLimit");
        Info($"FX 层 {fx.layers.Length} 层（含「NonToon LightMinLimit」={fx.layers.Any(l => l.name == "NonToon LightMinLimit")}）");
        Info($"参数 NT_Light = {fx.parameters.Any(p => p.name == "NT_Light")}｜低剪辑绑定 {bindBefore} 条");
        Info($"环境光/材质：NonToon 材质 _LightMinLimit={mat.GetFloat("_LightMinLimit")}，贴图={(mat.GetTexture("_BaseTexture") != null ? "有" : "无")}");

        var avatarState = SnapshotAvatar(root, fx, desc, descType, mat, mat2, clip);
        L("");
        L("  处理前快照：");
        foreach (var line in avatarState) L("    " + line);

        // ---------------- D. 跑真实 AAO 构建 ----------------
        L("");
        L("== D. 跑 NDMF + AAO 构建（AvatarProcessor.ProcessAvatar）==");
        // 按参数个数挑重载：优先 (GameObject, platform)，否则退回 (GameObject)
        var overloads = procType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "ProcessAvatar").ToArray();
        var process = overloads.FirstOrDefault(m => m.GetParameters().Length == 2)
                   ?? overloads.FirstOrDefault(m => m.GetParameters().Length == 1);
        Info("ProcessAvatar 重载：" + string.Join(" / ", overloads.Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")));
        Check(process != null, "找到可用的 ProcessAvatar 重载");
        if (process == null) return;

        int logMark = Logs.Count;
        object ctx = null;
        var buildRoot = UnityEngine.Object.Instantiate(root);
        try
        {
            ctx = process.GetParameters().Length == 2
                ? process.Invoke(null, new[] { buildRoot, platInst })
                : process.Invoke(null, new object[] { buildRoot });
            Ok("构建调用返回");
        }
        catch (Exception e) { Bad("构建抛异常：" + (e.InnerException ?? e).Message); }
        var newLogs = Logs.Skip(logMark).ToList();
        L($"  构建期间产生 {newLogs.Count} 条 warning/error：");
        foreach (var s in newLogs.Take(20)) L("    " + s);

        var processed = ctx == null ? null : (GameObject)ctx.GetType().GetProperty("AvatarRootObject")?.GetValue(ctx);
        Check(processed != null, "拿到处理后的 avatar（BuildContext.AvatarRootObject）");
        if (processed == null) return;

        Check(newLogs.Any(s => s.StartsWith("Log | [NonToon NDMF] Replay complete:")), "【NDMF】真实构建执行了 Replay pass");
        Check(newLogs.Any(s => s.StartsWith("Log | [NonToon NDMF] Queue pass complete:")), "【NDMF】真实构建执行了 Queue pass");
        Check(!newLogs.Any(s => s.StartsWith("Error |") || s.StartsWith("Exception |")), "【NDMF】构建无 error/exception");
        Check(newLogs.Any(s => s.StartsWith("Warning | [NonToon NDMF LEGACY_FX]")), "【NDMF】旧 FX 残留明确报警");
        CheckBuildMaterials(processed);
        Check(processed.GetComponentsInChildren(adjustType, true).Length == 0 &&
            processed.GetComponentsInChildren(T("NonToonTools.NTAvatarLight"), true).Length == 0, "【NDMF】已消费的 authoring 组件清理完成");
        Check(Mathf.Abs(processed.GetComponentsInChildren<Light>(true).Single().intensity - 1.23f) < 1e-6f,
            "【NDMF】构建灯光 intensity 0.11 → 1.23");
        Check(mat.renderQueue == 2000 && mat2.renderQueue == 3000 && swap.renderQueue == 2000 &&
            Mathf.Abs(mat2.GetFloat("_LightMaxLimit") - 0.12f) < 1e-6f && light.intensity == 0.11f,
            "【NDMF】原始 avatar / 材质内存值未被构建改写");
        Check(sourceBytes.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "【NDMF】源材质 / FX / 剪辑逐字节未改");
        var buildFx = GetFxController(processed.GetComponent(descType), descType);
        var builtSwaps = buildFx.animationClips.SelectMany(c => AnimationUtility.GetObjectReferenceCurveBindings(c)
            .SelectMany(b => AnimationUtility.GetObjectReferenceCurve(c, b))).Select(k => k.value).OfType<Material>()
            .Where(m => m.name.StartsWith("AnimatedQueue")).ToArray();
        Check(builtSwaps.Length > 0 && builtSwaps.All(m => m.renderQueue == 3000 && m != swap),
            "【NDMF】动画替换材质产物 queue=3000，源 queue=2000", "matches=" + builtSwaps.Length);
        // Build a second clone of the same source, then replay on the completed clone.
        // This checks repeat builds AND idempotence without changing any existing assertion.
        var secondRoot = UnityEngine.Object.Instantiate(root);
        secondRoot.name = "NTAAOAvatarRepeat"; // NDMF's temporary asset directory is keyed by avatar name.
        var ctx2 = process.GetParameters().Length == 2 ? process.Invoke(null, new[] { secondRoot, platInst }) : process.Invoke(null, new object[] { secondRoot });
        var second = (GameObject)ctx2.GetType().GetProperty("AvatarRootObject").GetValue(ctx2);
        CheckBuildMaterials(second);
        var beforeReplay = second.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).ToArray();
        var replayValues = beforeReplay.Select(m => EditorJsonUtility.ToJson(m)).ToArray();
        pluginType.GetMethod("Replay", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { ctx2 });
        var afterReplay = second.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).ToArray();
        Check(beforeReplay.SequenceEqual(afterReplay) && replayValues.SequenceEqual(afterReplay.Select(m => EditorJsonUtility.ToJson(m))),
            "【NDMF】重复重放材质引用及序列化字段不变（幂等）");
        Check(sourceBytes.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "【NDMF】第二次构建源资产仍逐字节未改");

        // ---------------- E. 处理后检查 ----------------
        L("");
        L("== E. AAO 构建后的结果 ==");
        var pDesc = processed.GetComponentInChildren(descType, true);
        var pFx = pDesc == null ? null : GetFxController(pDesc, descType);
        Check(pFx != null, "【Q1】构建后的 FX 必须存在，不能跳过后续断言");
        Info("处理后 FX 控制器：" + (pFx == null ? "❌ 没了" : pFx.name));
        if (pFx != null)
        {
            Check(pFx.layers.Any(l => l.name == "NonToon LightMinLimit"),
                "【Q1】我们的动画层在 AAO 构建后仍然存在",
                "层：" + string.Join(", ", pFx.layers.Select(l => l.name)));
            Check(pFx.parameters.Any(p => p.name == "NT_Light"),
                "【Q1】Float 参数 NT_Light 仍然存在",
                pFx.parameters.Length + " 个参数");
            // 参数是否仍被 BlendTree 使用
            var layerUsed = pFx.layers.Where(l => l.name == "NonToon LightMinLimit").ToArray();
            bool hasBlend = layerUsed.Any(l =>
            {
                var st = l.stateMachine?.defaultState;
                var bt = st?.motion as BlendTree;
                return bt != null && bt.blendParameter == "NT_Light";
            });
            Check(hasBlend, "【Q1】BlendTree 仍由 NT_Light 驱动");
        }

        // 剪辑绑定是否仍指向存在的对象
        var pClip = FindClipInController(pFx);
        Check(pClip != null, "【Q1】构建后的材质剪辑必须存在");
        if (pClip != null)
        {
            var binds = AnimationUtility.GetCurveBindings(pClip).Where(b => b.propertyName.StartsWith("material.")).ToArray();
            Check(binds.Length > 0, "【Q1】材质属性曲线仍在（" + pClip.name + "）",
                binds.Length > 0 ? binds[0].propertyName + " @ " + binds[0].path : "无");
            int resolvable = binds.Count(b => processed.transform.Find(b.path) != null || b.path == "");
            Check(binds.Length == 0 || resolvable > 0,
                "【Q1】至少有一条绑定能解析到处理后 avatar 里的对象",
                resolvable + "/" + binds.Length + " 条可解析（路径会被 AAO 按合并结果改写）");
            foreach (var b in binds.Take(6)) Info("    绑定：" + b.propertyName + " → " + (b.path == "" ? "(根)" : b.path));
        }
        else Info("没在处理后的控制器里找到材质曲线剪辑（可能被 AAO 转成了 BlendTree 子资产）");

        // Expression 参数
        var pPs = GetField(pDesc, "expressionParameters");
        Check(pPs != null, "【Q1】构建后的 ExpressionParameters 必须存在");
        if (pPs != null)
        {
            var arr = pPs.GetType().GetField("parameters")?.GetValue(pPs) as Array;
            bool has = arr != null && arr.Cast<object>().Any(e => e != null && (string)GetField(e, "name") == "NT_Light");
            Check(has, "【Q1】Expression Parameter NT_Light 仍然存在");
        }

        // Q2：AAO 有没有动 NonToon 的材质/贴图
        var pMat = body.GetComponent<MeshRenderer>()?.sharedMaterial;
        var pMatMerged = processed.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m != null && m.shader != null && m.shader.name.StartsWith("nontoon-fork"));
        var matNow = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        Check(matNow != null, "【Q2】NonToon 材质资产没被删掉");
        if (matNow != null)
        {
            Check(matNow.GetTexture("_BaseTexture") != null,
                "【Q2】材质的 _BaseTexture 引用还在（AAO 没误删 NonToon 用的贴图）");
            Info("      _LightMinLimit 现在 = " + matNow.GetFloat("_LightMinLimit"));
        }
        Check(File.Exists(Dir + "/NTAaoProbeTex.png"), "【Q2】贴图资产文件还在");
        if (pMatMerged != null) Info("     处理后 renderer 上的 NonToon 材质：" + pMatMerged.name);

        // 处理后快照
        L("");
        L("  处理后快照：");
        foreach (var line in SnapshotAvatar(processed, pFx, pDesc, descType, matNow, matNow, pClip)) L("    " + line);

        SceneView.RepaintAll();
    }

    // ---------------------------------------------------------------- 工具
    static Material AddQueueFixture(GameObject root, Shader shader, string name, int mode, int queue)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
        go.transform.SetParent(root.transform, false);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        var material = new Material(shader) { name = name, renderQueue = queue };
        material.SetInteger("_RenderingMode", mode);
        go.GetComponent<Renderer>().sharedMaterial = material;
        return material;
    }

    static void CheckBuildMaterials(GameObject root)
    {
        var materials = root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
        foreach (var pair in new[] { ("NTAaoMat", 3000), ("NTAaoMat2", 2000), ("CustomQueue", 3005), ("CutoutQueue", 2450), ("UnchangedQueue", 3000) })
        {
            var matches = materials.Where(m => m.name == pair.Item1 || m.name.StartsWith(pair.Item1 + " (")).ToArray();
            Check(matches.Length > 0 && matches.All(m => m.renderQueue == pair.Item2), "【NDMF】产物 " + pair.Item1 + " queue=" + pair.Item2,
                string.Join(",", matches.Select(m => m.renderQueue.ToString())));
            if (pair.Item1 == "NTAaoMat2")
                Check(matches.Length > 0 && matches.All(m => new SerializedObject(m).FindProperty("m_CustomRenderQueue").intValue == -1 && Mathf.Abs(m.GetFloat("_LightMaxLimit") - 0.73f) < 1e-6f),
                    "【NDMF】Opaque rawQueue=-1，固定亮度 0.12 → 0.73");
        }
    }

    static bool AssignFxLayer(Component desc, Type descType, AnimatorController ctrl)
    {
        try
        {
            // ⚠️ 用脚本 AddComponent 出来的 VRCAvatarDescriptor，baseAnimationLayers 是 **null**
            // （SDK 面板勾 Customize 时才会建 5 条）。必须自己建，否则 FX 层根本没参与构建，
            // 后面"层还在不在"的断言就毫无意义（实测踩过）。
            var f = descType.GetField("baseAnimationLayers");
            if (f == null) { Info("找不到 baseAnimationLayers 字段"); return false; }
            var elemType = f.FieldType.GetElementType();
            var typeField = elemType.GetField("type");
            var typeEnum = typeField.FieldType;
            var ctrlField = elemType.GetField("animatorController");
            var arr = Array.CreateInstance(elemType, 5);
            var names = new[] { "Base", "Additive", "Gesture", "Action", "FX" };
            for (int i = 0; i < 5; i++)
            {
                var e = Activator.CreateInstance(elemType);
                typeField.SetValue(e, Enum.Parse(typeEnum, names[i], true));
                if (names[i] == "FX") ctrlField.SetValue(e, ctrl);
                else
                {
                    // ⚠️ 其余层的 controller 不能留 null：AAO 的「Optimization Metrics」统计 pass 会
                    // 在 Enumerable.Concat(null, …) 上抛 ArgumentNullException（实测，见报告）。
                    // 真实 avatar 上这些层一般都有控制器，这里也照做，让测试环境更接近真实。
                    var p = Dir + "/empty_" + names[i] + ".controller";
                    if (File.Exists(p)) AssetDatabase.DeleteAsset(p);
                    var empty = AnimatorController.CreateAnimatorControllerAtPath(p);
                    ctrlField.SetValue(e, empty);
                }
                arr.SetValue(e, i);
            }
            f.SetValue(desc, arr);
            var cf = descType.GetField("customizeAnimationLayers");
            if (cf != null) cf.SetValue(desc, true);

            // ⚠️ 还必须初始化 specialAnimationLayers（Sitting/TPose/IKPose）。
            // AAO 的 VRCSDKUtils.GetAvatarLayerControllers 里有一句
            //     descriptor.specialAnimationLayers.Concat(descriptor.baseAnimationLayers)
            // 这个字段为 null 时会在 Concat 上抛 ArgumentNullException（实测踩过；
            // 真实 avatar 由 SDK 序列化成 3 条，所以用户一般不会遇到）。
            var sf = descType.GetField("specialAnimationLayers");
            if (sf != null)
            {
                var sElem = sf.FieldType.GetElementType();
                var sTypeField = sElem.GetField("type");
                var sCtrlField = sElem.GetField("animatorController");
                var allNames = Enum.GetNames(sTypeField.FieldType);
                Info("specialAnimationLayers 的枚举值：" + string.Join(", ", allNames));
                // ⚠️ 不能把 Enum.GetValues 全填进去：里面有 Deprecated0 之类的非法值，
                // AAO 的 AnimatorLayerMap 会抛 ArgumentOutOfRangeException 并**把 descriptor 改坏**
                // （实测：填了非法值后处理后 FX 层指向了我造的 empty_special5）。
                var valid = new[] { "Sitting", "TPose", "IKPose" }.Where(n => allNames.Contains(n)).ToArray();
                if (valid.Length == 0)
                    valid = allNames.Where(n => !n.StartsWith("Deprecated")
                        && !new[] { "Base", "Additive", "Gesture", "Action", "FX" }.Contains(n)).Take(3).ToArray();
                var vals = valid.Select(n => Enum.Parse(sTypeField.FieldType, n)).ToArray();
                var sArr = Array.CreateInstance(sElem, vals.Length);
                for (int i = 0; i < vals.Length; i++)
                {
                    var e = Activator.CreateInstance(sElem);
                    sTypeField.SetValue(e, vals[i]);
                    var p = Dir + "/empty_special" + i + ".controller";
                    if (File.Exists(p)) AssetDatabase.DeleteAsset(p);
                    sCtrlField?.SetValue(e, AnimatorController.CreateAnimatorControllerAtPath(p));
                    sArr.SetValue(e, i);
                }
                sf.SetValue(desc, sArr);
                Info("已自建 specialAnimationLayers（" + vals.Length + " 条：" + string.Join(", ", valid) + "）");
            }

            Info("已自建 baseAnimationLayers（5 条，FX=" + (ctrl != null ? ctrl.name : "null") + "）");
            return GetFxController(desc, descType) != null;
        }
        catch (Exception e) { Info("写 FX 层失败：" + e.Message); return false; }
    }

    static AnimatorController GetFxController(Component desc, Type descType)
    {
        var arr = descType.GetField("baseAnimationLayers")?.GetValue(desc) as Array;
        if (arr == null) return null;
        foreach (var item in arr)
        {
            var t = GetField(item, "type");
            if (t == null || !t.ToString().Equals("FX", StringComparison.OrdinalIgnoreCase)) continue;
            var c = GetField(item, "animatorController") as AnimatorController;
            if (c == null)
            {
                var o = GetField(item, "animatorController") as AnimatorOverrideController;
                if (o != null) c = o.runtimeAnimatorController as AnimatorController;
            }
            if (c != null) return c;
        }
        return null;
    }

    static AnimationClip FindClipInController(AnimatorController ctrl)
    {
        if (ctrl == null) return null;
        foreach (var l in ctrl.layers)
        {
            var st = l.stateMachine;
            if (st == null) continue;
            foreach (var c in st.states)
                if (c.state.motion is AnimationClip ac) return ac;
            foreach (var c in st.states)
                if (c.state.motion is BlendTree bt)
                    foreach (var ch in bt.children)
                        if (ch.motion is AnimationClip ac2) return ac2;
        }
        return null;
    }

    static List<string> SnapshotAvatar(GameObject root, AnimatorController fx, Component desc, Type descType, Material m1, Material m2, AnimationClip clip)
    {
        var lines = new List<string>();
        if (root == null) { lines.Add("avatar = null"); return lines; }
        lines.Add("root = " + root.name + "，子物体 " + root.GetComponentsInChildren<Transform>(true).Length + " 个");
        lines.Add("renderers = " + root.GetComponentsInChildren<Renderer>(true).Length
            + "： " + string.Join(", ", root.GetComponentsInChildren<Renderer>(true).Select(r => r.name + "(" + r.GetType().Name + ")")));
        lines.Add("FX 层 = " + (fx == null ? "无" : string.Join(" / ", fx.layers.Select(l => l.name))));
        var ps = desc == null ? null : GetField(desc, "expressionParameters");
        int n = 0;
        if (ps != null) { var arr = ps.GetType().GetField("parameters")?.GetValue(ps) as Array; n = arr?.Length ?? 0; }
        lines.Add("Expression Parameters 条数 = " + n);
        lines.Add("材质 = " + (m1 == null ? "null" : m1.name + " tex=" + (m1.GetTexture("_BaseTexture") != null)) + " / " + (m2 == null ? "null" : m2.name));
        lines.Add("剪辑 = " + (clip == null ? "null" : clip.name + " 绑定 " + AnimationUtility.GetCurveBindings(clip).Length + " 条"));
        return lines;
    }
}
