# 许可与署名

本包包含两个来源的代码。

## 1. 原始工具：LilToonToNonToonConverter 1.1.4

MIT License

Copyright (c) the LilToonToNonToonConverter authors
来源页面：https://vrc-levanilla.booth.pm/

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

原始来源文件的版权头（`SPDX-License-Identifier: MIT`）已保留在源码中。

## 2. 本 fork 的改动

以下改动由 NonToon (Fork) 维护，同样以 MIT 发布：

- 阴影默认走**上游 Shade 梯度 Ramp**（= 原版 NonToon 行为，保真 lilToon 的 border / blur /
  strength / 1st·2nd·3rd 色）；fork 自研的 `ShadowColor` 模块只在 ramp 不可用时回退。
  `[NT-FIX 30]` 修掉 `ReapplyModuleEnables()` 无条件打开 `ShadowColor` 的问题 ——
  它会让 `ShadowColor` 覆盖 ramp（该模块排在 Shade 之后且末尾是赋值而非相乘）
- 修 Int 属性写入：新增按 `Shader.GetPropertyType` 分派的 `SetNumber()`，
  修掉 `_ShadowColorEnable` 用 `SetFloat` 写导致"阴影模块没被打开"的问题
- 窗口增加拖放区（原版只认「当前选中」，拖进去没反应）
- VRChat 的 `PipelineManager` 改为**反射**访问，去掉对 VRChat SDK 的硬依赖
- 界面中文化

## 3. 关于 NonToon 本体

本包**不包含** NonToon 着色器本体。它依赖 `jp.lilxyzw.nontoon`（NonToon (Fork)），
该包基于 [lilxyzw/NonToon](https://github.com/lilxyzw/NonToon)，遵循其原始 LICENSE。
