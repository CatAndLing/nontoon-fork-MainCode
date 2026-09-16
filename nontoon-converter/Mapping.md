# lilToon → NonToon (Fork) 設定対応表

本转换器针对 **NonToon (Fork) ≥ 0.1.11**。原版工具（LilToonToNonToonConverter 1.1.4）面向 NonToon 0.1.3 / Shader Core 0.1.5，
很多地方只能"近似"。本分支多出来的模块与属性让其中几项可以**从近似升级为 1:1 拷贝**，下表标了 `[1:1]` / `[近似]`。

> 转换**只生成新材质**，原 lilToon 材质一个都不动。

## 一、本分支新增能力带来的升级（相对原版工具）

| lilToon | 原版工具的做法 | 现在 | 说明 |
| --- | --- | --- | --- |
| `_ShadowColor` / `_Shadow2ndColor` / `_Shadow3rdColor` + Border / Blur / Strength / BorderColor | 烧进 256px Ramp，边界只能放在固定位置 | **`[1:1]` 烧进上游 Shade 梯度 Ramp（默认路径）** | 默认与**原版 NonToon 行为一致**：颜色与 Border/Blur/Strength 烘成 NonToon 的 Shade ramp（`_ShadeGradientIndex ≥ 0`），并把 fork 自研的 `ShadowColor` 模块**明确关掉**。仅当目标没有上游 Shade 模块时才回退到 `ShadowColor` |
| `_UseEmission` / `_EmissionColor` / `_EmissionMap` / `_EmissionBlend` / `_EmissionBlendMask` / `_EmissionMainStrength` / `_EmissionBlendMode` | 只把强度近似成 `LightBoost`，**Emission Map 明确未对应** | **`[1:1]` Emission 模块** | 贴图与遮罩直接拷；算法逐字对应 lilToon `lilBlendColor`；跑在 `postpixel` 阶段 → **发光不被阴影衰减** |
| `_AsUnlit` / `_LightMinLimit` / `_LightMaxLimit` / `_MonochromeLighting` | 只写进诊断日志，**没有映射** | **`[1:1]` 同名属性** | 本分支这几个属性名与 lilToon 完全一致，所以是直接搬 |
| （lilToon 没有对应概念） | — | **`_ShadeDirectionBias` 置 0** | NonToon 原本的受光方向带 1.5 的「视线权重」（受光面跟着相机走）；lilToon 是**跟光**。转换时置 0 才与 lilToon 一致 |
| 发光遮罩 | 占共享遮罩一条通道 | **不再占用** | 有原生 Emission 模块后，Blend Mask 直接挂在模块上，省下共享遮罩通道（共享遮罩只有 4 条，0.1.8 起支持 `1-R` 反向来缓解） |

## 二、原有的映射（与原版工具一致）

| lilToon | NonToon 目标 | 模块 | 内容 |
| --- | --- | --- | --- |
| `_MainTex` + `_Color` | `_BaseTexture` | Core | 烧一张乘过颜色的 PNG；`_MainTex_ST` 作为 Scale/Offset 继承 |
| `_BumpMap` + `_BumpScale` | `_Detail0NormalMap` + `_Detail0NormalScale` | Details | 设到 **Details A**；没装 Details 才退回 `_NormalMap` |
| `_Smoothness` | `_Roughness` | Core | `roughness = 1 - smoothness` |
| `_Cutoff` | `_Cutoff` | Core | 直接拷 |
| `_TransparentMode` + `_UseDither` | `_RenderingMode` | Core | Opaque / Cutout / Dither / Transparent；Refraction・Gem 近似为 Transparent |
| `_Cull` / `_ZWrite` | 同名 | Core | 直接拷 |
| `_AlphaMask` | `_SharedMask.R` | Core | 烧进共享遮罩 R |
| `_ShadowStrengthMask` | `_SharedMask.G` | Shade | 烧进 G。**注意**：这些**贴图类**阴影遮罩只挂在 `ShadowColor` 回退模块上，走默认的 Shade ramp 时**用不到** |
| `_MatCapBlendMask` | `_SharedMask.B` | MatCaps | 烧进 B |
| `_RimShadeMask` | `_SharedMask.A` + `_RimShadeMaskChannel` | RimShade | 选 A 通道 |
| `_UseOutline` / `_OutlineColor` / `_OutlineWidth` | 同名 | Core | 色与宽度近似；顶点色设置 → `_OutlineFromVertexColor` |
| `_UseReflection` / `_ApplySpecular` / `_ReflectionColor` | `_SpecularColor` / `_SpecularMultiplyAlbedo` | Specular | 反射色近似成高光色；反射 Cube / 金属度 / 卡通边界未对应 |
| `_UseMatCap` / `_MatCapTex` / `_MatCapColor` | `_MatCapMultiply*` 或 `_MatCapAdd*` | MatCaps | 按 lilToon 的混合类型选加算/乘算 |
| `_UseRim` / `_RimColor` / `_RimMainStrength` / `_RimBorder` / `_RimBlur` | `_RimLightColor` / `_RimLightMultiplyAlbedo` / `_RimLightRange` | RimLight | 近似 |
| `_UseRimShade` / `_RimShadeColor` | `_SharedGradients` + `_RimShadeGradientIndex` | RimShade | 烧 256px Ramp（本分支没有原生替身） |
| `_DistanceFade` | `_DistanceFade` / `_DistanceFadeStrength` | DistanceFade | 近似 |
| `_Stencil*` / `_OutlineStencil*` | 同名 | Core | 直接拷 |
| `_UseAnisotropy` | `_HairSpecularGradientIndex` | HairSpecular | **仅检测 + 警告**，不自动推断 Ramp |
| `_UseBump2ndMap` | Details B–D | Details | **仅检测 + 警告** |

## 三、不转换的功能（转换后需人工确认）

Main 2nd / 3rd（**烧进 Base Texture**，不是丢弃）、Decal、Dissolve、Glitter、Refraction、Gem、
AudioLink、UV 动画、Parallax、**Emission Map 之外的第二套发光（`_Emission2nd*`）**、
渐变发光 / 闪烁 / 荧光 / 视差深度 / UV 模式、Reflection Cube、
以及 lilToon 那套细粒度的阴影边界/模糊行为（`_ShadowAOShift` 之类的重映射）。

> **0.4.0 起「贴图类阴影遮罩」已可转换**：`_ShadowStrengthMask` / `_ShadowBorderMask` /
> `_ShadowBlurMask` 三个同名属性直接搬进 NonToon 的 `ShadowColor` 模块，通道语义与 lilToon 一致
> （Strength 用 `.r`；Blur / Border 用 `.rgb` 分别对应第 1/2/3 层），转换器会自动打开
> `Use Shadow Masks` 并复制 tiling/offset。开关默认关，关着时**一次采样都不做**。
>
> ⚠️ **但自 0.5.x 起阴影默认走上游 Shade ramp（见第四节第 1 条），`ShadowColor` 模块是被关掉的**
> ⇒ 这三张遮罩贴图也就跟着不生效。它们是"回退路径"的配套能力。

## 四、⚠️ 转换后的注意事项（要点）

1. **阴影默认走上游 Shade 梯度 Ramp**（= 原版 NonToon 行为，`[NT-FIX 28]`）：
   转换器把 lilToon 的颜色与 Border/Blur/Strength 烘成 ramp，并把 fork 自研的 `ShadowColor`
   模块**明确关掉**（`_ShadowColorEnable = 0`）。
   ⛔ **两者不要同时开**：`ShadowColor` 按模块名排序（`"Shade" < "ShadowColor"`）**排在 Shade 之后**，
   而且它末尾是 `sd.col.rgb = lerp(...)` —— **赋值不是相乘** ⇒ 会把 ramp 的结果**整个覆盖掉**
   （详见 `Shaders/Modules/ShadowColor/phase_shade.hlsl:13-16` 与 `:124`）。
   这正是 `[NT-FIX 30]` 修的 bug：早先 `ReapplyModuleEnables()` 无条件把 `_ShadowColorEnable` 置 1，
   导致"日志说走 ramp、画面跑 ShadowColor"。
2. **受光方向被置 0**：这是为了贴近 lilToon 的跟光行为。想要 NonToon 原味（受光面跟相机）
   就把材质里的 **Shade Direction Bias 调回 1.5**；侧光场景觉得"该亮的面发暗"也调这个。
3. **发光是 1:1 的**：颜色/贴图/遮罩/混合模式都搬过去了，且不受阴影衰减。
   但 **`_Emission2nd*`、渐变发光、闪烁、荧光、视差深度、`_EmissionMap_UVMode` 仍未移植**。
4. **遮罩通道**：共享遮罩只有 RGBA 4 条。0.1.8 起通道下拉支持 `1-R / 1-G / 1-B / 1-A` 反向，
   同一条通道可以同时供"要遮罩"和"要反向遮罩"两个功能用 —— 手工调时可省通道。
   转换器烧出来的 `_SharedMask` 是**正向**的，反向选项要自己选。
5. **描边的深度偏移没自动映射**：本分支有 `_OutlineOffsetFactor` / `_OutlineOffsetUnits`，
   但 lilToon 的 `_OutlineZBias` 量纲不同，硬映射会出错，所以留给你手调（默认 0,0 中性）。
6. **`_SpecularF0` 保持 NonToon 默认 0.04**：lilToon 没有直接对应项，需要的话手动调。
7. **MatCap 在 VR 里默认逐眼采样**（`VR Parallax Strength = 1`，本分支新增）。
   如果你要的是"两眼一致"的贴纸感，把它设 0。
8. **细节法线走 Details A**：没装 Details 模块时会退回 `_NormalMap`（保真度下降）。
9. **贴图会被临时改成可读**再烧录，之后恢复原设置；Crunch 压缩图会先临时展开。
10. **一定要看报告**：`Assets/NonToonConverted/LilToonToNonToonReport.txt`，
    以及 `Assets/NonToonConversionLogs/` 里那份带环境信息的日志。
    每一条 `WARNING` / `MISSING MODULE` 都写明了哪一项没搬过去、为什么。
11. **转换是近似的**：lilToon 与 NonToon 的反射模型、UV、合成方式都不同，
    转换完请**目视确认**，尤其是反射（Roughness / Specular）与 MatCap 的明暗。
12. 原版工具面向 NonToon 0.1.3，**本转换器要求 NonToon (Fork) ≥ 0.1.11**。
    两者混用会出现两份转换器（0.1.10 及更早的着色包内含转换器）。
