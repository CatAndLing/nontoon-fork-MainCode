# nontoon-fork-MainCode

两个 VPM 包的**源码仓库** —— NonToon (Fork) 着色器本体，以及配套工具包。

> ⚠️ **这是源码仓库，不是发布仓库。** 可安装的版本由 `VPM-nontoon-fork` 仓库分发
> （`docs/vpm.json` + zip）。本仓库用于审阅、bug 报告与源码级贡献。

---

## 目标（一句话）

> **轻量化的真实光影。**
> 以**真实光照**为核心（自带光 / 世界光驱动明暗，而不是把光照烘死），阴影按**能力阶梯**逐级加强，
> **默认零开销**。差异化对象是 **lilToon 那条"全功能 + 真实光影"的重路线**（其性能影响过大）。
> ⛔ **不是"第二个 lilToon"**：不以补齐 lilToon 的能力清单为目标；lilToon→NonToon 的**迁移便利**是入口，不是终点。

**能力阶梯**（架构原则：性能占用低→高、实现难度易→难，用户按需选择）：

| 级 | 内容 | 性能占用 | 状态 |
|---|---|---|---|
| **T0** 默认 | 上游原生 ramp（`_ShadeGradientIndex` 默认 `-1` ⇒ 默认**关闭**，这是上游原版行为） | 零新增采样 | ✅ |
| **T1** | 原生阴影外围增强：**锐化** + **环境光染色**（+ 距离/遮罩 = T1b） | 零～极低 | 🟡 第一增量已落地（0.3.12，待发布） |
| **T2** | `fwidth` 解析分界 + 抗锯齿 | 低（无采样） | ⬜ |
| **T3** | PCSS 实时半影 | 高（20–96 采样/像素） | 🔒 路线保留，但**当前无实现**（⑥ 的深度来源未决） |

**唯一目标来源是 `_notes/目标契约.md`（v3）** —— 任何新工作必须先回答"它服务于哪一条验收口径"。

---

## 内容

| 目录 | 包 | 版本 | 说明 |
|---|---|---|---|
| `NonToon/` | `com.catandling.nontoon` "NonToon (Fork)" | 0.3.12 | 上游 `lilxyzw/NonToon` 0.1.3 的 fork。基于 ShaderCore，13 个模块（其中 `Emission` / `SelfLight` / `ShadowColor` 是 fork 新增）。着色器名 `nontoon-fork` / `nontoon-fork-fur` / `nontoon-fork-twopass`。0.3.12 起含 **T1 原生阴影增强**（锐化 + 环境光染色，**默认 no-op**） |
| `nontoon-converter/` | `com.catandling.nontoon-converter` "NonToon (Fork) Tools" | 0.5.4 | lilToon→NonToon 材质转换器、光照控制器与 MA 菜单、烘焙私有光源 |
| `tools/` | — | — | `make-twopass.mjs`：从 `NonToon.scshader` 生成两趟透明变体 `NonToonTwoPass.scshader`（**派生文件，别手改**） |

> ⚠️ 上表是**源码版本**。**线上已发布的是 0.3.11 / 0.5.3**；0.3.12 / 0.5.4 已备好但**尚未发布**
> （发布按用户决定推迟，先做 MA 全量接入）—— 见下面的「当前状态」。

## 先读这个

**`NonToon/SCOPE.md`** —— 支持范围与已知限制。它写明：支持什么、什么是实验性的、
什么是**明确不声称**的、以及哪些证据**还没做**。**如果 SCOPE.md 没列为支持，就按不支持处理。**

## 安装

**已发布**（2026-09-18 起）。用 VPM 客户端添加本 listing：

```
https://catandling.github.io/VPM-nontoon-fork/vpm.json
```

落地页 <https://catandling.github.io/VPM-nontoon-fork/> 上有一键添加按钮。也可以把包目录直接放进
Unity 工程的 `Packages/` 下。

- Unity 2022.3，**Built-in 渲染管线**（URP 未验证）
- 着色器包依赖 `jp.lilxyzw.shadercore` `^0.1.9`
- 工具包依赖 `com.catandling.nontoon >=0.3.11` 与 `nadena.dev.modular-avatar >=1.10.0`
- ⚠️ **实际需要一个 VRChat SDK 工程**：工具包 0.5.2 起把 Modular Avatar 设为**必需**依赖，
  而 MA 的 `Runtime` asmdef 用 `overrideReferences: true` 显式引用 `VRCSDKBase.dll` /
  `VRCSDK3A.dll` / `VRC.Dynamics.dll` / `System.Collections.Immutable.dll`
  （实测依据：MA 包的 asmdef；缺 SDK 时 Unity 以「Scripts have compiler errors」中止）。
  ⇒ 工具包描述里原来那句"不依赖 VRCSDK、在非 VRChat 工程也能用"**自 0.5.2 起就不成立**，
  **已在 0.5.4 改成如实表述**（"…its Modular Avatar dependency chain requires a VRChat SDK project;
  it is not buildable in a non-VRChat project"）。

## 包身份（**已于 2026-09-18 解决**）

本 fork **曾**沿用上游包 id `jp.lilxyzw.nontoon`；现已彻底切分为**自有身份**：

| | 旧（已废弃） | 现在 |
|---|---|---|
| 着色器包 | `jp.lilxyzw.nontoon` | `com.catandling.nontoon` @ 0.3.12 |
| 工具包 | `com.123cy321.nontoon-converter` | `com.catandling.nontoon-converter` @ 0.5.4 |
| 着色器名 | `NonToon` | `nontoon-fork`（`-fur` / `-twopass`） |

- 用户已确认本 fork **没有需要迁移的老用户** ⇒ 全部全新安装，**不提供迁移路径**。
- **程序集名与资产 GUID 保留**（实测仍为 `jp.lilxyzw.nontoon` / `com.123cy321.nontoon-converter`），
  目的是让已有材质的引用不断。**包 id ≠ 程序集名**，别把两者混为一谈。
- 第三方包若声明上游 `jp.lilxyzw.nontoon: ^0.1.3`，现在会**各自安装、互不覆盖**，
  但也**不会**作用于本 fork（LightLimit 例外：它按路径含 `nontoon` 扫描 `.scshader`，仍会命中）。

## 重新生成两趟透明变体

```bash
node tools/make-twopass.mjs
```

它会生成 `NonToon/Shaders/NonToonTwoPass.scshader` 与其核心属性文件。
**重新生成后必须零差异**；若有差异，说明有东西被手改了。

⚠️ ShaderCore 的两条硬约束（踩出来的，别再踩）：
1. `.scshader` 里 `Shader "名字"` **必须是文件第一行**（解析器用的正则没有 Multiline）。
2. `<着色器路径>_properties.hlsl` **必须是纯声明** —— 解析器**不跳 `//` 注释、也不认 `#include`**。

## 当前状态（2026-09-18）

| 项 | 状态 |
|---|---|
| **线上已发布** | 着色器 **0.3.11** ／ 工具包 **0.5.3** |
| **源码领先（待发布）** | **0.3.12 / 0.5.4**：T1 第一增量（锐化 + 环境光染色，默认 no-op）· 删掉零调用方的 `NTRTDepth.shader` · `SCOPE.md` **撤回一处假宣称** · 修掉包描述里的过期宣称 · 干净安装门禁去掉硬编码版本 |
| **进行中** | 工具包**完全接入 MA/NDMF 插件体系**（注册构建期 pass，把现在"事后手点菜单"的事搬进构建期；编辑期行为不变） |
| **队列** | T1b（距离/遮罩）· **T1 验收探针**（判据：分界过渡带宽度 + 色相随环境的数值断言）· 转换器三个症状的像素证据（夹克针织/噪点、头发灰紫、**透明容器内含物体**） |

**必须如实保留的不一致**：`NonToon/SCOPE.md` 原先那句"实时自阴影设计未变、该路径什么都没被移除"
**是假的** —— 实测 `_SelfLightRt*` 在 0.3.11 已被删（34 → 0），且着色器里**检索不到**任何
"采样私有光 Unity 阴影贴图"的路径。该说法**已撤回**；**⑥ 的深度来源仍待定**（走 Unity 阴影贴图 / 恢复自渲）。

## 许可证与归属

上游 NonToon 的许可证与归属**原样保留**。本仓库是 fork，**不是官方上游发布**。
