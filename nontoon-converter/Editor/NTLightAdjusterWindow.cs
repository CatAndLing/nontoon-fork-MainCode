using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
// 别名而不是 `using NonToon;` —— 避免与 Editor 侧的 NTSelfLight*/NTAvatarLight* 类型撞名。
using NTAmbient = NonToon.NTAmbient;
using NTSelfRealtimeShadow = NonToon.NTSelfRealtimeShadow;

namespace NonToonTools
{
    // [NT-FEAT 20] 「亮度自适应 / 防全黑地图变煤」编辑器插件。
    //
    // ── 两条关键事实（都来自官方资料，决定了这里的做法）────────────────────
    // 1) VRChat 里挂在 avatar 上的**自定义脚本不会运行**：
    //    https://creators.vrchat.com/avatars/whitelisted-avatar-components/whitelisted-avatar-components/
    //      "Other components or custom scripts won't work in VRChat and may stop you from uploading your avatar."
    //    ⇒ 所以效果全部落到白名单内的东西：材质属性（立刻生效）、Animator + 动画剪辑（游戏内可调）。
    //      本窗口不往场景里加任何组件。
    //
    // 2) 「变煤」的主因是**亮度下限**，不是缺灯。NonToon 与 lilToon 的亮度公式完全一致：
    //      RGB = clamp(RGB, _LightMinLimit, _LightMaxLimit);
    //      RGB = lerp(RGB, Mono, _MonochromeLighting);
    //      RGB = lerp(RGB, 1.0, _AsUnlit);
    //    （lilToon 官方文档「ライティング・明るさ設定」）
    //    而 NonToon 的 _LightMinLimit **默认是 0** ⇒ 地图没光时就是纯黑。
    //    lilToon 官方建议 VRChat 里把下限放在 0.0~0.2；本插件默认给 0.35（更保守，因为 NonToon 默认是 0）。
    //    这条路的代价是 **0 采样**，也不需要打开自有光源。
    //
    // 参考实现：Azukimochi 的 LightLimitChanger（LLC）就是"为亮度下限/上限生成动画"的工具，
    // 思路与本窗口第 ⑤ 节一致（Animator 参数 + 曲线剪辑 + 径向菜单）。
    public sealed class NTLightAdjusterWindow : EditorWindow
    {
        private const string PrefParamName = "NT_LightAdjuster.param";
        private const string PrefSynced = "NT_LightAdjuster.synced";
        private const string PrefLow = "NT_LightAdjuster.low";
        private const string PrefHigh = "NT_LightAdjuster.high";
        private const string PrefTarget = "NT_LightAdjuster.target";

        [MenuItem("Tools/NonToon/③ 亮度自适应与防煤（编辑器插件）", false, 30)]
        private static void Open()
        {
            var w = GetWindow<NTLightAdjusterWindow>("NonToon 亮度自适应");
            w.minSize = new Vector2(470, 600);
            w.Show();
        }

        // 可驱动/可写入的目标属性（只挑 Float 型：Int 型不能这样打曲线）
        private static readonly string[] TargetProps =
        {
            "_LightMinLimit",       // 亮度下限 —— 防煤首选，零采样
            "_LightMaxLimit",       // 亮度上限 —— 防过曝
            "_SelfLightIntensity",  // 自有光强度 —— 彻底不依赖地图光（需先开自有光源）
        };
        private static readonly string[] TargetLabels =
        {
            "亮度下限 _LightMinLimit（推荐：防煤、零采样代价）",
            "亮度上限 _LightMaxLimit（防白飞/过曝）",
            "自有光强度 _SelfLightIntensity（需先打开自有光源）",
        };
        private static readonly float[] TargetLo = { 0.05f, 0.6f, 0.3f };
        private static readonly float[] TargetHi = { 0.6f, 1f, 3f };

        // ---- 目标 ----
        private GameObject targetRoot;
        private int lightIdFilter = -1;
        private bool includeChildren = true;

        // ---- 亮度（Light Limit）----
        // ⛔ 默认 **0**（不是 0.35）。理由见下面 DrawBrightness 里的警告：
        //    `_LightMinLimit` 是 clamp 在**所有光照之后**的亮度地板，抬高它 = 把阴影对比整体压平。
        //    2026-09-17 实机事故正是"菜单默认把下限设成 0.25" ⇒ 全黑角色整片被抬到 0.25 ⇒ 完全没阴影。
        private float minLimit;
        private float maxLimit = 1f;
        private float mono;
        private float asUnlit;

        // ---- 自有光兜底 ----
        private bool selfFallback;
        private float selfIntensity = 1.5f;
        private bool selfOnlyThisLight;
        private float selfBlockAmbient;
        private Vector3 selfDirection = new Vector3(0f, 0.2f, -1f);

        // ---- ⑦ 环境光色温探测 ----
        private bool ambientFoldout = true;
        // ⚠️ 探测结果必须**缓存**：拟合是一次 ~2000 步的搜索，放进 OnGUI 会每个重绘都跑一遍。
        private bool ambientDetected;
        private NonToon.NTAmbient.Sample ambientSample;
        private NonToon.NTAmbient.Fit ambientFit;
        private float ambientMatchStrength = 1f;
        private float ambientBlock = 0f;

        // ---- 游戏内可调 ----
        private int targetIndex;
        private string paramName = "NT_Light";
        private bool synced = true;
        private float radialLow = 0.05f;
        private float radialHigh = 0.6f;
        private bool createMenu = true;

        private string lastResult;
        private Vector2 scroll;
        private bool blackSim;
        private AmbientMode oldAmbientMode;
        private Color oldAmbientLight;
        private float oldAmbientIntensity;
        private readonly List<(Light l, float intensity, Color color)> dimmedLights = new List<(Light, float, Color)>();

        private void OnEnable()
        {
            paramName = EditorPrefs.GetString(PrefParamName, "NT_Light");
            synced = EditorPrefs.GetBool(PrefSynced, true);
            radialLow = EditorPrefs.GetFloat(PrefLow, 0.05f);
            radialHigh = EditorPrefs.GetFloat(PrefHigh, 0.6f);
            targetIndex = Mathf.Clamp(EditorPrefs.GetInt(PrefTarget, 0), 0, TargetProps.Length - 1);
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.HelpBox(
                "两条前提（都有官方出处）：\n" +
                "① VRChat 里**挂在 avatar 上的自定义脚本不会运行**（白名单不允许），" +
                "所以本工具不往你的 avatar 挂任何东西：效果全落到材质属性（立刻生效）与 " +
                "Animator + 动画剪辑（游戏内可调）。\n" +
                "② 「全黑地图变煤」的主因是**亮度下限 = 0**（NonToon 默认值）。抬高它就能防煤，且**零采样代价**、" +
                "不必打开自有光源。",
                MessageType.Info);

            DrawSceneLight();
            DrawTarget();
            DrawBrightness();
            DrawSelfFallback();
            DrawAmbientTemperature();
            DrawVrcParams();
            DrawCleanup();

            if (!string.IsNullOrEmpty(lastResult))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("结果", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(lastResult, GUILayout.MinHeight(90));
            }
            EditorGUILayout.Space();
            if (GUILayout.Button("清空结果")) lastResult = null;

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- ① 场景诊断
        private void DrawSceneLight()
        {
            EditorGUILayout.LabelField("① 场景光照诊断", EditorStyles.boldLabel);
            float ambient, main, total;
            string detail;
            EstimateWorldLight(out ambient, out main, out total, out detail);
            EditorGUILayout.HelpBox(
                detail + "\n\n世界亮度估算 = " + total.ToString("F3") + "（0 = 全黑，1 ≈ 正常室内）\n" + Verdict(total) +
                "\n\n这里量的是**当前打开的场景**；VRChat 里地图光照各不相同、脚本又不运行，" +
                "所以真正的保险是 ③ 的亮度预设 + ⑤ 的游戏内径向。",
                total < 0.12f ? MessageType.Warning : MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!blackSim)
                {
                    if (GUILayout.Button("模拟全黑地图（肉眼验证会不会变煤）")) SimulateBlackMap();
                }
                else if (GUILayout.Button("还原原来的光照设置")) RestoreSceneLight();
                if (GUILayout.Button("刷新读数")) Repaint();
            }
        }

        private static string Verdict(float total)
        {
            if (total < 0.06f) return "判定：**接近全黑** —— 不处理的话 avatar 基本就是一块煤。";
            if (total < 0.15f) return "判定：**偏暗** —— 建议把亮度下限抬到 0.2~0.4。";
            if (total < 0.5f) return "判定：正常偏暗，按喜好决定。";
            return "判定：光照充足，一般不需要补偿。";
        }

        // ---------------------------------------------------------------- ② 目标
        private void DrawTarget()
        {
            EditorGUILayout.LabelField("② 目标", EditorStyles.boldLabel);
            targetRoot = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("模型根节点", "一般拖 avatar 的根对象；留空则用当前选中项"),
                targetRoot, typeof(GameObject), true);
            if (targetRoot == null && Selection.activeGameObject != null)
                EditorGUILayout.LabelField("（未指定时用当前选中：" + Selection.activeGameObject.name + "）", EditorStyles.miniLabel);
            includeChildren = EditorGUILayout.Toggle(new GUIContent("包含子物体"), includeChildren);
            lightIdFilter = EditorGUILayout.IntField(
                new GUIContent("只处理光源编号", "-1 = 不按编号过滤（处理全部 NonToon 材质）；0 = 任意；1..15 = 只处理指定编号"),
                lightIdFilter);
            var mats = CollectMaterials();
            EditorGUILayout.LabelField("找到 " + mats.Count + " 个 NonToon 材质", EditorStyles.miniLabel);
            foreach (var m in mats.Take(10))
            {
                EditorGUILayout.LabelField("   " + m.name
                    + "   下限=" + GetNumber(m, "_LightMinLimit").ToString("F2")
                    + " 上限=" + GetNumber(m, "_LightMaxLimit").ToString("F2")
                    + " 自有光=" + (GetNumber(m, "_UseSelfLight") > 0.5f ? "开" : "关"), EditorStyles.miniLabel);
            }
            if (mats.Count > 10) EditorGUILayout.LabelField("   …还有 " + (mats.Count - 10) + " 个", EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------- ③ 亮度
        private void DrawBrightness()
        {
            EditorGUILayout.LabelField("③ 亮度调整（Light Limit）—— 只推荐用上限", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "着色器里的算法（`birp.hlsl:158-159`）：\n" +
                "    lightColor = env + lightSum.color;\n" +
                "    lightColor = clamp(lightColor, 下限, 上限);   ← 在**所有**光照计算之后\n" +
                "    sd.lightColor = lightColor;                  ← 之后 albedo *= lightColor\n\n" +
                "⇒ **上限**（`_LightMaxLimit`）压过曝很好用，副作用小。\n" +
                "⇒ **下限**（`_LightMinLimit`）是防煤主力，但它是一条**与灯光无关的亮度地板**：\n" +
                "    抬高它会把阴影区一起抬亮 ⇒ **阴影对比被压平**，而且**实时 PCSS 的阴影强度也会被它吃掉**。\n" +
                "    对**深色/全黑角色**最致命（它们本来就只有 0.05~0.2，抬到 0.25 就整片平掉）。\n" +
                "    **所以默认是 0；要用请自己承担「阴影变弱」的代价。**",
                MessageType.Warning);

            minLimit = EditorGUILayout.Slider(new GUIContent("亮度下限 _LightMinLimit",
                "0 = 官方默认（地图黑就全黑）；抬高它会**压平阴影对比**，深色角色尤其明显"), minLimit, 0f, 1f);
            maxLimit = EditorGUILayout.Slider(new GUIContent("亮度上限 _LightMaxLimit",
                "低于 1 可以压掉地图过曝/白飞 —— 推荐优先用这个"), maxLimit, 0f, 1f);
            if (maxLimit < minLimit) maxLimit = minLimit;
            mono = EditorGUILayout.Slider(new GUIContent("单色化 _MonochromeLighting",
                "去掉光的颜色、只留明暗；地图灯光颜色很怪时用"), mono, 0f, 1f);
            asUnlit = EditorGUILayout.Slider(new GUIContent("Unlit 化 _AsUnlit",
                "越接近 1 越像 Unlit（底亮）；注意：你变亮的同时，相对地周围的人会显得更暗"), asUnlit, 0f, 1f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("防煤（下限 0.35）")) { minLimit = 0.35f; maxLimit = 1f; ApplyBrightness(); }
                if (GUILayout.Button("lilToon 推荐（0.15）")) { minLimit = 0.15f; maxLimit = 1f; ApplyBrightness(); }
                if (GUILayout.Button("明亮（0.5）")) { minLimit = 0.5f; maxLimit = 1f; ApplyBrightness(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("压过曝（上限 0.85）")) { maxLimit = 0.85f; ApplyBrightness(); }
                if (GUILayout.Button("还原官方默认（0 / 1）")) { minLimit = 0f; maxLimit = 1f; mono = 0f; asUnlit = 0f; ApplyBrightness(); }
            }
            using (new EditorGUI.DisabledScope(CollectMaterials().Count == 0))
                if (GUILayout.Button("写入上面这些值到材质", GUILayout.Height(24))) ApplyBrightness();
        }

        private void ApplyBrightness()
        {
            var mats = CollectMaterials();
            if (mats.Count == 0) { lastResult = "没找到 NonToon 材质。"; return; }
            Undo.RecordObjects(mats.ToArray(), "NonToon 亮度调整");
            foreach (var m in mats)
            {
                SetNumber(m, "_LightMinLimit", minLimit);
                SetNumber(m, "_LightMaxLimit", Mathf.Max(maxLimit, minLimit));
                SetNumber(m, "_MonochromeLighting", mono);
                SetNumber(m, "_AsUnlit", asUnlit);
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
            lastResult = "已写入 " + mats.Count + " 个材质：下限 " + minLimit.ToString("F2") + " / 上限 "
                + maxLimit.ToString("F2") + " / 单色 " + mono.ToString("F2") + " / Unlit " + asUnlit.ToString("F2") + "\n"
                + "建议接着点 ① 的「模拟全黑地图」再「还原」，肉眼确认一下。";
        }

        // ---------------------------------------------------------------- ④ 自有光兜底
        private void DrawSelfFallback()
        {
            EditorGUILayout.LabelField("④ 兜底：自有光源（可选，彻底不依赖地图光）", EditorStyles.boldLabel);
            selfFallback = EditorGUILayout.Foldout(selfFallback, "展开（一般用不到，亮度下限通常就够了）");
            if (!selfFallback) return;
            EditorGUILayout.HelpBox(
                "自有光源是 NonToon 着色器里的一路私有光：地图完全没灯时也能照亮自己，且不影响别人。\n" +
                "默认仍然**不排他**（地图有光时照常吃地图光）。采样代价 0~1/像素。",
                MessageType.None);
            selfIntensity = EditorGUILayout.Slider(new GUIContent("自有光强度"), selfIntensity, 0f, 8f);
            selfOnlyThisLight = EditorGUILayout.Toggle(new GUIContent("只由它照亮（丢掉地图所有光）",
                "勾上 = 任何地图里长得一样；代价是地图氛围也没了"), selfOnlyThisLight);
            using (new EditorGUI.DisabledScope(selfOnlyThisLight))
                selfBlockAmbient = EditorGUILayout.Slider(new GUIContent("屏蔽地图环境光（0–1）"), selfBlockAmbient, 0f, 1f);
            selfDirection = EditorGUILayout.Vector3Field(new GUIContent("光的方向（指向光源，世界空间）"), selfDirection);
            using (new EditorGUI.DisabledScope(CollectMaterials().Count == 0))
                if (GUILayout.Button("写入自有光配置到材质")) ApplySelfLight();
        }

        private void ApplySelfLight()
        {
            var mats = CollectMaterials();
            if (mats.Count == 0) { lastResult = "没找到 NonToon 材质。"; return; }
            Undo.RecordObjects(mats.ToArray(), "NonToon 自有光预设");
            foreach (var m in mats)
            {
                SetNumber(m, "_UseSelfLight", selfIntensity > 0.001f ? 1f : 0f);
                SetNumber(m, "_SelfLightIntensity", selfIntensity);
                SetNumber(m, "_SelfLightOnly", selfOnlyThisLight ? 1f : 0f);
                SetNumber(m, "_SelfLightBlockAmbient", selfOnlyThisLight ? 0f : selfBlockAmbient);
                if (m.HasProperty("_SelfLightDirection"))
                {
                    var d = selfDirection.sqrMagnitude > 1e-6f ? selfDirection.normalized : new Vector3(0f, 0f, -1f);
                    m.SetVector("_SelfLightDirection", new Vector4(d.x, d.y, d.z, 0f));
                }
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
            lastResult = "已把自有光配置写入 " + mats.Count + " 个材质（强度 " + selfIntensity.ToString("F2") + "）。";
        }

        // ---------------------------------------------------------------- ⑦ 环境光色温探测
        // 用户需求原话："探测环境光色温，避免在一个暖色地图而自身光是冷光。"
        //
        // 两条落点，机制完全不同，别混：
        //   · **④ 材质色温**（`_SelfLightUseTemperature` + `_SelfLightTemperature`）＝**烘焙**：
        //     把估出来的 K 写死进材质。简单、零运行时开销，但**换地图不会变**。
        //   · **④ 匹配环境色**（`_SelfLightMatchAmbient`）＝**运行时跟随**：
        //     着色器把自有光颜色朝世界环境色 lerp，换地图自动适应，还能表达色温表达不了的色相（比如偏绿）。
        //   · **⑥ 灯颜色**（组件 `lightColor` + `followAmbient`）＝ 实时阴影那盏 Spot 的色相跟随。
        private void DrawAmbientTemperature()
        {
            EditorGUILayout.LabelField("⑦ 环境光色温探测（避免「暖色地图 + 冷光自己」）", EditorStyles.boldLabel);
            ambientFoldout = EditorGUILayout.Foldout(ambientFoldout, "展开");
            if (!ambientFoldout) return;

            if (!ambientDetected)
            {
                EditorGUILayout.HelpBox("还没探测。点下面的「探测当前场景环境光」。", MessageType.None);
                if (GUILayout.Button("探测当前场景环境光", GUILayout.Height(24))) DetectAmbient();
                return;
            }

            // 色板：把探测到的环境色画出来，肉眼确认"是不是我以为的那个颜色"
            using (new EditorGUILayout.HorizontalScope())
            {
                var rect = GUILayoutUtility.GetRect(64, 22, GUILayout.Width(64));
                EditorGUI.DrawRect(rect, ambientSample.display);
                EditorGUILayout.LabelField("← 探测到的环境色（sRGB）", EditorStyles.miniLabel);
            }

            EditorGUILayout.HelpBox(NTAmbient.Describe(ambientSample, ambientFit), MessageType.None);

            ambientMatchStrength = EditorGUILayout.Slider(
                new GUIContent("匹配环境色的强度 _SelfLightMatchAmbient",
                    "1 = 自有光完全用环境色（推荐：换地图自动适应）；0 = 只用下面写的色温"), ambientMatchStrength, 0f, 1f);
            ambientBlock = EditorGUILayout.Slider(
                new GUIContent("屏蔽地图环境光 _SelfLightBlockAmbient",
                    "抬高它会把环境光从结果里减掉 ⇒ 阴影更黑、对比更强。只在 ④ 那条路有效，⑥ 不读它"),
                ambientBlock, 0f, 1f);

            EditorGUILayout.LabelField(
                "写入的色温：" + ambientFit.kelvin.ToString("F0") + " K（着色器范围 "
                + NTAmbient.KelvinMin.ToString("F0") + "–" + NTAmbient.KelvinMax.ToString("F0") + " K）",
                EditorStyles.miniLabel);

            int mats = CollectMaterials().Count;
            using (new EditorGUI.DisabledScope(mats == 0))
            {
                if (GUILayout.Button("④ 匹配环境色（运行时跟随，推荐）", GUILayout.Height(24)))
                    ApplyAmbientToMaterials(toTemperature: false);
                if (GUILayout.Button("④ 写入色温 " + ambientFit.kelvin.ToString("F0") + " K（烘焙，零运行时代价）"))
                    ApplyAmbientToMaterials(toTemperature: true);
            }
            if (mats == 0)
                EditorGUILayout.LabelField("（先在上面②指定模型根节点，或选中模型）", EditorStyles.miniLabel);

            // ⚠️ 只查一次：`GetComponentsInChildren` 每次调用都分配数组，而 OnGUI 一秒要跑很多次。
            var shadows = FindRealtimeShadows();
            using (new EditorGUI.DisabledScope(shadows.Length == 0))
                if (GUILayout.Button("⑥ 把这盏实时灯也改成环境色相（跟随）", GUILayout.Height(24)))
                    ApplyAmbientToRealtimeLight();
            if (shadows.Length == 0)
                EditorGUILayout.LabelField("（模型上没有 ⑥ 的实时阴影组件，跳过）", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重新探测")) DetectAmbient();
                if (GUILayout.Button("模拟暖色地图（看一眼还会不会像冷光）")) SimulateWarmMap();
            }
        }

        private void DetectAmbient()
        {
            ambientSample = NTAmbient.Read();
            ambientFit = NTAmbient.FitKelvin(ambientSample.linear);
            ambientDetected = true;
            lastResult = "环境光探测：" + ambientSample.source + "\n"
                + "线性 " + ambientSample.linear + "  亮度 " + ambientSample.luminance.ToString("F4") + "\n"
                + "拟合色温 ≈ " + ambientFit.kelvin.ToString("F0") + " K（色度残差 " + ambientFit.error.ToString("F4") + "）";
            Repaint();
        }

        /// <summary>把探测结果写进材质。`toTemperature` 决定走"烘焙色温"还是"运行时匹配环境色"——
        /// 两者在着色器里是**互斥**的：`_SelfLightMatchAmbient = 1` 时 `outColor` 直接被环境色替换，
        /// 色温完全不起作用。所以这里显式把另一条清零，免得用户以为两个都开了在叠加。</summary>
        private void ApplyAmbientToMaterials(bool toTemperature)
        {
            var mats = CollectMaterials();
            if (mats.Count == 0) { lastResult = "没找到 NonToon 材质。"; return; }
            Undo.RecordObjects(mats.ToArray(), toTemperature ? "NonToon 写入环境色温" : "NonToon 匹配环境色");

            int selfLightOff = 0;
            foreach (var m in mats)
            {
                // 色温/匹配都作用在**自有光**上；自有光没开的话写了也没用 ⇒ 明确告知而不是静默失败。
                if (GetNumber(m, "_UseSelfLight") < 0.5f) selfLightOff++;
                SetNumber(m, "_SelfLightUseTemperature", toTemperature ? 1f : 0f);
                if (toTemperature) SetNumber(m, "_SelfLightTemperature", ambientFit.kelvin);
                SetNumber(m, "_SelfLightMatchAmbient", toTemperature ? 0f : ambientMatchStrength);
                SetNumber(m, "_SelfLightBlockAmbient", ambientBlock);
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();

            lastResult = (toTemperature
                    ? "已把色温 " + ambientFit.kelvin.ToString("F0") + " K 写入 " + mats.Count + " 个材质的自有光（烘焙）。"
                    : "已把「匹配环境色」强度设为 " + ambientMatchStrength.ToString("F2") + "，写入 " + mats.Count + " 个材质。")
                + (ambientBlock > 0.001f ? "\n同时把「屏蔽地图环境光」设为 " + ambientBlock.ToString("F2") + "（阴影会更黑）。" : "")
                + (selfLightOff > 0
                    ? "\n\n⚠️ 其中 " + selfLightOff + " 个材质的**自有光没打开**（_UseSelfLight = 0）——"
                      + "这时色温和匹配都不会有任何可见效果。请在 ④ 展开里点「写入自有光配置到材质」，"
                      + "或直接抬高「自有光强度」。"
                    : "\n\n自有光已经开着，效果立刻可见（不需要进 Play）。");
        }

        private NTSelfRealtimeShadow[] FindRealtimeShadows()
        {
            var root = ResolveRoot();
            if (root == null) return new NTSelfRealtimeShadow[0];
            return root.GetComponentsInChildren<NTSelfRealtimeShadow>(true)
                .Where(c => c != null).ToArray();
        }

        private void ApplyAmbientToRealtimeLight()
        {
            var comps = FindRealtimeShadows();
            if (comps.Length == 0) { lastResult = "模型上没有 ⑥ 的实时阴影组件。"; return; }
            Undo.RecordObjects(comps.Cast<UnityEngine.Object>().ToArray(), "NonToon ⑥ 环境色相跟随");
            foreach (var c in comps)
            {
                if (c.targetLight == null) c.targetLight = c.GetComponentInChildren<Light>();
                c.lightColor = NTAmbient.HueOnly(ambientSample.linear);
                c.followAmbient = ambientMatchStrength;
                EditorUtility.SetDirty(c);
            }
            lastResult = "已把 " + comps.Length + " 个 ⑥ 组件设为「环境色相跟随」（强度 "
                + ambientMatchStrength.ToString("F2") + "）。\n"
                + "灯的**强度不受影响**（只取色相，最亮通道归一化到 1）—— 现在灯颜色 = "
                + NTAmbient.HueOnly(ambientSample.linear) + "。\n"
                + "运行时每帧按当前场景的环境光重算，换地图会自动适应。";
        }

        /// <summary>把场景环境光改成典型"暖色黄昏" —— 用来肉眼验证「暖色地图 + 冷光自己」有没有被解决。</summary>
        private void SimulateWarmMap()
        {
            if (!blackSim)
            {
                oldAmbientMode = RenderSettings.ambientMode;
                oldAmbientLight = RenderSettings.ambientLight;
                oldAmbientIntensity = RenderSettings.ambientIntensity;
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 0.62f, 0.32f); // 线性 ≈ 暖橙（≈2700K 观感）
            RenderSettings.ambientIntensity = 1f;
            foreach (var l in FindObjectsOfType<Light>())
            {
                if (!l.enabled || l.type != LightType.Directional) continue;
                if (!dimmedLights.Any(e => e.l == l)) dimmedLights.Add((l, l.intensity, l.color));
                l.color = new Color(1f, 0.68f, 0.42f);
            }
            blackSim = true;
            DetectAmbient();
            lastResult += "\n\n已模拟暖色地图（环境光 + 主平行光都调暖）。看完了点 ① 里的「还原原来的光照设置」。";
            SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- ⑤ 游戏内可调
        private void DrawVrcParams()
        {
            EditorGUILayout.LabelField("⑤ 游戏内可调（VRChat 参数 / 径向菜单）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "生成：Animator Float 参数 + 两条曲线剪辑 + 一层 BlendTree + VRC Expression Parameter + 径向菜单控件。\n" +
                "玩家在 Action Menu 里拉径向即可调；Float 参数占 8 bit 同步内存（VRChat 上限 256 bit）。\n" +
                "思路同 LightLimitChanger（LLC）：它也是「为亮度下限/上限生成动画」。",
                MessageType.None);

            int prev = targetIndex;
            targetIndex = EditorGUILayout.Popup(new GUIContent("驱动哪个属性"), targetIndex, TargetLabels);
            if (targetIndex != prev)
            {
                radialLow = TargetLo[targetIndex];
                radialHigh = TargetHi[targetIndex];
            }
            paramName = EditorGUILayout.TextField(new GUIContent("参数名", "别用空格；推荐 NT_Light"), paramName);
            synced = EditorGUILayout.Toggle(new GUIContent("同步给别人看（8 bit）",
                "关掉 = 省 8 bit，但只有你自己看得到变化"), synced);
            radialLow = EditorGUILayout.Slider(new GUIContent("径向 0 时的值"), radialLow, 0f, 8f);
            radialHigh = EditorGUILayout.Slider(new GUIContent("径向 1 时的值"), radialHigh, 0f, 8f);
            if (radialLow > radialHigh) radialHigh = radialLow;
            createMenu = EditorGUILayout.Toggle(new GUIContent("同时加 Expression Menu 径向控件",
                "需要 avatar 上有 VRCAvatarDescriptor；没装 VRCSDK 时只能生成 Animator 部分"), createMenu);

            EditorGUILayout.LabelField("提示：驱动 _LightMinLimit 时，把 0 / 1 两端都设在 0~1 之间（亮度下限本身就是 0~1）。",
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(CollectMaterials().Count == 0))
                if (GUILayout.Button("生成 / 更新（可重复运行）", GUILayout.Height(26))) Generate();

            if (GUILayout.Button("重置本窗口的设置"))
            {
                EditorPrefs.DeleteKey(PrefParamName); EditorPrefs.DeleteKey(PrefSynced);
                EditorPrefs.DeleteKey(PrefLow); EditorPrefs.DeleteKey(PrefHigh); EditorPrefs.DeleteKey(PrefTarget);
                OnEnable();
            }
        }

        private void Generate()
        {
            var root = ResolveRoot();
            if (root == null) { lastResult = "请先指定模型根节点（或选中它）。"; return; }
            var mats = CollectMaterials();
            var renderers = new List<Renderer>();
            foreach (var rd in root.GetComponentsInChildren<Renderer>(includeChildren))
                if (rd.sharedMaterials.Any(m => m != null && mats.Contains(m))) renderers.Add(rd);

            EditorPrefs.SetString(PrefParamName, paramName);
            EditorPrefs.SetBool(PrefSynced, synced);
            EditorPrefs.SetFloat(PrefLow, radialLow);
            EditorPrefs.SetFloat(PrefHigh, radialHigh);
            EditorPrefs.SetInt(PrefTarget, targetIndex);

            var prop = TargetProps[targetIndex];

            // ⓪ **MA 现在是必需的**：需要菜单的功能一律走声明式。
            //    这个窗口以前是"直接写用户 FX 控制器 / ExpressionParameters / ExpressionsMenu"的
            //    （它调 Build 时没传 standalone ⇒ 走的就是那条路）—— 实机事故正是它造成的：
            //    在用户的 `Torao_FXLayer.controller` 里留下 `defaultWeight = 0` 的死层。
            //    ⇒ 回退路径已删除。没装 MA 就什么都不做。
            if (!NTModularAvatarBridge.IsAvailable)
            {
                lastResult = "本工程里没装 Modular Avatar，而 ⑤ 只走声明式路径（不会改你的资产）。\n"
                    + "请在 VCC 里装上 Modular Avatar（nadena.dev.modular-avatar）后重试。";
                return;
            }

            var r = NTVrcParameterBuilder.Build(root, renderers, paramName, radialLow, radialHigh,
                NTVrcParameterBuilder.DefaultFolder, prop);
            var attach = NTModularAvatarBridge.Attach(root, r.Controller, paramName, synced, 1f, "亮度",
                // `saved: true` —— 亮度是**材质属性**，必须同步，否则别人眼里的你还是暗的/过曝的。
                // （同 `NTLightAdjustEditor` 里的那条注释。）
                saved: true);
            lastResult =
                "目标：" + root.name + "，材质 " + mats.Count + " 个，参与动画的 Renderer " + renderers.Count + " 个\n" +
                "驱动属性：" + prop + "（径向 " + radialLow.ToString("F2") + " → " + radialHigh.ToString("F2") + "）\n" +
                "路径：Modular Avatar（非破坏）—— **你的 FX 控制器 / 参数 / 菜单不会被改动**\n" +
                (r.Ok ? "生成完成：\n" : "有错误：\n") + r.Summary() +
                "\nMA：" + string.Join("\nMA：", attach.Done.ToArray()) +
                (attach.Errors.Count > 0 ? "\n❌ " + string.Join("\n❌ ", attach.Errors.ToArray()) : "") +
                "\n提示：上传一次，用 Action Menu 里的「亮度」径向试试。";
        }

        // ---------------------------------------------------------------- ⑥ 清理
        private void DrawCleanup()
        {
            EditorGUILayout.LabelField("⑥ 上传前清理（可选）", EditorStyles.boldLabel);
            var root = ResolveRoot();
            int n = root == null ? 0 : root.GetComponentsInChildren<MonoBehaviour>(true)
                .Count(c => c != null && (c.GetType().Name == "NTAvatarLight" || c.GetType().Name == "NTSelfLight"));
            EditorGUILayout.HelpBox(
                (n > 0 ? "当前模型上有 " + n + " 个本包的助手组件。\n" : "") +
                "这些组件只是**编辑器里的设置载体**，VRChat 里不会运行，上传时 SDK 可能提示「不被允许的组件」。\n" +
                "它们的效果已经落到「那盏 Light」和「材质里的参数/烘焙结果」上，**移除组件不影响效果**。",
                n > 0 ? MessageType.Warning : MessageType.None);
            using (new EditorGUI.DisabledScope(n == 0))
                if (GUILayout.Button("移除本包的助手组件（保留 Light 与材质效果）"))
                    CleanupHelperComponents(root);
        }

        private void CleanupHelperComponents(GameObject root)
        {
            if (root == null) return;
            var comps = root.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(c => c != null && (c.GetType().Name == "NTAvatarLight" || c.GetType().Name == "NTSelfLight"))
                .Cast<Component>().ToArray();
            if (comps.Length == 0) return;
            if (!EditorUtility.DisplayDialog("移除助手组件",
                "将移除 " + comps.Length + " 个本包的助手组件。\n\n" +
                "它们的效果（那盏 Light、材质里烘焙好的阴影贴图与参数）都会保留。\n\n继续？", "移除", "取消")) return;
            foreach (var c in comps) Undo.DestroyObjectImmediate(c);
            lastResult = "已移除 " + comps.Length + " 个助手组件（Light 与材质效果保留）。";
        }

        // ---------------------------------------------------------------- 逻辑/工具
        private GameObject ResolveRoot() { return targetRoot != null ? targetRoot : Selection.activeGameObject; }

        private List<Material> CollectMaterials()
        {
            var result = new List<Material>();
            var root = ResolveRoot();
            if (root == null) return result;
            foreach (var rd in root.GetComponentsInChildren<Renderer>(includeChildren))
            {
                foreach (var m in rd.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (m.shader.name != "NonToon" && m.shader.name != "NonToonFur") continue;
                    if (!m.HasProperty("_LightMinLimit")) continue;
                    if (lightIdFilter >= 0 && m.HasProperty("_SelfLightId") && m.GetInteger("_SelfLightId") != lightIdFilter) continue;
                    if (!result.Contains(m)) result.Add(m);
                }
            }
            return result;
        }

        private static float Lum(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

        private static void EstimateWorldLight(out float ambient, out float main, out float total, out string detail)
        {
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Flat:
                    ambient = Lum(RenderSettings.ambientLight) * RenderSettings.ambientIntensity;
                    detail = "环境光：Flat，颜色 " + RenderSettings.ambientLight + "，强度 " + RenderSettings.ambientIntensity;
                    break;
                case AmbientMode.Trilight:
                    ambient = (Lum(RenderSettings.ambientSkyColor) + Lum(RenderSettings.ambientEquatorColor) + Lum(RenderSettings.ambientGroundColor)) / 3f * RenderSettings.ambientIntensity;
                    detail = "环境光：Trilight（三色渐变）";
                    break;
                default:
                    var sh = RenderSettings.ambientProbe;
                    var l0 = new Color(sh[0, 0], sh[1, 0], sh[2, 0]);
                    ambient = Lum(l0) * RenderSettings.ambientIntensity;
                    detail = "环境光：Skybox，SH L0 = " + l0.ToString("F3");
                    break;
            }
            main = 0f;
            int count = 0;
            foreach (var l in FindObjectsOfType<Light>())
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (l.type != LightType.Directional) continue;
                count++;
                main = Mathf.Max(main, Lum(l.color) * l.intensity);
            }
            detail += "\n平行光：" + count + " 盏，最亮 " + main.ToString("F2")
                      + "\n（着色器里的 SH L0 就是环境光这一项；VRChat 里的地图另有一套）";
            total = Mathf.Clamp01(ambient + main * 0.5f);
        }

        private void SimulateBlackMap()
        {
            oldAmbientMode = RenderSettings.ambientMode;
            oldAmbientLight = RenderSettings.ambientLight;
            oldAmbientIntensity = RenderSettings.ambientIntensity;
            dimmedLights.Clear();
            foreach (var l in FindObjectsOfType<Light>())
            {
                if (!l.enabled || l.type != LightType.Directional) continue;
                dimmedLights.Add((l, l.intensity, l.color));
                l.intensity = 0.02f;
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.01f, 0.01f, 0.012f);
            RenderSettings.ambientIntensity = 1f;
            blackSim = true;
            lastResult = "已把当前场景压成「接近全黑」（主平行光 0.02、环境光 0.01）。看完了点「还原原来的光照设置」。";
            SceneView.RepaintAll();
        }

        private void RestoreSceneLight()
        {
            RenderSettings.ambientMode = oldAmbientMode;
            RenderSettings.ambientLight = oldAmbientLight;
            RenderSettings.ambientIntensity = oldAmbientIntensity;
            foreach (var (l, i, c) in dimmedLights) if (l != null) { l.intensity = i; l.color = c; }
            dimmedLights.Clear();
            blackSim = false;
            lastResult = "已还原场景光照。";
            SceneView.RepaintAll();
        }

        private static float GetNumber(Material m, string prop)
        {
            if (m == null || !m.HasProperty(prop)) return 0f;
            var idx = m.shader.FindPropertyIndex(prop);
            if (idx >= 0 && m.shader.GetPropertyType(idx) == ShaderPropertyType.Int) return m.GetInteger(prop);
            return m.GetFloat(prop);
        }

        // Int 属性必须走 SetInteger —— SetFloat/SetInt 对 ShaderLab Integer 是静默无效的（实测踩过）
        private static void SetNumber(Material m, string prop, float value)
        {
            if (m == null || !m.HasProperty(prop)) return;
            var idx = m.shader.FindPropertyIndex(prop);
            if (idx >= 0 && m.shader.GetPropertyType(idx) == ShaderPropertyType.Int)
                m.SetInteger(prop, Mathf.RoundToInt(value));
            else m.SetFloat(prop, value);
        }
    }
}
