using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 13] 自阴影的两种来源。
    public enum NTSelfLightMode
    {
        // 着色器侧采样烘焙好的深度图：永远可见、Quest 可用、不吃 VRChat 的 Lights 计数，
        // 但阴影是**烘焙那一刻的姿态**。
        BakedShadowMap = 0,
        // 生成一盏真·Spot Light 跟随 avatar：**实时**阴影（姿势实时），
        // 代价是占 VRChat 的 Lights 计数、只在观看者开了阴影时可见、且多盏光会叠加。
        RealtimeLight = 1,
    }

    // PCSS 采样档位（性能不再是约束，所以做成可调）
    public enum NTPCSSQuality { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    // [NT-TOOL] 「自有光源」配置组件。挂在 avatar（或任意根节点）上。
    //
    // 重要：运行时**没有任何行为**。它只是用来保存「指定哪一盏光源」+ 烘焙参数；
    // 真正的数据全部写进材质属性，烘焙完可以把它删掉。
    // 之所以不做成真的挂一盏 Unity Light：VRChat 性能等级 PC 上要求 Lights = 0
    // （只有 Poor 允许 1），而且真实光会照亮周围世界与别的玩家，做不到"只照自己"。
    //
    // [NT-FEAT 13] 但如果不在意性能等级（比如 avatar 本来就是极重负载），
    // 可以切到 RealtimeLight 模式：真的挂一盏 Spot Light，阴影就是实时的（PCSS4VRC 走的就是这条路）。
    // 两条路各自的问题见检视面板里的说明与 SelfLight.md。
    [AddComponentMenu("NonToon/② 自有光源（需要在材质启用）")]
    public sealed class NTSelfLight : MonoBehaviour
    {
        [Tooltip("自阴影来源。\n· 烘焙阴影图：着色器侧采样，永远可见、Quest 可用、不占 Lights 计数，但阴影是烘焙那一刻的姿态\n· 实时光源：挂一盏真 Spot Light，阴影实时（改姿势立刻变），但占 Lights 计数、依赖观看者的阴影设置、且多盏光会叠加")]
        public NTSelfLightMode mode = NTSelfLightMode.BakedShadowMap;

        // ---- [NT-FEAT 20] 自由度：想照谁、谁才认这条路光 ----
        [Tooltip("作用范围：留空 = 本组件所在节点（含子级）。\n想把组件挂在某个物体上、却让它照另一个物体，就把那个物体的根节点拖进来")]
        public Transform targetRoot;

        [Tooltip("是否包含子节点里的 Renderer（关掉就只作用于 targetRoot 自身）")]
        public bool includeChildren = true;

        [Range(0, 15)]
        [Tooltip("本组件的光源编号（1–15；0 = 不限制）。\n材质上的「只接受哪一路光源」填了别的编号时，这个组件不会去写它 —— 于是同一个模型上可以并存多路自有光各照各的")]
        public int lightId;

        [Tooltip("写入材质时顺便把材质的「只接受哪一路光源」盖成本组件的编号，避免以后被别的组件抢")]
        public bool stampId = true;

        [Tooltip("作用范围内、编号被别的光源占用的材质数（只读，由面板计算）")]
        [HideInInspector]
        public int lastSkippedMaterials;

        // 这个组件要作用的 Renderer（自由度：可指向别的节点）
        public System.Collections.Generic.IEnumerable<Renderer> TargetRenderers()
        {
            var root = targetRoot != null ? targetRoot : transform;
            return includeChildren
                ? root.GetComponentsInChildren<Renderer>(true)
                : root.GetComponents<Renderer>();
        }

        [Tooltip("指定的光源：烘焙时把它的方向/颜色/强度写进材质。不会真的产生一盏实时光。")]
        public Light lightSource;

        [Tooltip("只由这一路光照亮：忽略世界光与环境光，avatar 在任何世界里长得都一样（只对「烘焙阴影图」模式有效）")]
        public bool onlyThisLight;

        [Tooltip("光源颜色（两种模式都用）")]
        public Color color = Color.white;

        [Tooltip("用色温（Kelvin）决定自有光的颜色：6500K ≈ 白，低色温偏橙、高色温偏蓝。\n算法是几行 ALU 的黑体近似，零采样。")]
        public bool useColorTemperature;

        [Range(1000f, 20000f)]
        [Tooltip("色温（K）。6500 = 中性白")]
        public float temperature = 6500f;

        [Range(0f, 8f)]
        [Tooltip("光源强度")]
        public float intensity = 1f;

        [Range(64, 2048)]
        [Tooltip("自阴影贴图分辨率。256 够用，512/1024 更细腻但烘焙更慢（射线数按平方增长）")]
        public int shadowResolution = 256;

        [Tooltip("PCSS 软阴影：先求遮挡物平均深度，再按半影大小做变半径 PCF。\n关掉就是 1 次采样的硬阴影。")]
        public bool pcss = true;

        [Tooltip("PCSS 采样档位：低 8+12 / 中 12+24 / 高 20+40 / 极高 32+64 次采样")]
        public NTPCSSQuality pcssQuality = NTPCSSQuality.Low;

        [Range(0f, 1f)]
        [Tooltip("阴影柔度：越大半影越宽、边越软。只在开启 PCSS 时有效")]
        public float softness = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("阴影浓度。1 = 该黑的地方就黑，0 = 完全没有自阴影")]
        public float shadowDensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("阴影硬化：把 PCSS 的软边压成硬边（动画风）。0 = 保持软边")]
        public float shadowClamp = 0f;

        [Range(0f, 50f)]
        [Tooltip("阴影距离：离相机超过该距离就关闭自阴影（省算力，也避免远处噪点）。0 = 不限制")]
        public float shadowDistance = 10f;

        [Range(0f, 1f)]
        [Tooltip("匹配世界光颜色：让自带光染上地图环境光的颜色（0 = 完全用自己的颜色）。\n对应 PCSS4VRC 的「把世界光颜色反映到自身光源」")]
        public float matchWorldColor;

        [Range(0f, 1f)]
        [Tooltip("匹配世界光方向：让自带光（以及它投出的自阴影）跟随地图主光的方向（0 = 用自己的方向）")]
        public float matchWorldDirection;

        [Range(0f, 1f)]
        [Tooltip("阴影强度。0 = 关闭自阴影（只用私有光）")]
        public float shadowStrength = 1f;

        [Range(0f, 0.2f)]
        [Tooltip("阴影偏移：出现自阴影痤疮（条纹状噪点）时调大")]
        public float shadowBias = 0.02f;

        [Tooltip("接收遮罩：逐像素控制「哪里接收自阴影」，白色 = 正常接收阴影（对应 PCSS4VRC 的 ReceiveMask）")]
        public Texture2D receiveMask;

        [Range(0, 7)]
        [Tooltip("接收遮罩使用哪个通道：0=R 1=G 2=B 3=A 4=1-R 5=1-G 6=1-B 7=1-A")]
        public int receiveMaskChannel = 3;

        [Range(0f, 1f)]
        [Tooltip("接收遮罩强度：0 = 忽略遮罩，1 = 完全按遮罩")]
        public float receiveMaskStrength = 1f;

        // ---- 实时光源模式（NTSelfLightMode.RealtimeLight）----
        [Range(1f, 179f)]
        [Tooltip("实时光源：聚光灯角度")]
        public float spotAngle = 60f;

        [Range(0.1f, 20f)]
        [Tooltip("实时光源：照射范围（米）。只照自己就调小一点，别烧到别人")]
        public float lightRange = 3f;

        [Tooltip("实时光源的 Culling Mask。默认第 10 层（VRChat 的 PlayerLocal ＝ 本地玩家），这样只照到自己、不照世界和其他玩家")]
        public LayerMask realtimeCullingMask = 1 << 10;

        [Tooltip("实时光源：渲染模式。Important/ForcePixel = 强制逐像素，阴影质量最好")]
        public LightRenderMode realtimeRenderMode = LightRenderMode.ForcePixel;

        [Tooltip("实时光源：挂到 avatar 的哪个节点上。留空则用本组件所在的节点")]
        public Transform realtimeLightParent;

        [HideInInspector]
        public Texture2D bakedShadowMap;

        [HideInInspector]
        public Light realtimeLight;

        // 世界空间、指向光源的方向（与 Shader 里的 _SelfLightDirection 同义）
        public Vector3 Direction
        {
            get { return lightSource != null ? -lightSource.transform.forward : Vector3.up; }
        }
    }
}
