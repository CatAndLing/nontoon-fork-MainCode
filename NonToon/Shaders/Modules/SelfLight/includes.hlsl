#ifndef NT_SELFLIGHT_SHADOW_INCLUDED
#define NT_SELFLIGHT_SHADOW_INCLUDED

// [NT-FEAT 7/10/15] SelfLight 的公共片段。
//
// 为什么用宏而不是函数：phase_*.hlsl 的内容会被 ShaderCore **原样拼进 frag() 内部**，
// 函数里不能再定义函数。而这个 includes.hlsl 会被拼到 include 区，宏在展开处（frag 内部）
// 才解析 SCSample / NTMaskValue / 材质属性，所以放在这里定义、在多个 phase 里展开都成立。
//
// [NT-FIX 0.3.8] 自阴影的取样点**必须夹到「纹素内部」**（blocker 与 filter 两处）：
//   ShaderCore 的 SC_SamplerState 展开成裸 `SamplerState`（没有 filter / address 声明），
//   实测（Unity 2022.3.22f1 / D3D11）深度图走的是**点采样 + Repeat**，而且**与贴图导入设置无关**
//   —— 烘焙器写进去的 `wrapMode = Clamp` 对采样不生效。
//   不夹时，螺旋偏移一旦越过包围盒边界就会读到深度图**对面**的纹素，表现有两种：
//     · 对面是背景（远）→ 贴边一圈的半影被冲淡（实测遮挡 1.000 → 0.642，漏光 36%）
//     · 对面是几何（近）→ 凭空多出一条假阴影（实测亮度 0.93 → 0.000，约 7 纹素宽）
//   注意：**只夹到 [0,1] 不够** —— Repeat 的有效区间是 [0,1)，u = 1.0 会被映射回纹素 0，
//   于是右边界依旧漏光（实测仍是 0.642；左边界倒是好了）。必须夹到
//   `[0.5 纹素, 1 - 0.5 纹素]`，落点才是首/末纹素，与 Clamp 语义一致。
//   夹完之后行为由代码决定，不再依赖平台 / 采样器绑定 / 用户换图（Quest 上同类风险一并消除）。
//   实测装置与像素证据：`unity/_notes/probes/NTPcssProbe.cs` + `_notes/evidence/pcss/`。

// [NT-FIX 0.3.9] 半影的世界宽度必须与**烘焙分辨率**无关（P2）。
//   背景：半径以「纹素」定义，而一个纹素的世界尺寸 = `2·halfX / _SelfLightShadowTexels`
//   ⇒ 世界半径 ∝ 1/分辨率。实测同一场景 256²→512² 半影世界尺度只剩 71%、1024² 只剩 57%。
//   为什么不是"半径 ×s"（线性的、看起来最自然的修法）：**实际半影宽度对纹素半径是超线性的**。
//   判别实验（同一张 256² 贴图，只改取样足迹，贴图内容一点没变）：
//     足迹 4.4 纹素 → 7.0 px ；8.8 纹素 → 28.0 px ；17.6 纹素 → 49.0 px
//   ⇒ 宽度 ≈ `R^1.55`，而盘状核的理论值只有 `1.6R`（小半径处只实现一半）。
//   所以线性的 ×s 会把世界尺度从 71% 矫枉过正到 143%（实测），tap 数 ×1/×s/×s² 都改变不了。
//   修法：按 `g(s) = s^p`、`p = 1/1.55 ≈ 0.65` 换算半径与上下限，使世界尺度守恒。
//   锚点性质：256² 时 `s = 1 ⇒ g = 1`，与旧行为**逐像素一致** ⇒ 存量 avatar（默认 256²）零变化。
//   回归门禁：`NTPcssProbe` 的【P2】断言（256² vs 512² 世界尺度须落在 85%~120%）。

// ---------------------------------------------------------------------------
// 自阴影系数：1 = 完全受光，0 = 完全在阴影里。
// 用法：NTSELFSHADOW(outShadow, positionWS, uv)
// ---------------------------------------------------------------------------
#ifndef NTSELFSHADOW
#define NTSELFSHADOW(outShadow, positionWS, uv) \
{ \
    outShadow = 1; \
    if (_SelfLightShadowStrength > 0.001) \
    { \
    float3 ntSD_Rel = (positionWS) - _SelfLightOrigin.xyz; \
    float2 ntSD_UV = float2(dot(ntSD_Rel, _SelfLightRight.xyz), dot(ntSD_Rel, _SelfLightUp.xyz)); \
    ntSD_UV = ntSD_UV / float2(max(1e-5, _SelfLightHalfX), max(1e-5, _SelfLightHalfY)) * 0.5 + 0.5; \
    float ntSD_Depth = (dot(ntSD_Rel, _SelfLightForward.xyz) - _SelfLightNear) / max(1e-5, _SelfLightFar - _SelfLightNear); \
    bool ntSD_InBox = ntSD_UV.x > 0 && ntSD_UV.x < 1 && ntSD_UV.y > 0 && ntSD_UV.y < 1 && ntSD_Depth > 0 && ntSD_Depth < 1; \
    bool ntSD_InRange = _SelfLightDistance <= 0.0001 || distance(_WorldSpaceCameraPos, positionWS) <= _SelfLightDistance; \
    if (ntSD_InBox && ntSD_InRange) \
    { \
        float2 ntSD_Texel = (1.0 / max(_SelfLightShadowTexels, 8.0)).xx; \
        /* [NT-FIX 0.3.8] 取样点必须夹到「纹素内部」而不是 [0,1]：                            */ \
        /*   Repeat 寻址下 u = 1.0 会被映射回纹素 0（Repeat 的有效区间是 [0,1)），           */ \
        /*   所以只夹到 [0,1] 时，越界的取样仍然会读到对面那一列 —— 实测右边界依旧漏光 36%。 */ \
        /*   夹到 [0.5 纹素, 1 - 0.5 纹素] 后，落点必定是首/末纹素，与 Clamp 语义一致。      */ \
        float2 ntSD_TapMin = ntSD_Texel * 0.5; \
        float2 ntSD_TapMax = float2(1.0, 1.0) - ntSD_Texel * 0.5; \
        float ntSD_Rot = frac(sin(dot(ntSD_UV, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853; \
        float ntSD_RotSin = sin(ntSD_Rot); \
        float ntSD_RotCos = cos(ntSD_Rot); \
        if (_SelfLightPCSS > 0.5) \
        { \
            int ntSD_BlockerBase = _SelfLightPCSSQuality < 0.5 ? 8 : (_SelfLightPCSSQuality < 1.5 ? 12 : (_SelfLightPCSSQuality < 2.5 ? 20 : 32)); \
            int ntSD_FilterBase = _SelfLightPCSSQuality < 0.5 ? 12 : (_SelfLightPCSSQuality < 1.5 ? 24 : (_SelfLightPCSSQuality < 2.5 ? 40 : 64)); \
            float ntSD_MaxRadius = ntSD_FilterBase >= 40 ? 48.0 : 24.0; \
            /* [NT-FIX 0.3.9] 纹素 → 世界尺度的换算。指数 0.8 是**实测标定**出来的（见文件头）：    */
            /*   实际半影宽度对纹素半径是超线性的（≈R^1.55），线性 ×s 会矫枉过正（71%→143%）；  */
            /*   又因为下面同时补了 tap 数（超出"半径"之外的额外影响），标定值落在 0.8。        */ \
            float ntSD_ResScale = max(_SelfLightShadowTexels, 8.0) / 256.0; \
            float ntSD_ResGain = pow(ntSD_ResScale, 0.8); \
            /* [NT-FIX 0.3.9] tap 数也要补：1024² 下 12 个 tap 在大半径上已经"糊不动"（实测半径 4.4→17.6 */
            /*   纹素，半影只从 4.0 涨到 5.0 px，饱和了）。tap 按 s² 补（面积密度守恒）并把总数封顶在 */ \
            /*   64/32（= 极高档的预算，避免 1024² 以上把采样炸掉）。256² 时 s² = 1 ⇒ 与旧行为一致。 */ \
            int ntSD_TapScale = (int)clamp(floor(ntSD_ResScale * ntSD_ResScale + 0.5), 1.0, 16.0); \
            int ntSD_BlockerTaps = min(ntSD_BlockerBase * ntSD_TapScale, 32); \
            int ntSD_FilterTaps = min(ntSD_FilterBase * ntSD_TapScale, 64); \
            float ntSD_BlockerSum = 0; \
            float ntSD_BlockerCount = 0; \
            for (int ntSD_BI = 0; ntSD_BI < ntSD_BlockerTaps; ntSD_BI++) \
            { \
                float ntSD_BF = (ntSD_BI + 0.5) / (float)ntSD_BlockerTaps; \
                float ntSD_BA = ntSD_BI * 2.39996323 + ntSD_Rot; \
                float2 ntSD_BO = float2(cos(ntSD_BA), sin(ntSD_BA)) * sqrt(ntSD_BF) * ntSD_Texel * 8.0; \
                float ntSD_BZ = _SelfLightShadowMap.SampleLevel(sampler_BaseTexture, clamp(ntSD_UV + ntSD_BO, ntSD_TapMin, ntSD_TapMax), 0).r; \
                if (ntSD_BZ < ntSD_Depth - _SelfLightShadowBias) { ntSD_BlockerSum += ntSD_BZ; ntSD_BlockerCount += 1; } \
            } \
            if (ntSD_BlockerCount < 0.5) \
            { \
                outShadow = 1; \
            } \
            else \
            { \
                float ntSD_AvgBlocker = ntSD_BlockerSum / ntSD_BlockerCount; \
                float ntSD_Penumbra = (ntSD_Depth - ntSD_AvgBlocker) / max(ntSD_AvgBlocker, 1e-4) * _SelfLightSoftness; \
                /* [NT-FIX 0.3.9] 半径与上下限按 g(s) 换算 ⇒ 世界尺度半影与烘焙分辨率无关 */ \
                float ntSD_Radius = clamp(ntSD_Penumbra * 0.5 * ntSD_ResGain, 1.5 * ntSD_ResGain, ntSD_MaxRadius * ntSD_ResGain); \
                float ntSD_Sum = 0; \
                for (int ntSD_PI = 0; ntSD_PI < ntSD_FilterTaps; ntSD_PI++) \
                { \
                    float ntSD_PF = (ntSD_PI + 0.5) / (float)ntSD_FilterTaps; \
                    float ntSD_PA = ntSD_PI * 2.39996323 + ntSD_Rot; \
                    float2 ntSD_Local = float2(cos(ntSD_PA), sin(ntSD_PA)) * sqrt(ntSD_PF) * ntSD_Radius; \
                    float2 ntSD_PO = float2(ntSD_Local.x * ntSD_RotCos - ntSD_Local.y * ntSD_RotSin, ntSD_Local.x * ntSD_RotSin + ntSD_Local.y * ntSD_RotCos) * ntSD_Texel; \
                    float ntSD_PZ = _SelfLightShadowMap.SampleLevel(sampler_BaseTexture, clamp(ntSD_UV + ntSD_PO, ntSD_TapMin, ntSD_TapMax), 0).r; \
                    ntSD_Sum += (ntSD_Depth - _SelfLightShadowBias) > ntSD_PZ ? 0.0 : 1.0; \
                } \
                outShadow = ntSD_Sum / (float)ntSD_FilterTaps; \
            } \
        } \
        else \
        { \
            half ntSD_Occluder = _SelfLightShadowMap.SampleLevel(sampler_BaseTexture, ntSD_UV, 0).r; \
            outShadow = (ntSD_Depth - _SelfLightShadowBias) > ntSD_Occluder ? 0 : 1; \
        } \
        if (_SelfLightShadowStrength < 0.999) outShadow = lerp(1, outShadow, _SelfLightShadowStrength); \
        if (_SelfLightDensity < 0.999) outShadow = lerp(1, outShadow, _SelfLightDensity); \
        if (_SelfLightClamp > 0.001) \
        { \
            float ntSD_Contrast = lerp(1.0, 40.0, _SelfLightClamp); \
            outShadow = saturate((outShadow - 0.5) * ntSD_Contrast + 0.5); \
        } \
    } \
    } \
    if (_SelfLightShadowMaskStrength > 0.001)\
    {\
        half ntSD_SMask = NTMaskValue(SCSample(_SelfLightShadowMask, sampler_BaseTexture, uv), _SelfLightShadowMaskChannel);\
        outShadow *= lerp(1.0, ntSD_SMask, _SelfLightShadowMaskStrength);\
    }\
    if (_SelfLightReceiveMaskStrength > 0.001) \
    { \
        half ntSD_Mask = NTMaskValue(SCSample(_SelfLightReceiveMask, sampler_BaseTexture, uv), _SelfLightReceiveMaskChannel); \
        outShadow = lerp(1.0, outShadow, lerp(1.0, ntSD_Mask, _SelfLightReceiveMaskStrength)); \
    } \
}
#endif


// ---------------------------------------------------------------------------
// [NT-FEAT 16] 色温（Kelvin）→ RGB。
// Tanner Helland 的黑体辐射近似：几行 ALU、零采样。6500K ≈ 白，低色温偏橙、高色温偏蓝。
// 用法：NT_SELFLIGHT_KELVIN(outRGB, kelvin)
// ---------------------------------------------------------------------------
#ifndef NT_SELFLIGHT_KELVIN
#define NT_SELFLIGHT_KELVIN(outRGB, kelvin) \
{ \
    float ntSK_T = clamp((kelvin), 1000.0, 40000.0) / 100.0; \
    float ntSK_R = ntSK_T <= 66.0 ? 255.0 : 329.698727446 * pow(max(ntSK_T - 60.0, 1e-3), -0.1332047592); \
    float ntSK_G = ntSK_T <= 66.0 ? (99.4708025861 * log(max(ntSK_T, 1.0)) - 161.1195681661) : (288.1221695283 * pow(max(ntSK_T - 60.0, 1e-3), -0.0755148492)); \
    float ntSK_B = ntSK_T >= 66.0 ? 255.0 : (ntSK_T <= 19.0 ? 0.0 : (138.5177312231 * log(max(ntSK_T - 10.0, 1e-3)) - 305.0447927307)); \
    outRGB = saturate(half3(ntSK_R, ntSK_G, ntSK_B) / 255.0); \
}
#endif

// ---------------------------------------------------------------------------
// [NT-FEAT 17] 世界光的颜色（"探地图的灯"用）。
// 取 SH 的 L0 项（环境光的颜色）；光照贴图工程里 SH 不可用，就退化成主光颜色。
// 用法：NTSELFLIGHT_WORLD_COLOR(outWorld, ambientColor)
//   ambientColor 由调用点算 —— 宏体里不能放 #if 之类的预处理指令
// ---------------------------------------------------------------------------
#ifndef NTSELFLIGHT_WORLD_COLOR
#define NTSELFLIGHT_WORLD_COLOR(outWorld, ambientColor)\
{\
    outWorld = (ambientColor) + _LightColor0.rgb * 0.5;\
}
#endif

// ---------------------------------------------------------------------------
// [NT-FEAT 17] 自有光的最终颜色：色温 → 环境匹配。
// 用法：NTSELFLIGHT_COLOR(outColor, ambientColor)
// ---------------------------------------------------------------------------
#ifndef NTSELFLIGHT_COLOR
#define NTSELFLIGHT_COLOR(outColor, ambientColor) \
{ \
    half3 ntSLC_Base = _SelfLightColor.rgb; \
    if (_SelfLightUseTemperature > 0.001) \
    { \
        half3 ntSLC_Kelvin; \
        NT_SELFLIGHT_KELVIN(ntSLC_Kelvin, _SelfLightTemperature) \
        ntSLC_Base *= ntSLC_Kelvin; \
    } \
    outColor = ntSLC_Base; \
    if (_SelfLightMatchAmbient > 0.001) \
    { \
        half3 ntSLC_World = (ambientColor); \
        outColor = lerp(ntSLC_Base, ntSLC_World, saturate(_SelfLightMatchAmbient)); \
    } \
}
#endif

// ---------------------------------------------------------------------------
// [NT-FEAT 17] 自有光的方向：可选跟随"世界主光"的方向，
// 这样自带阴影的落向与地图里的太阳一致，不会看起来像贴上去的。
// 只在 base pass 有效（附加光 pass 里 _WorldSpaceLightPos0 指的是那盏附加光）。
// 用法：NTSELFLIGHT_DIR(outDir, positionWS)
// ---------------------------------------------------------------------------
#ifndef NTSELFLIGHT_DIR
#define NTSELFLIGHT_DIR(outDir, positionWS) \
{ \
    half3 ntSLD_Self = _SelfLightDirection.xyz; \
    ntSLD_Self = dot(ntSLD_Self, ntSLD_Self) > 0 ? normalize(ntSLD_Self) : half3(0, 1, 0); \
    if (_SelfLightMatchDirection > 0.001) \
    { \
        half3 ntSLD_World = _WorldSpaceLightPos0.w == 0 \
            ? normalize(_WorldSpaceLightPos0.xyz) \
            : normalize(_WorldSpaceLightPos0.xyz - (positionWS)); \
        ntSLD_Self = normalize(lerp(ntSLD_Self, ntSLD_World, saturate(_SelfLightMatchDirection))); \
    } \
    outDir = ntSLD_Self; \
}
#endif

#endif
