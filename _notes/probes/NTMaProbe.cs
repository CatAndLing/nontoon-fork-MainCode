// [NT-VERIFY 0.5.0] ⑤「亮度调整」的 **Modular Avatar 非破坏路径** 验证装置。
//
// 需要 _verify-proj/Packages 下真的有 MA + NDMF（见 _notes/项目状态与交接.md §6 的搭建方法）。
// 要钉死四件事：
//   A. 环境：MA 组件与 NDMF 构建器都在（不在就 exit 1，不静默跳过）
//   B. 生成时**不碰**用户的资产：descriptor 的 expressionParameters / expressionsMenu 仍为 null，
//      FX 层没有被赋值 —— 一切都交给 MA 在构建期做
//   C. 挂的四个 MA 组件配置正确（MergeAnimator=FX/相对路径/匹配 WD；Parameters=Float；
//      MenuItem=RadialPuppet；MenuInstaller 存在 —— 少了它 MA 根本不装菜单）
//   D. 跑真实 NDMF 构建后：我们的层/参数/径向菜单**全部存活**，且材质属性曲线还指向正确的对象
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class NTMaProbe
{
    const string OUT = "ntma.txt";
    const string Dir = "Assets/NTMaProbe";
    const string Param = "NT_Brightness";
    const string Layer = "NonToon LightMinLimit";

    static readonly StringBuilder Sb = new StringBuilder();
    static readonly List<string> Logs = new List<string>();
    static int Fail, Pass;
    static void L(string s) { Sb.AppendLine(s); Debug.Log("[NT-MA] " + s); }
    static void Ok(string s) { Pass++; L("  ✅ " + s); }
    static void Bad(string s) { Fail++; L("  ❌ " + s); }
    static void Info(string s) { L("  ·  " + s); }
    static void Check(bool c, string what, string detail = "")
    {
        if (c) Ok(what + (detail.Length > 0 ? " → " + detail : ""));
        else Bad(what + (detail.Length > 0 ? " → " + detail : ""));
    }

    static Type T(string full) { return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(full, false)).FirstOrDefault(t => t != null); }
    static Type TName(string simple)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
            foreach (var t in ts) if (t.Name == simple) return t;
        }
        return null;
    }
    static object GetField(object o, string n)
    {
        return o?.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
    }
    static void SetField(object o, string n, object v)
    {
        var f = o?.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new MissingFieldException((o?.GetType().Name ?? "null") + "." + n);
        f.SetValue(o, v);
    }

    public static void Run()
    {
        try { if (File.Exists(OUT)) File.Delete(OUT); } catch { }
        Application.logMessageReceived += (c, s, t) => { if (t != LogType.Log) Logs.Add(c + ": " + s); };
        L("== 运行信息 ==");
        L("时间  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        L("工程  = " + Directory.GetCurrentDirectory());
        L("");
        try { Body(); }
        catch (Exception e) { Bad("探针异常：" + e); }
        L("");
        L(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + "）====");
        File.WriteAllText(OUT, Sb.ToString());
        Debug.Log("[NTMaProbe] done -> " + OUT + " (fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        // ---------------- A. 环境 ----------------
        L("== A. MA / NDMF 环境 ==");
        var mergeType = TName("ModularAvatarMergeAnimator");
        var paramsType = TName("ModularAvatarParameters");
        var itemType = TName("ModularAvatarMenuItem");
        var installerType = TName("ModularAvatarMenuInstaller");
        var procType = T("nadena.dev.ndmf.AvatarProcessor");

        Type platType = null; object platInst = null;
        foreach (var an in new[] { "nadena.dev.ndmf.vrchat", "nadena.dev.ndmf" })
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == an) ?? Assembly.Load(an);
                var t = asm?.GetType("nadena.dev.ndmf.vrchat.VRChatPlatform", false);
                if (t != null) { platType = t; break; }
            }
            catch { }
        }
        if (platType != null)
            platInst = platType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? platType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (platInst == null)
        {
            var apType = T("nadena.dev.ndmf.AmbientPlatform");
            platInst = apType?.GetProperty("CurrentPlatform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }

        string maVersion = null;
        if (mergeType != null)
        {
            try
            {
                var pi = UnityEditor.PackageManager.PackageInfo.FindForAssembly(mergeType.Assembly);
                maVersion = pi?.version;
            }
            catch { }
        }
        Info("MA 版本 = " + (maVersion ?? "未知") + "；已加载的 ndmf 程序集：" + string.Join(", ",
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n.Contains("ndmf")).ToArray()));
        Check(mergeType != null, "找到 MA MergeAnimator", mergeType == null ? "缺 MA（本装置必须在装了 MA 的工程里跑）" : maVersion);
        Check(paramsType != null && itemType != null && installerType != null, "找到 MA Parameters / MenuItem / MenuInstaller");
        Check(procType != null, "找到 NDMF AvatarProcessor");
        Check(platInst != null, "拿到 NDMF 平台实例", platInst?.GetType().FullName);
        if (mergeType == null || paramsType == null || itemType == null || installerType == null || procType == null || platInst == null) return;

        // ---------------- B. 搭最小 avatar ----------------
        L("");
        L("== B. 搭最小 avatar ==");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (AssetDatabase.IsValidFolder(Dir)) AssetDatabase.DeleteAsset(Dir);
        AssetDatabase.CreateFolder("Assets", "NTMaProbe");

        var shader = Shader.Find("NonToon");
        Check(shader != null, "找到 NonToon 着色器");
        if (shader == null) return;

        var matPath = Dir + "/NTMaMat.mat";
        var mat = new Material(shader);
        mat.SetFloat("_LightMinLimit", 0f);
        AssetDatabase.CreateAsset(mat, matPath);
        mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);

        var root = new GameObject("NTMaAvatar");
        var descType = TName("VRCAvatarDescriptor");
        var desc = root.AddComponent(descType);
        root.AddComponent<Animator>();
        var pmType = T("VRC.SDKBase.VRCPipelineManager") ?? TName("VRCPipelineManager");
        if (pmType != null) root.AddComponent(pmType);

        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());
        body.GetComponent<MeshRenderer>().sharedMaterial = mat;

        var maCompType = TName("ModularAvatarMergeAnimator");
        Info("avatar 上的 MA 组件数（生成前）=" + root.GetComponents(maCompType).Length);
        Check(root.GetComponents(maCompType).Length == 0, "生成前 avatar 上没有 MA 组件（干净起点）");

        // ---------------- C. 加 ⑤ 组件并生成 ----------------
        L("");
        L("== C. ⑤「亮度调整」走 MA 非破坏路径 ==");
        var compType = TName("NTLightAdjust");
        Check(compType != null, "找到 NTLightAdjust 组件");
        if (compType == null) return;
        var comp = root.AddComponent(compType);
        SetField(comp, "mode", Enum.Parse(compType.GetNestedType("Mode"), "AdjustableInGame"));
        SetField(comp, "targetRoot", root.transform);
        SetField(comp, "includeChildren", true);
        SetField(comp, "propertyName", "_LightMinLimit");
        SetField(comp, "valueAtZero", 0.05f);
        SetField(comp, "valueAtOne", 0.35f);
        SetField(comp, "initialValue", 1f);
        SetField(comp, "parameterName", Param);
        SetField(comp, "synced", true);
        SetField(comp, "menuLabel", "亮度");
        SetField(comp, "outputFolder", Dir + "/gen");

        var editorType = TName("NTLightAdjustEditor");
        Check(editorType != null, "找到 NTLightAdjustEditor");
        if (editorType == null) return;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var collect = editorType.GetMethod("Collect", Any);
        var generate = editorType.GetMethod("Generate", Any);
        Check(collect != null && generate != null, "找到 Collect / Generate");
        if (collect == null || generate == null) return;

        var args = new object[] { comp, null, null };
        collect.Invoke(null, args);
        var mats = args[1] as List<Material>;
        var rens = args[2] as List<Renderer>;
        Check(mats != null && mats.Count == 1 && rens != null && rens.Count == 1,
            "收集到 1 个材质 / 1 个 Renderer", (mats?.Count ?? -1) + " / " + (rens?.Count ?? -1));

        generate.Invoke(null, new object[] { comp, mats, rens });

        // ---------------- D. 生成后的「非破坏」断言 ----------------
        L("");
        L("== D. 生成后：MA 组件与非破坏性 ==");
        Check((bool)compType.GetField("lastUsedModularAvatar").GetValue(comp), "生成时确实走了 MA 路径");
        var ctrlPath = compType.GetField("lastControllerPath").GetValue(comp) as string;
        Info("生成的控制器：" + ctrlPath);
        Check(!string.IsNullOrEmpty(ctrlPath) && File.Exists(ctrlPath), "生成了独立控制器资产");

        var merges = root.GetComponents(mergeType);
        var prms = root.GetComponents(paramsType);
        var items = root.GetComponents(itemType);
        var insts = root.GetComponents(installerType);
        Check(merges.Length == 1, "挂了 1 个 MA Merge Animator", merges.Length.ToString());
        Check(prms.Length == 1, "挂了 1 个 MA Parameters", prms.Length.ToString());
        Check(items.Length == 1, "挂了 1 个 MA Menu Item", items.Length.ToString());
        Check(insts.Length >= 1, "挂了 MA Menu Installer（MA 只在有 installer 时才装菜单）", insts.Length.ToString());

        if (merges.Length == 1)
        {
            var animator = GetField(merges[0], "animator") as RuntimeAnimatorController;
            Check(animator != null && AssetDatabase.GetAssetPath(animator) == ctrlPath, "Merge Animator 指向我们生成的控制器",
                animator == null ? "null" : animator.name);
            Check(GetField(merges[0], "layerType")?.ToString() == "FX", "layerType = FX", GetField(merges[0], "layerType")?.ToString());
            Check(GetField(merges[0], "pathMode")?.ToString() == "Relative", "pathMode = Relative（剪辑路径相对 avatar 根）",
                GetField(merges[0], "pathMode")?.ToString());
            Check(Equals(GetField(merges[0], "matchAvatarWriteDefaults"), true), "matchAvatarWriteDefaults = true");
        }

        if (prms.Length == 1)
        {
            var list = GetField(prms[0], "parameters") as System.Collections.IList;
            var cfg = list?.Cast<object>().FirstOrDefault(c => (string)GetField(c, "nameOrPrefix") == Param);
            Check(cfg != null, "MA Parameters 里注册了 " + Param);
            if (cfg != null)
            {
                Check(GetField(cfg, "syncType")?.ToString() == "Float", "参数类型 = Float", GetField(cfg, "syncType")?.ToString());
                Check(Equals(GetField(cfg, "localOnly"), false), "localOnly = false（同步给别人看）");
                Info("默认值 = " + GetField(cfg, "defaultValue"));
            }
        }

        if (items.Length == 1)
        {
            var ctrl = GetField(items[0], "Control");
            var typeStr = ctrl == null ? null : GetField(ctrl, "type")?.ToString();
            var pname = ctrl == null ? null : GetField(GetField(ctrl, "parameter"), "name") as string;
            var label = GetField(items[0], "label") as string;
            Check(typeStr == "RadialPuppet", "菜单项类型 = RadialPuppet", typeStr);
            Check(pname == Param, "菜单项绑定 " + Param, pname);
            Info("菜单名 = " + label);
        }

        // 非破坏的核心：descriptor 的两个资产引用与 FX 层都没被我们写过
        Check(GetField(desc, "expressionParameters") == null, "【非破坏】descriptor.expressionParameters 仍为 null（我们没写）");
        Check(GetField(desc, "expressionsMenu") == null, "【非破坏】descriptor.expressionsMenu 仍为 null（我们没写）");
        var ourFx = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
        Info("我们的控制器层：" + string.Join(" / ", ourFx.layers.Select(l => l.name)));
        Check(ourFx.layers.Any(l => l.name == Layer)
              && ourFx.layers.All(l => l.name == Layer || l.name == "Base Layer"),
            "我们的控制器里只有我们那一层 + Unity 默认的 Base Layer（没往别人的控制器里塞东西）");

        // ---------------- E. 跑真实 NDMF 构建 ----------------
        L("");
        L("== E. 跑 NDMF 构建（AvatarProcessor.ProcessAvatar）==");
        var overloads = procType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "ProcessAvatar").ToArray();
        var process = overloads.FirstOrDefault(m => m.GetParameters().Length == 2) ?? overloads.FirstOrDefault(m => m.GetParameters().Length == 1);
        Check(process != null, "找到可用的 ProcessAvatar 重载",
            string.Join(" / ", overloads.Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")));
        if (process == null) return;

        var mark = Logs.Count;
        object ctx = null;
        try
        {
            ctx = process.GetParameters().Length == 2
                ? process.Invoke(null, new[] { root, platInst })
                : process.Invoke(null, new object[] { root });
            Ok("构建调用返回");
        }
        catch (Exception e) { Bad("构建抛异常：" + (e.InnerException ?? e).Message); }
        var newLogs = Logs.Skip(mark).ToList();
        L("  构建期间 " + newLogs.Count + " 条 warning/error：");
        foreach (var s in newLogs.Take(15)) L("    " + s);

        var processed = ctx == null ? null : (GameObject)ctx.GetType().GetProperty("AvatarRootObject")?.GetValue(ctx);
        Check(processed != null, "拿到处理后的 avatar（BuildContext.AvatarRootObject）");
        if (processed == null) return;

        // ---------------- F. 构建后：层 / 参数 / 菜单是否存活 ----------------
        L("");
        L("== F. 构建后：层 / 参数 / 菜单 / 曲线 ==");
        var pDesc = processed.GetComponent(descType);
        Check(pDesc != null, "处理后的 avatar 仍有 VRCAvatarDescriptor");
        var pFx = pDesc == null ? null : GetFx(descType, pDesc);
        Info("构建后的 FX 控制器：" + (pFx == null ? "null" : pFx.name + "（层：" + string.Join(" / ", pFx.layers.Select(l => l.name)) + "）"));
        Check(pFx != null, "MA 构建后 avatar 有了 FX 控制器");
        if (pFx != null)
        {
            Check(pFx.layers.Any(l => l.name == Layer), "【核心】我们的动画层在构建后仍然存在",
                string.Join(" / ", pFx.layers.Select(l => l.name)));
            Check(pFx.parameters.Any(p => p.name == Param), "【核心】Float 参数 " + Param + " 仍然存在",
                pFx.parameters.Length + " 个参数");
            var layer = pFx.layers.FirstOrDefault(l => l.name == Layer);
            if (layer != null)
            {
                // 诊断：把这一层的状态与 motion 都打出来（MA 合并后 BlendTree 可能被重建，
                // 只盯 defaultState 会看不到它）
                var found = new List<BlendTree>();
                void Walk(AnimatorStateMachine sm)
                {
                    if (sm == null) return;
                    foreach (var st in sm.states)
                    {
                        if (st.state == null) continue;
                        if (st.state.motion is BlendTree b) found.Add(b);
                    }
                    foreach (var sub in sm.stateMachines) Walk(sub.stateMachine);
                }
                Walk(layer.stateMachine);
                Info("层「" + Layer + "」里的状态：" + string.Join(" / ",
                    layer.stateMachine.states.Select(s => s.state.name + ":" + (s.state.motion == null ? "null" : s.state.motion.GetType().Name)).ToArray()));
                var bt = found.FirstOrDefault(b => b.blendParameter == Param) ?? found.FirstOrDefault();
                Check(bt != null && bt.blendParameter == Param, "BlendTree 仍由 " + Param + " 驱动",
                    bt == null ? "层里没有 BlendTree" : "blendParameter=" + bt.blendParameter);
                if (bt != null)
                {
                    var kids = bt.children;
                    Check(kids != null && kids.Length == 2, "BlendTree 有两条子剪辑", (kids?.Length ?? 0).ToString());
                    var clips = (kids ?? new ChildMotion[0]).Select(k => k.motion).OfType<AnimationClip>().ToArray();
                    var binds = clips.SelectMany(AnimationUtility.GetCurveBindings)
                        .Where(b => b.propertyName.StartsWith("material.")).ToArray();
                    Check(binds.Length > 0, "材质属性曲线还在", binds.Length + " 条");
                    var resolvable = binds.Count(b => b.path == "" || processed.transform.Find(b.path) != null);
                    Check(binds.Length > 0 && resolvable == binds.Length,
                        "曲线绑定的路径都能解析到构建后的 avatar", resolvable + "/" + binds.Length);
                    foreach (var b in binds.Take(4)) Info("  绑定：" + b.propertyName + " → " + (b.path == "" ? "(根)" : b.path));
                }
            }
        }

        var ps = pDesc == null ? null : GetField(pDesc, "expressionParameters");
        Check(ps != null, "构建后 descriptor 有了 ExpressionParameters");
        if (ps != null)
        {
            var pf = ps.GetType().GetField("parameters");
            var arr = pf?.GetValue(ps) as Array;
            var found = false; string vt = null; object saved = null;
            foreach (var e in arr ?? Array.Empty<object>())
            {
                if (e != null && (string)GetField(e, "name") == Param)
                {
                    found = true; vt = GetField(e, "valueType")?.ToString(); saved = GetField(e, "saved");
                }
            }
            Check(found, "【核心】ExpressionParameters 里有 " + Param);
            Check(vt == "Float", "参数类型 = Float", vt);
            Info("同步（saved）= " + saved);
        }

        var menu = pDesc == null ? null : GetField(pDesc, "expressionsMenu");
        Check(menu != null, "构建后 descriptor 有了 ExpressionsMenu");
        if (menu != null)
        {
            var list = menu.GetType().GetField("controls")?.GetValue(menu) as System.Collections.IList;
            var hit = list?.Cast<object>().FirstOrDefault(c =>
            {
                var p = GetField(c, "parameter");
                return p != null && (string)GetField(p, "name") == Param;
            });
            Check(hit != null, "【核心】菜单里有指向 " + Param + " 的控件");
            if (hit != null)
                Check(GetField(hit, "type")?.ToString() == "RadialPuppet", "菜单控件类型 = RadialPuppet",
                    GetField(hit, "type")?.ToString());
        }

        Info("（处理后场景对象数 = " + processed.GetComponentsInChildren<Transform>(true).Length + "）");
    }

    static AnimatorController GetFx(Type descType, Component desc)
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
}
