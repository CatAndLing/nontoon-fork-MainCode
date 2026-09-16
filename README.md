# nontoon-fork-MainCode

两个 VPM 包的**源码仓库** —— NonToon (Fork) 着色器本体，以及配套工具包。

> ⚠️ **这是源码仓库，不是发布仓库。** 可安装的版本由 `VPM-nontoon-fork` 仓库分发
> （`docs/vpm.json` + zip）。本仓库用于审阅、bug 报告与源码级贡献。

---

## 内容

| 目录 | 包 | 版本 | 说明 |
|---|---|---|---|
| `NonToon/` | `com.catandling.nontoon` "NonToon (Fork)" | 0.3.11 | 上游 `lilxyzw/NonToon` 0.1.3 的 fork。基于 ShaderCore，13 个模块（其中 `Emission` / `SelfLight` / `ShadowColor` 是 fork 新增）。着色器名 `nontoon-fork` / `nontoon-fork-fur` / `nontoon-fork-twopass` |
| `nontoon-converter/` | `com.catandling.nontoon-converter` "NonToon (Fork) Tools" | 0.5.3 | lilToon→NonToon 材质转换器、光照控制器与 MA 菜单、烘焙私有光源 |
| `tools/` | — | — | `make-twopass.mjs`：从 `NonToon.scshader` 生成两趟透明变体 `NonToonTwoPass.scshader`（**派生文件，别手改**） |

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
  ⇒ 工具包 `package.json` 里"不依赖 VRCSDK、在非 VRChat 工程也能用"那句描述
  **自 0.5.2 起已不成立**，待修（见 `_notes/发布记录-0.3.11-0.5.3.md`）。

## 包身份（**已于 2026-09-18 解决**）

本 fork **曾**沿用上游包 id `jp.lilxyzw.nontoon`；现已彻底切分为**自有身份**：

| | 旧（已废弃） | 现在 |
|---|---|---|
| 着色器包 | `jp.lilxyzw.nontoon` | `com.catandling.nontoon` @ 0.3.11 |
| 工具包 | `com.123cy321.nontoon-converter` | `com.catandling.nontoon-converter` @ 0.5.3 |
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

## 许可证与归属

上游 NonToon 的许可证与归属**原样保留**。本仓库是 fork，**不是官方上游发布**。
