using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 14] Avatar 光源插件 —— 给 avatar 挂一盏「只照自己」的实时光。
    //
    // 和 NTSelfLight 的区别（这是独立的插件，不是它的附属）：
    //   · **着色器无关**：lilToon / Poiyomi / NonToon / 甚至 Standard 都能用。
    //     它只经营一盏 Unity Light，不碰任何材质属性。
    //   · 影子是 Unity 的实时阴影（跟随姿势），不需要烘焙。
    //   · 一键创建：菜单 GameObject ▸ NonToon ▸ 创建 Avatar 光源插件。
    //
    // 参考：nHaruka 的 PCSS4VRC「真实影システム」也是"给 avatar 挂一盏聚光灯"这个思路。
    // 我们这边多做了两件事：
    //   1) Culling Mask 默认第 10 层 PlayerLocal —— VRChat 里只有"本地玩家自己的 avatar"在这一层，
    //      所以这盏光不会照世界、也不会照其他玩家；
    //   2) 自动打开层级里所有 Renderer 的 Receive Shadows / Cast Shadows
    //      （有些 avatar 默认是关的，关了就不出影子）。
    //
    // 代价（面板上也会写清楚）：
    //   · 占 VRChat 性能等级的 Lights 计数（PC 上 Lights = 1 → 最高 Poor）
    //   · 影子能不能看到取决于**观看者**的 Shadow Quality 设置；Unity 的 Shadow Distance 之外没有影子
    //   · 多个玩家的这种光会互相叠加，人挤人时可能把 avatar 照白
    //   · Quest 端实时光基本不可用
    [AddComponentMenu("NonToon/① Avatar 光源（不用改材质）")]
    public sealed class NTAvatarLight : MonoBehaviour
    {
        [Tooltip("由创建器生成的那盏灯。一般不用手动改。")]
        public Light target;

        [Tooltip("光的颜色")]
        public Color color = Color.white;

        [Tooltip("用 Unity 的色温（Kelvin）控制光色：6500K ≈ 白。开了之后 colorTemperature 生效、color 作为乘算")]
        public bool useColorTemperature;

        [Range(1000f, 20000f)]
        [Tooltip("色温（K）")]
        public float temperature = 6500f;

        [Range(0f, 8f)]
        [Tooltip("光强")]
        public float intensity = 1.5f;

        [Range(1f, 179f)]
        [Tooltip("聚光灯角度。越小越集中、影子越干净")]
        public float spotAngle = 60f;

        [Range(0.1f, 20f)]
        [Tooltip("照射范围（米）。只照自己就调小，别烧到别人")]
        public float range = 3f;

        [Tooltip("阴影：Soft = 软阴影（推荐），None = 不要影子（最省）")]
        public LightShadows shadows = LightShadows.Soft;

        [Range(0f, 1f)]
        [Tooltip("阴影浓度")]
        public float shadowStrength = 1f;

        [Range(0f, 0.2f)]
        [Tooltip("阴影偏移：出现阴影痤疮（条纹状噪点）时调大")]
        public float shadowBias = 0.02f;

        [Tooltip("Culling Mask。默认第 10 层（VRChat 的 PlayerLocal ＝ 本地玩家），这样只照自己、不照世界和其他玩家")]
        public LayerMask cullingMask = 1 << 10;

        [Tooltip("渲染模式。ForcePixel / Important = 强制逐像素，阴影质量最好")]
        public LightRenderMode renderMode = LightRenderMode.ForcePixel;

        [Tooltip("光的方向来源。留空 = 用本节点自身的朝向；\n想让 PhysBone 控制方向，就把本节点（或它的父节点）挂到被 PhysBone 驱动的那根骨骼下")]
        public Transform directionSource;

        // 世界空间朝向（directionSource 为空时就是本节点）
        public Vector3 Direction
        {
            get { return directionSource != null ? directionSource.forward : transform.forward; }
        }
    }
}
