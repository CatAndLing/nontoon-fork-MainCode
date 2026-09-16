// [NT-FEAT 20] ⑥ 实时自阴影 —— 菜单入口（安装 / 卸载）。
//
// 判据：安装后场景里出现 `NonOn_RealtimeShadow` 灯架；卸载后干净移除。
// 这一步**不写任何用户资产**：灯的强度/软硬/开关都在组件上，菜单动画驱动组件字段。
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NonToonTools
{
    internal static class NTSelfRealtimeMenu
    {
        const string Menu = "Tools/NonToon/";

        static GameObject SelectedAvatarRoot()
        {
            var go = Selection.activeGameObject;
            if (go == null) return null;
            var t = go.transform;
            while (t.parent != null) t = t.parent;
            return t.gameObject;
        }

        [MenuItem(Menu + "⑥ 实时自阴影：安装到选中的 avatar", false, 620)]
        static void InstallDefault()
        {
            var root = SelectedAvatarRoot();
            if (root == null) { EditorUtility.DisplayDialog("NonToon ⑥", "请先在 Hierarchy 里选中 avatar 的任意物体。", "好"); return; }
            var r = NTSelfRealtimeSetup.Install(root, includeOtherPlayers: false,
                                                intensity: 0.8f, lightSize: 64f, softness: 1f,
                                                range: 6f, spotAngle: 50f, addMenu: true);
            Report(r, "安装 ⑥ 实时自阴影（只照自己）");
        }

        [MenuItem(Menu + "⑥ 实时自阴影：安装（让其他人也能看到）", false, 621)]
        static void InstallShared()
        {
            var root = SelectedAvatarRoot();
            if (root == null) { EditorUtility.DisplayDialog("NonToon ⑥", "请先在 Hierarchy 里选中 avatar 的任意物体。", "好"); return; }
            var r = NTSelfRealtimeSetup.Install(root, includeOtherPlayers: true,
                                                intensity: 0.8f, lightSize: 64f, softness: 1f,
                                                range: 6f, spotAngle: 50f, addMenu: true);
            Report(r, "安装 ⑥（含 Player 层 ⇒ 别人也看得到，注意人多时会互相打亮）");
        }

        [MenuItem(Menu + "⑥ 实时自阴影：卸载", false, 622)]
        static void Uninstall()
        {
            var root = SelectedAvatarRoot();
            if (root == null) { EditorUtility.DisplayDialog("NonToon ⑥", "请先在 Hierarchy 里选中 avatar 的任意物体。", "好"); return; }
            var r = NTSelfRealtimeSetup.Uninstall(root);
            Report(r, "卸载 ⑥");
        }

        [MenuItem(Menu + "⑥ 实时自阴影：卸载", true)]
        static bool UninstallValidate()
        {
            var root = SelectedAvatarRoot();
            return root != null && root.transform.Find(NTSelfRealtimeSetup.RigName) != null;
        }

        internal static void Report(NTSelfRealtimeSetup.Result r, string title)
        {
            var sb = new System.Text.StringBuilder();
            if (r.Done.Count > 0) sb.AppendLine("已完成：\n  · " + string.Join("\n  · ", r.Done));
            if (r.Warnings.Count > 0) sb.AppendLine("\n注意：\n  ⚠ " + string.Join("\n  ⚠ ", r.Warnings));
            if (r.Errors.Count > 0) sb.AppendLine("\n失败：\n  ✗ " + string.Join("\n  ✗ ", r.Errors));
            sb.AppendLine("\n⚠ 实时光影在 VRChat 里必然是 **Poor** 等级（Lights=1），且 **Quest 不可用**。");
            sb.AppendLine("⚠ 半影质量取决于观看者的阴影设置；必须保持该灯为 **Hard**（组件会自动关掉 Unity 阴影）。");
            if (r.Errors.Count > 0) Debug.LogError("[NonToon ⑥] " + title + "\n" + sb);
            else { Debug.Log("[NonToon ⑥] " + title + "\n" + sb); }
            EditorUtility.DisplayDialog("NonToon ⑥ — " + title, sb.ToString(), "好");
        }
    }
}
