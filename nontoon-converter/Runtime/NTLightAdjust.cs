using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 22] 亮度调整组件。
    //
    // 两种用法（同一个组件，开关切换）：
    //   · FixedAtBuild          —— 直接把材质属性写成固定值。零 Animator、零参数、零 VRCSDK 依赖。
    //   · AdjustableInGame      —— 生成 Animator 曲线 + 控制器，玩家在 Action Menu 里用径向随手调。
    //
    // AdjustableInGame 有两条路（自动选）：
    //   · 装了 Modular Avatar → **非破坏**：只生成资产 + 往 avatar 根节点挂 MA 的四个组件
    //     （MergeAnimator / Parameters / MenuItem / MenuInstaller），用户原有的 FX 控制器、
    //     ExpressionParameters、菜单**一个都不改**，删掉组件即回滚。
    //   · 没装 MA → 回退到"直接改控制器"的老路径（与 ③ 插件相同）。
    //
    // 运行时本组件没有任何行为（VRChat 白名单不允许自定义脚本）—— 能生效的全是白名单内的资产：
    // Animator / AnimationClip / ExpressionParameter / 菜单。
    [AddComponentMenu("NonToon/⑤ 亮度调整（构建时固定 / 游戏内可调）")]
    public sealed class NTLightAdjust : MonoBehaviour
    {
        public enum Mode
        {
            FixedAtBuild = 0,
            AdjustableInGame = 1,
        }

        [Tooltip("FixedAtBuild = 直接把材质属性写成固定值（零 Animator、零参数）；" +
                 "AdjustableInGame = 生成 Animator 曲线 + 径向菜单，玩家可在 Action Menu 里随手调")]
        public Mode mode = Mode.AdjustableInGame;

        // ---------------- 作用范围 ----------------
        [Tooltip("作用范围：留空 = 本组件所在节点（含子级）")]
        public Transform targetRoot;
        [Tooltip("是否包含子节点里的 Renderer")]
        public bool includeChildren = true;

        // ---------------- 驱动哪个属性 ----------------
        [Tooltip("要驱动的材质属性。NonToon 与 lilToon 的亮度公式一致，_LightMinLimit 是防煤主力")]
        public string propertyName = "_LightMinLimit";
        [HideInInspector] public string customPropertyName = "";

        // ---------------- FixedAtBuild ----------------
        [Range(0f, 1f)]
        [Tooltip("固定写入的值")]
        public float fixedValue = 0.35f;

        // ---------------- AdjustableInGame ----------------
        [Range(0f, 1f)]
        [Tooltip("径向拉到 0 时的属性值（通常是「最暗」那一端）")]
        public float valueAtZero = 0.05f;
        [Range(0f, 1f)]
        [Tooltip("径向拉到 1 时的属性值（通常是「最亮」那一端，也是推荐值 0.35 所在的位置）")]
        public float valueAtOne = 0.35f;
        [Range(0f, 1f)]
        [Tooltip("进游戏时的初始值（0 = 最暗端，1 = 最亮端）")]
        public float initialValue = 1f;

        [Tooltip("VRChat 参数名。会自动加 NT_ 前缀避免与别的工具撞名")]
        public string parameterName = "NT_Brightness";
        [Tooltip("勾上 = 同步给别人看（占 8 bit 同步内存）；不勾 = 只有自己看得到变化")]
        public bool synced = true;
        [Tooltip("Action Menu 里显示的名字")]
        public string menuLabel = "亮度";
        [Tooltip("生成的资产放在哪个目录")]
        public string outputFolder = "Assets/NonToonGenerated";

        // ---------------- 生成结果（只读） ----------------
        [HideInInspector] public int lastMaterialCount;
        [HideInInspector] public int lastRendererCount;
        [HideInInspector] public bool lastUsedModularAvatar;
        [HideInInspector] public string lastControllerPath;
        [HideInInspector] public string lastGeneratedAt;
        [HideInInspector] public string lastSummary;

        public System.Collections.Generic.IEnumerable<Renderer> TargetRenderers()
        {
            var root = targetRoot != null ? targetRoot : transform;
            return includeChildren
                ? root.GetComponentsInChildren<Renderer>(true)
                : root.GetComponents<Renderer>();
        }

        public string EffectiveProperty
        {
            get
            {
                if (propertyName == "__custom") return string.IsNullOrEmpty(customPropertyName) ? "_LightMinLimit" : customPropertyName;
                return string.IsNullOrEmpty(propertyName) ? "_LightMinLimit" : propertyName;
            }
        }
    }
}
