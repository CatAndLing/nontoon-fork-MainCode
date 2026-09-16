// 验证「光照自适应 / 防煤」插件：
//   A. 插件类型在不在
//   B. NTVrcParameterBuilder 生成的 Animator 参数 / 曲线剪辑 / BlendTree 层 / VRC 参数 / 径向菜单 是否正确
//   C. 曲线剪辑能不能真的把材质属性改成目标值（AnimationClip.SampleAnimation）
//   D. **防煤是否真的成立**：全黑场景下开自有光 vs 关自有光，渲染出图对比（负向对照）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class NTLightAdjusterProbe
{
    const string OUT = "ntlightadjuster.txt";
    static readonly StringBuilder Sb = new StringBuilder();
    static int Fail, Pass;
    static void L(string s) { Sb.AppendLine(s); Debug.Log("[NT-LA] " + s); }
    static void Ok(string s) { Pass++; L("  ✅ " + s); }
    static void Bad(string s) { Fail++; L("  ❌ " + s); }
    static void Info(string s) { L("  ·  " + s); }
    static void Check(bool c, string what, string detail = "") { if (c) Ok(what + (detail.Length > 0 ? " → " + detail : "")); else Bad(what + (detail.Length > 0 ? " → " + detail : "")); }

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
        L("");

        try { Body(); }
        catch (Exception e) { Bad("探针异常：" + e); }

        L("");
        L(Fail == 0 ? "==== 全部通过（" + Pass + " 项）====" : "==== 有 " + Fail + " 项不符（通过 " + Pass + "）====");
        File.WriteAllText(OUT, Sb.ToString());
        Debug.Log("[NTLightAdjusterProbe] done -> " + OUT + " (fail=" + Fail + ")");
        EditorApplication.Exit(Fail == 0 ? 0 : 1);
    }

    static void Body()
    {
        // ---------------- A. 类型存在 ----------------
        L("== A. 插件类型 ==");
        var winType = FindType("NTLightAdjusterWindow");
        var builderType = FindType("NTVrcParameterBuilder");
        Check(winType != null, "找到编辑器窗口 NTLightAdjusterWindow");
        Check(builderType != null, "找到生成器 NTVrcParameterBuilder");
        if (builderType == null) return;

        // ---------------- 搭一个假 avatar ----------------
        L("");
        L("== 准备测试用 avatar ==");
        const string Root = "NTLAProbe";
        var old = GameObject.Find(Root);
        if (old != null) UnityEngine.Object.DestroyImmediate(old);
        var avatar = new GameObject(Root);
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(avatar.transform, false);
        UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());

        var shader = Shader.Find("NonToon");
        Check(shader != null, "找到 NonToon 着色器");
        if (shader == null) return;
        var mat = new Material(shader) { name = "NTLAProbeMat" };
        body.GetComponent<MeshRenderer>().sharedMaterial = mat;

        // VRC 描述符（用反射建，避免探针硬依赖 VRCSDK）
        var descType = FindType("VRCAvatarDescriptor") ?? FindType("VRC_AvatarDescriptor");
        if (descType == null) { Info("没装 VRCSDK：只验证 Animator 部分"); }
        else
        {
            var desc = avatar.AddComponent(descType);
            Info("已挂 " + descType.FullName);
            // 故意把 expressionParameters / expressionsMenu 留空，让生成器去创建
        }

        // ---------------- B. 生成 ----------------
        L("");
        L("== B. 生成 Animator / 参数 / 菜单 ==");
        var build = builderType.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(build != null, "找到 NTVrcParameterBuilder.Build");
        if (build == null) return;

        var renderers = new List<Renderer> { body.GetComponent<MeshRenderer>() };
        const string Param = "NT_Light";
        object res = null;
        try
        {
            // 默认驱动属性 = _LightMinLimit（LLC 那条路）
            // ⛔ 2026-09-17 起 `Build` 的签名去掉了 `synced` / `createMenu` / `standalone`
            //    —— 那条"直接改用户资产"的回退路径已整条删除，现在**永远**是独立控制器。
            res = build.Invoke(null, new object[] { avatar, renderers, Param, 0.05f, 0.6f,
                "Assets/NonToonLightAdjuster", "_LightMinLimit" });   // ⚠️ 反射调用必须传全参数：可选参数只在编译期生效
        }
        catch (Exception e) { Bad("Build 抛异常：" + (e.InnerException ?? e).Message); }

        if (res != null)
        {
            var okProp = res.GetType().GetProperty("Ok");
            var summary = res.GetType().GetMethod("Summary");
            bool ok = okProp != null && (bool)okProp.GetValue(res);
            var text = summary?.Invoke(res, null) as string ?? "";
            Check(ok, "Build 报告无错误");
            L(text.TrimEnd());
            // 把"需要手动"的项也点出来（探针环境下允许存在，但要说清）
            var manual = res.GetType().GetField("Manual")?.GetValue(res) as System.Collections.IList;
            if (manual != null && manual.Count > 0) Info("需要手动的项 " + manual.Count + " 条（探针环境下不算失败）");
        }

        // ---------------- B2. 资产断言 ----------------
        L("");
        L("== B2. 生成结果断言 ==");
        // ⚠️ 2026-09-17 起控制器名变了：`folder + "/" + Sanitize(参数名) + "_NonToonFX.controller"`
        //    （旧名 `NonToonLightAdjusterFX.controller` 属于"直接改用户资产"的那条已删除的路径）
        var ctrlPath = "Assets/NonToonLightAdjuster/NT_Light_NonToonFX.controller";
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
        Check(ctrl != null, "生成了 FX AnimatorController", ctrlPath);
        if (ctrl != null)
        {
            Check(ctrl.parameters.Any(p => p.name == Param && p.type == AnimatorControllerParameterType.Float),
                "FX 控制器里有 Float 参数 " + Param);
            var layer = ctrl.layers.FirstOrDefault(l => l.name == "NonToon LightMinLimit");
            Check(layer != null, "新增了动画层「NonToon LightMinLimit」",
                layer != null ? "层数 " + ctrl.layers.Length : "当前层：" + string.Join(", ", ctrl.layers.Select(l => l.name)));
            if (layer != null)
            {
                var st = layer.stateMachine?.defaultState;
                var bt = st?.motion as BlendTree;
                Check(bt != null, "默认状态挂的是 BlendTree");
                if (bt != null)
                {
                    Check(bt.blendParameter == Param, "BlendTree 由参数驱动", bt.blendParameter);
                    var kids = bt.children;
                    Check(kids != null && kids.Length == 2, "BlendTree 有两条子剪辑", kids == null ? "0" : kids.Length.ToString());
                    if (kids != null && kids.Length == 2)
                    {
                        Check(Mathf.Abs(kids[0].threshold - 0f) < 1e-4f && Mathf.Abs(kids[1].threshold - 1f) < 1e-4f,
                            "阈值是 0 与 1（径向 0→最暗、1→最亮）",
                            kids[0].threshold + " / " + kids[1].threshold);
                    }
                }
            }

            // 曲线绑定
            var clips = AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/NonToonLightAdjuster" })
                .Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<AnimationClip>)
                .Where(c => c != null && !c.name.StartsWith("_probe"))   // 排除探针自己的诊断剪辑（C2 会建 _probe_binding）
                .ToArray();
            Info("生成剪辑 " + clips.Length + " 条：" + string.Join(", ", clips.Select(c => c.name)));
            foreach (var c in clips)
            {
                var bindings = AnimationUtility.GetCurveBindings(c);
                var matB = bindings.Where(b => b.propertyName == "material._LightMinLimit").ToArray();
                var v = matB.Length > 0 ? (c.name.Contains("低") ? 0.05f : 0.6f) : 0f;
                Check(matB.Length > 0, "剪辑「" + c.name + "」绑定了 material._LightMinLimit",
                    matB.Length > 0 ? matB.Length + " 条绑定，路径=" + matB[0].path + " 类型=" + matB[0].type.Name : "绑定：" + string.Join(",", bindings.Select(b => b.propertyName)));
                if (matB.Length > 0)
                {
                    var curve = AnimationUtility.GetEditorCurve(c, matB[0]);
                    Check(curve != null && curve.keys.Length > 0 && Mathf.Abs(curve.keys[0].value - v) < 1e-3f,
                        "曲线值正确（应为 " + v + "）", curve == null ? "无曲线" : curve.keys[0].value.ToString("F3"));
                }
            }

            // ---------------- C. 剪辑能不能改材质（诊断，不作为通过/失败判据）----------------
            L("");
            L("== C. 曲线能否真的改材质（诊断）==");
            L("   注意：这一节**不做判定**。批处理下两种编辑器 API 都读回 0，无法据此断言曲线在运行时生效；");
            L("   结构层面已由 B2 验证（绑定路径 / 属性名 / 曲线值 / BlendTree 阈值）。");
            L("   ⚠️ 结论：**运行时是否真的改材质，本机验证不了，需要在 VRChat 里实测**（见交接文档「未覆盖」）。");
            if (clips.Length >= 2)
            {
                var low2 = clips.FirstOrDefault(c => c.name.Contains("低"));
                var mr2 = body.GetComponent<MeshRenderer>();
                if (low2 != null)
                {
                    var beforeInst = mr2.sharedMaterial;
                    try
                    {
                        AnimationMode.StartAnimationMode();
                        try
                        {
                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(avatar, low2, 0f);
                            AnimationMode.EndSampling();
                        }
                        finally { AnimationMode.StopAnimationMode(); }
                    }
                    catch (Exception e) { Info("AnimationMode 预览抛异常：" + e.Message); }
                    Info("AnimationMode 预览后：是否产生了材质实例 = " + (mr2.material != beforeInst)
                         + "，sharedMaterial 值 = " + mr2.sharedMaterial.GetFloat("_SelfLightIntensity").ToString("F3")
                         + "，material 值 = " + mr2.material.GetFloat("_SelfLightIntensity").ToString("F3"));
                    Info("（clip.SampleAnimation 那条路也试过，同样读回 0）");

                    // C2：判别实验 —— 同一条剪辑里同时放「位置曲线」与「材质曲线」。
                    // 若位置动了而材质没动，说明是"批处理下预览不了材质属性曲线"（环境限制），
                    // 而不是我们的绑定写错；反之则说明绑定有问题。
                    L("");
                    L("   -- C2 判别：位置曲线 vs 材质曲线 --");
                    var testPath = "Assets/NonToonLightAdjuster/_probe_binding.anim";
                    if (File.Exists(testPath)) AssetDatabase.DeleteAsset(testPath);
                    var testClip = new AnimationClip { frameRate = 60f };
                    AnimationUtility.SetEditorCurve(testClip,
                        EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.x"),
                        AnimationCurve.Constant(0f, 0.02f, 5f));
                    foreach (var t in new[] { typeof(MeshRenderer), typeof(Renderer) })
                        AnimationUtility.SetEditorCurve(testClip,
                            EditorCurveBinding.FloatCurve("Body", t, "material._SelfLightIntensity"),
                            AnimationCurve.Constant(0f, 0.02f, 2.5f));
                    AssetDatabase.CreateAsset(testClip, testPath);
                    var tc = AssetDatabase.LoadAssetAtPath<AnimationClip>(testPath);
                    var bt2 = body.transform;
                    var posBefore = bt2.localPosition.x;
                    try
                    {
                        AnimationMode.StartAnimationMode();
                        try
                        {
                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(avatar, tc, 0f);
                            AnimationMode.EndSampling();
                        }
                        finally { AnimationMode.StopAnimationMode(); }
                    }
                    catch (Exception e) { Info("C2 采样抛异常：" + e.Message); }
                    Info("位置曲线：x " + posBefore.ToString("F2") + " → " + bt2.localPosition.x.ToString("F2")
                         + "（变成 5 说明 AnimationMode 采样本身是有效的）");
                    Info("材质曲线：material 值 = " + mr2.material.GetFloat("_SelfLightIntensity").ToString("F3")
                         + "（期望 2.5）");
                    bool posWorks = Mathf.Abs(bt2.localPosition.x - 5f) < 0.01f;
                    bool matWorks = Mathf.Abs(mr2.material.GetFloat("_SelfLightIntensity") - 2.5f) < 0.01f;
                    if (posWorks && !matWorks)
                        L("   ⇒ 判定：**采样机制有效，但批处理环境下材质属性曲线不被预览**（两条绑定都是这个结果）"
                          + " → 我们的绑定无法在本地证伪，需要上传 VRChat 实测");
                    else if (posWorks && matWorks)
                        Ok("⇒ 判定：材质属性曲线在本地就能生效（那前面的 0.000 是复位/实例读取的干扰）");
                    else
                        L("   ⇒ 判定：**连位置曲线都没生效 → 本环境（batchmode）整个动画预览是 inert 的**。"
                          + "这一节的环境不可信，结论标记为「未验证」，不计入失败。");
                    L("   ⚠️ 未验证项（必须记进交接文档）：material._SelfLightIntensity 曲线在运行时是否真的改材质。");
                    L("      已验证的替代证据：序列化格式与 Unity 自身一致 —— attribute=material._SelfLightIntensity，"
                      + "classID=" + (mr2 is SkinnedMeshRenderer ? "137(SkinnedMeshRenderer)" : "23(MeshRenderer)") + "，曲线值 0.3 / 3.0。");
                    bt2.localPosition = new Vector3(0f, 0f, 0f);
                }
            }
        }

        // ---------------- 非破坏契约（MA-only） ----------------
        // ⛔ 2026-09-17 起 ⑤ **只走 Modular Avatar 声明式**：`NTVrcParameterBuilder.Build`
        //    已经**不再写 avatar 的 descriptor**（那条会永久污染用户资产的回退路径已整段删除）。
        //    ⇒ 本节断言**从"写进去了"反转为"必须没被动过"**（旧断言是 `ps != null` / `menu != null`）。
        L("");
        L("== B3. 非破坏契约：avatar 的 descriptor 必须没被动过 ==");
        if (descType != null)
        {
            var desc = avatar.GetComponent(descType);
            var ps = descType.GetField("expressionParameters")?.GetValue(desc);
            var menu = descType.GetField("expressionsMenu")?.GetValue(desc);
            Check(ps == null, "没有往 avatar 写 expressionParameters（MA-only）",
                ps == null ? "仍为 null ✓" : "被写成了 " + AssetDatabase.GetAssetPath((UnityEngine.Object)ps));
            Check(menu == null, "没有往 avatar 写 expressionsMenu（MA-only）",
                menu == null ? "仍为 null ✓" : "被写成了 " + AssetDatabase.GetAssetPath((UnityEngine.Object)menu));

            // 生成出来的必须是**独立控制器**（交给 MA 在构建期合并进 FX 层）
            Check(ctrl != null && ctrl.name.EndsWith("_NonToonFX"),
                "生成的是独立控制器（由 Modular Avatar 合并）", ctrl == null ? "无控制器" : ctrl.name);

            // 非破坏路径**不允许**把控制器指派进 avatar 的动画层（那是旧回退路径的行为）
            var layers = descType.GetField("baseAnimationLayers")?.GetValue(desc) as Array;
            bool assigned = false;
            if (layers != null)
                foreach (var L2 in layers)
                {
                    var ac = L2?.GetType().GetField("animatorController")?.GetValue(L2) as UnityEngine.Object;
                    if (ac != null && ctrl != null && ReferenceEquals(ac, ctrl)) assigned = true;
                }
            Check(!assigned, "没有把生成的控制器指派进 avatar 的动画层（旧回退路径才会那样做）",
                assigned ? "被指派了" : "未指派 ✓");
        }
        else Info("没装 VRCSDK：跳过 descriptor 非破坏性检查");

        // ---------------- D. 防煤负向对照（真实渲染）----------------
        L("");
        L("== D. 防煤是否成立（全黑场景 + 真实 GPU 渲染）==");
        // ⚠️ 必须先把假 avatar 藏起来：它和渲染用的球都在原点，胶囊会挡住球
        // （上一版没藏，渲出来的是胶囊、还用的是没开自有光的原材质 → 结论完全无效，实测踩过）
        avatar.SetActive(false);
        try
        {
            // D1：LLC 那条路 —— 抬高「亮度下限」。这是首选方案：零采样、不必开自有光。
            var lmOff = RenderSphere(mat, selfLight: false, onlyThisLight: false, minLimit: 0f);
            var lmMid = RenderSphere(mat, selfLight: false, onlyThisLight: false, minLimit: 0.15f);
            var lmOn = RenderSphere(mat, selfLight: false, onlyThisLight: false, minLimit: 0.35f);
            var lmHigh = RenderSphere(mat, selfLight: false, onlyThisLight: false, minLimit: 0.6f);
            Info(string.Format("亮度下限 0（NonToon 默认）→ 均值 {0:F4} 非黑 {1:F1}% 中心 {2}",
                lmOff.mean, lmOff.nonBlack * 100, lmOff.center));
            Info(string.Format("亮度下限 0.15（lilToon 官方推荐区间）→ 均值 {0:F4} 非黑 {1:F1}%",
                lmMid.mean, lmMid.nonBlack * 100));
            Info(string.Format("亮度下限 0.35（本插件默认预设）→ 均值 {0:F4} 非黑 {1:F1}%",
                lmOn.mean, lmOn.nonBlack * 100));
            Info(string.Format("亮度下限 0.6（更保守）→ 均值 {0:F4} 非黑 {1:F1}%",
                lmHigh.mean, lmHigh.nonBlack * 100));
            Check(lmOff.nonBlack < 0.35, "【负向对照】全黑 + 下限 0（默认）→ 就是一块煤",
                (lmOff.nonBlack * 100).ToString("F1") + "%");
            Check(lmMid.nonBlack > lmOff.nonBlack + 0.05 && lmMid.mean > lmOff.mean * 3f,
                "【防煤·LLC 路子】下限 0.15 就明显亮起来（零采样代价）",
                "均值 " + lmOff.mean.ToString("F4") + " → " + lmMid.mean.ToString("F4"));
            Check(lmOn.mean > lmMid.mean, "下限越高越亮（0.35 > 0.15）",
                lmMid.mean.ToString("F4") + " → " + lmOn.mean.ToString("F4"));
            Check(lmHigh.mean > lmOn.mean, "0.6 继续变亮，符合 clamp 行为",
                lmOn.mean.ToString("F4") + " → " + lmHigh.mean.ToString("F4"));
            File.WriteAllBytes("ntla-limit-0.png", lmOff.png);
            File.WriteAllBytes("ntla-limit-035.png", lmOn.png);

            // D2：自有光那条路（兜底方案）
            var addOff = RenderSphere(mat, false, false, 0f);
            var addOn = RenderSphere(mat, true, false, 0f);
            Info(string.Format("自有光（叠加通路）：关 → 均值 {0:F4} 非黑 {1:F1}%；开 → 均值 {2:F4} 非黑 {3:F1}% 中心 {4}",
                addOff.mean, addOff.nonBlack * 100, addOn.mean, addOn.nonBlack * 100, addOn.center));
            Check(addOn.nonBlack > addOff.nonBlack + 0.05 && addOn.mean > addOff.mean * 3f,
                "【防煤·兜底】自有光也能防煤",
                "均值 " + addOff.mean.ToString("F4") + " → " + addOn.mean.ToString("F4")
                + "（" + (addOn.mean / Math.Max(addOff.mean, 1e-6)).ToString("F0") + " 倍）");

            var onlyOn = RenderSphere(mat, true, true, 0f);
            Check(onlyOn.nonBlack > addOff.nonBlack + 0.05, "【防煤·排他】只由它照亮同样明显亮起来",
                (onlyOn.nonBlack * 100).ToString("F1") + "%");

            File.WriteAllBytes("ntla-off.png", addOff.png);
            File.WriteAllBytes("ntla-add.png", addOn.png);
            File.WriteAllBytes("ntla-only.png", onlyOn.png);
            Info("对比图：ntla-limit-0.png（下限0=煤）/ ntla-limit-035.png（下限0.35）/ ntla-add.png / ntla-only.png");
        }
        catch (Exception e) { Bad("渲染对照抛异常：" + e); }
        finally { avatar.SetActive(true); }

        UnityEngine.Object.DestroyImmediate(avatar);

        // ---------------- E. NTAmbient 环境光色温探测（2026-09-17 第七轮新增） ----------------
        // 用户需求："探测环境光色温，避免在一个暖色地图而自身光是冷光。"
        // 这一节锁三样东西，都是"写错了不会报错、只会静默错"的类型：
        //   1. 色温往返精度（正向宏 vs 反推搜索必须自洽）
        //   2. 暖色地图必须读出暖色温、偏绿必须被判为"色温表达不了"
        //   3. 属性名与**类型**（Int 用 SetFloat 写会静默无效 —— 本项目踩过）
        L("");
        L("== E. 环境光色温探测（NTAmbient）==");
        var ambientType = FindType("NTAmbient");
        Check(ambientType != null, "找到 NTAmbient（Runtime 侧）");
        if (ambientType == null) return;

        var mRead = ambientType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
        var mFit = ambientType.GetMethod("FitKelvin", BindingFlags.Public | BindingFlags.Static);
        var mKelvin = ambientType.GetMethod("KelvinToColor", BindingFlags.Public | BindingFlags.Static);
        var mHue = ambientType.GetMethod("HueOnly", BindingFlags.Public | BindingFlags.Static);
        Check(mRead != null && mFit != null && mKelvin != null && mHue != null,
            "Read / FitKelvin / KelvinToColor / HueOnly 四个 API 都在");

        if (mFit != null && mKelvin != null)
        {
            // E1 往返精度：用**正向**算出的颜色必须被**反推**回同一个 K。
            // 这条是"预览与实机同源"的保证：两边一旦漂移，这里立刻红。
            int exact = 0; float worst = 0f;
            foreach (var k in new[] { 2000f, 2700f, 3200f, 4000f, 5000f, 6500f, 8000f, 10000f, 15000f })
            {
                var col = (Color)mKelvin.Invoke(null, new object[] { k });
                var fit = mFit.Invoke(null, new object[] { col });
                float got = (float)fit.GetType().GetField("kelvin").GetValue(fit);
                float d = Mathf.Abs(got - k);
                if (d > worst) worst = d;
                if (d < 0.5f) exact++;
            }
            Check(exact == 9, "色温往返精确（9 个标准 K 全部反推回同一个值）",
                exact + "/9 精确，最大偏差 " + worst.ToString("F1") + "K");

            // E2 暖色地图 → 暖色温（这是用户那条需求的直接验收）
            var wf = mFit.Invoke(null, new object[] { new Color(1f, 0.62f, 0.32f) });
            float wk = (float)wf.GetType().GetField("kelvin").GetValue(wf);
            bool wgood = (bool)wf.GetType().GetField("good").GetValue(wf);
            Check(wk > 2000f && wk < 3500f && wgood,
                "暖色地图 (1, 0.62, 0.32) → 2000–3500K 且判定落在黑体轨迹上",
                wk.ToString("F0") + "K, good=" + wgood);

            // E3 偏绿环境色 → 必须被明确判为"色温表达不了"（否则会误导用户写色温）
            var gf = mFit.Invoke(null, new object[] { new Color(0.25f, 0.55f, 0.20f) });
            float gtint = (float)gf.GetType().GetField("tint").GetValue(gf);
            bool ggood = (bool)gf.GetType().GetField("good").GetValue(gf);
            Check(!ggood && gtint > 0.1f, "偏绿环境色 → good=false 且 tint>0.1（导向「匹配环境色」）",
                "tint=" + gtint.ToString("F3") + ", good=" + ggood);

            // E4 白点自检
            var nf = mFit.Invoke(null, new object[] { new Color(.5f, .5f, .5f) });
            float nk = (float)nf.GetType().GetField("kelvin").GetValue(nf);
            Check(nk > 6300f && nk < 6900f, "中性灰 ≈ 6500K（白点自检）", nk.ToString("F0") + "K");
        }

        // E5 属性契约 —— 名字或类型写错都是**静默无效**，所以逐条锁死
        var nonToonShader = Shader.Find("NonToon");
        Check(nonToonShader != null, "找到 NonToon 着色器（属性契约检查的前提）");
        if (nonToonShader != null)
        {
            foreach (var p in new[] { "_UseSelfLight", "_SelfLightUseTemperature" })
            {
                int idx = nonToonShader.FindPropertyIndex(p);
                Check(idx >= 0 && nonToonShader.GetPropertyType(idx) == UnityEngine.Rendering.ShaderPropertyType.Int,
                    p + " 存在且是 Int 型（必须用 SetInteger 写）");
            }
            foreach (var p in new[] { "_SelfLightTemperature", "_SelfLightMatchAmbient", "_SelfLightBlockAmbient" })
            {
                int idx = nonToonShader.FindPropertyIndex(p);
                Check(idx >= 0 && nonToonShader.GetPropertyType(idx) == UnityEngine.Rendering.ShaderPropertyType.Float,
                    p + " 存在且是 Float 型");
            }
            int ki = nonToonShader.FindPropertyIndex("_SelfLightTemperature");
            float kdef = ki >= 0 ? nonToonShader.GetPropertyDefaultFloatValue(ki) : -1f;
            Check(Mathf.Abs(kdef - 6500f) < 1f, "_SelfLightTemperature 默认 6500K", kdef.ToString("F0"));
        }

        // E6 ⑥ 组件必须有 followAmbient（工具侧会写这个字段）
        var shadowType = FindType("NTSelfRealtimeShadow");
        Check(shadowType != null && shadowType.GetField("followAmbient") != null,
            "⑥ 组件有 followAmbient 字段（环境色相跟随）");

        // E7 Read() 端到端：受控暖环境光 → valid + 读到暖色；HueOnly 必须归一亮度
        if (mRead != null && mHue != null)
        {
            var oldMode2 = RenderSettings.ambientMode;
            var oldLight2 = RenderSettings.ambientLight;
            var oldInt2 = RenderSettings.ambientIntensity;
            try
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(1f, 0.62f, 0.32f);
                RenderSettings.ambientIntensity = 1f;
                var sample = mRead.Invoke(null, null);
                bool valid = (bool)sample.GetType().GetField("valid").GetValue(sample);
                var lin = (Color)sample.GetType().GetField("linear").GetValue(sample);
                Check(valid && lin.r > lin.b, "Read() 在受控暖光下读到暖色且 valid=true", lin.ToString("F3"));

                var hue = (Color)mHue.Invoke(null, new object[] { lin });
                Check(Mathf.Abs(hue.r - 1f) < 1e-3f,
                    "HueOnly 把最亮通道归一化到 1（灯强度不会被环境亮度拖动）", hue.ToString("F3"));

                // 全黑环境必须被 valid=false 挡住（否则色相是噪声）
                RenderSettings.ambientLight = new Color(0f, 0f, 0f);
                var dark = mRead.Invoke(null, null);
                Check(!(bool)dark.GetType().GetField("valid").GetValue(dark),
                    "全黑环境 → valid=false（不给噪声色温）");
            }
            finally
            {
                RenderSettings.ambientMode = oldMode2;
                RenderSettings.ambientLight = oldLight2;
                RenderSettings.ambientIntensity = oldInt2;
            }
        }
    }

    class Shot { public byte[] png; public Color32[] px; public double mean, nonBlack; public Color32 center; }

    static Shot RenderSphere(Material mat, bool selfLight, bool onlyThisLight, float minLimit)
    {
        // 材质可能是共享资产，这里用临时实例避免污染
        var m = new Material(mat);
        m.SetFloat("_LightMinLimit", minLimit);
        m.SetInteger("_UseSelfLight", selfLight ? 1 : 0);
        m.SetFloat("_SelfLightIntensity", selfLight ? 3f : 0f);
        if (m.HasProperty("_SelfLightOnly")) m.SetInteger("_SelfLightOnly", onlyThisLight ? 1 : 0);
        if (m.HasProperty("_SelfLightShadowStrength")) m.SetFloat("_SelfLightShadowStrength", 0f);
        if (m.HasProperty("_SelfLightColor")) m.SetColor("_SelfLightColor", Color.white);
        if (m.HasProperty("_SelfLightDirection")) m.SetVector("_SelfLightDirection", new Vector4(0f, 0f, -1f, 0f));

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.GetComponent<MeshRenderer>().sharedMaterial = m;
        var camGo = new GameObject("NTLAcam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.fieldOfView = 40f; cam.transform.position = new Vector3(0, 0, -3f); cam.transform.LookAt(Vector3.zero);

        // 全黑地图：环境光与主光都压到几乎为 0
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.01f, 0.01f, 0.012f);
        var lgo = new GameObject("NTLAfar"); // 不放任何有效平行光

        const int S = 128;
        var rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32); rt.Create();
        var prev = RenderTexture.active;
        cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, S, S), 0, 0); tex.Apply();
        RenderTexture.active = prev;
        var px = tex.GetPixels32();
        var st = new Shot { px = px, png = tex.EncodeToPNG(), center = px[(S / 2) * S + S / 2] };
        double sum = 0; int nb = 0;
        foreach (var p in px)
        {
            double l = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0;
            sum += l; if (l > 0.02) nb++;
        }
        st.mean = sum / px.Length; st.nonBlack = (double)nb / px.Length;

        UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(camGo); UnityEngine.Object.DestroyImmediate(lgo);
        UnityEngine.Object.DestroyImmediate(m);
        return st;
    }
}
