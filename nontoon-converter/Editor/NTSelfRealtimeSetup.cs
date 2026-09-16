// [NT-FEAT 20] ⑥ 实时自阴影 —— **一键安装 / 卸载**（非破坏式，MA 优先）。
//
// ── 这一套是照着付费资产 PCSS4VRC 的"光源挂载 + MA 落地"学的（只学做法，不抄代码）──
//   1. **灯必须锚定在骨骼上**，不能裸挂在 avatar 根下：
//      它用 `AutoLighting`(PositionConstraint→Neck) + `AimSphere/TargetSphere`(ParentConstraint→Hips)
//      + `Sphere`(AimConstraint) 把灯保持"身前上方"。
//      为什么必要：实机踩过 —— 灯与相机同侧/在背后时画面里**没有可见自遮挡**，
//      于是"PCSS 完全没效果"；而且实测灯会被工程里的编辑期动画（GestureManager？）拖走。
//   2. **灯只照玩家层**：它用 `CullingMask = Player(9) | PlayerLocal(10) | MirrorReflection(18)`。
//      本安装器按同一个思路给：默认 PlayerLocal（只照自己，最安全），可选加 Player（别人也能看到）。
//   3. **MA 是声明式的**：它挂 `ModularAvatarMergeAnimator` / `ModularAvatarParameters` /
//      `ModularAvatarMenuItem` / `ModularAvatarMenuInstaller` 四个组件，**不碰用户的
//      AnimatorController / ExpressionParameters / ExpressionsMenu**。
//      我们复用 `NTModularAvatarBridge.Attach(...)`（同一组四个，全反射、软依赖）。
//   4. **绝不复用用户资产做原地修改**：它靠"复制材质"来承载菜单动画。
//      我们不需要 —— ⑥ 的旋钮在 **组件** 上，菜单动画直接驱动组件字段
//      ⇒ `_targets` 的材质始终由组件每帧写入，用户材质不会被当成资产改。
//
// ── 装出来的结构 ────────────────────────────────────────────────────────────
//   <Avatar>
//     └── NonToon_RealtimeShadow        (NTSelfRealtimeShadow + PositionConstraint + AimConstraint)
//           └── Spot Light              (Light: Spot / ForcePixel / Hard→组件会关 / 只照玩家层)
//
// 有 MA：挂 4 个 MA 组件（FX 层驱动 `lightSize` + Float 参数 + 径向菜单项 + installer）
// 没 MA：回退到 `NTVrcParameterBuilder` 的老路径（直接写用户的 FX 层 / 菜单），并给出警告。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;

namespace NonToonTools
{
    internal static class NTSelfRealtimeSetup
    {
        internal const string RigName = "NonToon_RealtimeShadow";
        internal const string ParamName = "NT_RealtimeShadow";
        internal const string ParamFloor = "NT_RT_ShadowFloor";
        internal const string ParamOn = "NT_RT_ShadowOn";
        internal const string ParamIntensity = "NT_RT_LightIntensity";

        // ── 菜单参数是**归一化 0..1** 的 Float（1D 混合树用），这里记录它映射到的组件字段实际区间 ──
        //    lightSize 实测：32 偏硬、64 起步、512 会把阴影洗掉 ⇒ 区间 1..200、默认 64。
        const float SizeLo = 1f, SizeHi = 200f, SizeDefault = 64f;
        const float FloorLo = 0f, FloorHi = 0.6f, FloorDefault = 0.25f;
        const float OnLo = 0f, OnHi = 1f;
        //    灯强度：组件字段本身是 `[Range(0,30)]`，但游戏内径向要留给常用区间
        //    （0 = 关灯、5 左右是默认、15 已经相当亮）⇒ 菜单只映射 0..15。
        const float IntLo = 0f, IntHi = 15f;

        /// <summary>把组件字段的**实际值**换成一维混合树用的归一化阈值，
        ///   这样菜单参数默认值 = `ModularAvatarParameters` 里写的默认值 = 组件默认值，三者一致
        ///   （不一致的话：avatar 一加载动画就把检视面板里调好的值冲掉）。</summary>
        static float Norm(float v, float lo, float hi)
        {
            return Mathf.Clamp01((v - lo) / Mathf.Max(hi - lo, 1e-6f));
        }

        internal sealed class Result
        {
            public readonly List<string> Done = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
            public GameObject Rig;
        }

        // ------------------------------------------------------------------ 入口
        internal static Result Install(GameObject avatarRoot,
                                       bool includeOtherPlayers,
                                       float intensity, float lightSize, float softness,
                                       float range, float spotAngle,
                                       bool addMenu)
        {
            var r = new Result();
            if (avatarRoot == null) { r.Errors.Add("没有选中 avatar 根节点"); return r; }

            var compType = FindType("NonToon.NTSelfRealtimeShadow");
            if (compType == null)
            {
                r.Errors.Add("找不到 NonToon.NTSelfRealtimeShadow（工具包 Runtime 没装？）");
                return r;
            }

            // ---- 1) 灯架（**自愈**：去重 + 拉进容器）----
            // ⛔ 不能只用 `avatarRoot.transform.Find(RigName)`（那只找**直接子级**）：
            //    实机出现过"灯架长在 `NT_Menu_NonToon` 里面"的**重复体** ⇒
            //    两盏 `intensity=5` 的聚光灯叠加，表现为**不在 Play 状态也亮得离谱**，
            //    而且多渲一遍深度图、两套约束互相打架。
            //    ⇒ 在整个 avatar 子树里找：在**容器**下的那个留下，其余删除；
            //      一个都不在容器下就把第一个拉进来。（无论重复体怎么产生的都能收敛）
            // 🆕 容器化（用户 2026-09-17）：「将所有 ma 组件和子物体都放在一个物体下」
            //    ⇒ 灯架不再直接挂在 avatar 根下，而是挂在 `<avatar>/_NonToonLight/` 里。
            var container = NTModularAvatarBridge.EnsureContainer(avatarRoot);
            var existing = avatarRoot.GetComponentsInChildren<Transform>(true)
                                     .Where(t => t.name == RigName).ToList();
            var rig = existing.FirstOrDefault(t => t.parent == container.transform);
            if (rig == null && existing.Count > 0)
            {
                rig = existing[0];
                r.Done.Add("把灯架移进 " + container.name + "（原来挂在 "
                         + (rig.parent == null ? "?" : rig.parent.name) + " 下）");
                rig.SetParent(container.transform, false);
            }
            foreach (var extra in existing)
            {
                if (extra == null || extra == rig) continue;
                r.Done.Add("删除重复灯架 @" + extra.name + "（父物体 " + (extra.parent == null ? "?" : extra.parent.name) + "）");
                Undo.DestroyObjectImmediate(extra.gameObject);
            }
            if (rig == null)
            {
                var go = new GameObject(RigName);
                Undo.RegisterCreatedObjectUndo(go, "NonToon ⑥ 灯架");
                go.transform.SetParent(container.transform, false);
                rig = go.transform;
                r.Done.Add("新建灯架 " + RigName + "（挂在 " + container.name + " 下）");
            }
            else if (existing.Count == 1) r.Warnings.Add(RigName + " 已存在，就地更新（幂等）");
            r.Rig = rig.gameObject;

            var lightT = rig.Find("Spot Light");
            if (lightT == null)
            {
                var go = new GameObject("Spot Light");
                Undo.RegisterCreatedObjectUndo(go, "NonToon ⑥ Spot");
                go.transform.SetParent(rig, false);
                lightT = go.transform;
            }
            var light = lightT.GetComponent<Light>();
            if (light == null) light = Undo.AddComponent<Light>(lightT.gameObject);
            light.type = LightType.Spot;
            light.spotAngle = spotAngle;
            light.range = range;
            light.intensity = intensity;
            light.color = Color.white;
            light.renderMode = LightRenderMode.ForcePixel;   // 实时光必须逐像素，否则拿不到附加光 pass
            light.shadows = LightShadows.Hard;               // 阴影走 Unity 自己的阴影贴图（⑥ 采它）
            light.shadowNearPlane = 0.1f;
            light.cullingMask = BuildCullingMask(avatarRoot, includeOtherPlayers);
            EditorUtility.SetDirty(light);
            r.Done.Add("Spot：ForcePixel / " + spotAngle + "° / range " + range
                     + " / cullingMask = " + DescribeMask(light.cullingMask)
                     + "（Unity 阴影**开启**：⑥ 直接采 Unity 的阴影贴图，1 次硬件比较采样）");

            // ---- 2) 组件 ----
            var comp = rig.GetComponent(compType);
            if (comp == null) comp = Undo.AddComponent(rig.gameObject, compType);
            SetField(comp, "targetLight", light);
            SetField(comp, "pcssOn", false);   // 自带阴影默认关闭
            SetField(comp, "lightSize", lightSize);
            SetField(comp, "softness", softness);
            SetField(comp, "biasMeters", 0.01f);
            SetField(comp, "quality", 3f);
            SetField(comp, "lightIntensity", intensity);
            SetField(comp, "lightColor", Color.white);
            EditorUtility.SetDirty(comp);

            // 🆕 [NT-FIX 29] **灯必须随开关启停**（用户 2026-09-17 的第二个批评：
            //    「为什么灯组件没有随着开关启用和关闭」）。
            //    以前只在 Play 的 `LateUpdate → RenderDepth → SuspendRig()` 里关灯
            //    ⇒ **编辑器里怎么看灯都是亮的**（组件没有 `[ExecuteAlways]`，编辑期
            //    `OnEnable/LateUpdate` 都不跑）。现在组件带 `[ExecuteAlways]`，
            //    编辑期也会同步，这里在**安装时**就先对齐一次，避免"装完了还是亮的"。
            //    `pcssOn` 默认 false ⇒ 安装后灯是关的，这正是"⑥ off = 纯原版 NonToon"。
            light.enabled = false;
            r.Done.Add("NTSelfRealtimeShadow：LightSize=" + lightSize + "、Quality=3(24/32 采样)、Bias=1cm"
                     + "；灯已随 `pcssOn=false` **关闭**（在组件上勾上 pcssOn 即点亮，编辑期实时生效）");

            // ---- 3) 骨骼锚定 ----
            AnchorBones(rig, avatarRoot, r);

            // ---- 4) 菜单：交给 NTLightMenuSetup 统一建**子菜单** ----
            // ⛔ 不再在 avatar 根上平铺多个菜单项（用户 2026-09-17 明确要求：
            //    「应该是创建一个子菜单然后将开关轮盘放入」）。
            //    本类从此**只负责灯架 + 组件 + 约束**，菜单全部由 NTLightMenuSetup 拥有。
            if (addMenu)
            {
                var menu = NTLightMenuSetup.Install(avatarRoot, true);
                r.Done.AddRange(menu.Done);
                r.Warnings.AddRange(menu.Warnings);
                r.Errors.AddRange(menu.Errors);
            }

            return r;
        }

        internal static Result Uninstall(GameObject avatarRoot)
        {
            var r = new Result();
            if (avatarRoot == null) { r.Errors.Add("没有选中 avatar 根节点"); return r; }
            // 灯架现在在容器 `_NonToonLight` 下（不再是 avatar 的直接子级）
            var container = avatarRoot.transform.Find(NTModularAvatarBridge.ContainerName);
            var rig = container != null ? container.Find(RigName) : null;
            if (rig == null) rig = avatarRoot.transform.Find(RigName);   // 兼容未迁移的旧场景
            if (rig == null) { r.Warnings.Add("没有找到 " + RigName + "，无需卸载"); return r; }

            // 把材质开关归零（组件已经不再写它们了，但预览期可能留了值）
            // MA 组件：**只摘掉 ⑥ 自己的**。
            // ⛔ 不能整个删掉 `ModularAvatarParameters` —— ⑤（亮度调整）的 `NT_Light`
            //    也注册在同一个组件里，整删会把 ⑤ 一起弄坏。
            var paramsType = FindType("nadena.dev.modular_avatar.core.ModularAvatarParameters");
            var cfgType = FindType("nadena.dev.modular_avatar.core.ParameterConfig");
            if (paramsType != null && cfgType != null)
            {
                // 参数现在注册在容器的 `ModularAvatarParameters` 上 ⇒ 全子树找（兼容旧场景）
                var pars = avatarRoot.GetComponent(paramsType)
                        ?? avatarRoot.GetComponentInChildren(paramsType, true);
                var listField = paramsType.GetField("parameters");
                var list = pars == null || listField == null ? null : listField.GetValue(pars) as System.Collections.IList;
                if (list != null)
                {
                    var mine = new HashSet<string> { ParamName, ParamFloor, ParamOn, ParamIntensity };
                    var nameField = cfgType.GetField("nameOrPrefix");
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        var nm = nameField == null ? null : nameField.GetValue(list[i]) as string;
                        if (nm != null && mine.Contains(nm)) { list.RemoveAt(i); r.Done.Add("移除 MA 参数 " + nm); }
                    }
                    EditorUtility.SetDirty(pars);
                    if (list.Count == 0) { Undo.DestroyObjectImmediate(pars); r.Done.Add("移除 MA Parameters（已空）"); }
                }
            }

            // MergeAnimator：**只删指向 ⑥ 那个控制器的**。
            // ⛔ 现在是"每个控制器一个 MergeAnimator"（见 `NTModularAvatarBridge.Attach` 的注释），
            //    所以不能见一个删一个 —— ⑤（亮度）的 MergeAnimator 也在同一个 avatar 根上。
            var mergeType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var ourCtrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/NonToonRealtimeShadow/NTRealtimeShadowSize.controller");
            if (mergeType != null)
            {
                var animField = mergeType.GetField("animator");
                foreach (var c in avatarRoot.GetComponentsInChildren(mergeType, true))
                {
                    if (c == null) continue;
                    var cur = animField == null ? null : animField.GetValue(c);
                    if (ourCtrl == null || ReferenceEquals(cur, ourCtrl))
                    { Undo.DestroyObjectImmediate(c); r.Done.Add("移除 MA MergeAnimator（⑥ 的控制器）"); }
                }
            }

            // 专属菜单 host（MenuItem + MenuInstaller 都挂在它上面）—— 现在在容器下
            var hostName = NTModularAvatarBridge.HostName(ParamName);
            var host = container != null ? container.Find(hostName) : null;
            if (host == null) host = avatarRoot.transform.Find(hostName);
            if (host != null) { Undo.DestroyObjectImmediate(host.gameObject); r.Done.Add("移除菜单 host " + host.name); }

            // 兼容旧版：2026-09-17 之前 `Attach` 是把 MenuItem/Installer 挂在 **avatar 根**上的。
            // 只在它确实指向我们的参数时才删，避免误伤别的工具的菜单项。
            var itemType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMenuItem");
            var instType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller");
            var ours = new HashSet<string> { ParamName, ParamFloor, ParamOn, ParamIntensity };
            if (itemType != null)
            {
                var it = avatarRoot.GetComponent(itemType);
                var ctrl = it == null ? null : itemType.GetField("Control").GetValue(it);
                var pf = ctrl == null ? null : ctrl.GetType().GetField("parameter");
                var pv = pf == null ? null : pf.GetValue(ctrl);
                var pn = pv == null ? null : pv.GetType().GetField("name").GetValue(pv) as string;
                if (pn != null && ours.Contains(pn))
                {
                    Undo.DestroyObjectImmediate(it); r.Done.Add("移除根上的旧菜单项（指向 " + pn + "）");
                    if (instType != null)
                    {
                        var ins = avatarRoot.GetComponent(instType);
                        if (ins != null) { Undo.DestroyObjectImmediate(ins); r.Done.Add("移除根上的旧 MenuInstaller"); }
                    }
                }
            }

            Undo.DestroyObjectImmediate(rig.gameObject);
            r.Done.Add("删除灯架 " + RigName);

            // 容器如果被清空了就一起删掉（保持 avatar 干净：卸载 = 根上不留任何我方残骸）
            if (container != null)
            {
                var left = container.Cast<Transform>().ToList();
                var stillHasComp = container.GetComponents<Component>()
                                          .Any(c => c != null && !(c is Transform));
                if (left.Count == 0 && !stillHasComp)
                {
                    Undo.DestroyObjectImmediate(container.gameObject);
                    r.Done.Add("删除已清空的容器 " + NTModularAvatarBridge.ContainerName);
                }
                else
                {
                    r.Warnings.Add("容器 " + NTModularAvatarBridge.ContainerName + " 里还有 " + left.Count
                                 + " 个子物体 / " + (stillHasComp ? "有" : "无") + " 组件，未删除（可能还有 ⑤ 的东西）");
                }
            }
            return r;
        }

        // ------------------------------------------------------------------ 骨骼锚定
        static void AnchorBones(Transform rig, GameObject avatarRoot, Result r)
        {
            var all = avatarRoot.GetComponentsInChildren<Transform>(true);
            Func<string[], Transform> pick = names =>
            {
                foreach (var n in names)
                {
                    var t = all.FirstOrDefault(x => string.Equals(x.name, n, StringComparison.OrdinalIgnoreCase));
                    if (t != null) return t;
                }
                foreach (var n in names)
                {
                    var t = all.FirstOrDefault(x => x.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (t != null) return t;
                }
                return null;
            };

            var neck = pick(new[] { "Neck", "neck" });
            var chest = pick(new[] { "Chest", "Spine2", "UpperChest", "Spine1", "Spine" });
            var head = pick(new[] { "Head" });

            // 中性姿态下先把灯摆到"身前上方"（约束失效时也有个合理位置）
            var anchor = neck ?? head ?? chest;
            // [NT-FIX 24] 位置基准用【头】而不是脖子：灯要吊在【头顶上方】，刘海才会在脸上投影
            var posBase = head ?? anchor;
            if (anchor != null)
            {
                // ⛔ **偏移必须相对 avatar 朝向，不能写死世界坐标。**
                //    以前是 `anchor.position + new Vector3(0.25f, 0.30f, 0.45f)` ——
                //    那只对"正好朝向 +z"的角色碰巧正确。角色换个朝向，灯就跑到**背面**，
                //    表现为"拉「阴影强度」完全没反应"（观看者看到的是没被这盏灯照亮的一面；实机踩过）。
                //    ⚠️ 用**根节点**的 forward/right，不要用骨骼的：骨骼局部轴常常沿骨头方向
                //    （Blender 导入的尤其明显），拿它当"正面"会偏。竖直方向直接用世界 `up` 最稳。
                var root = avatarRoot.transform;
                rig.position = posBase.position + root.right * 0.30f + Vector3.up * 0.50f + root.forward * 0.35f;
                var aim = head ?? chest ?? anchor;
                rig.LookAt(aim.position);
            }
            else
            {
                rig.localPosition = new Vector3(0.30f, 1.60f, 0.35f);
                r.Warnings.Add("没找到 Neck/Chest/Head 骨骼，灯放在默认位置（请手动微调）");
            }

            // PositionConstraint：跟住 Neck（这就是 PCSS4VRC 的 AutoLighting 的做法）
            if (anchor != null)
            {
                var pc = rig.GetComponent<PositionConstraint>();
                if (pc == null) pc = Undo.AddComponent<PositionConstraint>(rig.gameObject);
                pc.constraintActive = true;
                pc.weight = 1f;
                while (pc.sourceCount > 0) pc.RemoveSource(0);
                pc.AddSource(new ConstraintSource { sourceTransform = anchor, weight = 1f });
                pc.translationAtRest = rig.localPosition;
                pc.translationOffset = Vector3.zero;
                pc.locked = false;
                EditorUtility.SetDirty(pc);
                r.Done.Add("PositionConstraint → " + anchor.name + "（灯跟着脖子走，这就是它的 AutoLighting 的做法）");
            }

            // AimConstraint：始终看向胸口（对应它的 AimSphere/Sphere）
            if (head != null || chest != null)
            {
                var ac = rig.GetComponent<AimConstraint>();
                if (ac == null) ac = Undo.AddComponent<AimConstraint>(rig.gameObject);
                ac.constraintActive = true;
                ac.weight = 1f;
                while (ac.sourceCount > 0) ac.RemoveSource(0);
                ac.AddSource(new ConstraintSource { sourceTransform = head ?? chest, weight = 1f });
                ac.aimVector = Vector3.forward;
                ac.upVector = Vector3.up;
                ac.worldUpType = AimConstraint.WorldUpType.SceneUp;
                ac.locked = false;
                EditorUtility.SetDirty(ac);
                r.Done.Add("AimConstraint → " + (head ?? chest).name + "（灯始终朝向【头】⇒ 刘海会在脸上投影）");
            }

            // 约束是"每帧解算"的：编辑器里立刻解一次，方便看效果
        }

        // ------------------------------------------------------------------ 菜单（MA 优先）
        // ------------------------------------------------------------------ 工具
        static int BuildCullingMask(GameObject avatarRoot, bool includeOtherPlayers)
        {
            int mask = 0;
            mask |= 1 << avatarRoot.layer;                    // avatar 自己所在的层
            int local = LayerMask.NameToLayer("PlayerLocal");
            int player = LayerMask.NameToLayer("Player");
            int mirror = LayerMask.NameToLayer("MirrorReflection");
            if (local >= 0) mask |= 1 << local;               // 本机玩家
            if (includeOtherPlayers && player >= 0) mask |= 1 << player;
            if (mirror >= 0) mask |= 1 << mirror;
            return mask == 0 ? ~0 : mask;
        }

        static string DescribeMask(int mask)
        {
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                {
                    var n = LayerMask.LayerToName(i);
                    names.Add(string.IsNullOrEmpty(n) ? ("layer" + i) : n);
                }
            return string.Join("|", names);
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

        static void SetField(object o, string n, object v)
        {
            if (o == null) return;
            var f = o.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            try { f.SetValue(o, v); } catch { }
        }
    }
}
