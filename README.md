# nontoon-fork-MainCode

两个 VPM 包的**源码仓库** —— NonToon (Fork) 着色器本体，以及配套工具包。

> ⚠️ **这是源码仓库，不是发布仓库。** 可安装的版本由 `VPM-nontoon-fork` 仓库分发
> （`docs/vpm.json` + zip）。本仓库用于审阅、bug 报告与源码级贡献。

---

## 内容

| 目录 | 包 | 版本 | 说明 |
|---|---|---|---|
| `NonToon/` | `jp.lilxyzw.nontoon` "NonToon (Fork)" | 0.3.10 | 上游 `lilxyzw/NonToon` 0.1.3 的 fork。基于 ShaderCore，13 个模块（其中 `Emission` / `SelfLight` / `ShadowColor` 是 fork 新增） |
| `nontoon-converter/` | `com.123cy321.nontoon-converter` "NonToon (Fork) Tools" | 0.5.2 | lilToon→NonToon 材质转换器、光照控制器与 MA 菜单、烘焙私有光源 |
| `tools/` | — | — | `make-twopass.mjs`：从 `NonToon.scshader` 生成两趟透明变体 `NonToonTwoPass.scshader`（**派生文件，别手改**） |

## 先读这个

**`NonToon/SCOPE.md`** —— 支持范围与已知限制。它写明：支持什么、什么是实验性的、
什么是**明确不声称**的、以及哪些证据**还没做**。**如果 SCOPE.md 没列为支持，就按不支持处理。**

## 安装

**未发布到本仓库。** 从 VPM listing 安装（`VPM-nontoon-fork`），或把包目录直接放进 Unity 工程的
`Packages/` 下。

- Unity 2022.3，**Built-in 渲染管线**（URP 未验证）
- 依赖 `jp.lilxyzw.shadercore` `^0.1.9`

## 包身份（**发布前必须解决的阻塞点**）

本 fork **故意沿用上游的包 id** `jp.lilxyzw.nontoon`，目的是让安装它等同于原地升级官方 NonToon。
代价是：任何声明 `jp.lilxyzw.nontoon: ^0.1.3` 的第三方包（caret 语义 = `>=0.1.3 <0.2.0`）
**无法与本 fork 共存**（0.3.x 不满足）。已有一个公开包如此声明。

⇒ 在发布前必须二选一：**明确的上游替代策略 + 协调依赖方**，或者**独立的包身份 + 资产/GUID 共存策略**。

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
