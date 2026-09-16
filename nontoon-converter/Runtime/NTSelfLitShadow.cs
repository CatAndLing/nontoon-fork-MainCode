using UnityEngine;

namespace NonToonTools
{
    // [NT-FEAT 21] 「自带光照与阴影」—— **不需要地图光源**的自我照明 + 自阴影。
    //
    // 与 ② NTSelfLight 的关系：
    //   · ② 是手动模式：光来自场景里的一盏 Light（`Light Source`），参数一件件调；
    //   · ④（本组件）是一键模式：光的方向/颜色/强度、亮度下限、自阴影烘焙**全在组件上**，
    //     不需要往场景里摆任何灯 —— 这正是"不需要世界光源"的形态。
    //
    // 运行时同样**没有任何行为**：所有数据都写进材质（含烘焙出的深度图），烘焙完可以删掉组件。
    // 之所以不做成真的挂一盏 Unity Light：VRChat 性能等级在 PC 上要求 Lights = 0（只有 Poor 允许 1），
    // 而且真实光会照亮周围世界与其他玩家，做不到"只照自己"。
    [AddComponentMenu("NonToon/④ 自带光照与阴影（不需要地图光源）")]
    public sealed class NTSelfLitShadow : MonoBehaviour
    {
        // ---------------- 作用范围 ----------------
        [Tooltip("作用范围：留空 = 本组件所在节点（含子级）。想挂在某个物体上却照另一个物体，就把那个根节点拖进来")]
        public Transform targetRoot;
        [Tooltip("是否包含子节点里的 Renderer（关掉就只作用于 targetRoot 自身）")]
        public bool includeChildren = true;
        [Range(0, 15)]
        [Tooltip("本组件的光源编号（1–15；0 = 不限制）。材质上的「只接受哪一路光源」填了别的编号时，本组件不会写它")]
        public int lightId;
        [Tooltip("写入材质时顺便把材质的「只接受哪一路光源」盖成本组件的编号，避免被别的组件抢")]
        public bool stampId = true;

        // ---------------- 虚拟光（不依赖场景里的 Light） ----------------
        [Range(-180f, 180f)]
        [Tooltip("光从哪个环绕方向来：0 = 角色正前方，+90 = 角色右侧，-90 = 左侧")]
        public float yaw = 30f;
        [Range(-90f, 90f)]
        [Tooltip("光的俯仰角：+90 = 头顶正上方，0 = 与角色同高，负值 = 从下方来")]
        public float pitch = 35f;

        [Tooltip("光源颜色")]
        public Color color = Color.white;
        [Tooltip("用色温（Kelvin）决定光色：6500K ≈ 白，低色温偏橙、高色温偏蓝（几行 ALU，零采样）")]
        public bool useColorTemperature;
        [Range(1000f, 20000f)]
        [Tooltip("色温（K）。6500 = 中性白")]
        public float temperature = 6500f;
        [Range(0f, 8f)]
        [Tooltip("光源强度")]
        public float intensity = 1f;

        // ---------------- 自给自足（不依赖地图光照） ----------------
        [Tooltip("只由这一路光照亮：丢弃世界光、环境光、光照贴图与天空盒反射 —— avatar 在任何地图里长得都一样。" +
                 "这是「不需要世界光源」的关键开关；关掉则世界光照旧影响 avatar")]
        public bool onlyThisLight = true;
        [Tooltip("同时抬高亮度下限（防煤）：全黑地图里 avatar 不会变成一块煤。零采样代价")]
        public bool raiseBrightnessFloor = true;
        [Range(0f, 1f)]
        [Tooltip("亮度下限 _LightMinLimit。0 = 官方默认（全黑地图会变煤），0.35 = 本工具包推荐值")]
        public float minLimit = 0.35f;
        [Range(0f, 1f)]
        [Tooltip("亮度上限 _LightMaxLimit。用于压制过曝，1 = 不限制")]
        public float maxLimit = 1f;

        [Range(0f, 1f)]
        [Tooltip("匹配世界光颜色：让自带光染上地图环境光的颜色（0 = 完全用自己的颜色）")]
        public float matchWorldColor;
        [Range(0f, 1f)]
        [Tooltip("匹配世界光方向：让自带光（以及它投出的自阴影）跟随地图主光方向（0 = 用自己的方向）")]
        public float matchWorldDirection;

        // ---------------- 自阴影 ----------------
        [Tooltip("烘焙自阴影（需要 NonToon 材质的 Self Light 模块）。关掉则只写光照参数、不烘焙")]
        public bool enableShadow = true;
        [Range(64, 2048)]
        [Tooltip("自阴影贴图分辨率。256 够用，512/1024 更细腻但烘焙更慢（射线数按平方增长）")]
        public int shadowResolution = 256;
        [Tooltip("PCSS 软阴影：先求遮挡物平均深度，再按半影大小做变半径 PCF。关掉就是 1 次采样的硬阴影")]
        public bool pcss = true;
        [Tooltip("PCSS 采样档位：低 8+12 / 中 12+24 / 高 20+40 / 极高 32+64 次采样。目标就是低消耗，不确定就留在低档")]
        public NTPCSSQuality pcssQuality = NTPCSSQuality.Low;
        [Range(0f, 1f)]
        [Tooltip("阴影柔度。真实尺度下默认值的柔化量只有毫米级，想要明显软影请拉到 1 并提高烘焙分辨率")]
        public float softness = 0.5f;
        [Range(0f, 1f)]
        [Tooltip("阴影浓度。1 = 该黑的地方就黑，0 = 完全没有自阴影")]
        public float shadowDensity = 1f;
        [Range(0f, 1f)]
        [Tooltip("阴影硬化：把 PCSS 的软边压成硬边（动画风）。0 = 保持软边")]
        public float shadowClamp;
        [Range(0f, 50f)]
        [Tooltip("阴影距离：离相机超过该距离就关闭自阴影（省算力，也避免远处噪点）。0 = 不限制")]
        public float shadowDistance = 10f;
        [Range(0f, 1f)]
        [Tooltip("阴影强度。0 = 关闭自阴影（只用私有光）")]
        public float shadowStrength = 1f;
        [Range(0f, 0.2f)]
        [Tooltip("阴影偏移：出现自阴影痤疮（条纹状噪点）时调大。它是包围盒深度的比例，不是米")]
        public float shadowBias = 0.02f;

        [Tooltip("接收遮罩：逐像素控制「哪里接收自阴影」，白色 = 正常接收阴影")]
        public Texture2D receiveMask;
        [Range(0, 7)]
        [Tooltip("接收遮罩使用哪个通道：0=R 1=G 2=B 3=A 4=1-R 5=1-G 6=1-B 7=1-A")]
        public int receiveMaskChannel = 3;
        [Range(0f, 1f)]
        [Tooltip("接收遮罩强度：0 = 忽略遮罩，1 = 完全按遮罩")]
        public float receiveMaskStrength = 1f;

        // 上一次烘焙出来的深度图（组件只是载体，真正的数据在材质里）
        [HideInInspector]
        public Texture2D bakedShadowMap;
        [HideInInspector]
        public int lastBakedResolution;
        [HideInInspector]
        public int lastMaterialCount;
        [HideInInspector]
        public int lastSkippedMaterials;

        // 「光从哪来」的方向（世界空间）→ 与材质属性 _SelfLightDirection 同义（指向光源）
        public Vector3 LightDirection { get { return DirectionFromAngles(yaw, pitch); } }

        // 光线**传播**方向（从光源射向场景）= 烘焙器要的 forward
        public Vector3 LightForward { get { return -LightDirection; } }

        // yaw = 环绕角（0 = 角色正前方 +Z，+ 向右）；pitch = 俯仰角（+90 = 头顶正上方）。
        // 放在 Runtime 是为了让 Runtime 组件自己也能算方向（Editor 程序集引用 Runtime，反向不行）。
        public static Vector3 DirectionFromAngles(float yaw, float pitch)
        {
            var y = yaw * Mathf.Deg2Rad;
            var p = pitch * Mathf.Deg2Rad;
            var v = new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.up;
        }

        // 这个组件要作用的 Renderer（自由度：可指向别的节点）
        public System.Collections.Generic.IEnumerable<Renderer> TargetRenderers()
        {
            var root = targetRoot != null ? targetRoot : transform;
            return includeChildren
                ? root.GetComponentsInChildren<Renderer>(true)
                : root.GetComponents<Renderer>();
        }
    }
}
