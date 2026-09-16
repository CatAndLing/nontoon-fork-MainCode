// [NT-FEAT 20] ⑥ 实时自阴影：把「阴影深度图」渲染到我们自己的 RenderTexture。
//
// 为什么需要它：Unity 的逐光源阴影贴图在普通材质上**只给比较采样器**（0/1），
// PCSS 的 blocker 搜索拿不到遮挡物深度。实测排除了所有绕行方案（见
// `_notes/续接-实时PCSS实现.md` §9.6/§9.10），只能用**自己渲染**这条路。
//
// 做法（每帧、每盏光一次，不依赖相机）：
//   1. 收集要投影的 Renderer（avatar 的 SkinnedMesh + 场景里的遮挡物）
//   2. 用一个 CommandBuffer：SetViewProjectionMatrices(我们的灯视角矩阵)
//      → SetRenderTarget(我们的 RT, depth) → DrawRenderer(..., depthMat, 0)
//   3. 把 RT 与**同一个** worldToShadow 矩阵绑到 avatar 材质
//
// ⚠️⚠️ 2026-09-16 的关键修复：**必须显式设 view / projection**。
//   `Graphics.ExecuteCommandBuffer` **不会**替你建相机矩阵 —— 旧版只做
//   `SetRenderTarget + DrawRenderer`，于是矩阵是**单位矩阵**，深度图变成
//   "世界坐标的正交投影"（实机读回：球体是圆盘、接收面被整体裁掉），
//   与接收面片元用的 uv 完全对不上 ⇒ 内核恒判"受光" ⇒ 看起来 PCSS 毫无作用。
//   修法：C# 侧构造 view / proj 交给 CommandBuffer，并把**由同一组矩阵拼出来的**
//   `worldToShadow`（`uvMat * gpuProj * view`）按 4 行传给着色器 ⇒ 投影与采样
//   由构造保证对齐，不依赖任何 Unity 内部约定（也不再用 `unity_WorldToShadow`）。
//
// ⚠️ 本组件会**在开关打开时**关掉那盏灯的 Unity 实时阴影（`LightShadows.None`）：
//   阴影完全由我们的深度图 + PCSS 提供。好处是数值干净（画面上没有"硬阴影 + 软
//   阴影"两层叠加）、半影内外都软，而且**省掉该灯全部 ShadowCaster draw call**。
//   开关**关掉时**会把该灯原本的 Unity 阴影**还原**、并把灯本身关掉（见 `SuspendRig()`）。
//
// ⚠️⚠️ [NT-FIX 21 / 2026-09-17 实机] **绝不把深度图绑到材质上的 `_SelfLightRtShadowMap`**。
//   那个 `SC_Texture2D` 在任何 HLSL 里都没被读过（内核读的是自声明的 `_NTRTShadowMapRaw`，
//   只能走 `Shader.SetGlobalTexture`）。而一旦把它写进**场景里的材质实例**，那张
//   `HideAndDontSave` 的 RenderTexture 就进了场景对象图 ⇒ NDMF 在 Apply-on-Play
//   保存场景时撞 `Assertion failed ... kDontSaveInEditor ... kAllowDontSaveObjectsToBePersistent`
//   ⇒ **整场烘焙中途作废**。表现极具误导性：菜单合并了、MA 组件也删了，但那 4 条 FX 层
//   没挂上 —— 看着就像"菜单是死的"。**Manual bake 反而是好的**（那时组件还没建 RT），
//   所以这条只有"进 Play"才会暴露。
//
// 深度约定与 `NTRTDepth.shader` 一致：**线性** `(dist-near)/(far-near)`，越大越远。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NonToon
{
    [AddComponentMenu("NonToon/Realtime Shadow Depth (⑥)")]
    [DisallowMultipleComponent]
    // 🆕 [NT-FIX 29] `[ExecuteAlways]`：让**编辑器里**组件也跑 `OnEnable/LateUpdate`。
    //   为什么必须有（用户 2026-09-17 的批评：「为什么灯组件没有随着开关启用和关闭」）：
    //   以前只有 Play 才跑，所以在编辑器里勾/去勾 `pcssOn` **完全没反应**，
    //   灯一直亮着 —— 看起来就像"开关没接上"。加上之后：改 `pcssOn` ⇒ 灯立刻亮/灭。
    //   ⚠️ 代价与对策：编辑期也跑 `RenderDepth()` ⇒ 必须保证**默认路径不创建任何
    //      `HideAndDontSave` 对象**（见 `OnEnable` 里的注释），否则 NDMF 的
    //      Apply-on-Play 会撞 `kDontSaveInEditor` 断言、整场烘焙作废。
    [ExecuteAlways]
    public sealed class NTSelfRealtimeShadow : MonoBehaviour
    {
        [Tooltip("提供阴影的那盏 Spot 光。留空则自动找子物体上的 Spot。")]
        public Light targetLight;

        [Tooltip("深度图分辨率。512 够用；1024 更细但更贵。")]
        [Range(128, 2048)] public int resolution = 512;

        [Tooltip("额外要投影的物体（avatar 自己会自动收集）。")]
        public List<Renderer> extraCasters = new List<Renderer>();

        [Tooltip("每帧渲染深度图。关掉可用于排查。")]
        public bool renderEveryFrame = true;

        [Tooltip("把该灯的 Unity 实时阴影关掉（阴影完全由本组件 + PCSS 提供）。")]
        public bool disableUnityShadows = true;

        [Tooltip("扫描投影体与目标材质用的根节点。留空 = 自动取本物体所在层级的**最顶层父节点**" +
                 "（VRChat 的 avatar 就是场景根，一般留空即可）。")]
        public Transform searchRoot;

        // ────────────────────────────────────────────────────────────────────
        // ⑥ 的**用户旋钮全部放在组件上**，由本组件每帧推给材质。
        // ⚠️ 为什么不放在材质上（这是从付费的 PCSS4VRC 学到的教训）：
        //   它的旋钮在材质属性上，于是 ExpressionMenu 的动画必须写材质 ⇒ 它只能把用户的
        //   材质**复制成带后缀的副本**再换 shader，否则就会污染用户资产、并与别的资产打架。
        //   我们把旋钮放在组件上，菜单动画直接驱动这些字段 ⇒ **完全不需要复制或修改用户材质**。
        // ────────────────────────────────────────────────────────────────────

        [Tooltip("总开关（ExpressionMenu 的「阴影开关」驱动这个字段）。\n" +
                 "关 = **整套 ⑥ 停**：这盏 Spot 被关掉、该灯原本的 Unity 阴影被还原、深度图也不再渲染\n" +
                 "（所以开关关掉后你看不到那盏补光灯，也不会白付一个深度图 pass）。")]
        // ⚠️ **默认关闭**（用户 2026-09-17 要求："自带阴影默认关闭"）——
        //    ⑥ 是重功能（多一层深度图渲染 + 一盏 Spot），让用户主动在菜单里开，
        //    而不是装完就默默生效。菜单的「阴影开关」也是默认关。
        public bool pcssOn = false;

        [Tooltip("虚拟光源半径（512 纹素基准）。越大越软；avatar 自遮挡距离只有几厘米，" +
                 "想看到明显半影要给到 32~128。")]
        [Range(0f, 512f)] public float lightSize = 64f;

        [Tooltip("额外的软硬倍率。")]
        [Range(0f, 2f)] public float softness = 1f;

        [Tooltip("深度偏移（米）。自遮挡下用来压 shadow acne。")]
        [Range(0f, 0.05f)] public float biasMeters = 0.01f;

        [Tooltip("采样档：0=8/12、1=12/24、2=16/32、3=24/32。越高越平滑越贵。")]
        [Range(0f, 3f)] public float quality = 3f;

        [Tooltip("阴影下限：完全被遮蔽处保留多少比例的灯光（0 = 纯黑）。" +
                 "NonToon 随后会 clamp(env + light, _LightMinLimit, _LightMaxLimit)，" +
                 "而 _LightMinLimit 默认 0，所以没有环境光时不留下限就会整个模型变黑。" +
                 "0.2~0.3 观感像阴影里的环境反射。")]
        [Range(0f, 0.95f)] public float shadowFloor = 0.25f;

        [Tooltip("由本组件驱动的灯强度（会写回 Light.intensity）。")]
        [Range(0f, 30f)] public float lightIntensity = 0.8f;

        [Tooltip("由本组件驱动的灯颜色。")]
        public Color lightColor = Color.white;

        [Tooltip("跟随地图环境光的**色相**（0 = 不跟随，1 = 完全用环境色）。\n" +
                 "只跟色相不跟亮度（归一化到最亮通道 = 1），所以灯强度不会被环境光拖动。\n" +
                 "用途：在暖色地图里自带光自动变暖、冷色地图里变冷，避免「地图是暖的、自己的灯是冷光」。")]
        [Range(0f, 1f)] public float followAmbient = 0f;

        // [NT-FEAT 25] 用 **Unity 自己的阴影贴图**（推荐，🔒 现在也是唯一被使用的路径）。
        //
        // ⛔⛔ **`false` 那条分支（自渲深度图）已被标记为「错误方向 / 已废弃」，不要再走：**
        //   · 它要自己用 CommandBuffer 渲一张 512² 的 RFloat 径向深度图 + 一整套自建矩阵；
        //   · **分辨不出"头发压在脸上"** 这种毫米级接触阴影（512² 在脸上就是好几毫米/纹素）；
        //   · 只有**常数深度偏移**（1cm 起），而刘海到额头的间距往往不到 1cm ⇒ 阴影被整体抹掉；
        //   · 没有逐像素法线偏移、没有逐像素 bias 蒙版（Unity 的阴影贴图三样都有）；
        //   · 它还会把 `HideAndDontSave` 的 RenderTexture 带进场景对象图，导致 NDMF 的
        //     Apply-on-Play 保存场景时撞断言、**整场烘焙作废**（实机踩过）。
        //   ⇒ 保留代码只为**对照/回退**，默认永不执行；删掉它是后续的清理项。
        //   （原实验性 PCSS 宏劫持草稿已从包内移出到 `_notes/experiments/`。）
        [Tooltip("用 Unity 自己的阴影贴图（推荐，也是当前唯一使用的路径）。\n" +
                 "关掉 = 旧的『自渲深度图』路径 —— **已废弃**（分辨不出头发压脸、1cm 常数偏移会抹掉接触阴影），仅供对照。")]
        public bool useUnityShadowMap = true;

        [Tooltip("阴影画质档（低占用优先）：\n" +
                 "0 = **原生**（默认）：NonToon 天然接受 Unity 阴影，1 次硬件比较采样，**零额外开销**、边缘锐。\n" +
                 "1 = PCSS 8+16 采样（半影更软，约 24 次深度采样/像素）\n" +
                 "2 = PCSS 16+32 采样（最软最贵）")]
        [Range(0f, 2f)] public float pcssQuality = 0f;

        // 全局参数（本文件在 SC 属性声明之前展开，材质属性在那里不可见 ⇒ 必须走全局）
        static readonly int IdRtEnabled = Shader.PropertyToID("_NTRTShadowEnabled");
        static readonly int IdRtQuality = Shader.PropertyToID("_NTRTShadowQuality");
        static readonly int IdRtSoftness = Shader.PropertyToID("_NTRTShadowSoftness");
        static readonly int IdRtFloor = Shader.PropertyToID("_NTRTShadowFloor");
        static readonly int IdRtBias = Shader.PropertyToID("_NTRTShadowBias");
        static readonly int IdRtTanHalf = Shader.PropertyToID("_NTRTShadowTanHalfAngle");
        static readonly int IdRtPos = Shader.PropertyToID("_NTRTShadowLightPos");

        RenderTexture _rt;
        Material _depthMat;
        CommandBuffer _cb;
        readonly List<Renderer> _casters = new List<Renderer>();
        readonly List<Material> _targets = new List<Material>();
        int _lastRes;
        bool _shadowsForced;
        // 我们"抢走"阴影之前，这盏灯原本的 Unity 阴影模式 —— 关掉开关时要还原成它，
        // 而不是硬编码回 Hard（用户可能本来设的就是 Soft / None）。
        LightShadows _originalShadows = LightShadows.Hard;

        static readonly int IdNear = Shader.PropertyToID("_NTRTDepthNear");
        static readonly int IdFar = Shader.PropertyToID("_NTRTDepthFar");
        static readonly int IdLightPos = Shader.PropertyToID("_NTRTDepthLightPos");
        // ⚠️ 名字必须与 `NonToon/Shaders/Modules/SelfLight/properties.hlsl` 里的 `SC_*` **一致**，
        //    名字写错**不会编译错**，只会静默读到默认值（踩过），所以这里集中定义。
        // ⛔ 这里**故意没有** `_SelfLightRtShadowMap` 的 PropertyToID：那个 SC 属性没有任何 HLSL 读它，
        //    而往场景材质上写它会把 HideAndDontSave 的 RT 拖进序列化 ⇒ NDMF 烘焙会中途失败。
        //    深度图只走 `Shader.SetGlobalTexture("_NTRTShadowMapRaw", rt)`。
        static readonly int PropTexels = Shader.PropertyToID("_SelfLightRtShadowTexels");
        static readonly int PropNear = Shader.PropertyToID("_SelfLightRtShadowNear");
        static readonly int PropFar = Shader.PropertyToID("_SelfLightRtShadowFar");
        static readonly int PropLightPos = Shader.PropertyToID("_SelfLightRtLightPos");
        static readonly int PropW2S0 = Shader.PropertyToID("_SelfLightRtW2S0");
        static readonly int PropW2S1 = Shader.PropertyToID("_SelfLightRtW2S1");
        static readonly int PropW2S2 = Shader.PropertyToID("_SelfLightRtW2S2");
        static readonly int PropW2S3 = Shader.PropertyToID("_SelfLightRtW2S3");
        static readonly int PropPcss = Shader.PropertyToID("_SelfLightRtPcss");
        static readonly int PropSize = Shader.PropertyToID("_SelfLightRtPcssScale");
        static readonly int PropSoft = Shader.PropertyToID("_SelfLightRtSoftness");
        static readonly int PropBias = Shader.PropertyToID("_SelfLightRtBias");
        static readonly int PropQuality = Shader.PropertyToID("_SelfLightRtQuality");
        static readonly int PropFloor = Shader.PropertyToID("_SelfLightRtShadowFloor");
        // ⚠️ 内核（includes.hlsl）**看不到** `SC_*` 属性（模块 includes 展开在属性声明之前），
        //    而它在 includes 里自声明的 uniform 又**绑不上材质**（材质只认 ShaderLab 属性）。
        //    ⇒ 深度图必须用 **`Shader.SetGlobalTexture`** 绑到这个自声明名字上。
        static readonly int IdGlobalShadowMap = Shader.PropertyToID("_NTRTShadowMapRaw");

        void OnEnable()
        {
            if (targetLight == null) targetLight = GetComponentInChildren<Light>();
            RefreshTargets();
            // ⛔⛔ 默认路径（`useUnityShadowMap = true`）**绝不能**在这里调 `EnsureResources()`：
            //   它会建一张 `HideAndDontSave` 的 RFloat RenderTexture + 一个同样标记的深度材质，
            //   并把它们挂在**本组件的字段**上。NDMF 在 Apply-on-Play 会遍历场景对象的引用、
            //   尝试把它们 `AssetDatabase.AddObjectToAsset` 存成资产 ⇒ 撞 Unity 断言
            //   `kDontSaveInEditor … kAllowDontSaveObjectsToBePersistent` ⇒ **整场烘焙中途作废**
            //   （表现极具误导性：菜单合并了、MA 组件也删了，但那几条 FX 层就是没挂上）。
            //   本项目已经因为这个（当时是往材质写 `_SelfLightRtShadowMap`）踩过一次。
            //   🆕 加了 `[ExecuteAlways]` 之后它在**编辑期**也会跑，风险更大 ⇒ 只让那条
            //   **已废弃**的自渲分支自己按需创建（见 `RenderDepth` 里它自己的 `EnsureResources()`）。
            if (!useUnityShadowMap) EnsureResources();
            RenderDepth();
        }

        void OnDisable()
        {
            Release();
            RestoreUnityShadows();
        }

        void OnDestroy()
        {
            Release();
            RestoreUnityShadows();
        }

        void LateUpdate()
        {
            if (!renderEveryFrame) return;
            if (targetLight == null) return;
            // 遮挡物可能变（换装 / 动态物体），定期刷新
            if (Time.frameCount % 30 == 0) RefreshTargets();
            RenderDepth();
        }

        /// <summary>扫描用的根节点：显式指定优先，否则取**最顶层父节点**。
        /// ⚠️ 踩过的坑：组件挂在**灯**上（avatar 的子物体）时，`GetComponentsInChildren`
        /// 只会看灯自己的子层级 —— 找不到 avatar 的材质 ⇒ `_targets` 为空 ⇒
        /// 深度图与参数全部不绑 ⇒ "PCSS 完全没效果"（实机第一次跑就是这样，targets=0）。</summary>
        public Transform ResolveRoot()
        {
            if (searchRoot != null) return searchRoot;
            var t = transform;
            while (t.parent != null) t = t.parent;
            return t;
        }

        /// <summary>收集"要投影的物体"与"要绑深度图的材质"。</summary>
        public void RefreshTargets()
        {
            _casters.Clear();
            _targets.Clear();
            var root = ResolveRoot();

            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr != null && smr.enabled) _casters.Add(smr);
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                if (mr != null && mr.enabled && mr.shadowCastingMode != ShadowCastingMode.Off)
                    _casters.Add(mr);
            foreach (var r in extraCasters)
                if (r != null && !_casters.Contains(r)) _casters.Add(r);

            // 需要绑深度图的材质：avatar 上所有 NonToon 材质
            var seen = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (m.shader.name != "NonToon" && m.shader.name != "NonToonFur") continue;
                    if (seen.Add(m)) _targets.Add(m);
                }
            }
            // 显式指定的额外投影体，它们的材质也要绑
            foreach (var r in extraCasters)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (m.shader.name != "NonToon" && m.shader.name != "NonToonFur") continue;
                    if (seen.Add(m)) _targets.Add(m);
                }
            }
        }

        /// <summary>销毁运行期临时对象。⛔ **不能无脑 `DestroyImmediate`**（实机 Play 退出时复现）：
        ///   · Play 模式下要用 `Destroy`（延迟销毁）；
        ///   · 编辑器里如果对象已变成**持久对象/资产**，`DestroyImmediate` 会抛
        ///     `Destroying assets is not permitted to avoid data loss`。
        ///     NDMF 在烘焙（`ApplyOnPlay`）时会遍历 avatar 上的引用并尝试把它们
        ///     `AssetDatabase.AddObjectToAsset` 存成资产 —— 我们的深度图/材质带着
        ///     `HideFlags.HideAndDontSave`，正好会被它碰到。
        ///   ⇒ **持久对象一律不碰**，交给 Unity 自己回收（泄漏一张 512² RFloat 也无所谓）。</summary>
        static void DestroyRuntimeObj(UnityEngine.Object o)
        {
            if (o == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying && UnityEditor.EditorUtility.IsPersistent(o)) return;
#endif
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        void EnsureResources()
        {
            if (_depthMat == null)
            {
                var sh = Shader.Find("NonToon/NTRTDepth");
                if (sh == null)
                {
                    Debug.LogWarning("[NTSelfRealtimeShadow] 找不到 NonToon/NTRTDepth 着色器，实时阴影关闭。");
                    enabled = false;
                    return;
                }
                _depthMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_rt == null || _rt.width != resolution || _lastRes != resolution)
            {
                if (_rt != null) { _rt.Release(); DestroyRuntimeObj(_rt); }
                _rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat)
                {
                    name = "NTRTShadowDepth",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _rt.Create();
                _lastRes = resolution;
            }
            if (_cb == null) _cb = new CommandBuffer { name = "NTRTShadowDepth" };
        }

        void Release()
        {
            if (_rt != null) { _rt.Release(); DestroyRuntimeObj(_rt); _rt = null; }
            // 解绑全局深度图：换回白（= 远 = 全受光），避免别的材质读到已释放的 RT
            if (Shader.GetGlobalTexture(IdGlobalShadowMap) != null)
                Shader.SetGlobalTexture(IdGlobalShadowMap, Texture2D.whiteTexture);
            if (_depthMat != null) { DestroyRuntimeObj(_depthMat); _depthMat = null; }
            if (_cb != null) { _cb.Release(); _cb = null; }
            // ⚠️ **不要**再 `m.SetTexture(_SelfLightRtShadowMap, …)`：那个 SC 属性在任何 HLSL 里
            //    都没被读过（内核读的是自声明的 `_NTRTShadowMapRaw`，走全局绑定）。
            //    而把一张 `HideAndDontSave` 的 RenderTexture 写进**场景里的材质实例**，会让
            //    NDMF 在 Apply-on-Play 保存场景时撞 Unity 断言
            //    （`kDontSaveInEditor` 被持久化）⇒ **整场烘焙中途作废**，
            //    表现就是"进了 Play 但菜单四项全死"（实机 2026-09-17 踩到，Manual bake 却是好的）。
            _targets.Clear();
        }

        void RestoreUnityShadows()
        {
            if (_shadowsForced && targetLight != null) targetLight.shadows = _originalShadows;
            _shadowsForced = false;
        }

        /// <summary>[NT-FIX 21] 关掉「阴影开关」= 关掉**整套 ⑥**。
        /// ⚠️ 旧行为只把材质的 `_SelfLightRtPcss` 置 0：灯照样全强度亮着，而 Unity 自己的阴影
        /// 已经被我们关掉 ⇒ 用户看到的是一盏**没有阴影的补光灯**。
        /// 实机反馈原话：「你的阴影开关根本就没有控制 Spot Light」。这里改成真的管灯。</summary>
        void SuspendRig()
        {
            if (targetLight != null && targetLight.enabled) targetLight.enabled = false;
            RestoreUnityShadows();
            foreach (var m in _targets)
                if (m != null && m.HasProperty(PropPcss)) m.SetFloat(PropPcss, 0f);
            // 🆕 [NT-FIX 29] 着色器侧判断开关读的是**全局** `_NTRTShadowEnabled`
            //   （见 `PushShadowGlobals` 的注释：模块 includes 展开在 SC 属性声明之前，
            //   材质属性在那里不可见）。关掉开关时**必须**把它推成 0 ——
            //   否则 `[ExecuteAlways]` 下编辑期关掉开关后，着色器仍按"开启"分支取参数，
            //   画面上就成了"灯灭了但着色器还在按有灯算"的错位状态。
            Shader.SetGlobalFloat(IdRtEnabled, 0f);
        }

        /// <summary>开回来：点亮那盏灯，并把 Unity 的实时阴影让给我们自己（避免硬+软两层叠加）。</summary>
        void RestoreRig()
        {
            if (targetLight == null) return;
            if (!targetLight.enabled) targetLight.enabled = true;
            // ⚠️ 用 Unity 的阴影贴图时**绝不能**把 shadows 关掉 —— 那张图正是我们要采的。
            //    关掉 = Unity 不再渲染它 ⇒ 阴影彻底消失（这正是之前"完全没有阴影"的一条原因）。
            if (disableUnityShadows && !useUnityShadowMap && targetLight.shadows != LightShadows.None)
            {
                if (!_shadowsForced) { _originalShadows = targetLight.shadows; _shadowsForced = true; }
                targetLight.shadows = LightShadows.None;
            }
        }

        /// <summary>[NT-FEAT 25] 把 PCSS 参数用 **全局** 推给着色器。
        /// ⚠️ 必须用 `Shader.SetGlobalFloat`：`NTPcssShadow.hlsl` 在 SC 的属性声明**之前**展开，
        ///    材质属性在那里根本不可见（本项目踩过的老坑；和 `_NTRTShadowMapRaw` 同一个道理）。
        /// `pcssQuality = 0`（默认）时着色器走原生 1 次采样 ⇒ 这些全局量只是备用，不产生开销。</summary>
        void PushShadowGlobals()
        {
            if (targetLight == null) return;
            bool on = pcssOn;
            Shader.SetGlobalFloat(IdRtEnabled, on ? 1f : 0f);
            Shader.SetGlobalFloat(IdRtQuality, pcssQuality);
            Shader.SetGlobalFloat(IdRtSoftness, lightSize * 0.0005f);   // 纹素基准 → 世界米（约 64 纹素 ≈ 3 cm）
            Shader.SetGlobalFloat(IdRtFloor, shadowFloor);
            Shader.SetGlobalFloat(IdRtBias, biasMeters);
            Shader.SetGlobalFloat(IdRtTanHalf, Mathf.Tan(targetLight.spotAngle * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalVector(IdRtPos, targetLight.transform.position);

            // 灯本身
            if (!Mathf.Approximately(targetLight.intensity, lightIntensity)) targetLight.intensity = lightIntensity;
            if (targetLight.shadows == LightShadows.None) targetLight.shadows = _originalShadows == LightShadows.None ? LightShadows.Hard : _originalShadows;

            var wantColor = lightColor;
            if (followAmbient > 0.001f)
            {
                var ambient = NTAmbient.Read();
                if (ambient.valid) wantColor = Color.Lerp(lightColor, NTAmbient.HueOnly(ambient.linear), followAmbient);
            }
            if (targetLight.color != wantColor) targetLight.color = wantColor;
        }

        /// <summary>
        /// 构造"灯视角"的 view / projection，以及供着色器使用的 worldToShadow。
        /// 返回 false 表示无法构造（灯不是 Spot）。
        /// </summary>
        public bool BuildMatrices(out Matrix4x4 view, out Matrix4x4 gpuProj, out Matrix4x4 worldToShadow)
        {
            view = Matrix4x4.identity;
            gpuProj = Matrix4x4.identity;
            worldToShadow = Matrix4x4.identity;
            if (targetLight == null || targetLight.type != LightType.Spot) return false;

            var lt = targetLight.transform;
            float near = Mathf.Max(targetLight.shadowNearPlane, 0.01f);
            float far = Mathf.Max(targetLight.range, near + 0.01f);

            // Unity 的视图矩阵约定：右手系、朝 -Z 看 ⇒ 先 1/-1/1 缩放再取 worldToLocal
            view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * lt.worldToLocalMatrix;
            // 聚光锥：半角 = spotAngle/2；aspect 固定 1（与深度图正方形的 RT 一致）
            var proj = Matrix4x4.Perspective(targetLight.spotAngle, 1f, near, far);
            gpuProj = GL.GetGPUProjectionMatrix(proj, true);
            // ★ 覆盖 z 行：near→0、far→1 的**单调**映射。
            //   `GL.GetGPUProjectionMatrix` 在 `usesReversedZBuffer` 的平台上把 z 变成
            //   "近=1、远=0"，这正是"球的片元被接收面拒绝"的根源（见 NTRTDepth.shader 头注释）。
            //   我们现在用 min 混合做归约、不用深度缓冲，但保留单调 z 让裁剪平面正常、
            //   将来若想改回深度测试也不需要再踩这个坑。
            gpuProj.m20 = 0f; gpuProj.m21 = 0f;
            gpuProj.m22 = -far / (far - near);
            gpuProj.m23 = -far * near / (far - near);

            // uvMat: NDC(-1..1) → uv(0..1)
            var uv = Matrix4x4.identity;
            uv.m00 = 0.5f; uv.m03 = 0.5f;
            uv.m11 = 0.5f; uv.m13 = 0.5f;

            worldToShadow = uv * gpuProj * view;
            return true;
        }

        /// <summary>渲染一张深度图并绑到材质。
        /// ⚠️ 自己先调 `EnsureResources()`：**编辑模式下 `OnEnable()` 不会跑**
        /// （Unity 只在运行期/`[ExecuteAlways]` 时调），而且**脚本重编译会清掉未序列化的字段**
        /// （`_depthMat` / `_rt` / `_cb`）—— 实测第一次实机跑就是 `_cb == null` 抛 NRE。
        /// 所以这里每次都做一次幂等的资源检查，调用方不需要自己保证顺序。</summary>
        public void RenderDepth()
        {
            if (targetLight == null) return;

            // [NT-FIX 21] 「阴影开关」必须真的管这盏灯（实机反馈："阴影开关根本就没有控制 Spot Light"）。
            // 关 = 整套 ⑥ 停：灯灭 + 还原该灯原本的 Unity 阴影 + 不渲深度图（也省掉那个 pass）。
            if (!pcssOn) { SuspendRig(); return; }
            RestoreRig();

            // [NT-FEAT 25] 新路径：不渲自己的深度图，直接用 Unity 的阴影贴图（着色器侧做 PCSS）。
            // 代价几乎为零（没有额外 pass、没有 RFloat RT、没有 HideAndDontSave 对象 ⇒
            // 也顺手绕开了"NDMF 烘焙被 DontSave 对象搞挂"那个坑）。
            if (useUnityShadowMap) { PushShadowGlobals(); return; }

            EnsureResources();
            if (_depthMat == null || _rt == null || _cb == null) return;

            var lt = targetLight.transform;
            float near = Mathf.Max(targetLight.shadowNearPlane, 0.01f);
            float far = Mathf.Max(targetLight.range, near + 0.01f);

            _depthMat.SetFloat(IdNear, near);
            _depthMat.SetFloat(IdFar, far);
            _depthMat.SetVector(IdLightPos, lt.position);

            Matrix4x4 view, gpuProj, w2s;
            bool ok = BuildMatrices(out view, out gpuProj, out w2s);
            if (!ok) return;

            // 阴影完全由我们提供 ⇒ 关掉 Unity 的实时阴影（避免两层阴影叠加）
            // 现在这步在 RestoreRig() 里做（它只在本帧确实要开 ⑥ 时才动手）。

            _cb.Clear();
            _cb.SetRenderTarget(_rt);                          // 无深度缓冲：归约靠 min 混合
            _cb.ClearRenderTarget(false, true, Color.white);   // 背景 = 1（远）
            _cb.SetViewProjectionMatrices(view, gpuProj);     // ★ 关键修复：不设就是单位矩阵
            foreach (var r in _casters)
            {
                if (r == null || !r.enabled) continue;
                _cb.DrawRenderer(r, _depthMat, 0, 0);
            }
            Graphics.ExecuteCommandBuffer(_cb);

            // ★ 关键：内核读的是 includes 里自声明的 `_NTRTShadowMapRaw`，
            //   ShaderLab 属性列表里没有它 ⇒ `Material.SetTexture` 无效，
            //   只有 `Shader.SetGlobalTexture` 绑得上（实测：不绑时深度图读不到、滤波无效）。
            Shader.SetGlobalTexture(IdGlobalShadowMap, _rt);

            // 灯的强度/颜色也由组件驱动（这样菜单动画驱动组件字段就够了，不必碰材质）
            if (targetLight != null)
            {
                if (!Mathf.Approximately(targetLight.intensity, lightIntensity)) targetLight.intensity = lightIntensity;

                // [NT-FEAT 21] 环境色相跟随：只取色相（HueOnly 归一化），因此灯强度不变。
                // 环境光太暗时 HueOnly 是噪声 ⇒ NTAmbient.Read() 的 valid 会挡住。
                var wantColor = lightColor;
                if (followAmbient > 0.001f)
                {
                    var ambient = NTAmbient.Read();
                    if (ambient.valid)
                    {
                        var hue = NTAmbient.HueOnly(ambient.linear);
                        wantColor = Color.Lerp(lightColor, hue, followAmbient);
                    }
                }
                if (targetLight.color != wantColor) targetLight.color = wantColor;
            }

            foreach (var m in _targets)
            {
                if (m == null) continue;
                // ⛔ **不要**绑 `_SelfLightRtShadowMap`（见 Release() 里的长注释：会把
                //    HideAndDontSave 的 RT 写进场景材质 ⇒ NDMF 烘焙保存时断言、整场作废）。
                //    内核读的是全局 `_NTRTShadowMapRaw`，上面已经 Shader.SetGlobalTexture 过了。
                if (m.HasProperty(PropTexels)) m.SetFloat(PropTexels, resolution);
                if (m.HasProperty(PropNear)) m.SetFloat(PropNear, near);
                if (m.HasProperty(PropFar)) m.SetFloat(PropFar, far);
                if (m.HasProperty(PropLightPos)) m.SetVector(PropLightPos, lt.position);
                if (m.HasProperty(PropW2S0)) m.SetVector(PropW2S0, w2s.GetRow(0));
                if (m.HasProperty(PropW2S1)) m.SetVector(PropW2S1, w2s.GetRow(1));
                if (m.HasProperty(PropW2S2)) m.SetVector(PropW2S2, w2s.GetRow(2));
                if (m.HasProperty(PropW2S3)) m.SetVector(PropW2S3, w2s.GetRow(3));
                // ⑥ 的用户旋钮（旋钮的真身在组件上，这里只是推给着色器）
                if (m.HasProperty(PropPcss)) m.SetFloat(PropPcss, pcssOn ? 1f : 0f);
                if (m.HasProperty(PropSize)) m.SetFloat(PropSize, lightSize);
                if (m.HasProperty(PropSoft)) m.SetFloat(PropSoft, softness);
                if (m.HasProperty(PropBias)) m.SetFloat(PropBias, biasMeters);
                if (m.HasProperty(PropQuality)) m.SetFloat(PropQuality, quality);
                if (m.HasProperty(PropFloor)) m.SetFloat(PropFloor, shadowFloor);
            }
        }
    }
}
