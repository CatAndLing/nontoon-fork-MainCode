using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 22] ⑤「亮度调整」的检视面板。
    //
    // 关键取舍：装了 Modular Avatar 就**只生成资产 + 挂 MA 组件**（非破坏，删组件即回滚）；
    // 没装才回退到"直接改 avatar 的 FX 控制器 / 参数 / 菜单"（与 ③ 插件同一套代码）。
    // 两条路都会在面板上写清楚，不让用户猜自己的资产被动过没有。
    [CustomEditor(typeof(NTLightAdjust))]
    public sealed class NTLightAdjustEditor : Editor
    {
        static readonly string[] KnownProps = { "_LightMinLimit", "_LightMaxLimit", "_SelfLightIntensity", "__custom" };
        static readonly string[] KnownNames = { "_LightMinLimit（亮度下限，防煤）", "_LightMaxLimit（亮度上限，压过曝）",
                                                "_SelfLightIntensity（自有光强度）", "自定义…" };

        [MenuItem("GameObject/NonToon/⑤ 亮度调整（构建时固定 / 游戏内可调）", true, 53)]
        private static bool ValidateCreate()
        {
            return Selection.gameObjects.Any(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
        }

        [MenuItem("GameObject/NonToon/⑤ 亮度调整（构建时固定 / 游戏内可调）", false, 53)]
        private static void Create()
        {
            var root = Selection.gameObjects.FirstOrDefault(go => go != null && go.GetComponentsInChildren<Renderer>(true).Any());
            if (root == null) { Debug.LogWarning("[NTLightAdjust] 请先选中要作用的节点（或它的根节点）。"); return; }
            var existing = root.GetComponent<NTLightAdjust>();
            var comp = existing != null ? existing : Undo.AddComponent<NTLightAdjust>(root);
            if (comp.targetRoot == null && root.transform != comp.transform) comp.targetRoot = root.transform;
            Selection.activeGameObject = comp.gameObject;
            Debug.Log("[NTLightAdjust] 已挂好「亮度调整」。选模式后点生成即可 —— "
                + "装了 Modular Avatar 的话走非破坏路径（不改你原有的控制器/菜单）。");
        }

        [MenuItem("Tools/NonToon/⑤ 亮度调整（构建时固定 / 游戏内可调）")]
        private static void CreateFromTools() { Create(); }

        public override void OnInspectorGUI()
        {
            var s = (NTLightAdjust)target;

            EditorGUILayout.HelpBox(
                "让 avatar 不再是「一块煤」，并且（可选）让玩家在 Action Menu 里随手调亮度。\n" +
                "· 构建时固定：直接写材质属性，零 Animator、零参数、不需要 VRCSDK\n" +
                "· 游戏内可调：生成 Animator 曲线 + 径向菜单（装了就自动走 Modular Avatar 非破坏路径）\n" +
                "运行时本组件没有任何行为；数据都在生成的资产/材质里。",
                MessageType.None);

            var maAvailable = NTModularAvatarBridge.IsAvailable;
            var maVersion = maAvailable ? NTModularAvatarBridge.Version : null;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("模式", EditorStyles.boldLabel);
            s.mode = (NTLightAdjust.Mode)EditorGUILayout.EnumPopup(
                new GUIContent("用法", "构建时固定 = 零依赖最省；游戏内可调 = 生成 Animator + 径向菜单"),
                s.mode);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("作用范围", EditorStyles.boldLabel);
            s.targetRoot = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("作用节点", "留空 = 本组件所在节点"), s.targetRoot, typeof(Transform), true);
            s.includeChildren = EditorGUILayout.Toggle(new GUIContent("包含子节点"), s.includeChildren);

            var idx = Array.IndexOf(KnownProps, s.propertyName);
            if (idx < 0) idx = KnownProps.Length - 1;
            idx = EditorGUILayout.Popup(new GUIContent("驱动属性", "NonToon 与 lilToon 的亮度公式一致，_LightMinLimit 是防煤主力"),
                idx, KnownNames);
            s.propertyName = KnownProps[idx];
            if (s.propertyName == "__custom")
                s.customPropertyName = EditorGUILayout.TextField(new GUIContent("属性名"), s.customPropertyName);

            // ---------------- 模式参数 ----------------
            if (s.mode == NTLightAdjust.Mode.FixedAtBuild)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("构建时固定", EditorStyles.boldLabel);
                s.fixedValue = EditorGUILayout.Slider(new GUIContent("写入值"), s.fixedValue, 0f, 1f);
                EditorGUILayout.HelpBox("推荐：" + s.EffectiveProperty + " = 0.35（零采样代价，全黑地图里也不会变煤）。",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("游戏内可调", EditorStyles.boldLabel);
                s.valueAtZero = EditorGUILayout.Slider(new GUIContent("径向 0（最暗）"), s.valueAtZero, 0f, 1f);
                s.valueAtOne = EditorGUILayout.Slider(new GUIContent("径向 1（最亮）"), s.valueAtOne, 0f, 1f);
                s.initialValue = EditorGUILayout.Slider(new GUIContent("进游戏初始值", "0 = 最暗端，1 = 最亮端"), s.initialValue, 0f, 1f);
                s.parameterName = EditorGUILayout.TextField(new GUIContent("参数名"), s.parameterName);
                s.synced = EditorGUILayout.Toggle(
                    new GUIContent("同步给别人看", "勾上占 8 bit 同步内存（Float）；不勾则只有自己能调"), s.synced);
                s.menuLabel = EditorGUILayout.TextField(new GUIContent("菜单名"), s.menuLabel);
                s.outputFolder = EditorGUILayout.TextField(new GUIContent("生成目录"), s.outputFolder);
            }

            // ---------------- 生成路径提示 ----------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("生成路径", EditorStyles.boldLabel);
            if (s.mode == NTLightAdjust.Mode.FixedAtBuild)
            {
                EditorGUILayout.HelpBox("只写材质属性：不生成 Animator、不碰 avatar 的任何资产。", MessageType.None);
            }
            else if (maAvailable)
            {
                EditorGUILayout.HelpBox(
                    "检测到 Modular Avatar" + (string.IsNullOrEmpty(maVersion) ? "" : " " + maVersion)
                    + " ⇒ 走非破坏路径：\n"
                    + "只生成曲线剪辑 + 一个独立控制器，然后往 avatar 根节点挂 MA 的四个组件\n"
                    + "（Merge Animator / Parameters / Menu Item / Menu Installer）。\n"
                    + "不会修改你原有的 FX 控制器、ExpressionParameters 与菜单；删掉本组件即回滚。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "没有检测到 Modular Avatar ⇒ 回退到直接写入路径：会把动画层写进 avatar 现有的 FX 控制器，\n" +
                    "并直接修改 ExpressionParameters 与 ExpressionsMenu（与 ③ 插件相同）。\n" +
                    "想要非破坏的话，装一个 Modular Avatar（免费）再点生成即可。",
                    MessageType.Warning);
            }

            Collect(s, out var materials, out var renderers);
            EditorGUILayout.LabelField("命中的材质 / Renderer", materials.Count + " / " + renderers.Count);
            if (materials.Count == 0)
                EditorGUILayout.HelpBox("作用范围里没有包含属性「" + s.EffectiveProperty + "」的材质 —— " +
                                        "先确认这里用的是 NonToon 材质（或换一个属性名）。", MessageType.Warning);

            using (new EditorGUI.DisabledScope(materials.Count == 0))
            {
                var label = s.mode == NTLightAdjust.Mode.FixedAtBuild ? "写入材质（不生成 Animator）" : "生成（含游戏内径向）";
                if (GUILayout.Button(label, GUILayout.Height(28))) Generate(s, materials, renderers);
            }

            if (!string.IsNullOrEmpty(s.lastSummary))
                EditorGUILayout.HelpBox(s.lastSummary, MessageType.Info);

            if (s.mode == NTLightAdjust.Mode.AdjustableInGame && !string.IsNullOrEmpty(s.parameterName)
                && !s.parameterName.StartsWith("NT_"))
                EditorGUILayout.HelpBox("建议参数名以 NT_ 开头（避免与别的改模工具撞名，例如 LLC 用的是 Kik/… 系列）。",
                    MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "与常用改模工具的配合：\n" +
                "· Modular Avatar：走非破坏路径；参数名会被 MA 注册，菜单项由 MA 装进根菜单\n" +
                "· AAO（Avatar Optimizer）：曲线对每个 Renderer 写同一个值，这是 AAO 网格合并支持的形式\n" +
                "· VRCFury：若同时用它的 Full Controller 接管 FX 层，行为由你决定 —— 两者都能工作，但别重复控制同一层\n" +
                "· 想改 UI 文案/图标：生成后在 MA Menu Item 上直接改（那是 MA 资产，不再需要本组件）",
                MessageType.None);
        }

        // ---------------------------------------------------------------- 收集
        // 命中规则比 ②/④ 宽松：只要材质**有**这个属性就纳入（所以 lilToon 材质也能用同一个组件调亮度）
        internal static void Collect(NTLightAdjust s, out List<Material> materials, out List<Renderer> renderers)
        {
            materials = new List<Material>();
            renderers = new List<Renderer>();
            var prop = s.EffectiveProperty;
            foreach (var rd in s.TargetRenderers())
            {
                if (rd == null) continue;
                var hit = false;
                foreach (var m in rd.sharedMaterials)
                {
                    if (m == null || !m.HasProperty(prop)) continue;
                    hit = true;
                    if (!materials.Contains(m)) materials.Add(m);
                }
                if (hit) renderers.Add(rd);
            }
        }

        // ---------------------------------------------------------------- 生成
        internal static void Generate(NTLightAdjust s, List<Material> materials, List<Renderer> renderers)
        {
            s.lastGeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            s.lastMaterialCount = materials.Count;
            s.lastRendererCount = renderers.Count;
            s.lastUsedModularAvatar = false;
            s.lastControllerPath = null;
            var prop = s.EffectiveProperty;

            // ---- 构建时固定 ----
            if (s.mode == NTLightAdjust.Mode.FixedAtBuild)
            {
                foreach (var m in materials)
                {
                    NTSelfLightWriter.SetNumber(m, prop, s.fixedValue);
                    EditorUtility.SetDirty(m);
                }
                AssetDatabase.SaveAssets();
                s.lastSummary = "已把 " + prop + " = " + s.fixedValue.ToString("F3") + " 写入 " + materials.Count + " 个材质。\n"
                    + "没有生成任何 Animator / 参数，也没有修改 avatar 的资产。";
                EditorUtility.SetDirty(s);
                Debug.Log("[NTLightAdjust] " + s.lastSummary);
                return;
            }

            // ---- 游戏内可调 ----
            var avatarRoot = FindAvatarRoot(s);
            if (avatarRoot == null)
            {
                s.lastSummary = "找不到 avatar 根节点（需要有 VRCAvatarDescriptor）：请把本组件挂到 avatar 根节点下，"
                    + "或改用「构建时固定」模式。";
                EditorUtility.SetDirty(s);
                return;
            }

            var folder = string.IsNullOrEmpty(s.outputFolder) ? "Assets/NonToonGenerated" : s.outputFolder;

            // ⓪ **MA 现在是必需的**（2026-09-17 用户决定：需要菜单的功能一律走声明式）。
            //    我们**故意不留回退路径**：旧回退会直接改用户的 FX 控制器 / ExpressionParameters /
            //    ExpressionsMenu，已经在实机上造成过事故 —— 用户的 `Torao_FXLayer.controller` 里被塞进
            //    一个 `defaultWeight = 0` 的死层 + `NT_Light` 参数 + 菜单项，导致"游戏内亮度"永远拉不动，
            //    而用户完全不知道自己的资产被动过。
            if (!NTModularAvatarBridge.IsAvailable)
            {
                s.lastSummary = "本工程里没装 Modular Avatar，而 ⑤ 只走声明式路径（不会改你的资产）。\n"
                    + "请在 VCC 里装上 Modular Avatar（nadena.dev.modular-avatar）后重试。\n"
                    + "没装 MA 时仍然可以用「构建时固定」模式：它直接把值写进材质，不需要菜单。";
                s.lastUsedModularAvatar = false;
                EditorUtility.SetDirty(s);
                return;
            }

            // 1) 菜单**统一交给 `NTLightMenuSetup`**（一个「NonToon 光影」子菜单里放 5 个独立控制项）。
            //    ⛔ 本组件**不再自己生成控制器 / 菜单项** —— 否则会和子菜单里的「光照下限/上限」
            //    两条层同时动画 `_LightMinLimit`，谁后加载谁赢，还多出一个平级菜单项。
            //    仍然想要"只给自己看 / 自定义属性"的话，用下面的「构建时固定」模式。
            var r = NTLightMenuSetup.Install(avatarRoot, true);
            s.lastUsedModularAvatar = true;

            var lines = new List<string>();
            lines.Add("路径：Modular Avatar（非破坏）—— 菜单收在子菜单「" + NTLightMenuSetup.SubmenuLabel + "」里");
            lines.AddRange(r.Done.Select(x => "· " + x));
            lines.Add("本组件驱动的属性 " + prop + " 已由子菜单的「光照下限 / 光照上限」覆盖"
                    + (prop == "_LightMinLimit" || prop == "_LightMaxLimit" ? "" : "（⚠️ 你选的是自定义属性，子菜单不会驱动它；请用「构建时固定」）"));

            foreach (var w in r.Warnings) lines.Add("⚠️ " + w);
            foreach (var e in r.Errors) lines.Add("❌ " + e);

            s.lastSummary = string.Join("\n", lines.ToArray());
            EditorUtility.SetDirty(s);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[NTLightAdjust] 生成完成（MA 子菜单）：" + NTLightMenuSetup.SubmenuLabel);
        }

        // avatar 根节点 = 带 VRCAvatarDescriptor 的那一级（反射找，保持零 SDK 依赖）
        internal static GameObject FindAvatarRoot(NTLightAdjust s)
        {
            var start = s.targetRoot != null ? s.targetRoot : s.transform;
            var descType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", "VRC_AvatarDescriptor");
            if (descType == null) return null;
            for (var t = start; t != null; t = t.parent)
                if (t.GetComponent(descType) != null) return t.gameObject;
            return null;
        }

        static Type FindType(params string[] names)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var n in names)
                {
                    var t = asm.GetType(n, false);
                    if (t != null) return t;
                }
            return null;
        }
    }
}
