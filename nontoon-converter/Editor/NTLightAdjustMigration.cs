// [NT-FEAT 22] ⑤ 迁移：把旧版"直接改用户资产"留下的痕迹清理干净。
//
// ── 为什么需要这条命令 ────────────────────────────────────────────────────────
// 2026-09-17 之前，⑤ 在**没装 Modular Avatar** 时会走一条回退路径，直接往用户的：
//   · FX 控制器（`baseAnimationLayers` 里 type == FX 的那个）
//   · `ExpressionParameters`
//   · `ExpressionsMenu`
// 里写东西。更糟的是那条路径用 `AnimatorController.AddLayer(string)` 建层，而那个重载
// 建出来的层 **`defaultWeight = 0`** ⇒ 该层运行时零贡献 ⇒ "游戏内亮度"永远拉不动，
// 而且用户完全看不出自己的资产被动过（实机事故：`Torao_FXLayer.controller` 里留下
// 一个 `NonToon LightMinLimit` 死层 + `NT_Light` 参数 + 菜单项）。
//
// 现在 ⑤ **只走 MA 声明式**（`NTVrcParameterBuilder` 的破坏性分支已整体删除），
// 这条命令负责把旧版留下的痕迹摘掉，让用户能干净地改用 MA 重新装一遍。
//
// 它只删**我们自己的**东西，判据是明确的前缀/名字：
//   层：`NonToon ` 前缀（`NTVrcParameterBuilder.LayerPrefix`）
//   参数：`NT_Light`（⑤ 的历史默认参数名）
// 其它任何资产内容都不碰。删之前请自行备份（`git` 或复制 .controller / .asset）。
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
    internal static class NTLightAdjustMigration
    {
        // ⑤ 的历史默认参数名（`NTLightAdjust.parameterName` / 工具窗口的 `paramName`）
        const string LegacyParam = "NT_Light";

        [MenuItem("Tools/NonToon/⑤ 清理旧的非 MA 残留（迁移到 Modular Avatar）", false, 55)]
        private static void RunMenu()
        {
            var desc = FindDescriptor();
            if (desc == null)
            {
                EditorUtility.DisplayDialog("NonToon ⑤ 迁移",
                    "在选中对象（或当前场景）里找不到 VRCAvatarDescriptor。\n请先选中 avatar 根节点。", "好");
                return;
            }
            EditorUtility.DisplayDialog("NonToon ⑤ 迁移完成", Cleanup(desc), "好");
        }

        /// <summary>核心逻辑。**不弹任何对话框**（方便脚本 / 探针 / 自动化调用），返回人读报告。</summary>
        internal static string Cleanup(object desc)
        {
            var log = new List<string>();
            if (desc == null) { log.Add("没有 VRCAvatarDescriptor"); return string.Join("\n", log.ToArray()); }
            var descGo = ((Component)desc).gameObject;
            log.Add("avatar：" + descGo.name);

            // ---- 1) FX 控制器：删我们加的层 + 该参数 ----
            int layers = 0, pars = 0;
            foreach (var ctrl in FxControllers(desc))
            {
                if (ctrl == null) continue;
                var path = AssetDatabase.GetAssetPath(ctrl);

                for (int i = ctrl.layers.Length - 1; i >= 0; i--)
                    if (ctrl.layers[i].name.StartsWith(NTVrcParameterBuilder.LayerPrefix, StringComparison.Ordinal))
                    {
                        log.Add("  − 层「" + ctrl.layers[i].name + "」  @ " + path);
                        ctrl.RemoveLayer(i);
                        layers++;
                    }

                var ps = ctrl.parameters;
                for (int i = ps.Length - 1; i >= 0; i--)
                    if (ps[i].name == LegacyParam)
                    {
                        log.Add("  − 参数 " + LegacyParam + "  @ " + path);
                        ctrl.RemoveParameter(i);
                        pars++;
                    }

                if (layers > 0 || pars > 0) EditorUtility.SetDirty(ctrl);
            }

            // ---- 2) ExpressionsMenu：删指向该参数的控制项 ----
            var menu = Get(desc, "expressionsMenu");
            int menus = 0;
            if (menu != null) menus = RemoveControlsByParam(menu, LegacyParam, log, 0);

            // ---- 3) ExpressionParameters：删该参数 ----
            int eps = RemoveExpressionParameter(desc, LegacyParam, log);

            // ---- 4) 工程里的 ExpressionParameters **资产** ----
            // ⛔ 只扫 descriptor 引用的那个是不够的：实机上用户的 `Torao_Parameters.asset`
            //    里就残留着 `NT_Light`，而 **`descriptor.expressionsParameters` 是 NULL**
            //    （真 avatar 上很常见 —— NDMF/MA 在构建期才新建），于是漏掉了它，
            //    烘焙出来的同步预算里一直多占 8 bit。
            eps += StripFromAllExpressionParameterAssets(LegacyParam, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.Add("");
            log.Add("共删除：层 " + layers + "、控制器参数 " + pars + "、菜单项 " + menus + "、ExpressionParameter " + eps);
            if (layers + pars + menus + eps == 0)
                log.Add("（没找到旧残留 —— 可能这本就没走过那条老路径，或者已经清过了。）");
            log.Add("接下来：给 avatar 挂一个「⑤ 亮度调整」组件（GameObject ▸ NonToon ▸ ⑤），"
                  + "选「游戏内可调」后点生成 —— 现在只会生成资产 + MA 组件，不会再动你的资产。");

            Debug.Log("[NonToon ⑤ 迁移]\n" + string.Join("\n", log.ToArray()));
            return string.Join("\n", log.ToArray());
        }

        /// <summary>扫描**工程里所有** `VRCExpressionParameters` 资产，删掉名为 paramName 的条目。
        ///   为什么不能只看 descriptor 引用的那个：真 avatar 上 `descriptor.expressionsParameters`
        ///   往往是 **NULL**（构建期才由 NDMF/MA 新建），旧版写进别的资产里的残留就扫不到。</summary>
        static int StripFromAllExpressionParameterAssets(string paramName, List<string> log)
        {
            var epType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters");
            if (epType == null) return 0;
            int removed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets", "Packages" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ep = AssetDatabase.LoadAssetAtPath(path, epType);
                if (ep == null) continue;
                var f = F(epType, "parameters");
                var arr = f == null ? null : f.GetValue(ep) as Array;
                if (arr == null) continue;

                var kept = new List<object>();
                int hit = 0;
                foreach (var p in arr)
                {
                    if (p != null && Convert.ToString(Get(p, "name")) == paramName) { hit++; continue; }
                    kept.Add(p);
                }
                if (hit == 0) continue;

                var elem = f.FieldType.GetElementType();
                var na = Array.CreateInstance(elem, kept.Count);
                for (int i = 0; i < kept.Count; i++) na.SetValue(kept[i], i);
                f.SetValue(ep, na);
                EditorUtility.SetDirty((UnityEngine.Object)ep);
                log.Add("  − ExpressionParameters 资产 " + path + " → 删掉 " + paramName + "（释放同步位）");
                removed += hit;
            }
            return removed;
        }

        // ---------------------------------------------------------------- 工具
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

        static FieldInfo F(Type t, string n)
        {
            return t?.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static object Get(object o, string n)
        {
            return o == null ? null : F(o.GetType(), n)?.GetValue(o);
        }

        static object FindDescriptor()
        {
            var t = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", "VRC_AvatarDescriptor");
            if (t == null) return null;
            var sel = Selection.gameObjects.FirstOrDefault(go => go != null && go.GetComponent(t) != null);
            if (sel != null) return sel.GetComponent(t);
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                var c = root.GetComponent(t);
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>descriptor 的 `baseAnimationLayers` 里所有 type == FX 的控制器。</summary>
        static IEnumerable<AnimatorController> FxControllers(object desc)
        {
            var arr = Get(desc, "baseAnimationLayers") as IEnumerable;
            if (arr == null) yield break;
            foreach (var layer in arr)
            {
                if (layer == null) continue;
                if (!string.Equals(Convert.ToString(Get(layer, "type")), "FX", StringComparison.OrdinalIgnoreCase)) continue;
                var ctrl = Get(layer, "animatorController") as AnimatorController;
                if (ctrl != null) yield return ctrl;
            }
        }

        /// <summary>递归删掉 parameter.name == paramName 的菜单控制项。返回删除数量。</summary>
        static int RemoveControlsByParam(object menu, string paramName, List<string> log, int depth)
        {
            if (menu == null || depth > 8) return 0;
            var listField = F(menu.GetType(), "controls");
            var list = listField?.GetValue(menu) as IList;
            if (list == null) return 0;

            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var c = list[i];
                var p = Get(c, "parameter");
                var pn = p == null ? null : Convert.ToString(Get(p, "name"));
                if (pn == paramName)
                {
                    log.Add("  − 菜单项「" + Get(c, "name") + "」→ " + paramName);
                    list.RemoveAt(i);
                    removed++;
                }
                else
                {
                    var sub = Get(c, "subMenu");
                    if (sub != null) removed += RemoveControlsByParam(sub, paramName, log, depth + 1);
                }
            }
            if (removed > 0) EditorUtility.SetDirty((UnityEngine.Object)menu);
            return removed;
        }

        /// <summary>从 descriptor 的 ExpressionParameters 里删掉名为 paramName 的条目。</summary>
        static int RemoveExpressionParameter(object desc, string paramName, List<string> log)
        {
            var ep = Get(desc, "expressionsParameters");
            if (ep == null) return 0;
            var f = F(ep.GetType(), "parameters");
            var arr = f?.GetValue(ep) as Array;
            if (arr == null) return 0;

            var kept = new List<object>();
            int removed = 0;
            foreach (var p in arr)
            {
                if (p != null && Convert.ToString(Get(p, "name")) == paramName) { removed++; continue; }
                kept.Add(p);
            }
            if (removed == 0) return 0;

            var elem = f.FieldType.GetElementType();
            var newArr = Array.CreateInstance(elem, kept.Count);
            for (int i = 0; i < kept.Count; i++) newArr.SetValue(kept[i], i);
            f.SetValue(ep, newArr);
            EditorUtility.SetDirty((UnityEngine.Object)ep);
            log.Add("  − ExpressionParameter " + paramName + "（占用的同步位已释放）");
            return removed;
        }
    }
}
