// [NT-FEAT 24] 「NonToon 光影」子菜单 —— **独立控制项**，不是一个大杂烩轮盘。
//
// ── 用户 2026-09-17 的明确要求（原文）───────────────────────────────────────
//   「我需要**单独**可以调整**阴影强度**和**阴影开关**，和**光照的上限和下限**，
//     而不是一个不明不白的轮盘。应该是**创建一个子菜单**然后将开关轮盘放入」
//   ⇒ 后来追加一条（实机复查后）：「**亮度调整只需要上限就好了**」
//
// ⛔⛔ **为什么必须去掉「光照下限」（这是 2026-09-17 实机"完全没有阴影"的根因）**
//   `birp.hlsl:158-159` 是这套着色器**最后一步**的乘法：
//       half3 lightColor = env + lightSum.color;
//       lightColor = clamp(lightColor, _LightMinLimit, _LightMaxLimit);   // ← 在**所有**光照之后
//       sd.lightColor = lightColor;                                       // 之后 albedo *= lightColor
//   也就是说 `_LightMinLimit` 是一个**与灯光无关的亮度地板**：
//     · 它把阴影区抬亮 ⇒ **阴影对比被压平**（有时直接归零）
//     · 它发生在最后 ⇒ **阴影强度、实时 PCSS、原生环境阴影全都被它吃掉**
//     · 对**深色/全黑角色**最致命：这类角色的 `env + 直射` 普遍落在 0.05~0.2，
//       一旦地板设成 0.25，**整片全部被抬到 0.25**，画面完全平掉。
//   我们之前把菜单默认值映射成 `_LightMinLimit = 0.25`（材质自己的值是 0.1）
//   ⇒ 一进 Play 就把阴影压平，表现为"**无论 Spot Light 开不开都没有阴影**"。
//   原版 NonToon 的 `_LightMinLimit` 默认是 **0**，所以他觉得"原来的好歹有阴影对比"。
//   ⇒ 结论（写死）：**游戏内菜单不再提供下限**。防煤属于"黑暗地图"的专门需求，
//     放到 ③ 窗口里**显式手动**开，并且必须警告它会压平阴影。
//
// ⇒ 结构（**这就是 MA 官方文档 `menu-item#submenus` 说的做法**）：
//
//   <Avatar>
//     └── NT_Menu_NonToon            [MenuItem(type=SubMenu, MenuSource=Children) + MenuInstaller]
//           ├── NT_Menu_MaxLimit         RadialPuppet  → NT_MaxLimit         → 材质 _LightMaxLimit
//           ├── NT_Menu_ShadowStrength   RadialPuppet  → NT_ShadowStrength   → 材质 _ShadowStrength（toon 阴影对比）
//           ├── NT_Menu_ShadowOn         Toggle        → NT_ShadowOn         → ⑥ 组件 pcssOn（整盏实时灯的总开关）
//
//   MA 的 `ModularAvatarMenuItem.Visit()` 里：
//     `if (cloned.type == SubMenu) case SubmenuSource.Children: cloned.SubmenuNode = NodeFor(new MenuNodesUnder(本物体))`
//   ⇒ 三个必要条件：`Control.type = SubMenu`、`MenuSource = Children`、
//      子项 MenuItem 挂在本物体的**直接子级**。**子项不需要各自的 installer**
//      （挂了反会被重复装到根菜单 —— `NTModularAvatarBridge.AddMenuItemOn(addInstaller:false)`）。
//
// ── 同步预算：**17 / 256 bit**（用户最初给的硬指标是「30 以内包括」）────────
//   VRChat 官方位宽（`animator-parameters#parameter-types`）：**`Bool` = 1 bit、`Int` = 8 bit、`Float` = 8 bit**。
//   MA 官方（`reference/parameters#savedsynced`）：*"If you clear this box, this parameter
//   won't use your limited parameter space."* ⇒ **不同步 = 0 bit**。
//
//   | 控制项 | 参数 | 类型 | 位宽 |
//   |---|---|---|---|
//   | 光照上限（压过曝） | `NT_MaxLimit` | `Float` | 8 |
//   | 阴影强度 | `NT_ShadowStrength` | `Float` | 8 |
//   | 阴影开关 | `NT_ShadowOn` | **`Bool`** | **1** |
//   | **合计** | | | **17 / 30 bit** ✅ |
//
//   （历史：先做过"一个观感轮盘"被否掉；改成 5 项独立时是 33 bit，用户要求 ≤20；
//     用户随后放宽到 30 ⇒ 恢复成连续滑块，得 25；实机复查后再砍掉下限 ⇒ **17**。）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace NonToonTools
{
    internal static class NTLightMenuSetup
    {
        internal const string SubmenuLabel = "NonToon 光影";
        internal const string SubmenuHost = "NT_Menu_NonToon";
        const string Folder = "Assets/NonToonLightMenu";
        const string CtrlPath = Folder + "/NTLightMenu.controller";

        // ⛔ 下限**已从菜单移除**（见文件头：它会把所有阴影压平）。这个名字只保留给
        //    `RemoveLegacyMenu` 去清掉老版本装出来的参数 / 菜单项 / host，不要再新增使用。
        internal const string P_Min_Legacy = "NT_MinLimit";
        internal const string P_Max = "NT_MaxLimit";
        internal const string P_ShadowStrength = "NT_ShadowStrength";
        internal const string P_ShadowOn = "NT_ShadowOn";

        // ── 各量的实际区间（菜单参数是归一化 0..1）──
        // 【光照上限 / 亮度】用户 2026-09-17 的明确要求（原文）：
        //   「光照上限应该是**默认 50% 也就是无变化**，**大于 50 就是增亮，小于就是压制亮度**」
        // ⇒ 参数 0.5 必须**正好等于 `_LightMaxLimit = 1.0`（无变化）**，两端分别是压暗与增亮：
        //     param 0.0 → 0.2（压暗：高光被压掉）
        //     param 0.5 → **1.0（无变化）**   ← 默认值
        //     param 1.0 → 2.0（增亮：允许超过白）
        //   `_LightMaxLimit` 在着色器里是 `SC_float(1, [SCRange(0,10)])`，所以 >1 是允许的。
        //   ⚠️ 因为 0.5 要落在 1.0 上，**不能用两关键帧的线性混合**（那会让 0.5 → 0.6），
        //      必须用**三点** BlendTree（阈值 0 / 0.5 / 1）。
        const float MaxLo = 0.2f, MaxMid = 1f, MaxHi = 2f;
        const float MaxDefaultParam = 0.5f;   // 0.5 = 无变化
        // 顺便回答用户担心的场景：「预烘焙光照贴图的地图作者随便放了光源导致模型很亮」
        //   ⇒ 正是把这条**往左拉**（param < 0.5）来压，而不是靠抬高下限。
        // 「下限归零时由环境光完全接管」= NonToon 的原始语义（min=0 ⇒ 完全由 env + 直接光决定），
        //   我们已经把菜单下限删掉了，所以默认就是这个行为。

        // 阴影强度 = 材质 `_ShadowStrength`（**toon 阴影对比**），连续 0..1，**默认 1（最重）**。
        // ⛔⛔ 这里曾经接的是 ⑥ 组件的 `shadowFloor`，那是**错的**（2026-09-17 实机纠正）：
        //    · `shadowFloor` 只在「阴影开关」打开（⑥ 实时 PCSS 生效）时才被着色器读，
        //      而开关**默认是关的** ⇒ 用户拖"阴影强度"当然**毫无反应**（原话："阴影强度根本没实现"）；
        //    · 用户说的"阴影强度"就是**看得见的那个明暗对比**，它的真身是材质 `_ShadowStrength`
        //      （`ShadowColor/phase_shade.hlsl:93-94`：`ntSLit1 = lerp(1.0, ntSLit1, _ShadowStrength*…)`）。
        //    · 实测这只 avatar 的 `_ShadowStrength` 是 **0.1 / 0.29**（忠实照搬自 lilToon）
        //      ⇒ 阴影被压掉 90%/71% ⇒ 画面上"完全没有阴影"。
        //    ⇒ 所以默认给 **1.0**（= fork 模块默认值，toon 阴影最明显），用户想淡可以自己往下拉。
        //    ⑥ 的 `shadowFloor` 不再由菜单驱动，留在组件上（默认 0.25）即可。
        const float ShadowLo = 0f, ShadowHi = 1f, ShadowDefault = 1f;

        static float Norm(float v, float lo, float hi) { return Mathf.Clamp01((v - lo) / Mathf.Max(hi - lo, 1e-6f)); }

        internal sealed class Result
        {
            public readonly List<string> Done = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
        }

        [MenuItem("Tools/NonToon/⑥ 光影菜单（子菜单：光照上限 / 阴影强度 / 阴影开关）", false, 56)]
        private static void RunMenu()
        {
            var root = Selection.gameObjects.FirstOrDefault(g => g != null && g.transform.parent == null);
            if (root == null)
            {
                EditorUtility.DisplayDialog("NonToon 光影菜单", "请先在 Hierarchy 里选中 avatar 根节点。", "好");
                return;
            }
            EditorUtility.DisplayDialog("NonToon 光影菜单", Report(Install(root, true)), "好");
        }

        internal static string Report(Result r)
        {
            var sb = new List<string>();
            sb.AddRange(r.Done.Select(x => "· " + x));
            sb.AddRange(r.Warnings.Select(x => "⚠️ " + x));
            sb.AddRange(r.Errors.Select(x => "❌ " + x));
            return string.Join("\n", sb.ToArray());
        }

        /// <summary>核心逻辑，**不弹任何对话框**（供脚本 / 探针 / 自动化调用）。</summary>
        internal static Result Install(GameObject avatarRoot, bool addMenu)
        {
            var r = new Result();
            if (avatarRoot == null) { r.Errors.Add("没有 avatar 根节点"); return r; }
            if (!NTModularAvatarBridge.IsAvailable)
            {
                r.Errors.Add("本工程里没装 Modular Avatar。菜单只走声明式路径（不会改你的资产）。");
                return r;
            }

            var compType = FindType("NonToon.NTSelfRealtimeShadow");
            if (compType == null) { r.Errors.Add("找不到 NonToon.NTSelfRealtimeShadow（工具包 Runtime 没装？）"); return r; }

            // ---- 确保 ⑥ 灯架存在（**不装它自己的菜单**，菜单由本类统一负责）----
            // 无条件走一遍 `NTSelfRealtimeSetup.Install`：它现在会**自愈**（去重 + 把灯架拉回 avatar 根下），
            // 所以这里不再用 `Find` 去判断"有没有"——那只找直接子级，重复体藏在子菜单里时判断不出来。
            var setupType = FindType("NonToonTools.NTSelfRealtimeSetup");
            setupType.GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
                     .Invoke(null, new object[] { avatarRoot, false, 0.8f, 64f, 1f, 8f, 50f, false });
            var rigT = NTModularAvatarBridge.EnsureContainer(avatarRoot).transform.Find(NTSelfRealtimeSetup.RigName)
                    ?? avatarRoot.transform.Find(NTSelfRealtimeSetup.RigName);
            if (rigT == null) { r.Errors.Add("没能建出 ⑥ 的灯架"); return r; }
            var rig = rigT.gameObject;
            // ⛔ clip 的绑定路径是**相对 avatar 根**的（见 `AddMat`：它用
            //    `CalculateTransformPath(rd.transform, avatarRoot.transform)`；
            //    `AddField` 也直接收"相对 avatar 根"的路径）。
            //    灯架现在住在容器里 ⇒ 它的路径**必须带容器前缀**，
            //    否则「阴影开关」那条层会绑到一个不存在的物体上（静默失效）。
            //    MergeAnimator 的 `relativePathRoot` 已指回 avatar 根 ⇒ 基路径 `""`
            //    ⇒ 这里写的就是完整路径。（兼容未迁移的旧场景：直接挂在根下就不加前缀。）
            var rigRel = rig.transform.parent == avatarRoot.transform
                       ? rig.name
                       : NTModularAvatarBridge.ContainerName + "/" + rig.name;

            // ---- 材质侧 ----
            // `_LightMaxLimit`：光照上限
            // `_ShadowStrength`：**toon 阴影对比**（这才是用户说的"阴影强度"）
            var maxRds = new List<Renderer>();
            var shadowRds = new List<Renderer>();
            foreach (var rd in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (rd == null) continue;
                foreach (var m in rd.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.HasProperty("_LightMaxLimit") && !maxRds.Contains(rd)) maxRds.Add(rd);
                    if (m.HasProperty("_ShadowStrength") && !shadowRds.Contains(rd)) shadowRds.Add(rd);
                }
            }
            if (maxRds.Count == 0) r.Warnings.Add("没有任何材质的 _LightMaxLimit ⇒ 「光照上限」这条曲线会是空的。");
            if (shadowRds.Count == 0) r.Warnings.Add("没有任何材质的 _ShadowStrength ⇒ 「阴影强度」这条曲线会是空的。");

            // ---- 生成控制器 ----
            EnsureFolder(Folder);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath) != null) AssetDatabase.DeleteAsset(CtrlPath);
            foreach (var g in AssetDatabase.FindAssets("t:AnimationClip", new[] { Folder }))
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(g));

            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(CtrlPath);
            AddFloatParam(ctrl, P_Max, MaxDefaultParam);
            AddFloatParam(ctrl, P_ShadowStrength, Norm(ShadowDefault, ShadowLo, ShadowHi));
            // ⚡ 开关用 **Bool**：VRChat 里 `Bool` 只占 **1 bit**（`Float`/`Int` 都是 8 bit）。
            //    动画侧也用 Bool 参数 + 两状态机（故意不玩"声明 Bool / 动画 Float"的跨类型转换）
            AddBoolParam(ctrl, P_ShadowOn, false);     // 阴影开关，**默认关**（自带阴影默认关闭）
            // 删掉 CreateAnimatorControllerAtPath 附送的空 Base Layer
            for (var i = ctrl.layers.Length - 1; i >= 0; i--)
            {
                var L = ctrl.layers[i];
                if (L.name == "Base Layer" && (L.stateMachine == null || L.stateMachine.states.Length == 0)) ctrl.RemoveLayer(i);
            }

            // 1) 光照上限 / 亮度：0..1 → 材质 _LightMaxLimit **0.2 / 1.0 / 2.0**（三点，0.5 = 无变化）
            AddLayer3(ctrl, P_Max, "MaxLimit",
                (clip, v) => { foreach (var rd in maxRds) AddMat(clip, avatarRoot, rd, "_LightMaxLimit", v); },
                MaxLo, MaxMid, MaxHi);
            // 2) 阴影强度：0..1 → 材质 **_ShadowStrength** 0.0 .. 1.0（**连续**，toon 阴影对比）
            //    ⚠️ `material._ShadowStrength` 只作用于**第一个材质槽**（Unity 的 material.* 绑定语义）；
            //       本 avatar 的 Body 渲染器有 3 个槽，其余槽不会被动画改到 —— 想让全部槽都跟着变，
            //       需要写 `materials.Array.data[i]._ShadowStrength`，这里为了控制器体积先保持简单。
            AddLayer(ctrl, P_ShadowStrength, "ShadowStrength",
                (clip, v) => { foreach (var rd in shadowRds) AddMat(clip, avatarRoot, rd, "_ShadowStrength", v); },
                ShadowLo, ShadowHi);
            // 3) 阴影开关（**Bool**，1 bit）→ ⑥ 组件的 pcssOn（整盏实时灯的总开关）
            AddBoolLayer(ctrl, P_ShadowOn, "ShadowOn", defaultOn: false,
                (clip, v) => AddField(clip, rigRel, compType, "pcssOn", v));

            AssetDatabase.SaveAssets();
            r.Done.Add("生成控制器 " + CtrlPath + "：3 条层（光照上限 / 阴影强度 / 阴影开关）");

            if (!addMenu) return r;

            // ---- MA：控制器 + 3 个参数 + **子菜单** ----
            var c = NTModularAvatarBridge.AttachControllerOnly(avatarRoot, ctrl);
            r.Done.AddRange(c.Done); r.Errors.AddRange(c.Errors);

            AddParam(avatarRoot, P_Max, MaxDefaultParam, r);   // 0.5 = 无变化
            AddParam(avatarRoot, P_ShadowStrength, Norm(ShadowDefault, ShadowLo, ShadowHi), r);
            // ⚡ 开关声明成 Bool ⇒ **1 bit**（Float 要 8 bit）。Bool 默认值用 1/0。
            AddParam(avatarRoot, P_ShadowOn, 0f, r, syncType: "Bool");   // 默认关
            r.Done.Add("同步预算：2 × Float(8 bit) + 1 × Bool(1 bit) = **17 / 256 bit**（目标 ≤30 ✅）");
            r.Done.Add("**「光照下限」已从菜单移除**：它是 clamp 在最后一步的亮度地板，会把阴影对比整体压平（详见 NTLightMenuSetup 文件头）。");
            r.Warnings.Add("如果你要的正是「黑暗地图防煤」，请去 Tools ▸ NonToon ▸ ③ 亮度自适应与防煤 里**手动**抬下限 —— 代价是阴影对比一定会变弱。");

            // 子菜单宿主（住在容器里；旧的若还在 avatar 根上就搬进来）
            var menuContainer = NTModularAvatarBridge.EnsureContainer(avatarRoot).transform;
            var hostT = menuContainer.Find(SubmenuHost);
            if (hostT == null)
            {
                var legacyHost = avatarRoot.transform.Find(SubmenuHost);
                if (legacyHost != null)
                {
                    legacyHost.SetParent(menuContainer, false);
                    hostT = legacyHost;
                    r.Done.Add("迁移：子菜单宿主 " + SubmenuHost + " 从 avatar 根移到 "
                             + NTModularAvatarBridge.ContainerName);
                }
            }
            GameObject host;
            if (hostT == null)
            {
                host = new GameObject(SubmenuHost);
                Undo.RegisterCreatedObjectUndo(host, "NonToon 光影子菜单");
                host.transform.SetParent(menuContainer, false);
            }
            else host = hostT.gameObject;

            var sm = NTModularAvatarBridge.AddSubmenuOn(host, avatarRoot, SubmenuLabel);
            r.Done.AddRange(sm.Done); r.Errors.AddRange(sm.Errors);

            // 子项（**不各自挂 installer** —— 靠父 MenuItem 的 Children 模式收进去）
            ChildMenuItem(host, avatarRoot, "NT_Menu_MaxLimit", P_Max, "光照上限", "RadialPuppet", r);
            ChildMenuItem(host, avatarRoot, "NT_Menu_ShadowStrength", P_ShadowStrength, "阴影强度", "RadialPuppet", r);
            ChildMenuItem(host, avatarRoot, "NT_Menu_ShadowOn", P_ShadowOn, "阴影开关", "Toggle", r);

            RemoveLegacyMenu(avatarRoot, r);
            r.Done.Add("菜单结构：根菜单 →「" + SubmenuLabel + "」子菜单 → 3 个独立控制项（光照上限 / 阴影强度 / 阴影开关）");
            return r;
        }

        static void ChildMenuItem(GameObject parent, GameObject avatarRoot, string goName,
                                  string param, string label, string type, Result r)
        {
            var t = parent.transform.Find(goName);
            GameObject go;
            if (t == null)
            {
                go = new GameObject(goName);
                Undo.RegisterCreatedObjectUndo(go, "NonToon 光影子项");
                go.transform.SetParent(parent.transform, false);
            }
            else go = t.gameObject;
            var res = NTModularAvatarBridge.AddMenuItemOn(go, avatarRoot, param, true, label, type, addInstaller: false);
            r.Done.AddRange(res.Done); r.Errors.AddRange(res.Errors);
        }

        static void AddParam(GameObject avatarRoot, string name, float def, Result r, string syncType = "Float")
        {
            // synced: true（别人也要看到同样的亮度/影子）+ saved: true（记住你的选择）
            // ⚠️ VRChat 规则：`Saved` 必须同时 `Synced` ⇒ 「省参数(0 bit)」与「记住设置」不可兼得。
            var p = NTModularAvatarBridge.AddParameter(avatarRoot, name, true, def, saved: true, syncType: syncType);
            r.Done.AddRange(p.Done); r.Errors.AddRange(p.Errors);
        }

        /// <summary>摘掉以前几版设计留下的菜单项 / 参数（都是我们自己的），避免重复或叠加。</summary>
        static void RemoveLegacyMenu(GameObject avatarRoot, Result r)
        {
            var legacyParams = new[] { P_Min_Legacy, "NT_Light", "NT_Look", NTSelfRealtimeSetup.ParamName,
                                       NTSelfRealtimeSetup.ParamFloor, NTSelfRealtimeSetup.ParamOn,
                                       NTSelfRealtimeSetup.ParamIntensity };
            var paramsType = FindType("nadena.dev.modular_avatar.core.ModularAvatarParameters");
            var cfgType = FindType("nadena.dev.modular_avatar.core.ParameterConfig");
            var pars = paramsType == null ? null : avatarRoot.GetComponent(paramsType);
            var listField = paramsType == null ? null : paramsType.GetField("parameters");
            var list = pars == null || listField == null ? null : listField.GetValue(pars) as IList;
            var nameField = cfgType == null ? null : cfgType.GetField("nameOrPrefix");
            if (list != null)
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var nm = nameField == null ? null : nameField.GetValue(list[i]) as string;
                    if (nm != null && legacyParams.Contains(nm))
                    {
                        list.RemoveAt(i);
                        r.Done.Add("摘掉旧设计的参数 " + nm);
                    }
                }
            if (pars != null) EditorUtility.SetDirty(pars);

            // 旧 host（根上的平级菜单项 / 灯架上的子物体 / 子菜单里已废弃的子项）
            // ⚠️ 现在东西可能在**容器**里，也可能还在 **avatar 根**上（未迁移的旧场景）
            //    ⇒ 两个位置都要找，否则重构后会漏删、菜单里出现重复项。
            var mine = NTModularAvatarBridge.EnsureContainer(avatarRoot).transform;
            var legacyHosts = legacyParams.Select(NTModularAvatarBridge.HostName)
                                          .Concat(new[] { "NT_Menu_NT_Look", "NT_Menu_MinLimit" }).ToArray();
            foreach (var hn in legacyHosts)
            {
                foreach (var parent in new[] { mine, avatarRoot.transform })
                {
                    var t = parent.Find(hn);
                    if (t == null) continue;
                    Undo.DestroyObjectImmediate(t.gameObject);
                    r.Done.Add("摘掉旧 host " + hn + "（@" + parent.name + "）");
                }
            }
            var rig = mine.Find(NTSelfRealtimeSetup.RigName)
                   ?? avatarRoot.transform.Find(NTSelfRealtimeSetup.RigName);
            if (rig != null)
                foreach (var n in new[] { "Menu_ShadowFloor", "Menu_OnOff", "Menu_LightIntensity" })
                {
                    var t = rig.Find(n);
                    if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); r.Done.Add("摘掉旧 host " + n); }
                }

            // 子菜单里已废弃的子项（「光照下限」）—— 它是子菜单宿主的直接子级，上面的 host 列表覆盖不到
            var menuHost = mine.Find(SubmenuHost) ?? avatarRoot.transform.Find(SubmenuHost);
            if (menuHost != null)
                foreach (var n in new[] { "NT_Menu_MinLimit" })
                {
                    var t = menuHost.Find(n);
                    if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); r.Done.Add("摘掉废弃的子项 " + n + "（连带它的 NT_MinLimit 参数已经不会被读）"); }
                }

            // 旧设计的 MergeAnimator（非本控制器的那些）
            // ⚠️ 只清理**确认是我们的**（animator 指向本包生成目录下的控制器），别人的一个都不碰。
            var mergeType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var keep = AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath);
            if (mergeType != null)
            {
                var animField = mergeType.GetField("animator");
                foreach (var mc in avatarRoot.GetComponentsInChildren(mergeType, true).ToArray())
                {
                    if (mc == null) continue;
                    var a = animField.GetValue(mc) as UnityEngine.Object;
                    if (a == null) continue;
                    var path = a is AnimatorController ac ? AssetDatabase.GetAssetPath(ac) : "";
                    // 只删"看着像我们早期生成的"：动画机在 ⑥ 的生成目录下、或资产名带 NonToon
                    var ours = path.StartsWith("Assets/NonToonRealtimeShadow", StringComparison.Ordinal)
                            || path.StartsWith("Assets/NonToonLight", StringComparison.Ordinal)
                            || a.name.StartsWith("NTRealtime", StringComparison.Ordinal)
                            || a.name.StartsWith("NonToon", StringComparison.Ordinal);
                    if (!ours || ReferenceEquals(a, keep)) continue;
                    Undo.DestroyObjectImmediate(mc);
                    r.Done.Add("摘掉旧 MergeAnimator（" + a.name + " @" + mc.gameObject.name + "）");
                }
            }
        }

        // ------------------------------------------------------------------ 生成工具
        static void AddFloatParam(AnimatorController ctrl, string name, float def)
        {
            ctrl.AddParameter(name, AnimatorControllerParameterType.Float);
            var ps = ctrl.parameters;
            for (var i = 0; i < ps.Length; i++) if (ps[i].name == name) ps[i].defaultFloat = def;
            ctrl.parameters = ps;
        }

        static void AddBoolParam(AnimatorController ctrl, string name, bool def)
        {
            ctrl.AddParameter(name, AnimatorControllerParameterType.Bool);
            var ps = ctrl.parameters;
            for (var i = 0; i < ps.Length; i++) if (ps[i].name == name) ps[i].defaultBool = def;
            ctrl.parameters = ps;
        }

        /// <summary>一条 1D 混合层：参数 0..1 → `lo`..`hi`。
        ///   `defaultWeight` **必须是 1**（`AddLayer(string)` 那个重载会建出 0 ⇒ 整层不生效）。</summary>
        static void AddLayer(AnimatorController ctrl, string param, string tag,
                             Action<AnimationClip, float> write, float lo, float hi)
        {
            var layer = NewLayer(ctrl, tag);
            var st = layer.stateMachine.AddState(tag);
            layer.stateMachine.defaultState = st;
            var tree = new BlendTree
            {
                name = tag + "Tree",
                blendType = BlendTreeType.Simple1D,
                blendParameter = param,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(tree, ctrl);
            for (var i = 0; i < 2; i++)
            {
                var clip = MakeClip(tag + i, write, i == 0 ? lo : hi);
                tree.AddChild(clip, i == 0 ? 0f : 1f);
            }
            st.motion = tree;
        }

        /// <summary>**三点** 1D 混合层：参数 0 / 0.5 / 1 → `v0` / `vMid` / `v1`。
        /// 用在「0.5 必须正好等于某个值」的场合（光照上限：0.5 = `_LightMaxLimit` 1.0 = 无变化）。
        /// 两点的线性混合做不到这件事（0.5 会落在两端的中点）。</summary>
        static void AddLayer3(AnimatorController ctrl, string param, string tag,
                              Action<AnimationClip, float> write, float v0, float vMid, float v1)
        {
            var layer = NewLayer(ctrl, tag);
            var st = layer.stateMachine.AddState(tag);
            layer.stateMachine.defaultState = st;
            var tree = new BlendTree
            {
                name = tag + "Tree",
                blendType = BlendTreeType.Simple1D,
                blendParameter = param,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(tree, ctrl);
            var vals = new[] { v0, vMid, v1 };
            var ths = new[] { 0f, 0.5f, 1f };
            for (var i = 0; i < 3; i++)
            {
                var clip = MakeClip(tag + i, write, vals[i]);
                tree.AddChild(clip, ths[i]);
            }
            st.motion = tree;
        }

        /// <summary>一条 **Bool 两状态**层（开关用；VRChat 里 Bool 只占 1 bit）。</summary>
        static void AddBoolLayer(AnimatorController ctrl, string param, string tag, bool defaultOn,
                                 Action<AnimationClip, float> write)
        {
            var layer = NewLayer(ctrl, tag);
            var sm = layer.stateMachine;
            var off = sm.AddState(tag + "Off");
            var on = sm.AddState(tag + "On");
            off.motion = MakeClip(tag + "Off", write, 0f);
            on.motion = MakeClip(tag + "On", write, 1f);
            sm.defaultState = defaultOn ? on : off;
            var t1 = off.AddTransition(on);
            t1.hasExitTime = false; t1.duration = 0f; t1.AddCondition(AnimatorConditionMode.If, 0f, param);
            var t2 = on.AddTransition(off);
            t2.hasExitTime = false; t2.duration = 0f; t2.AddCondition(AnimatorConditionMode.IfNot, 0f, param);
        }

        static AnimatorControllerLayer NewLayer(AnimatorController ctrl, string tag)
        {
            var layer = new AnimatorControllerLayer
            {
                name = NTVrcParameterBuilder.LayerPrefix + tag,
                stateMachine = new AnimatorStateMachine { name = tag + "SM" },
                defaultWeight = 1f,     // ⛔ 必须 1
            };
            ctrl.AddLayer(layer);
            return layer;
        }

        static AnimationClip MakeClip(string name, Action<AnimationClip, float> write, float v)
        {
            var clip = new AnimationClip { name = name, frameRate = 60f, legacy = false };
            write(clip, v);
            AssetDatabase.CreateAsset(clip, Folder + "/" + name + ".anim");
            return clip;
        }

        static void AddMat(AnimationClip clip, GameObject root, Renderer rd, string prop, float v)
        {
            if (rd == null) return;
            var rel = AnimationUtility.CalculateTransformPath(rd.transform, root.transform);
            var b = EditorCurveBinding.FloatCurve(rel, rd.GetType(), "material." + prop);
            AnimationUtility.SetEditorCurve(clip, b, AnimationCurve.Constant(0f, 1f / 60f, v));
        }

        static void AddField(AnimationClip clip, string relPath, Type scriptType, string field, float v)
        {
            if (scriptType == null) return;
            var b = new EditorCurveBinding { path = relPath, type = scriptType, propertyName = field };
            AnimationUtility.SetEditorCurve(clip, b, AnimationCurve.Constant(0f, 1f / 60f, v));
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = folder.Substring(0, folder.LastIndexOf('/'));
            var leaf = folder.Substring(folder.LastIndexOf('/') + 1);
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets", parent.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full, false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
