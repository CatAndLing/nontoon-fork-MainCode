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
    // [NT-FEAT 22] Modular Avatar 桥 —— **软依赖**。
    //
    // 装了就自动走非破坏模式：只生成资产 + 往 avatar 根节点挂 MA 的组件，
    // **绝不修改**用户的 FX 控制器 / ExpressionParameters / ExpressionsMenu；
    // 没装就返回不可用，调用方回退到 NTVrcParameterBuilder 的"直接改控制器"老路径。
    //
    // 全走反射（与 VRCSDK / AAO 的做法一致）：本包不引入 MA 硬依赖、也不加 asmdef 引用。
    //
    // 挂哪四个（缺一不可，读 MA 1.18.1 源码确认）：
    //   1. ModularAvatarMergeAnimator   —— 把生成的控制器并进 FX 层（pathMode=Relative、匹配 WD）
    //   2. ModularAvatarParameters      —— 注册 Float 参数（syncType=Float；localOnly = 不同步给别人）
    //   3. ModularAvatarMenuItem        —— 径向菜单项（VRCExpressionsMenu.Control.type = RadialPuppet）
    //   4. ModularAvatarMenuInstaller   —— **必须**：MA 只在存在 installer 时才安装菜单
    //      （Editor/MenuInstallHook.cs:35 `if (menuInstallers.Length == 0) return;`）
    // 四个都挂在**容器** `_NonToonLight` 上（不再是 avatar 根，用户 2026-09-17 要求）：
    //   avatar 根保持零我方组件，卸载 = 删掉这一个物体。
    //   ⚠️ 搬进容器后**必须**把 MergeAnimator 的 `relativePathRoot` 指回 avatar 根，
    //      否则基路径变成容器名、clip 路径被整体加前缀 ⇒ 动画静默失效。
    //      详见 `SetRelativePathRoot` 的注释（含 MA 源码行号）。
    internal static class NTModularAvatarBridge
    {
        const string NS = "nadena.dev.modular_avatar.core.";

        internal sealed class Result
        {
            public readonly List<string> Done = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
        }

        // ---------------------------------------------------------------- 探测
        static Type Find(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        internal static Type MergeAnimatorType { get { return Find(NS + "ModularAvatarMergeAnimator"); } }

        internal static bool IsAvailable { get { return MergeAnimatorType != null; } }

        /// <summary>`Attach` 给每个参数用的专属菜单 host 物体名（见 `Attach` 里"不能挂在 avatar 根上"的注释）。
        ///   卸载时按这个名字把它一起删掉。</summary>
        internal const string HostPrefix = "NT_Menu_";
        internal static string HostName(string paramName) { return HostPrefix + paramName; }

        // ── 我方所有 MA 组件 + 子物体的**唯一容器** ────────────────────────────────
        //   用户 2026-09-17 要求：「将所有 ma 组件和子物体都放在一个物体下」。
        //
        //   <Avatar>
        //     └── _NonToonLight                    ← MergeAnimator ×N + Parameters 都在这里
        //           ├── NT_Menu_*                  ← MenuItem + MenuInstaller
        //           └── NonToon_RealtimeShadow     ← ⑥ 的灯架（组件 + 两个约束）
        //                 └── Spot Light           ← Light
        //
        //   ⇒ **avatar 根节点上零我方组件**：卸载 = 删掉这一个物体。
        internal const string ContainerName = "_NonToonLight";

        internal static GameObject EnsureContainer(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;
            var t = avatarRoot.transform.Find(ContainerName);
            if (t != null) return t.gameObject;
            var go = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(go, "NonToon 容器");
            go.transform.SetParent(avatarRoot.transform, false);
            return go;
        }

        /// <summary>把 MergeAnimator 的 `relativePathRoot` 指到 **avatar 根**。
        ///
        /// ⛔ **这一步不能省，省了就是静默全废。** MA 的基路径逻辑
        /// （`Packages/nadena.dev.modular-avatar/Editor/MergeAnimatorProcessor.cs:72-83`）是：
        /// ```csharp
        /// if (merge.pathMode == MergeAnimatorPathMode.Absolute) return "";
        /// var targetObject = merge.relativePathRoot.Get(context.AvatarRootTransform);
        /// if (targetObject == null) targetObject = merge.gameObject;   // ← 回落！
        /// var relativePath = RuntimeUtil.RelativePath(context.AvatarRootObject, targetObject);
        /// ```
        /// ⇒ 组件搬进 `_NonToonLight` 而 `relativePathRoot` 为空时，基路径变成 `_NonToonLight`，
        ///   clip 里所有「相对 avatar 根」的路径被整体加前缀 ⇒ **不报错、纯静默失效**
        ///   （材质旋钮与开关全部没反应，最难查的一类故障）。
        /// 设成 avatar 根 ⇒ 基路径 `""` ⇒ 与「组件挂在 avatar 根上」**完全等价**。
        ///
        /// 官方中文文档同义：「`Relative` 模式下所有路径相对于一个对象，**通常是组件所在对象**……
        /// 您可以通过设定 `Relative Path Root` 来指定动画路径的根路径。」</summary>
        static void SetRelativePathRoot(Component merge, GameObject avatarRoot)
        {
            if (merge == null || avatarRoot == null) return;
            var f = F(merge.GetType(), "relativePathRoot");
            if (f == null) return;
            var reference = f.GetValue(merge);
            if (reference == null)
            {
                reference = Activator.CreateInstance(f.FieldType);
                f.SetValue(merge, reference);
            }
            // 用 MA 自己的 `Set(GameObject)`：它会写 referencePath = "$$$AVATAR_ROOT$$$" + targetObject
            var set = f.FieldType.GetMethod("Set", new[] { typeof(GameObject) });
            if (set != null) { try { set.Invoke(reference, new object[] { avatarRoot }); } catch { } }

            // ⚠️ 强制回写哨兵值。`Set()` 只在 `RuntimeUtil.IsAvatarRoot(target)` 为真时才写
            //    `$$$AVATAR_ROOT$$$`；否则写 `AvatarRootPath(root)`（可能是**空串**），
            //    而 `Get()` 见到空串**直接返回 null** ⇒ 又回落成 `_NonToonLight` ⇒ 静默失效。
            //    所以这里不依赖 `Set()` 的判断，直接把哨兵和 targetObject 都写死。
            var rp = f.FieldType.GetField("referencePath", BindingFlags.Public | BindingFlags.Instance);
            var avRoot = f.FieldType.GetField("AVATAR_ROOT", BindingFlags.Public | BindingFlags.Static);
            if (rp != null && avRoot != null) rp.SetValue(reference, avRoot.GetValue(null));
            var to = f.FieldType.GetField("targetObject", BindingFlags.NonPublic | BindingFlags.Instance);
            if (to != null) to.SetValue(reference, avatarRoot);
        }

        /// <summary>把早期版本挂在 **avatar 根 / 别处**、且 `animator` 正好是**我们这个控制器**的
        /// MergeAnimator 迁走（组件不能换物体，只能删掉再在容器上重建）。
        /// ⚠️ **只动 `animator` 严格等于本控制器的那一个**：别人的 MergeAnimator（空引用 /
        /// 指向别的控制器）一律不碰，否则会误伤用户 avatar 上其它资产的动画层。
        ///
        /// 🆕 另外清掉 **avatar 根直属的空壳**（`animator == null`）：那是我们早期安装器留下的
        /// 僵尸 —— 旧逻辑用"复用一个 animator 为空的组件"来避免堆垃圾，结果这个空壳留在了根上。
        /// 边界故意收得很紧：**只删 avatar 根的直属子物体上的**，不看深层（别人预制件里的空壳
        /// 通常在它们自己的子物体下，不受影响）。删除走 `Undo.DestroyObjectImmediate` ⇒ 可 Ctrl+Z。
        /// 功能上它本来就是无害的（MA 的 `Editor/MergeAnimatorProcessor.cs:167-170`
        /// 明确 `if (merge.animator == null) return;`），删它是为了满足
        /// "avatar 根上零我方组件"的结构要求。</summary>
        static void MigrateLegacyMerges(GameObject avatarRoot, GameObject container, Type mergeType,
                                        AnimatorController controller, Result r)
        {
            var animField = F(mergeType, "animator");
            if (animField == null) return;
            foreach (var c in avatarRoot.GetComponentsInChildren(mergeType, true).ToArray())
            {
                if (c == null || c.gameObject == container) continue;
                var cur = animField.GetValue(c) as UnityEngine.Object;
                if (ReferenceEquals(cur, controller))
                {
                    r.Done.Add("迁移：旧 MergeAnimator 原来挂在 @" + c.gameObject.name + "，改挂到 " + ContainerName);
                    Undo.DestroyObjectImmediate(c);
                    continue;
                }
                // 空壳：只在"挂在 avatar 根自己身上 / 是根的直属子物体"时才清（边界见方法注释）
                // ⚠️ 必须同时判 `c.transform == avatarRoot.transform` —— 旧安装器是把这个组件
                //    **加在 avatar 根自己身上**的，那种情况下 `c.transform.parent` 是 `null`
                //    而不是 `avatarRoot.transform`（我第一版就写错了，僵尸没被清掉）。
                if (cur == null
                    && (c.transform == avatarRoot.transform || c.transform.parent == avatarRoot.transform))
                {
                    r.Done.Add("清理：avatar 根上的空 MergeAnimator（僵尸空壳，MA 本来就会跳过它）已移除 @" + c.gameObject.name);
                    Undo.DestroyObjectImmediate(c);
                }
            }
        }

        /// <summary>确保**容器上**有一个 `ModularAvatarParameters`，并把**我们自己的**参数
        /// （名字以 `NT_` 开头）从别处（早期版本挂在 avatar 根上）搬进来。
        /// ⚠️ 只搬 `NT_` 前缀的条目 —— 用户的 `ModularAvatarParameters` 里可能混着其它资产的参数，
        /// 一条都不动。被搬空了的旧组件会被删掉（避免留一个空壳）。
        /// 位置无所谓：MA/NDMF 用 provider 模式（`MAParametersIntrospection` 带
        /// `[ParameterProviderFor(typeof(ModularAvatarParameters))]`）扫全层级收集。</summary>
        static Component EnsureParameters(GameObject container, GameObject avatarRoot, Type paramsType,
                                          Type cfgType, Result r)
        {
            var target = AddOrGet(container, paramsType);
            var listField = F(paramsType, "parameters");
            var nameField = F(cfgType, "nameOrPrefix");
            if (listField == null || nameField == null) return target;
            var targetList = listField.GetValue(target) as IList;
            if (targetList == null)
            {
                targetList = (IList)Activator.CreateInstance(listField.FieldType);
                listField.SetValue(target, targetList);
            }
            foreach (var other in avatarRoot.GetComponentsInChildren(paramsType, true).ToArray())
            {
                if (other == null || other == target) continue;
                var src = listField.GetValue(other) as IList;
                if (src == null) continue;
                for (var i = src.Count - 1; i >= 0; i--)
                {
                    var nm = nameField.GetValue(src[i]) as string;
                    if (nm == null || !nm.StartsWith("NT_", StringComparison.Ordinal)) continue;
                    for (var k = targetList.Count - 1; k >= 0; k--)   // 目的地同名先删（幂等）
                        if (nameField.GetValue(targetList[k]) as string == nm) targetList.RemoveAt(k);
                    targetList.Add(src[i]);
                    src.RemoveAt(i);
                }
                EditorUtility.SetDirty(other);
                if (src.Count == 0)
                {
                    r.Done.Add("迁移：旧的空 ModularAvatarParameters（原挂 @" + other.gameObject.name + "）已删除");
                    Undo.DestroyObjectImmediate(other);
                }
            }
            EditorUtility.SetDirty(target);
            return target;
        }

        // MA 版本（给面板显示用；读不到就返回 null）
        internal static string Version
        {
            get
            {
                var t = MergeAnimatorType;
                if (t == null) return null;
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(t.Assembly);
                    return info != null ? info.version : null;
                }
                catch { return null; }
            }
        }

        // ---------------------------------------------------------------- 反射小工具
        static FieldInfo F(Type t, string n)
        {
            return t?.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void SetField(object o, string n, object v)
        {
            var f = F(o?.GetType(), n);
            if (f == null) throw new MissingFieldException((o?.GetType().Name ?? "null") + "." + n);
            f.SetValue(o, v);
        }

        static void SetEnumField(object o, string n, string enumName)
        {
            var f = F(o?.GetType(), n);
            if (f == null) throw new MissingFieldException((o?.GetType().Name ?? "null") + "." + n);
            var underlying = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
            if (!underlying.IsEnum) throw new InvalidOperationException(n + " 不是枚举（" + underlying.Name + "）");
            f.SetValue(o, Enum.Parse(underlying, enumName, true));
        }

        static Component AddOrGet(GameObject go, Type t)
        {
            var existing = go.GetComponent(t);
            if (existing != null) return existing;
            return Undo.AddComponent(go, t);
        }

        /// <summary>找出 `go` 上 `animator == controller` 的那个 MergeAnimator。
        ///   找不到时**优先复用一个 `animator` 为空的组件**（fake-null 也算），而不是直接新建 ——
        ///   否则反复安装会不停堆垃圾组件。
        ///   ⚠️ 判空必须走 **Unity 的重载**（先 `as UnityEngine.Object` 再 `== null`）：
        ///   对"已销毁/被删除的引用"，`object == null` 是 **false**（Unity fake-null 坑）。
        ///   实机就是因此堆出了 2 个 MergeAnimator（一个 animator 是 fake-null 的僵尸）。</summary>
        static Component FindMergeFor(GameObject go, Type mergeType, AnimatorController controller,
                                      out List<Component> junk)
        {
            junk = new List<Component>();
            var animField = F(mergeType, "animator");
            Component emptySlot = null;
            foreach (var c in go.GetComponents(mergeType))
            {
                if (c == null) continue;
                var cur = animField == null ? null : animField.GetValue(c) as UnityEngine.Object;
                if (cur == null)
                {
                    if (emptySlot == null) emptySlot = c;   // 留一个复用
                    else junk.Add(c);                       // 多出来的都是垃圾
                    continue;
                }
                if (ReferenceEquals(cur, controller)) return c;
            }
            return emptySlot;
        }

        // ---------------------------------------------------------------- 追加控制项
        // ⑥ 实时自阴影需要**多个**菜单控制项（开关 / 软硬 / 阴影下限）。
        // MA 的模型是：一个 avatar 根上挂**一个** `ModularAvatarParameters`（里面是参数列表）
        // 与**一个** `ModularAvatarMenuInstaller`，而每个菜单项是**各自 GameObject 上的**
        // `ModularAvatarMenuItem` —— 所以"加一个控制项"= 追加一条参数 + 在指定物体上挂一个 MenuItem。

        /// <summary>往 avatar 根的 `ModularAvatarParameters` 里追加一条参数（同名先删，幂等）。
        ///   `syncType`：`"Float"` / `"Int"` / `"Bool"`（VRChat 里 **Bool = 1 bit，Float/Int = 8 bit**）。
        ///   `saved`：VRChat 的「跨世界保存」标记。
        ///   ⚠️ **VRChat 规则：`Saved` 必须同时 `Synced`** —— 实测把 `saved=true` + `localOnly=true`
        ///   交给 MA，烘焙出来的 `ExpressionParameters` 仍然是 `networkSynced = True`。
        ///   所以「省参数（不同步 = 0 bit）」和「记住设置（Saved）」**不可兼得**。</summary>
        internal static Result AddParameter(GameObject avatarRoot, string paramName, bool synced, float defaultValue,
                                            bool saved = false, string syncType = "Float")
        {
            var r = new Result();
            var paramsType = Find(NS + "ModularAvatarParameters");
            var cfgType = Find(NS + "ParameterConfig");
            if (avatarRoot == null || paramsType == null || cfgType == null)
            { r.Errors.Add("Modular Avatar 的 Parameters / ParameterConfig 不可用"); return r; }
            try
            {
                var container = EnsureContainer(avatarRoot);
                var pars = EnsureParameters(container, avatarRoot, paramsType, cfgType, r);
                var listField = F(paramsType, "parameters");
                var list = listField.GetValue(pars) as IList;
                if (list == null)
                {
                    list = (IList)Activator.CreateInstance(listField.FieldType);
                    listField.SetValue(pars, list);
                }
                for (var i = list.Count - 1; i >= 0; i--)
                    if (F(cfgType, "nameOrPrefix")?.GetValue(list[i]) as string == paramName) list.RemoveAt(i);
                var cfg = Activator.CreateInstance(cfgType);
                SetField(cfg, "nameOrPrefix", paramName);
                SetEnumField(cfg, "syncType", syncType);
                SetField(cfg, "localOnly", !synced);
                SetField(cfg, "saved", saved);
                SetField(cfg, "defaultValue", defaultValue);
                SetField(cfg, "hasExplicitDefaultValue", true);
                // 让 MA 在构建时把 AnimatorController 里这几个参数的默认值也按这里的默认值覆盖。
                // 不做的话：Unity 侧（Play / 手势管理器）混合树按 0 求值 ⇒ 开关参数被驱动成 0，
                // avatar 一加载功能就"自己关了"。（私有字段，MA 1.18.7 实测存在。）
                TrySet(cfg, "m_overrideAnimatorDefaults", true);
                list.Add(cfg);
                EditorUtility.SetDirty(pars);
                r.Done.Add("MA Parameters → Float " + paramName + "（默认 " + defaultValue.ToString("F2") + "）");
            }
            catch (Exception e) { r.Errors.Add("追加参数失败：" + (e.InnerException ?? e).Message); }
            return r;
        }

        /// <summary>在 `host` 上挂一个菜单项。
        ///   `controlType`：`RadialPuppet`（连续量）/ `Toggle`（开关）。
        ///   `addInstaller`：**子菜单里的子项必须传 false** —— 它们靠"父 MenuItem 的 Children 模式"
        ///   自动收进去；如果同时给它们各挂一个 installer，它们会**又被装到根菜单**（重复）。</summary>
        internal static Result AddMenuItemOn(GameObject host, GameObject avatarRoot,
                                             string paramName, bool synced, string menuLabel,
                                             string controlType = "RadialPuppet",
                                             bool addInstaller = true)
        {
            var r = new Result();
            var itemType = Find(NS + "ModularAvatarMenuItem");
            var installerType = Find(NS + "ModularAvatarMenuInstaller");
            if (host == null || itemType == null || installerType == null)
            { r.Errors.Add("Modular Avatar 的 MenuItem / MenuInstaller 不可用"); return r; }
            try
            {
                var item = AddOrGet(host, itemType);
                SetField(item, "label", menuLabel);
                SetField(item, "isSynced", synced);
                // isSaved 只在"MA 自己补建参数"时才起作用；我们的参数都是显式声明的、且不该占
                // VRChat 的 saved 额度 ⇒ 关掉，与 `ParameterConfig.saved = false` 保持一致。
                SetField(item, "isSaved", false);
                SetControl(item, itemType, paramName, synced, controlType);
                EditorUtility.SetDirty(item);
                // ⛔ installer 必须挂在 **host 自己**身上，不能只挂在 avatar 根。
                //    MA 的 `VirtualMenu.RegisterMenuInstaller` 只认
                //    `installer.GetComponent<MenuSource>()`（同一个 GameObject，不是 InChildren），
                //    而 `PushNode(installer)` 也只处理自己这一个 MenuItem
                //    ⇒ 一个 installer = 安装它自己那一个菜单项。
                //    `installTargetMenu = null` 时目标就是**根菜单**，所以每个 host 各挂一个
                //    就能得到"根菜单里 N 个平级控制项"。
                //    （实机踩过：只挂根上一个 installer ⇒ 另外两项根本没进菜单。）
                if (addInstaller) AddOrGet(host, installerType);
                r.Done.Add("MA Menu Item → " + controlType + "「" + menuLabel + "」绑定 " + paramName
                         + (addInstaller ? "" : "（子菜单子项，不单独装 installer）"));
            }
            catch (Exception e) { r.Errors.Add("挂菜单项失败：" + (e.InnerException ?? e).Message); }
            return r;
        }

        /// <summary>在 `host` 上挂一个**子菜单**，内容来自它**直接子物体**上的 MenuItem。
        ///
        ///   这就是 MA 官方文档 `menu-item#submenus` 说的做法，也对应
        ///   `ModularAvatarMenuItem.Visit()`：
        ///   ```csharp
        ///   if (cloned.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
        ///       case SubmenuSource.Children:
        ///           cloned.SubmenuNode = context.NodeFor(new MenuNodesUnder(root));   // root = 本物体（或 override）
        ///   ```
        ///   ⇒ 三个必要条件：`Control.type = SubMenu`、`MenuSource = Children`、
        ///      子项 MenuItem 挂在本物体的**直接子级**。
        ///   外层（把子菜单挂到根菜单）仍然靠本物体上的 installer。</summary>
        internal static Result AddSubmenuOn(GameObject host, GameObject avatarRoot, string label)
        {
            var r = new Result();
            var itemType = Find(NS + "ModularAvatarMenuItem");
            var installerType = Find(NS + "ModularAvatarMenuInstaller");
            if (host == null || itemType == null || installerType == null)
            { r.Errors.Add("Modular Avatar 的 MenuItem / MenuInstaller 不可用"); return r; }
            try
            {
                var item = AddOrGet(host, itemType);
                SetField(item, "label", label);
                SetField(item, "isSynced", false);
                SetField(item, "isSaved", false);
                // 参数名传 null ⇒ parameter / subParameters 都是空数组（子菜单本身不带参数）
                SetControl(item, itemType, null, false, "SubMenu");
                SetEnumField(item, "MenuSource", "Children");
                TrySet(item, "menuSource_otherObjectChildren", null);   // null = 用本物体的直接子级
                EditorUtility.SetDirty(item);
                AddOrGet(host, installerType);                          // 把子菜单本身挂到根菜单
                r.Done.Add("MA 子菜单「" + label + "」：内容取 @" + host.name + " 的直接子级（MenuSource=Children）");
            }
            catch (Exception e) { r.Errors.Add("挂子菜单失败：" + (e.InnerException ?? e).Message); }
            return r;
        }

        /// <summary>只挂"合并控制器"这一半（MergeAnimator），不碰菜单。
        ///   `Attach` = 本方法 + 参数 + 菜单项 + installer；做子菜单时用这个更合适。</summary>
        internal static Result AttachControllerOnly(GameObject avatarRoot, AnimatorController controller)
        {
            var r = new Result();
            var mergeType = MergeAnimatorType;
            if (avatarRoot == null || controller == null || mergeType == null)
            { r.Errors.Add("attach 控制器失败：参数或 Modular Avatar 不可用"); return r; }
            try
            {
                var container = EnsureContainer(avatarRoot);
                MigrateLegacyMerges(avatarRoot, container, mergeType, controller, r);

                List<Component> junk;
                var merge = FindMergeFor(container, mergeType, controller, out junk);
                foreach (var j in junk) { Undo.DestroyObjectImmediate(j); }
                if (merge == null) merge = Undo.AddComponent(container, mergeType);
                SetField(merge, "animator", controller);
                SetEnumField(merge, "layerType", "FX");
                SetEnumField(merge, "pathMode", "Relative");
                // ⛔ 必须设：组件已不在 avatar 根上，不设 relativePathRoot 的话基路径会变成
                //    `_NonToonLight`，clip 路径被整体加前缀 ⇒ 动画静默失效（见方法注释）。
                SetRelativePathRoot(merge, avatarRoot);
                SetField(merge, "matchAvatarWriteDefaults", true);
                EditorUtility.SetDirty(merge);
                r.Done.Add("MA Merge Animator → " + controller.name + "（挂 @" + container.name
                         + "；FX 层、Relative + 相对路径根 = avatar 根、匹配 Write Defaults）");
            }
            catch (Exception e) { r.Errors.Add("挂 MergeAnimator 失败：" + (e.InnerException ?? e).Message); }
            return r;
        }

        // ---------------------------------------------------------------- 主入口
        // controller：NTVrcParameterBuilder 以 standalone 模式生成的独立控制器
        // saved：保持 true 以兼容既有调用方（④ 光照调节）；⑥ 传 false（装饰性旋钮不该占 saved 额度）
        internal static Result Attach(GameObject avatarRoot, AnimatorController controller, string paramName,
                                      bool synced, float defaultValue, string menuLabel, bool saved = true)
        {
            var r = new Result();
            if (avatarRoot == null) { r.Errors.Add("没有 avatar 根节点"); return r; }
            if (controller == null) { r.Errors.Add("没有可合并的 AnimatorController"); return r; }

            var mergeType = MergeAnimatorType;
            var paramsType = Find(NS + "ModularAvatarParameters");
            var itemType = Find(NS + "ModularAvatarMenuItem");
            var installerType = Find(NS + "ModularAvatarMenuInstaller");
            var cfgType = Find(NS + "ParameterConfig");
            var missing = new List<string>();
            if (mergeType == null) missing.Add("MergeAnimator");
            if (paramsType == null) missing.Add("Parameters");
            if (itemType == null) missing.Add("MenuItem");
            if (installerType == null) missing.Add("MenuInstaller");
            if (cfgType == null) missing.Add("ParameterConfig");
            if (missing.Count > 0) { r.Errors.Add("Modular Avatar 组件不完整，缺少：" + string.Join("、", missing)); return r; }

            try
            {
                // 0) 容器：我方所有东西的唯一落脚点（用户 2026-09-17 要求）
                var container = EnsureContainer(avatarRoot);

                // 1) Merge Animator
                // ⛔ **每个控制器一个 `ModularAvatarMergeAnimator` 组件**，不能复用！
                //    MA 的 `MergeAnimator` 一个组件只带**一个** `animator` 引用；用
                //    `AddOrGet(avatarRoot, mergeType)` 复用的话，后装的功能会把先装的
                //    `animator` 直接覆盖掉 —— 实机实测：装完 ⑥ 之后，⑤ 的
                //    `NonToon LightMinLimit` 层从烘焙结果里**整个消失**（它换成了 ⑥ 的控制器）。
                //    MA 支持同一物体上挂多个 MergeAnimator（会全部合并），所以这里按控制器去找；
                //    找不到就复用一个 `animator` 为空的（避免反复安装堆垃圾），仍然没有才新建。
                MigrateLegacyMerges(avatarRoot, container, mergeType, controller, r);
                List<Component> junk;
                var merge = FindMergeFor(container, mergeType, controller, out junk);
                foreach (var j in junk) { Undo.DestroyObjectImmediate(j); }
                if (merge == null) merge = Undo.AddComponent(container, mergeType);
                SetField(merge, "animator", controller);
                SetEnumField(merge, "layerType", "FX");          // VRCAvatarDescriptor.AnimLayerType.FX
                SetEnumField(merge, "pathMode", "Relative");     // MergeAnimatorPathMode.Relative
                // ⛔ 必须设：见 SetRelativePathRoot 的注释（不设 = 路径被加前缀 = 动画静默失效）
                SetRelativePathRoot(merge, avatarRoot);
                SetField(merge, "matchAvatarWriteDefaults", true);
                EditorUtility.SetDirty(merge);
                r.Done.Add("MA Merge Animator → " + controller.name + "（挂 @" + container.name
                         + "；FX 层、Relative + 相对路径根 = avatar 根、匹配 Write Defaults）");

                // 2) Parameters
                var pars = EnsureParameters(container, avatarRoot, paramsType, cfgType, r);
                var listField = F(paramsType, "parameters");
                if (listField == null) throw new MissingFieldException("ModularAvatarParameters.parameters");
                var list = listField.GetValue(pars) as IList;
                if (list == null)
                {
                    list = (IList)Activator.CreateInstance(listField.FieldType);
                    listField.SetValue(pars, list);
                }
                for (var i = list.Count - 1; i >= 0; i--)       // 幂等：同名先删
                {
                    var existingName = F(cfgType, "nameOrPrefix")?.GetValue(list[i]) as string;
                    if (existingName == paramName) list.RemoveAt(i);
                }
                var cfg = Activator.CreateInstance(cfgType);
                SetField(cfg, "nameOrPrefix", paramName);
                SetEnumField(cfg, "syncType", "Float");         // ParameterSyncType.Float
                SetField(cfg, "localOnly", !synced);            // 「同步给别人看」的开关
                SetField(cfg, "saved", saved);
                SetField(cfg, "defaultValue", defaultValue);
                SetField(cfg, "hasExplicitDefaultValue", true);
                // 让 MA 在构建时把 AnimatorController 里这几个参数的默认值也按这里的默认值覆盖。
                // 不做的话：Unity 侧（Play / 手势管理器）混合树按 0 求值 ⇒ 开关参数被驱动成 0，
                // avatar 一加载功能就"自己关了"。（私有字段，MA 1.18.7 实测存在。）
                TrySet(cfg, "m_overrideAnimatorDefaults", true);
                list.Add(cfg);
                EditorUtility.SetDirty(pars);
                r.Done.Add("MA Parameters → Float " + paramName + "（默认 " + defaultValue.ToString("F2")
                           + (synced ? "，同步给别人" : "，仅自己可见（localOnly）") + "）");

                // 3) Menu Item（径向）
                // ⛔ **不能把 MenuItem 挂在 avatar 根上**：⑤（亮度）与 ⑥（实时阴影）都会调 `Attach`，
                //    而 `AddOrGet(avatarRoot, itemType)` 会让它们**共用同一个 `ModularAvatarMenuItem` 组件** ——
                //    后装的把先装的整个覆盖掉。实机实测：装了 ⑤ 之后，⑥ 的「实时阴影 软硬」直接从菜单里消失
                //    （根上那个 MenuItem 的 label/parameter 被改成了「亮度」/`NT_Light`）。
                //    ⇒ 每个参数用**自己专属的 host 物体**，MenuItem + MenuInstaller 都放在它上面。
                //       MA 的 `RegisterMenuInstaller` 只认同 GameObject 的 MenuSource，
                //       而 `installTargetMenu = null` 时装进根菜单 ⇒ 对外表现与挂在根上完全一样。
                //    ⚠️ host 现在放在**容器**里（不是 avatar 根）。MA 用
                //       `GetComponentsInChildren` 找 installer/MenuItem，深层无所谓。
                var hostT = container.transform.Find(HostName(paramName));
                if (hostT == null)
                {
                    // 迁移：早期版本挂在 avatar 根上的同名 host —— 直接搬进来（保留里面的组件）
                    var legacy = avatarRoot.transform.Find(HostName(paramName));
                    if (legacy != null)
                    {
                        legacy.SetParent(container.transform, false);
                        hostT = legacy;
                        r.Done.Add("迁移：菜单 host " + legacy.name + " 从 avatar 根移到 " + ContainerName);
                    }
                }
                GameObject host;
                if (hostT == null)
                {
                    host = new GameObject(HostName(paramName));
                    Undo.RegisterCreatedObjectUndo(host, "NonToon menu host");
                    host.transform.SetParent(container.transform, false);
                }
                else host = hostT.gameObject;

                var item = AddOrGet(host, itemType);
                SetField(item, "label", menuLabel);
                SetField(item, "isSynced", synced);
                SetField(item, "isSaved", saved);   // 与 ParameterConfig.saved 一致（⑥ 传 false）
                SetControl(item, itemType, paramName, synced, "RadialPuppet");
                EditorUtility.SetDirty(item);
                r.Done.Add("MA Menu Item → 径向「" + menuLabel + "」绑定 " + paramName + "（专属 host：" + host.name + "）");

                // 4) Menu Installer（没有它 MA 根本不会装菜单；必须与 MenuItem 同物体）
                var installer = AddOrGet(host, installerType);
                EditorUtility.SetDirty(installer);
                r.Done.Add("MA Menu Installer → 把上面的菜单项装进 avatar 根菜单（不需要手动摆菜单资产）");
            }
            catch (Exception e)
            {
                r.Errors.Add("挂 MA 组件失败：" + (e.InnerException ?? e).Message);
            }
            return r;
        }

        // ModularAvatarMenuItem.Control 在 MA_VRCSDK3_AVATARS 下是 `VRCExpressionsMenu.Control?`
        // （**可空引用类型**，不是 Nullable<T>）；`PortableControl` 只是它的只读包装
        // （读 MA 1.18.7 的 Runtime/ModularAvatarMenuItem.cs 确认：PortableMenuControl.BackingControl
        //   直接 get/set `BackingMenuItem.Control`）⇒ 只需要设 `Control` 这一个字段。
        static void SetControl(Component item, Type itemType, string paramName, bool synced,
                               string controlTypeName = "RadialPuppet")
        {
            var ctrlField = F(itemType, "Control");
            var portableField = F(itemType, "Control") == null ? F(itemType, "control") : null;
            var field = ctrlField ?? portableField;
            if (field == null) throw new MissingFieldException("ModularAvatarMenuItem.Control");

            var fieldType = field.FieldType;
            var underlying = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
            var control = Activator.CreateInstance(underlying);

            // name / type / parameter / value —— 逐项尽力设置（字段名变化时不至于整体失败）
            TrySet(control, "name", "亮度");
            var typeField = F(underlying, "type");
            if (typeField != null && typeField.FieldType.IsEnum)
                typeField.SetValue(control, Enum.Parse(typeField.FieldType, controlTypeName, true));

            // ⛔⛔ **参数放哪个字段取决于控件类型** —— 这里曾经把参数一律写进 `parameter`、
            //   把 `subParameters` 留空，后果（实机 Play 复现）：
            //     · GestureManager 抛 `IndexOutOfRangeException`（`RadialPuppet.Get => Control.GetSubValue(0)`
            //       → `_subParameters[0]`）⇒ **游戏内那条径向整个是空的（"轮盘缺口"）**；
            //     · VRChat 里径向也拉不动任何参数。
            //   VRCSDK 自己的编辑器就是这么分的（`ExpressionsControlOptions.cs`）：
            //     case RadialPuppet:  propSubParameters.arraySize = 1;  ← 参数在 subParameters[0]
            //     case Toggle:        （parameter 才是那个参数）
            //   ⇒ RadialPuppet / TwoAxisPuppet / FourAxisPuppet 一律用 `subParameters`（长度 1 或 2/4），
            //      Button / Toggle 用 `parameter`。
            var paramField = F(underlying, "parameter");
            var subField = F(underlying, "subParameters");
            var labelsField = F(underlying, "labels");
            TrySet(control, "value", 1f);

            var isPuppet = !string.IsNullOrEmpty(controlTypeName)
                           && controlTypeName.IndexOf("Puppet", StringComparison.OrdinalIgnoreCase) >= 0;

            var paramType = paramField != null ? paramField.FieldType
                          : (subField != null ? subField.FieldType.GetElementType() : null);
            var pname = paramName ?? string.Empty;      // 子菜单没有参数 ⇒ 传 null 时写成空串（别留 null 给 VRChat）
            if (paramType != null)
            {
                if (paramField != null)
                {
                    var ps = Activator.CreateInstance(paramType);
                    TrySet(ps, "name", isPuppet ? string.Empty : pname);
                    paramField.SetValue(control, ps);
                }
                if (subField != null)
                {
                    var n = isPuppet ? 1 : 0;              // 轴向 puppet 需要 2/4，但我们只用径向
                    var arr = Array.CreateInstance(paramType, n);
                    if (n > 0)
                    {
                        var ps = Activator.CreateInstance(paramType);
                        TrySet(ps, "name", pname);
                        arr.SetValue(ps, 0);
                    }
                    subField.SetValue(control, arr);
                }
            }
            // labels 留空（VRCSDK 在 RadialPuppet 分支里也把它清成 0）
            if (labelsField != null)
            {
                var lt = labelsField.FieldType.GetElementType();
                if (lt != null) labelsField.SetValue(control, Array.CreateInstance(lt, 0));
            }
            // subMenu 保持 null（MA 会用 MenuItem 上的 `label` 当显示名）
            var boxed = underlying == fieldType ? control : Activator.CreateInstance(fieldType, control);
            field.SetValue(item, boxed);
        }

        static void TrySet(object o, string name, object value)
        {
            var f = F(o?.GetType(), name);
            if (f != null) f.SetValue(o, value);
        }
    }
}
