using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    // [NT-TOOL] NTSelfLight 的检视面板。
    //
    // 两种模式：
    //   · 烘焙阴影图（默认）：数据写进材质，运行时零行为，影子永远可见（Quest 也能用）
    //   · 实时光源：真挂一盏 Spot Light，影子实时（姿势实时），代价见面板上的说明
    //
    // 注意 Int 属性必须用 SetInteger：NonToon 的 SC_uint / ShaderLab Int 属性用 SetFloat 写是**静默无效**的
    // （这个坑在材质转换器那边踩过一次：_ShadowColorEnable 写进去变 0）。
    //
    // [NT-L10N] 字段标签也自己画，因为 Unity 默认是用 C# 字段名 nicify 出来的英文。
    [CustomEditor(typeof(NTSelfLight))]
    public sealed class NTSelfLightEditor : Editor
    {
        private string lastSummary;
        private bool showAdvanced;

        [MenuItem("GameObject/NonToon/② 创建自有光源（需要在材质启用）", true, 51)]
        private static bool ValidateCreateSelfLight()
        {
            return Selection.gameObjects.Any(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
        }

        [MenuItem("GameObject/NonToon/② 创建自有光源（需要在材质启用）", false, 51)]
        private static void CreateSelfLight()
        {
            var root = Selection.gameObjects.FirstOrDefault(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
            if (root == null) { Debug.LogWarning("[NTSelfLight] 请先选中要作用的节点（或它的根节点）。"); return; }
            var existing = root.GetComponent<NTSelfLight>();
            var comp = existing != null ? existing : Undo.AddComponent<NTSelfLight>(root);
            if (comp.targetRoot == null && root.transform != comp.transform) comp.targetRoot = root.transform;
            Selection.activeGameObject = comp.gameObject;
            Debug.Log("[NTSelfLight] 已在 " + root.name + " 上挂好「自有光源」。"
                + "材质侧：NonToon 材质的「自有光源」开关由本组件自动写入（也可以用 Light ID 指定只接受哪一路）。");
        }

        [MenuItem("Tools/NonToon/② 创建自有光源（需要在材质启用）")]
        private static void CreateSelfLightFromTools() { CreateSelfLight(); }

        public override void OnInspectorGUI()
        {
            var settings = (NTSelfLight)target;
            var previousMode = settings.mode;

            EditorGUILayout.HelpBox(
                "⚠️ 这类组件需要在材质球上启用：材质必须是 NonToon，并打开「自有光源」开关（Lights ▸ Self Light）。\n" +
                "好消息是：开关与参数由本组件自动写入材质，你不用手点；\n" +
                "也可以用「光源编号 / 只接受哪一路光源」让多个组件各照各的材质。\n" +
                "（不想动材质的话，用 Add Component ▸ NonToon ▸ ① Avatar 光源，那是真 Unity Light）",
                MessageType.None);
            EditorGUILayout.LabelField("模式", EditorStyles.boldLabel);
            settings.mode = (NTSelfLightMode)EditorGUILayout.EnumPopup(
                new GUIContent("自阴影来源", "烘焙阴影图 = 永远可见、Quest 可用、不占 Lights 计数，但阴影是烘焙那一刻的姿态\n实时光源 = 真挂一盏 Spot Light，阴影实时，但占 Lights 计数、依赖观看者阴影设置"),
                settings.mode);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("作用范围", EditorStyles.boldLabel);
            settings.targetRoot = (Transform)EditorGUILayout.ObjectField(new GUIContent("作用节点", "留空 = 本组件所在节点。想挂在某个物体上却照另一个物体，就把那个根节点拖进来"), settings.targetRoot, typeof(Transform), true);
            settings.includeChildren = EditorGUILayout.Toggle(new GUIContent("包含子节点"), settings.includeChildren);
            settings.lightId = EditorGUILayout.IntSlider(new GUIContent("光源编号", "1–15；0 = 不限制。材质上的「只接受哪一路光源」填了别的编号时，本组件不会写它 —— 于是同一模型上可以并存多路自有光各照各的"), settings.lightId, 0, 15);
            settings.stampId = EditorGUILayout.Toggle(new GUIContent("把编号写到材质", "写入时顺便把材质的「只接受哪一路光源」盖上本组件的编号，避免被别的组件抢"), settings.stampId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("光源", EditorStyles.boldLabel);
            settings.color = EditorGUILayout.ColorField(new GUIContent("光源颜色"), settings.color);
            settings.useColorTemperature = EditorGUILayout.Toggle(new GUIContent("使用色温", "用 Kelvin 决定光色：6500K ≈ 白，低色温偏橙、高色温偏蓝（几行 ALU，零采样）"), settings.useColorTemperature);
            using (new EditorGUI.DisabledScope(!settings.useColorTemperature))
                settings.temperature = EditorGUILayout.Slider(new GUIContent("色温（K）"), settings.temperature, 1000f, 20000f);
            settings.intensity = EditorGUILayout.Slider(new GUIContent("光源强度"), settings.intensity, 0f, 8f);

            var materials = CollectTargetMaterials(settings);
            var renderers = settings.GetComponentsInChildren<Renderer>(true);

            if (settings.mode == NTSelfLightMode.BakedShadowMap) DrawBaked(settings, materials);
            else DrawRealtime(settings, renderers, materials);

            if (previousMode != settings.mode)
            {
                // 换模式时把上一条路的效果收干净，避免两条路同时生效
                if (settings.mode == NTSelfLightMode.RealtimeLight) NTSelfRealtimeLight.Disable(settings);
                else if (settings.realtimeLight != null) NTSelfRealtimeLight.Disable(settings);
                if (settings.lightSource != null || materials.Count > 0) Apply(settings, materials);
            }

            if (!string.IsNullOrEmpty(lastSummary))
                EditorGUILayout.HelpBox(lastSummary, MessageType.Info);
        }

        // ---------------------------------------------------------------- 烘焙模式
        private void DrawBaked(NTSelfLight settings, List<Material> materials)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("烘焙阴影图", EditorStyles.boldLabel);

            settings.lightSource = (Light)EditorGUILayout.ObjectField(
                new GUIContent("光源 Light Source", "烘焙时读取它的方向/颜色/强度。不会真的把它挂到 avatar 上。"),
                settings.lightSource, typeof(Light), true);
            settings.onlyThisLight = EditorGUILayout.Toggle(
                new GUIContent("只由它照亮", "忽略世界光与环境光，avatar 在任何世界里长得都一样"), settings.onlyThisLight);

            using (new EditorGUI.ChangeCheckScope())
            {
                settings.pcss = EditorGUILayout.Toggle(new GUIContent("PCSS 软阴影"), settings.pcss);
                using (new EditorGUI.DisabledScope(!settings.pcss))
                {
                    settings.pcssQuality = (NTPCSSQuality)EditorGUILayout.EnumPopup(
                        new GUIContent("画质档位", "低 8+12（默认，最省）/ 中 12+24 / 高 20+40 / 极高 32+64 次采样。NonToon 的目标就是低消耗，不确定就留在低档。"),
                        settings.pcssQuality);
                    settings.softness = EditorGUILayout.Slider(new GUIContent("柔度 Softness"), settings.softness, 0f, 1f);
                    settings.shadowClamp = EditorGUILayout.Slider(new GUIContent("硬化 Shadow Clamp", "把软边压成硬边（动画风），0 = 保持软边"), settings.shadowClamp, 0f, 1f);
                }
                // 预算读数放在 DisabledScope 外面：PCSS 关掉时它是"1 次"的有效信息，不该被灰掉
                EditorGUILayout.HelpBox(ShadowBudgetText(settings), MessageType.None);
                settings.matchWorldColor = EditorGUILayout.Slider(new GUIContent("匹配世界光颜色", "让自带光染上地图环境光的颜色（0 = 完全用自己的颜色）"), settings.matchWorldColor, 0f, 1f);
                settings.matchWorldDirection = EditorGUILayout.Slider(new GUIContent("匹配世界光方向", "让自带光与它的自阴影跟随地图主光方向（0 = 用自己的方向）"), settings.matchWorldDirection, 0f, 1f);
                settings.shadowDensity = EditorGUILayout.Slider(new GUIContent("浓度 Density"), settings.shadowDensity, 0f, 1f);
                settings.shadowStrength = EditorGUILayout.Slider(new GUIContent("强度 Strength"), settings.shadowStrength, 0f, 1f);
                settings.shadowDistance = EditorGUILayout.Slider(new GUIContent("阴影距离", "离相机超过该距离就关闭自阴影。0 = 不限制"), settings.shadowDistance, 0f, 50f);
                settings.receiveMask = (Texture2D)EditorGUILayout.ObjectField(new GUIContent("接收遮罩 Receive Mask", "逐像素控制哪里接收自阴影，白色 = 正常接收"), settings.receiveMask, typeof(Texture2D), false);
                using (new EditorGUI.DisabledScope(settings.receiveMask == null))
                {
                    settings.receiveMaskChannel = EditorGUILayout.Popup(new GUIContent("遮罩通道"), settings.receiveMaskChannel, new[] { "R", "G", "B", "A", "1-R", "1-G", "1-B", "1-A" });
                    settings.receiveMaskStrength = EditorGUILayout.Slider(new GUIContent("遮罩强度"), settings.receiveMaskStrength, 0f, 1f);
                }

                showAdvanced = EditorGUILayout.Foldout(showAdvanced, "高级（烘焙分辨率 / 偏移）");
                if (showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    var resolutions = new[] { 64, 128, 256, 512, 1024, 2048 };
                    var resolutionNames = new[] { "64", "128", "256", "512", "1024", "2048" };
                    var resolutionIndex = Mathf.Max(0, System.Array.IndexOf(resolutions, settings.shadowResolution));
                    resolutionIndex = EditorGUILayout.Popup(new GUIContent("烘焙分辨率", "256 够用；2048 = 4M 条射线，明显更慢但边缘更干净"), resolutionIndex, resolutionNames);
                    settings.shadowResolution = resolutions[Mathf.Clamp(resolutionIndex, 0, resolutions.Length - 1)];
                    settings.shadowBias = EditorGUILayout.Slider(new GUIContent("阴影偏移 Bias", "出现自阴影痤疮（条纹状噪点）时调大"), settings.shadowBias, 0f, 0.2f);
                    EditorGUI.indentLevel--;
                }
            }

            if (GUI.changed && settings.lightSource != null) Apply(settings, materials);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            if (settings.lightSource == null)
                EditorGUILayout.HelpBox("请先指定一盏光源（Light Source）。烘焙时只读取它的方向/颜色，不会真的把它挂到 avatar 上。", MessageType.Warning);

            int skipped;
            CollectTargetMaterials(settings, out skipped);
            EditorGUILayout.LabelField("作用范围内的 NonToon 材质", materials.Count + (skipped > 0 ? "（另有 " + skipped + " 个被别的光源编号占用，已跳过）" : ""));
            using (new EditorGUI.DisabledScope(settings.lightSource == null || materials.Count == 0))
            {
                if (GUILayout.Button("只同步参数（不烘焙）", GUILayout.Height(22)))
                    Apply(settings, materials);
                if (GUILayout.Button("烘焙自阴影并写入材质", GUILayout.Height(26)))
                {
                    var result = NTSelfShadowBaker.Bake(settings.lightSource, settings.GetComponentsInChildren<Renderer>(true), settings.shadowResolution, "Assets/NonToonSelfLight");
                    settings.bakedShadowMap = result.map;
                    EditorUtility.SetDirty(settings);
                    Apply(settings, materials, result);
                    lastSummary = "烘焙完成：" + result.map.name + "（" + result.resolution + "² = " + result.texels + " 像素，命中 " + result.hits + "，" + result.meshCount + " 个网格）\n阴影贴图：" + result.path;
                }
            }

            EditorGUILayout.HelpBox(
                "说明：烘焙模式下本组件运行时没有任何行为 —— 所有数据都写进材质属性，烘焙完可以把它删掉。\n" +
                "PCSS 软阴影与「阴影距离」的思路参考 nHaruka 的 PCSS4VRC（真实影システム），但换成不需要实时光的着色器实现。\n" +
                "改了姿态/网格/光源方向后需要重新烘焙 —— 想要影子跟着姿势实时变，请切到「实时光源」模式。",
                MessageType.None);
        }

        // ---------------------------------------------------------------- 实时模式
        private void DrawRealtime(NTSelfLight settings, Renderer[] renderers, List<Material> materials)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("实时光源", EditorStyles.boldLabel);

            settings.realtimeLightParent = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("挂载节点", "留空 = 挂在本组件所在节点"), settings.realtimeLightParent, typeof(Transform), true);
            settings.spotAngle = EditorGUILayout.Slider(new GUIContent("聚光灯角度"), settings.spotAngle, 1f, 179f);
            settings.lightRange = EditorGUILayout.Slider(new GUIContent("照射范围（米）", "只照自己就调小，别烧到别人"), settings.lightRange, 0.1f, 20f);
            // LayerMask 用 SerializedProperty 画：Unity 才会给出正确的「层级多选」下拉
            // （EditorGUILayout.LayerMaskField 在这个 Unity 版本里不存在；用 MaskField + 名字数组会踩
            //   "选项序号 != 层序号" 的老坑）
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("realtimeCullingMask"),
                new GUIContent("Culling Mask", "默认第 10 层 PlayerLocal = VRChat 里本地玩家所在的层，只照自己"));
            serializedObject.ApplyModifiedProperties();
            settings.realtimeRenderMode = (LightRenderMode)EditorGUILayout.EnumPopup(
                new GUIContent("渲染模式", "ForcePixel/Important = 强制逐像素，阴影质量最好"), settings.realtimeRenderMode);

            var warnings = NTSelfRealtimeLight.Warnings(settings);
            foreach (var w in warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);

            EditorGUILayout.Space();
            if (GUILayout.Button("创建 / 同步实时光源", GUILayout.Height(26)))
            {
                var light = NTSelfRealtimeLight.Sync(settings, true);
                Apply(settings, materials);
                lastSummary = "实时光源：" + (light != null ? light.name : "创建失败")
                    + "（" + renderers.Length + " 个 Renderer 已确保 Receive Shadows / Cast Shadows 打开）";
            }
            using (new EditorGUI.DisabledScope(settings.realtimeLight == null))
            {
                if (GUILayout.Button("移除实时光源", GUILayout.Height(22)))
                {
                    if (settings.realtimeLight != null)
                    {
                        Undo.DestroyObjectImmediate(settings.realtimeLight.gameObject);
                        settings.realtimeLight = null;
                        EditorUtility.SetDirty(settings);
                    }
                }
            }

            EditorGUILayout.HelpBox(
                "实时光源模式（参考 PCSS4VRC 的做法）：影子是**实时**的，改姿势立刻变，不需要烘焙。\n" +
                "需要注意：\n" +
                "① 它会占 VRChat 性能等级的 Lights 计数（PC 上 Lights = 1 → 最高 Poor；不在意等级就无所谓）；\n" +
                "② 影子能不能看到取决于**观看者**的 Shadow Quality 设置，别人关掉阴影就看不到；\n" +
                "③ 多个玩家的这种光会互相叠加，人挤人时可能把 avatar 照白（PCSS4VRC 也有这个已知问题）；\n" +
                "④ Quest 端实时光基本不可用。\n" +
                "⑤ 这个模式下 NonToon 材质里的「自有光源」会被关掉（_UseSelfLight = 0），否则会双份打光。\n" +
                "⑥ 光只照自己靠 Culling Mask；Unity 的层在 VRChat 里是固定的：Player(9) = 其他玩家，PlayerLocal(10) = 本地玩家。",
                MessageType.None);
        }

        // [NT-PERF] 把"每像素采样预算"直接摊开给用户看 —— 消耗可见才会被在意。
        internal static string ShadowBudgetText(NTSelfLight settings)
        {
            return ShadowBudgetText(settings.pcss, settings.pcssQuality);
        }

        // ④ 组件也用它（同一个说法，别写两遍）
        internal static string ShadowBudgetText(bool pcss, NTPCSSQuality quality)
        {
            if (!pcss) return "每像素自阴影采样：**1 次**（硬阴影）。再想省就把「强度 Strength」拉到 0 → **0 次**采样。";
            var taps = PCSSBodyTaps(quality);
            return "每像素自阴影采样：**" + (taps.Item1 + taps.Item2) + " 次**（" + taps.Item1 + " blocker + " + taps.Item2 + " PCF）。\n"
                + "且只在「光源包围盒内」且「阴影距离之内」才发生；把「强度」拉到 0 就是 **0 次**采样。\n"
                + "烘焙分辨率高于 256² 时，采样数会按分辨率放大（上限 64 + 32 = 极高档的预算）—— "
                + "这是 0.3.9 起为了让「半影的世界宽度不随烘焙分辨率变化」而加的；256² 下与以前完全一致。";
        }

        internal static Tuple<int, int> PCSSBodyTaps(NTPCSSQuality quality)
        {
            switch (quality)
            {
                case NTPCSSQuality.Medium: return Tuple.Create(12, 24);
                case NTPCSSQuality.High: return Tuple.Create(20, 40);
                case NTPCSSQuality.Ultra: return Tuple.Create(32, 64);
                default: return Tuple.Create(8, 12);
            }
        }

        // [NT-FEAT 20] 自由度：
        //   · 作用范围来自组件自己（可指向别的节点），不再固定用 transform.root
        //   · 材质可以声明"只接受哪一路光源"（_SelfLightId），编号不匹配就不写它
        //     ⇒ 同一个模型上可以并存多路自有光，各照各的
        private static List<Material> CollectTargetMaterials(NTSelfLight settings)
        {
            int skipped;
            return CollectTargetMaterials(settings, out skipped);
        }

        private static List<Material> CollectTargetMaterials(NTSelfLight settings, out int skipped)
        {
            var result = new HashSet<Material>();
            var skip = new HashSet<Material>();
            foreach (var renderer in settings.TargetRenderers())
            {
                if (renderer == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty("_UseSelfLight")) continue;
                    var wants = material.HasProperty("_SelfLightId") ? material.GetInteger("_SelfLightId") : 0;
                    if (settings.lightId != 0 && wants != 0 && wants != settings.lightId) { skip.Add(material); continue; }
                    result.Add(material);
                }
            }
            skipped = skip.Count;
            return result.ToList();
        }

        private static void Apply(NTSelfLight settings, List<Material> materials, NTSelfShadowBaker.Result baked = null)
        {
            var realtime = settings.mode == NTSelfLightMode.RealtimeLight;
            foreach (var material in materials)
            {
                // 实时模式：交给真光源，关掉着色器侧私有光，避免双份
                if (realtime)
                {
                    NTSelfLightWriter.SetNumber(material, "_UseSelfLight", 0f);
                    NTSelfLightWriter.SetNumber(material, "_SelfLightOnly", 0f);
                    EditorUtility.SetDirty(material);
                    continue;
                }

                // [NT-REFACTOR 0.5.0] 写入逻辑统一在 NTSelfLightWriter（④ 组件共用同一份）。
                // 注意 setLimits = false：② 从不写 _LightMinLimit / _LightMaxLimit（那是 ③/④ 的职责），
                // 保持 0.4.x 的行为不变。
                var s = new NTSelfLightWriter.Settings
                {
                    enabled = true,
                    onlyThisLight = settings.onlyThisLight,
                    color = settings.color,
                    useTemperature = settings.useColorTemperature,
                    temperature = settings.temperature,
                    intensity = settings.intensity,
                    towardLight = settings.Direction,
                    setLimits = false,
                    matchAmbientAmount = settings.matchWorldColor,
                    matchDirectionAmount = settings.matchWorldDirection,
                    pcss = settings.pcss,
                    quality = (int)settings.pcssQuality,
                    softness = settings.softness,
                    density = settings.shadowDensity,
                    clamp = settings.shadowClamp,
                    distance = settings.shadowDistance,
                    bias = settings.shadowBias,
                    shadowStrength = settings.shadowStrength,
                    receiveMask = settings.receiveMask,
                    receiveMaskChannel = settings.receiveMaskChannel,
                    receiveMaskStrength = settings.receiveMaskStrength,
                    lightId = settings.lightId,
                    stampId = settings.stampId,
                };

                NTSelfLightWriter.Write(material, s, baked);
                if (baked == null) NTSelfLightWriter.WriteExistingMap(material, settings.bakedShadowMap);

                EditorUtility.SetDirty(material);
            }
            AssetDatabase.SaveAssets();
        }
    }
}
