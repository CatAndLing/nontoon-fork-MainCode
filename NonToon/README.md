NonToon (Fork)
====

> **简体中文说明（本分支 0.3.0）**
>
> 这是 [lilxyzw/NonToon](https://github.com/lilxyzw/NonToon) 的 **fork**，包 id 与官方相同
> （`com.catandling.nontoon`），所以安装时是**升级**官方版而不是并存。
> 仓库与一键添加地址：<https://catandling.github.io/VPM-nontoon-fork/>
>
> **相对官方 0.1.3 的主要差异**
>
> - 修掉半透明失效（#11）、透明排序队列；新增 lilToon 式 `ShadowColor`（1/2/3 层阴影色）与 `Emission` 模块
> - 与 lilToon **同名同义**的光照调整：`_AsUnlit` / `_LightMinLimit` / `_LightMaxLimit` / `_MonochromeLighting`
> - `_ShadeDirectionBias`（受光方向偏移）、`VR Parallax Strength`、逐材质 `Nearer` 开关、遮罩通道 8 档（含 `1-R`）
> - **SelfLight 模块**：角色私有光 + 烘焙自阴影，**不需要实时光**（不影响 VRChat 性能等级的 Lights 计数）；
>   0.3.0 起支持 **PCSS 软阴影**（blocker search + 变半径 PCF，固定 8+12 次采样）、
>   **Shadow Distance**、**ReceiveMask**、**阴影浓度**与 **Shadow Clamp**
> - **ShadowColor 支持 lilToon 的逐像素阴影遮罩**：`_ShadowStrengthMask`（`.r`）/
>   `_ShadowBorderMask`（`.rgb` = 第 1/2/3 层）/ `_ShadowBlurMask`（`.rgb` = 第 1/2/3 层），
>   默认关闭时零采样
> - **界面全中文**：材质面板的属性名/提示/模块标题/枚举标签/ShaderLab 渲染与模板属性，以及配套工具包
>   （`com.catandling.nontoon-converter`）的窗口 / 报告 / 日志
>
> 材质转换器与 SelfLight 烘焙器在**配套工具包** `com.catandling.nontoon-converter` 里
> （本包保持"纯着色器库"）。用法与注意事项见该包内的 `README.md` / `Mapping.md` / `SelfLight.md`。

---

以下为上游原文（日文）。

PBRの手法とNPRの手法を組み合わせたシェーダーです。

開発中であるため、仕様が大幅に変更される可能性があります。

### 開発段階のツイート

- https://x.com/lil_xyzw/status/2021533071905063072
- https://x.com/lil_xyzw/status/2051643868932931784
- https://x.com/lil_xyzw/status/2069333318991290773
- https://x.com/lil_xyzw/status/2069670974312886514

## インストール

vpmでインストールできます。

## 主な機能

- 不透明、カットアウト、ディザ、半透明、ファー
- 各機能のマスクとして使える共有マスクテクスチャ
- ノーマルマップ
- 影のオフセット
- アウトライン（頂点カラーによって太さや奥へのオフセットが可能）
- RGBAでマスク可能な4つのディテールテクスチャ・ノーマルマップ
- スペキュラー
- 瞳の発光などに使える明るさのブースト
- リムシェード
- Rampテクスチャによる陰影表現
- SDFテクスチャ
- 加算マットキャップ・乗算マットキャップ
- 異方性反射を応用したヘアスペキュラー
- リムライト
- 距離フェード
- ステンシル

## 仕様

- モジュール型シェーダーシステム[Shader Core](https://github.com/lilxyzw/Shader-Core)を使用しているためモジュールをインストールするだけで拡張可能です。
- 本体の機能もほとんどがモジュールとして定義されているため、別のシェーダーで利用することができます。

## 対応しないこと

- ドキュメントは日本語のみです。ブラウザなどの機械翻訳で十分読める上、仮に私が英語のドキュメントを書く場合には機械翻訳を使用するため結果として品質がほとんど変わらないためです。
- 機能の追加はキャラクター表現必須に近いと思われるもの以外には基本的に行われません。ほとんど使われない不要な機能であふれかえるのを防ぎ、本体を最小限に保つためです。代わりにモジュールで拡張していきます。
- プラットフォーム固有の機能には対応しません。こちらもモジュールで対応します。
- アウトラインの太さをマスクテクスチャで制御することには対応していません。頂点単位の処理は頂点データで行われるべきであるためです。代わりに[lilOutlineSmoother](https://github.com/lilxyzw/lilOutlineSmoother)でテクスチャを頂点カラーに焼き込むことができます。
