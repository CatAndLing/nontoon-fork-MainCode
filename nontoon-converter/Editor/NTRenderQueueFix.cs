// [NT-FIX 31] 渲染队列兜底修复 —— 透明件排序。
//
// ── 为什么需要它（2026-09-17 实测确认，不是推测）────────────────────────────
// NonToon 的两个 `.scshader` 的 SubShader **都没有 `Queue` 标签**
// （`NonToon.scshader:57-62` / `:224-231` 的 Tags 里只有 RenderType / RenderPipeline /
//  UniversalMaterialType / IgnoreProjector / LTCGI；`NonToonFur.scshader` 那个
//  `"Queue" = "AlphaTest"` 是**硬编码**的，与渲染模式无关）。
// ⇒ 材质的 renderQueue 只能靠**材质覆盖**（`m_CustomRenderQueue`）来定，
//   而写它的地方全项目只有两处：
//     ① `NonToon/Editor/RenderingModeElement.cs` —— **只在用户手动拨下拉框时**执行
//        （它用 `new StackFrame(3,false).GetMethod().ToString() == "Void ChangeValueFromMenu(Int32)"`
//         做门控，脚本/撤销/粘贴都进不去）；
//     ② 转换器（`LilToonToNonToonConverter.cs:1141`）。
//   ⇒ 任何**其它**途径改 `_RenderingMode`（脚本、MaterialProperty、Undo、复制粘贴、
//     第三方工具、ShaderCore 面板 reset）都会让队列**陈旧**。实测两个方向：
//       · Opaque 材质被脚本改成 Transparent ⇒ renderQueue 仍是 **2000 (Geometry)**
//         ⇒ 透明件被当**不透明**排序；
//       · Transparent 材质被脚本改回 Opaque ⇒ renderQueue 仍是 **3000**
//         ⇒ 不透明件**留在透明队列**（更糟：它会最后画、还写深度）。
//
// 本命令就是那条兜底路径：按 `_RenderingMode` 把队列重新对齐。幂等，可反复跑。
//
// ── 映射（与 `NTRenderingModeElement` 保持一致）—— **现在只是"回退值"，不是策略** ──
//   0 Opaque      → -1   （= 用 shader 默认，即 Geometry 2000）
//   1 Cutout      → 2450 （AlphaTest）
//   2 Transparent → 3000 （Transparent）
//
// ⛔⛔ `[NT-FIX 9]`（"透明一律统一成 3000"）**已被 `[NT-FIX 32]` 推翻，别再改回去**：
//   本命令与 `NTRenderingModeElement` 现在**只允许改写"看起来像模式默认值"的队列**
//   （-1 / 2000 / 2450 / 3000）；任何别的值都视为**作者/转换器有意设置**，一律跳过。
//   为什么：转换器自 `[NT-FIX 32]` 起**照搬源材质的生效队列**。实测辉夜模型里
//     · `age` / `face_transparent` = 2460（由源 shader 声明）、`HeartGun` = 2900（Gem）；
//     · **`stockings` = 2450、`tail_Crystal` = 3005 都是作者手动覆盖** ——
//       后者是典型的"保证它最后画"。旧策略把这两个值双双碾成 3000，排序意图被彻底破坏。
//   机理：BiRP 默认 ≤2500 按不透明排序、>2500 才按透明排序 ⇒ 改队列会同时改变
//   「相对其它材质的位置」与「默认距离排序策略」；配合 lilToon 透明一律 `_ZWrite = 1`，
//   画序直接决定谁先写深度、谁被后来的片元剔除。
//   （lilToon 自己在 Worlds 里有 `FixTransparentRenderQueue()` 强制 3000，但被
//    `#if LILTOON_VRCSDK3_WORLDS` 包着，**Avatar 用不到** —— 那也说明"3000 更对"只适用于 World。）
//
// ⚠️ `_ZWrite` **不在本命令的职责内**，而且它**不是 bug**：
//   透明档不去改 `_ZWrite` 与 lilToon 一致 —— lilToon 除 `Gem` 外一律 `_ZWrite = 1`
//   （`lilMaterialUtils.cs:266-276`）。别照着"透明就该 ZWrite Off"的直觉去改。
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    internal static class NTRenderQueueFix
    {
        // 与 NTRenderingModeElement / 转换器同源。改这里必须同步改那两处。
        internal static int QueueFor(int mode)
        {
            return mode == 1 ? 2450 : mode == 2 ? 3000 : -1;
        }

        internal static bool IsNonToonMaterial(Material m)
        {
            return m != null && m.shader != null && m.shader.name.StartsWith("nontoon-fork");
        }

        [MenuItem("Tools/NonToon/⑦ 修复渲染队列（透明排序）", false, 57)]
        private static void RunFromMenu()
        {
            // 有选中就只处理选中的（材质资产 + 选中的模型上的材质），否则全工程扫。
            var scope = Selection.objects != null && Selection.objects.Length > 0
                      ? "选中范围"
                      : "整个工程";
            if (!EditorUtility.DisplayDialog(
                    "NonToon ▸ 修复渲染队列",
                    "把 NonToon 材质的 renderQueue 按它的「渲染模式」重新对齐：\n\n"
                  + "  不透明 → -1（Geometry 2000）\n"
                  + "  裁剪   → 2450（AlphaTest）\n"
                  + "  透明   → 3000（Transparent）\n\n"
                  + "范围：" + scope + "\n\n"
                  + "为什么需要：着色器没有 Queue 标签，队列只能存在材质上；\n"
                  + "而它只在「手动拨下拉框」和「转换器」时被写，\n"
                  + "脚本 / 撤销 / 粘贴 / 第三方工具改过渲染模式后就会陈旧。\n\n"
                  + "本操作幂等、可 Ctrl+Z。",
                    "开始修复", "取消"))
                return;

            var report = Fix(Selection.objects != null && Selection.objects.Length > 0);
            Debug.Log(report);
            EditorUtility.DisplayDialog("NonToon ▸ 修复渲染队列", report, "好");
        }

        /// <summary>返回人类可读的报告。`selectedOnly` 时只看当前选中。</summary>
        internal static string Fix(bool selectedOnly)
        {
            var mats = new List<Material>();
            var seen = new HashSet<Material>();

            if (selectedOnly)
            {
                foreach (var m in Selection.GetFiltered<Material>(SelectionMode.Assets))
                    if (IsNonToonMaterial(m) && seen.Add(m)) mats.Add(m);
                foreach (var go in Selection.GetFiltered<GameObject>(SelectionMode.Deep))
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (IsNonToonMaterial(m) && seen.Add(m)) mats.Add(m);
            }
            else
            {
                foreach (var g in AssetDatabase.FindAssets("t:Material"))
                {
                    var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
                    if (IsNonToonMaterial(m) && seen.Add(m)) mats.Add(m);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("[NT-FIX 31] 渲染队列修复 —— 扫描 " + mats.Count + " 个 NonToon 材质");
            var fixedCount = 0;
            var skippedNoMode = 0;
            var skippedCustom = 0;
            var shown = 0;
            foreach (var m in mats)
            {
                if (!m.HasProperty("_RenderingMode"))
                {
                    // `NonToonFur` 就是这一类：它只有 `#define _RenderingMode 0`，
                    // **没有把 `_RenderingMode` 声明成属性** ⇒ 它的下拉框压根不出现，
                    // 也就没有"被拨成透明却还留在不透明队列"的问题。
                    skippedNoMode++;
                    continue;
                }
                var mode = m.GetInteger("_RenderingMode");
                var want = QueueFor(mode);
                var have = m.renderQueue;
                if (have == want) continue;
                // [NT-FIX 32] **自定义队列一律跳过。** 转换器现在照搬源材质的生效队列
                // （2460 / 2900 / 2450 / 3005 …），本命令若照旧按模式改写，就会把它们全碾平。
                if (!IsModeDefaultQueue(have)) { skippedCustom++; continue; }
                Undo.RecordObject(m, "NonToon 修复渲染队列");
                m.renderQueue = want;
                EditorUtility.SetDirty(m);
                fixedCount++;
                if (shown++ < 30)
                    sb.AppendLine("   " + m.name + "（模式 " + ModeName(mode) + "）："
                                + have + " → " + want);
            }
            if (shown >= 30 && fixedCount > 30)
                sb.AppendLine("   …另有 " + (fixedCount - 30) + " 个，见下方总数");
            if (fixedCount > 0) AssetDatabase.SaveAssets();

            sb.AppendLine("已修正 " + fixedCount + " 个"
                        + (skippedNoMode > 0 ? "；跳过 " + skippedNoMode + " 个无 `_RenderingMode` 的（如 Fur）" : "")
                        + (skippedCustom > 0 ? "；**跳过 " + skippedCustom + " 个自定义队列**（非模式默认值，视为有意设置）" : "")
                        + "。");
            sb.AppendLine();
            sb.AppendLine("提示：脚本 / 撤销 / 粘贴改过「渲染模式」之后，再跑一次本命令即可对齐。"
                        + "着色器没有 Queue 标签，所以这一步无法全自动 —— 这是已知的结构限制。");
            sb.AppendLine("注意：本命令**不会**动自定义队列（如 lilToon 的 2460、Gem 的 2900、作者手设的 3005）。"
                        + "那是有意设置，不是陈旧值。");
            return sb.ToString();
        }

        /// <summary>[NT-FIX 32] 这个队列值看起来是不是"由渲染模式推出来的默认值"？
        ///   是 ⇒ 可能陈旧（脚本/Undo/粘贴改过模式），允许改写；
        ///   不是（2460 / 2900 / 3005 …）⇒ 当成有意设置，别动。
        ///   ⚠️ 与 `NonToon/Editor/RenderingModeElement.cs` 的 `IsModeDefaultQueue` 必须逐字一致
        ///      （本包不能引用着色器包，只能各自维护一份）。</summary>
        internal static bool IsModeDefaultQueue(int queue)
        {
            return queue == -1 || queue == 2000 || queue == 2450 || queue == 3000;
        }

        private static string ModeName(int mode)
        {
            return mode == 1 ? "裁剪" : mode == 2 ? "透明" : "不透明";
        }
    }
}
