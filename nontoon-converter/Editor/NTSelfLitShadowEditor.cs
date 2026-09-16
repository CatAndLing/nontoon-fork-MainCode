using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 21] ④「自带光照与阴影」的检视面板。
    //
    // 这一步的意义：把「不需要世界光源」缩成**一个组件 + 一个按钮** ——
    // 不需要往场景里摆 Light，不需要碰材质面板，也不需要懂 ShaderCore 的模块。
    //
    // 与 ② 的关系：两者写的是同一批材质属性（写入逻辑共用 NTSelfLightWriter），
    // 所以不要在同一个目标上同时挂 ② 和 ④（面板会检测并警告）。
    [CustomEditor(typeof(NTSelfLitShadow))]
    public sealed class NTSelfLitShadowEditor : Editor
    {
        const string OutputFolder = "Assets/NonToonSelfLight";
        static string _lastSummary;
        static bool _showShadowAdvanced;

        [MenuItem("GameObject/NonToon/④ 自带光照与阴影（不需要地图光源）", true, 52)]
        private static bool ValidateCreate()
        {
            return Selection.gameObjects.Any(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
        }

        [MenuItem("GameObject/NonToon/④ 自带光照与阴影（不需要地图光源）", false, 52)]
        private static void Create()
        {
            var root = Selection.gameObjects.FirstOrDefault(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
            if (root == null) { Debug.LogWarning("[NTSelfLitShadow] 请先选中要作用的节点（或它的根节点）。"); return; }
            var existing = root.GetComponent<NTSelfLitShadow>();
            var comp = existing != null ? existing : Undo.AddComponent<NTSelfLitShadow>(root);
            if (comp.targetRoot == null && root.transform != comp.transform) comp.targetRoot = root.transform;
            Selection.activeGameObject = comp.gameObject;
            Debug.Log("[NTSelfLitShadow] 已挂好「自带光照与阴影」。点面板上的「一键配置并烘焙」即可 —— "
                + "不需要在场景里放任何灯。");
        }

        [MenuItem("Tools/NonToon/④ 自带光照与阴影（不需要地图光源）")]
        private static void CreateFromTools() { Create(); }

        public override void OnInspectorGUI()
        {
            var s = (NTSelfLitShadow)target;

            EditorGUILayout.HelpBox(
                "这是「不需要世界光源」的一体化组件：光的方向/颜色/强度、亮度下限、自阴影烘焙全在这里，\n" +
                "不用往场景里摆 Light，也不用逐项调材质面板。\n" +
                "要求：目标材质必须是 **NonToon** 且已启用「自有光源」开关（组件会自动开启）。\n" +
                "运行时本组件没有任何行为 —— 数据都写进材质，烘焙完可以删掉它。",
                MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("作用范围", EditorStyles.boldLabel);
            s.targetRoot = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("作用节点", "留空 = 本组件所在节点"), s.targetRoot, typeof(Transform), true);
            s.includeChildren = EditorGUILayout.Toggle(new GUIContent("包含子节点"), s.includeChildren);
            s.lightId = EditorGUILayout.IntSlider(
                new GUIContent("光源编号", "1–15；0 = 不限制。材质上的「只接受哪一路光源」填了别的编号时，本组件不会写它"),
                s.lightId, 0, 15);
            s.stampId = EditorGUILayout.Toggle(new GUIContent("把编号写到材质"), s.stampId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("光（虚拟光源，不需要场景里的灯）", EditorStyles.boldLabel);
            using (new EditorGUI.ChangeCheckScope())
            {
                s.yaw = EditorGUILayout.Slider(new GUIContent("环绕角", "0 = 角色正前方，+90 = 右侧"), s.yaw, -180f, 180f);
                s.pitch = EditorGUILayout.Slider(new GUIContent("俯仰角", "+90 = 头顶正上方"), s.pitch, -90f, 90f);

                var dir = s.LightDirection;
                EditorGUILayout.HelpBox(string.Format(
                    "光从 ({0:F2}, {1:F2}, {2:F2}) 来（世界空间，指向光源）；光线传播方向 = ({3:F2}, {4:F2}, {5:F2})。\n" +
                    "自阴影的落向也由这个方向决定，和地图里的太阳没有关系。",
                    dir.x, dir.y, dir.z, -dir.x, -dir.y, -dir.z), MessageType.None);

                s.color = EditorGUILayout.ColorField(new GUIContent("光源颜色"), s.color);
                s.useColorTemperature = EditorGUILayout.Toggle(new GUIContent("使用色温"), s.useColorTemperature);
                using (new EditorGUI.DisabledScope(!s.useColorTemperature))
                    s.temperature = EditorGUILayout.Slider(new GUIContent("色温（K）"), s.temperature, 1000f, 20000f);
                s.intensity = EditorGUILayout.Slider(new GUIContent("强度"), s.intensity, 0f, 8f);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("自给自足（不依赖地图光照）", EditorStyles.boldLabel);
            s.onlyThisLight = EditorGUILayout.Toggle(
                new GUIContent("只由它照亮", "丢弃世界光/环境光/光照贴图/天空盒反射 —— avatar 在任何地图里长得都一样"),
                s.onlyThisLight);
            s.raiseBrightnessFloor = EditorGUILayout.Toggle(
                new GUIContent("同时抬高亮度下限（防煤）", "全黑地图里不会变成一块煤。零采样代价"), s.raiseBrightnessFloor);
            using (new EditorGUI.DisabledScope(!s.raiseBrightnessFloor))
            {
                EditorGUI.indentLevel++;
                s.minLimit = EditorGUILayout.Slider(new GUIContent("亮度下限", "0 = 官方默认（暗图会黑），0.35 = 推荐值"),
                    s.minLimit, 0f, 1f);
                s.maxLimit = EditorGUILayout.Slider(new GUIContent("亮度上限", "压过曝；1 = 不限制"), s.maxLimit, 0f, 1f);
                EditorGUI.indentLevel--;
            }
            s.matchWorldColor = EditorGUILayout.Slider(
                new GUIContent("匹配世界光颜色", "让自带光染上地图环境光的颜色（0 = 完全用自己的颜色）"), s.matchWorldColor, 0f, 1f);
            s.matchWorldDirection = EditorGUILayout.Slider(
                new GUIContent("匹配世界光方向", "让自带光与自阴影跟随地图主光方向"), s.matchWorldDirection, 0f, 1f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("自阴影", EditorStyles.boldLabel);
            s.enableShadow = EditorGUILayout.Toggle(
                new GUIContent("烘焙自阴影", "关掉则只写光照参数，材质里的自阴影强度会被置 0"), s.enableShadow);
            using (new EditorGUI.DisabledScope(!s.enableShadow))
            {
                s.pcss = EditorGUILayout.Toggle(new GUIContent("PCSS 软阴影"), s.pcss);
                using (new EditorGUI.DisabledScope(!s.pcss))
                {
                    s.pcssQuality = (NTPCSSQuality)EditorGUILayout.EnumPopup(
                        new GUIContent("画质档位", "低 8+12（默认，最省）/ 中 12+24 / 高 20+40 / 极高 32+64 次采样"),
                        s.pcssQuality);
                    s.softness = EditorGUILayout.Slider(new GUIContent("柔度"), s.softness, 0f, 1f);
                    s.shadowClamp = EditorGUILayout.Slider(new GUIContent("硬化 Shadow Clamp"), s.shadowClamp, 0f, 1f);
                }
                EditorGUILayout.HelpBox(NTSelfLightEditor.ShadowBudgetText(s.pcss, s.pcssQuality), MessageType.None);
                s.shadowDensity = EditorGUILayout.Slider(new GUIContent("浓度 Density"), s.shadowDensity, 0f, 1f);
                s.shadowStrength = EditorGUILayout.Slider(new GUIContent("强度 Strength"), s.shadowStrength, 0f, 1f);
                s.shadowDistance = EditorGUILayout.Slider(new GUIContent("阴影距离", "0 = 不限制"), s.shadowDistance, 0f, 50f);
                s.receiveMask = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("接收遮罩", "逐像素控制哪里接收自阴影，白色 = 正常接收"), s.receiveMask, typeof(Texture2D), false);
                using (new EditorGUI.DisabledScope(s.receiveMask == null))
                {
                    s.receiveMaskChannel = EditorGUILayout.Popup(new GUIContent("遮罩通道"), s.receiveMaskChannel,
                        new[] { "R", "G", "B", "A", "1-R", "1-G", "1-B", "1-A" });
                    s.receiveMaskStrength = EditorGUILayout.Slider(new GUIContent("遮罩强度"), s.receiveMaskStrength, 0f, 1f);
                }

                _showShadowAdvanced = EditorGUILayout.Foldout(_showShadowAdvanced, "高级（烘焙分辨率 / 偏移）");
                if (_showShadowAdvanced)
                {
                    EditorGUI.indentLevel++;
                    var resolutions = new[] { 64, 128, 256, 512, 1024, 2048 };
                    var names = new[] { "64", "128", "256", "512", "1024", "2048" };
                    var idx = Mathf.Max(0, Array.IndexOf(resolutions, s.shadowResolution));
                    idx = EditorGUILayout.Popup(new GUIContent("烘焙分辨率", "256 够用；2048 = 4M 条射线，明显更慢"), idx, names);
                    s.shadowResolution = resolutions[Mathf.Clamp(idx, 0, resolutions.Length - 1)];
                    s.shadowBias = EditorGUILayout.Slider(
                        new GUIContent("阴影偏移 Bias", "出现条纹状噪点（痤疮）时调大；它是包围盒深度的比例，不是米"),
                        s.shadowBias, 0f, 0.2f);
                    EditorGUI.indentLevel--;
                }
            }

            // ---------------- 操作 ----------------
            var materials = CollectTargetMaterials(s);
            int skipped;
            CollectTargetMaterials(s, out skipped);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("作用范围内的 NonToon 材质",
                materials.Count + (skipped > 0 ? "（另有 " + skipped + " 个被别的光源编号占用，已跳过）" : ""));

            var conflicts = FindConflicts(s);
            foreach (var c in conflicts) EditorGUILayout.HelpBox(c, MessageType.Warning);

            using (new EditorGUI.DisabledScope(materials.Count == 0))
            {
                if (GUILayout.Button("只同步参数（不烘焙）", GUILayout.Height(22)))
                {
                    Apply(s, materials, null);
                    _lastSummary = "已同步 " + materials.Count + " 个材质的自有光参数"
                        + (s.enableShadow && s.bakedShadowMap != null ? "（沿用上次烘焙的阴影贴图）" : "（未烘焙自阴影）");
                }
                if (GUILayout.Button("一键配置并烘焙", GUILayout.Height(28)))
                {
                    BakeAndApply(s, materials);
                }
            }

            if (!string.IsNullOrEmpty(_lastSummary))
                EditorGUILayout.HelpBox(_lastSummary, MessageType.Info);

            EditorGUILayout.HelpBox(
                "说明：影子是**烘焙那一刻的姿态**。换了姿势、改了网格、或用 MA 的 Scale Adjuster / Merge Armature " +
                "调过比例位置之后，需要重新烘焙。\n" +
                "被 PhysBone 驱动或被 MA WorldFixedObject 固定到世界的部件**不要参与烘焙**（影子不会跟着动）。",
                MessageType.None);
        }

        // ---------------------------------------------------------------- 写入 / 烘焙
        internal static NTSelfLightWriter.Settings BuildSettings(NTSelfLitShadow s)
        {
            return new NTSelfLightWriter.Settings
            {
                enabled = true,
                onlyThisLight = s.onlyThisLight,
                color = s.color,
                useTemperature = s.useColorTemperature,
                temperature = s.temperature,
                intensity = s.intensity,
                towardLight = s.LightDirection,
                setLimits = s.raiseBrightnessFloor,
                minLimit = s.minLimit,
                maxLimit = s.maxLimit,
                matchAmbientAmount = s.matchWorldColor,
                matchDirectionAmount = s.matchWorldDirection,
                pcss = s.pcss,
                quality = (int)s.pcssQuality,
                softness = s.softness,
                density = s.shadowDensity,
                clamp = s.shadowClamp,
                distance = s.shadowDistance,
                bias = s.shadowBias,
                shadowStrength = s.enableShadow ? s.shadowStrength : 0f,
                receiveMask = s.receiveMask,
                receiveMaskChannel = s.receiveMaskChannel,
                receiveMaskStrength = s.receiveMaskStrength,
                lightId = s.lightId,
                stampId = s.stampId,
            };
        }

        internal static void Apply(NTSelfLitShadow s, List<Material> materials, NTSelfShadowBaker.Result baked)
        {
            var settings = BuildSettings(s);
            foreach (var m in materials)
            {
                NTSelfLightWriter.Write(m, settings, baked);
                if (baked != null) { /* 基与贴图已由 Write 写入 */ }
                else if (s.enableShadow) NTSelfLightWriter.WriteExistingMap(m, s.bakedShadowMap);
                else m.SetTexture("_SelfLightShadowMap", null);   // 关掉自阴影就别再让材质引用那张贴图
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
            s.lastMaterialCount = materials.Count;
        }

        internal static void BakeAndApply(NTSelfLitShadow s, List<Material> materials)
        {
            // 自阴影关掉时**不要烘焙**（探针抓到的 bug：以前会照样烘一张、还写进材质，
            // 于是"关掉自阴影"只把强度写成 0、贴图引用却留着 → 白白占显存）
            if (!s.enableShadow)
            {
                Apply(s, materials, null);
                EditorUtility.SetDirty(s);
                _lastSummary = "已写入光照参数（自阴影已关闭：强度 0、材质上的深度图引用已清除）\n材质："
                    + materials.Count + " 个";
                return;
            }

            var forward = s.LightForward;
            NTSelfLightWriter.OrthonormalBasis(forward, out var right, out var up);
            var result = NTSelfShadowBaker.Bake(forward, right, up, "SelfLit",
                s.TargetRenderers(), s.shadowResolution, OutputFolder);
            s.bakedShadowMap = result.map;
            s.lastBakedResolution = result.resolution;
            Apply(s, materials, result);
            EditorUtility.SetDirty(s);
            _lastSummary = string.Format(
                "烘焙完成：{0}（{1}² = {2} 像素，命中 {3}，{4} 个网格）\n材质：{5} 个\n阴影贴图：{6}",
                result.map.name, result.resolution, result.texels, result.hits, result.meshCount,
                materials.Count, result.path);
        }

        // ---------------------------------------------------------------- 材质收集
        internal static List<Material> CollectTargetMaterials(NTSelfLitShadow s)
        {
            int skipped;
            return CollectTargetMaterials(s, out skipped);
        }

        internal static List<Material> CollectTargetMaterials(NTSelfLitShadow s, out int skipped)
        {
            var result = new HashSet<Material>();
            var skip = new HashSet<Material>();
            foreach (var renderer in s.TargetRenderers())
            {
                if (renderer == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty("_UseSelfLight")) continue;
                    var wants = material.HasProperty("_SelfLightId") ? material.GetInteger("_SelfLightId") : 0;
                    if (s.lightId != 0 && wants != 0 && wants != s.lightId) { skip.Add(material); continue; }
                    result.Add(material);
                }
            }
            skipped = skip.Count;
            return result.ToList();
        }

        // 同一个目标上同时挂了 ② 和 ④ 会互相覆盖（两者写同一批属性）
        private static List<string> FindConflicts(NTSelfLitShadow s)
        {
            var list = new List<string>();
            var root = s.targetRoot != null ? s.targetRoot : s.transform;
            if (root.GetComponentInParent<NTSelfLight>() != null || root.GetComponentInChildren<NTSelfLight>(true) != null)
                list.Add("这个范围里还挂着 ②「自有光源」组件 —— 两者写的是同一批材质属性，会互相覆盖。" +
                         "请只保留一个（② 是手动模式，④ 是一键模式）。");
            foreach (var r in s.TargetRenderers())
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (!m.HasProperty("_UseSelfLight"))
                    {
                        list.Add("材质「" + m.name + "」不是 NonToon（找不到 Self Light 模块属性），会被跳过。" +
                                 "混用 lilToon / Poiyomi 时请只对本组件的 NonToon 部位生效。");
                        return list;
                    }
                }
            }
            return list;
        }
    }
}
