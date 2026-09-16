// [NT-FEAT 20] ⑥ 实时自阴影 —— 组件的本地化检视面板。
//
// 为什么要有这个面板（而不是只靠 `[Tooltip]`）：
//   Unity 的 `[Tooltip]` / `[Range]` 是**编译期常量特性**，没法本地化；
//   而 ⑥ 的旋钮现在都在**组件**上（不在材质里），所以只有在 `CustomEditor` 里
//   自己画标签与提示，才能真正做到"中文界面 + 中文提示"。
//
// 面板还承担两件事：
//   1. 把"必然 Poor / Quest 不可用 / 必须 Hard / 影质量取决于观看者"这些**硬约束**顶到脸上；
//   2. 直接给出「安装到 avatar / 卸载」按钮，走 MA 声明式路径（不碰用户资产）。
using System;
using UnityEditor;
using UnityEngine;
using NonToon;
using LilToonToNonToonConverter;   // NTL10n 在这个命名空间里

namespace NonToonTools
{
    [CustomEditor(typeof(NTSelfRealtimeShadow))]
    internal sealed class NTSelfRealtimeShadowEditor : Editor
    {
        static string L(string s) { return NTL10n.L(s); }

        // ⛔ 这里**必须**逐字段自己画，不能用 `DrawDefaultInspector()`（原来就是那么写的）：
        //   默认绘制会用 Unity 的"字段名美化"当标签 ⇒ 显示成英文的 `Target Light` / `Extra Casters` /
        //   `Render Every Frame` / `Light Size` / `Pcss On` …
        //   而且它是**不可控**的：实测同一次绘制里 `resolution` / `softness` 被 Unity 的中文本地化
        //   翻成了「分辨率」「柔和度」，其余多词字段却还是英文 ⇒ 中英混排。
        //   两列：{ 字段名, 标签的英文原文, 提示的英文原文 }（都由 `NTL10n.L` 查 `lang/zh-Hans.po`）。
        static readonly string[,] LightGroup =
        {
            { "targetLight",    "Shadow light",            "The Spot light that provides the shadow. Leave empty to auto-find a Spot in children." },
            { "lightIntensity", "Light intensity",         "Light intensity driven by this component (written back to Light.intensity). Also exposed to the in-game Expression Menu." },
            { "lightColor",     "Light color",             "Light color driven by this component." },
        };

        static readonly string[,] ShadowGroup =
        {
            { "pcssOn",      "Realtime PCSS",       "Master switch. The Expression Menu drives this field, so it can be toggled in game." },
            { "lightSize",   "Virtual light size",  "Penumbra size (texels at a 512 reference). Larger is softer. Self-occlusion on an avatar is only a few centimetres, so 32-128 is the useful range." },
            { "softness",    "Softness",            "Extra soft/hard multiplier on top of the light size." },
            { "quality",     "Quality 0-3",         "Sample budget: 0 = 8/12, 1 = 12/24, 2 = 16/32, 3 = 24/32. Higher is smoother and more expensive." },
            { "shadowFloor", "Shadow floor",        "How much of the light survives in fully shadowed areas (0 = pure black). NonToon then clamps env + light to [_LightMinLimit, _LightMaxLimit]; _LightMinLimit defaults to 0, so without a floor the model turns black where there is no ambient light." },
            { "biasMeters",  "Depth bias (m)",      "Depth offset in metres, used to suppress shadow acne on self-occlusion." },
        };

        static readonly string[,] DepthGroup =
        {
            { "resolution",           "Depth resolution",          "Depth map resolution. 512 is enough; 1024 is finer but costs more." },
            { "renderEveryFrame",     "Render depth every frame",  "Render the depth map every frame. Turn off for troubleshooting." },
            { "extraCasters",         "Extra casters",             "Extra renderers that should cast into the depth map (the avatar itself is collected automatically)." },
            { "searchRoot",           "Search root",               "Root used to scan for casters and target materials. Leave empty to use the topmost parent of this object." },
            { "disableUnityShadows",  "Disable Unity shadows",     "Turn the light's Unity realtime shadow off: the shadow is provided entirely by this component plus PCSS." },
        };

        void DrawGroup(string title, string[,] rows)
        {
            EditorGUILayout.LabelField(L(title), EditorStyles.boldLabel);
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                var p = serializedObject.FindProperty(rows[i, 0]);
                if (p == null)
                {
                    EditorGUILayout.HelpBox("missing field: " + rows[i, 0], MessageType.Error);
                    continue;
                }
                EditorGUILayout.PropertyField(p, new GUIContent(L(rows[i, 1]), L(rows[i, 2])));
            }
            EditorGUILayout.Space();
        }

        public override void OnInspectorGUI()
        {
            var c = (NTSelfRealtimeShadow)target;
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                L("This light must be anchored in front of / above the avatar, otherwise no self-occlusion " +
                  "is visible and the shadow looks like it does nothing. Use the Install button to build " +
                  "a properly anchored rig (Neck position + Chest aim)."),
                MessageType.Info);

            EditorGUILayout.Space();
            DrawGroup("Light", LightGroup);
            DrawGroup("Shadow (PCSS)", ShadowGroup);
            DrawGroup("Depth map", DepthGroup);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.LabelField(L("Hard limits (VRChat)"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L("Realtime lights force the avatar to the Poor performance rank (Lights = 1) and are NOT " +
                  "available on Quest. Shadow quality depends on the viewer's own Unity quality settings. " +
                  "Do not use Unity's Soft shadows: this component turns the light's Unity shadow off on " +
                  "purpose and provides the shadow itself."),
                MessageType.Warning);

            if (c.shadowFloor < 0.05f)
                EditorGUILayout.HelpBox(
                    L("Shadow Floor is 0: fully shadowed areas will go pure black unless the material's " +
                      "Light Min Limit is above 0 or there is ambient light. Raise it to 0.2-0.3 if the " +
                      "model turns black."),
                    MessageType.Warning);

            // ⚠️ 锥外区域**只能靠环境光 / 世界光**：环境光被设成黑色时，光源照不到的地方会死黑。
            //    这是 ⑥ 最容易被误判成"着色器坏了"的情况（我们自己就在实机测试里踩过）。
            var amb = RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Flat
                    ? RenderSettings.ambientLight : RenderSettings.ambientSkyColor;
            if (amb.maxColorComponent < 0.02f)
                EditorGUILayout.HelpBox(
                    L("The scene ambient light is near black. Areas outside this spot light cone have no " +
                      "other light source, so they render pure black. That is lighting setup, not the shadow. " +
                      "Restore ambient in the Lighting window, or widen Spot Angle and Range so the cone " +
                      "covers the whole body."),
                    MessageType.Error);

            // ⑥（真实光）与 ⑤（亮度调整）是**互补**的，不是二选一：
            //   ⑥ 负责"这盏灯照到的地方 + 它的影子"，而整体亮度（尤其是灯照不到的暗处）
            //   由材质的 `_LightMinLimit` 抬起来。NonToon 的顺序是
            //   `clamp(env + lightSum, _LightMinLimit, _LightMaxLimit)` —— 我们的影是在 lightSum 上乘的，
            //   夹紧发生在**之后** ⇒ 两者天然兼容、不会互相打架。
            EditorGUILayout.HelpBox(
                L("Too dark overall, especially where the light does not reach? Raise the material's " +
                  "Light Min Limit with Tools > NonToon > Brightness adjust (5). It lifts the whole " +
                  "result after this shadow has been applied, so the two compose instead of fighting."),
                MessageType.Info);

            if (!NTModularAvatarBridge.IsAvailable)
                EditorGUILayout.HelpBox(
                    L("Modular Avatar was not found in this project. The rig can still be built, but the " +
                      "Expression Menu will not be wired automatically. Install Modular Avatar (VCC) and " +
                      "run Install again to use the declarative path."),
                    MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    L("Modular Avatar detected: the menu is installed declaratively (MergeAnimator / " +
                      "Parameters / MenuItem / MenuInstaller). Your FX controller, ExpressionParameters " +
                      "and ExpressionsMenu are never modified."),
                    MessageType.Info);

            EditorGUILayout.Space();
            var root = FindAvatarRoot(c.gameObject);
            using (new EditorGUI.DisabledScope(root == null))
            {
                if (GUILayout.Button(L("Install / update the rig on this avatar")))
                {
                    var r = NTSelfRealtimeSetup.Install(root, false, c.lightIntensity, c.lightSize,
                                                        c.softness, 6f, 90f, true);
                    NTSelfRealtimeMenu.Report(r, L("Install realtime self shadow"));
                }
            }
            using (new EditorGUI.DisabledScope(root == null || root.transform.Find(NTSelfRealtimeSetup.RigName) == null))
            {
                if (GUILayout.Button(L("Uninstall from this avatar")))
                {
                    var r = NTSelfRealtimeSetup.Uninstall(root);
                    NTSelfRealtimeMenu.Report(r, L("Uninstall realtime self shadow"));
                }
            }
            if (root == null)
                EditorGUILayout.HelpBox(L("This component is not under a scene-root avatar; " +
                                          "put it under the avatar or use Tools/NonToon menu."), MessageType.Warning);
        }

        static GameObject FindAvatarRoot(GameObject go)
        {
            var t = go.transform;
            while (t.parent != null) t = t.parent;
            return t.gameObject;
        }
    }
}
