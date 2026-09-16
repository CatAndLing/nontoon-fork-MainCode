using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    // [NT-L10N] 字段标签自己画（Unity 默认用 C# 字段名 nicify 出来是英文）。
    [CustomEditor(typeof(NTAvatarLight))]
    public sealed class NTAvatarLightEditor : Editor
    {
        [MenuItem("GameObject/NonToon/① 创建 Avatar 光源（不用改材质）", true, 50)]
        private static bool ValidateCreate()
        {
            return Selection.gameObjects.Any(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
        }

        [MenuItem("GameObject/NonToon/① 创建 Avatar 光源（不用改材质）", false, 50)]
        private static void CreateFromSelection()
        {
            var root = Selection.gameObjects.FirstOrDefault(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
            if (root == null) { Debug.LogWarning("[NTAvatarLight] 请先选中 avatar 的根节点。"); return; }
            NTAvatarLightCreator.Create(root);
        }

        [MenuItem("Tools/NonToon/① 创建 Avatar 光源（不用改材质）")]
        private static void CreateFromToolsMenu()
        {
            CreateFromSelection();
        }

        public override void OnInspectorGUI()
        {
            var settings = (NTAvatarLight)target;

            EditorGUILayout.HelpBox(
                "✅ 这类组件不需要在材质球上做任何事：它挂的是一盏真正的 Unity Light，\n" +
                "lilToon / Poiyomi / NonToon / Standard 任何着色器都会照到它。\n" +
                "（另一类是需要材质启用的「自有光源」，见 Add Component ▸ NonToon ▸ ②）",
                MessageType.None);
            EditorGUILayout.LabelField("光源", EditorStyles.boldLabel);
            settings.target = (Light)EditorGUILayout.ObjectField(new GUIContent("光源组件", "由创建器生成的那盏灯"), settings.target, typeof(Light), true);
            settings.color = EditorGUILayout.ColorField(new GUIContent("颜色"), settings.color);
            settings.useColorTemperature = EditorGUILayout.Toggle(new GUIContent("使用色温", "交给 Unity 的 Light.colorTemperature（6500K ≈ 白）"), settings.useColorTemperature);
            using (new EditorGUI.DisabledScope(!settings.useColorTemperature))
                settings.temperature = EditorGUILayout.Slider(new GUIContent("色温（K）"), settings.temperature, 1000f, 20000f);
            settings.intensity = EditorGUILayout.Slider(new GUIContent("光强"), settings.intensity, 0f, 8f);
            settings.spotAngle = EditorGUILayout.Slider(new GUIContent("聚光灯角度", "越小越集中、影子越干净"), settings.spotAngle, 1f, 179f);
            settings.range = EditorGUILayout.Slider(new GUIContent("照射范围（米）", "只照自己就调小，别烧到别人"), settings.range, 0.1f, 20f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("阴影", EditorStyles.boldLabel);
            settings.shadows = (LightShadows)EditorGUILayout.EnumPopup(new GUIContent("阴影类型", "Soft = 软阴影（推荐）；None = 不要影子"), settings.shadows);
            using (new EditorGUI.DisabledScope(settings.shadows == LightShadows.None))
            {
                settings.shadowStrength = EditorGUILayout.Slider(new GUIContent("阴影浓度"), settings.shadowStrength, 0f, 1f);
                settings.shadowBias = EditorGUILayout.Slider(new GUIContent("阴影偏移", "出现条纹状噪点（阴影痤疮）时调大"), settings.shadowBias, 0f, 0.2f);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("只照自己", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("cullingMask"),
                new GUIContent("Culling Mask", "默认第 10 层 PlayerLocal。VRChat 里只有本地玩家自己的 avatar 在这一层"));
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("只照自己（PlayerLocal）", GUILayout.Height(20)))
                    settings.cullingMask = 1 << NTAvatarLightCreator.PlayerLocalLayer;
                if (GUILayout.Button("照所有层（不推荐）", GUILayout.Height(20)))
                    settings.cullingMask = ~0;
            }
            settings.renderMode = (LightRenderMode)EditorGUILayout.EnumPopup(new GUIContent("渲染模式", "ForcePixel / Important = 强制逐像素"), settings.renderMode);
            settings.directionSource = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("方向来源", "留空 = 用本节点朝向；想让 PhysBone 控制方向，把本节点挂到被 PhysBone 驱动的骨骼下，或在这里指定那根骨骼"),
                settings.directionSource, typeof(Transform), true);

            var warnings = NTAvatarLightCreator.Warnings(settings);
            foreach (var w in warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            if (GUILayout.Button("同步到光源组件", GUILayout.Height(24)))
            {
                NTAvatarLightCreator.Sync(settings, true);
            }
            if (GUILayout.Button("移除光源插件", GUILayout.Height(20)))
            {
                NTAvatarLightCreator.Remove(settings);
                return;
            }

            EditorGUILayout.HelpBox(
                "这是一个**着色器无关**的实时光源插件：lilToon / Poiyomi / NonToon / Standard 都能用，它不碰任何材质属性。\n" +
                "影子是 Unity 的实时阴影（跟着姿势变），不需要烘焙。\n\n" +
                "注意：\n" +
                "① 占 VRChat 性能等级的 Lights 计数（PC 上 Lights = 1 → 最高 Poor）；\n" +
                "② 影子能不能看到取决于**观看者**的 Shadow Quality；Unity 的 Shadow Distance 之外没有影子；\n" +
                "③ 多个玩家的这种光会互相叠加，人挤人时可能把 avatar 照白；\n" +
                "④ Quest 端实时光基本不可用。\n\n" +
                "想用 ExpressionMenu 控制开关 / 光强 / 颜色：\n" +
                "  在 FX 层建一个 float 参数 → 用 AnimationClip 写 Light 的 intensity / color，\n" +
                "  再挂到 ExpressionMenu 上即可（这一步要 VRCSDK，所以本插件不代劳，避免给包加 SDK 依赖）。\n" +
                "想用 PhysBone 控制方向：把本节点挂到被 PhysBone 驱动的那根骨骼下，或用「方向来源」指给它。",
                MessageType.None);
        }
    }
}
