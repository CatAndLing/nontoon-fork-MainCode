# AGENTS.md —— NonToon (Fork) 工作区指令

> 这是给**自动化审计方 / agent** 的项目上下文。人类读者请看 `README.md` 与 `NonToon/SCOPE.md`。

## 1. 项目目标（一句话）

**在 VRChat 头像上做到「リアル影システム (for Avatar) PCSS For VRC」那样的真实光影，但开销可接受。**

实现手段（用户明确设计，**不要建议推翻它**）：

```
在 avatar 上挂一盏 Unity 实时 Light
  → Unity 为该光源渲染阴影贴图
  → 着色器采样该贴图
       现在 = 1 次硬件比较采样（硬阴影）
       PCSS = N 次比较采样 + 距离相关半影（软阴影）
```

⇒ 「挂灯」与「PCSS」是**同一条链的前后两段**。

四根支柱：① 把 NonToon 完善到接近 lilToon（按**真实素材使用频次**定边界）；
② **lilToon → NonToon 转换器（必须，lilToon 素材才是主流）**；③ 光照控制器 + 自发光；
④ 接入 Modular Avatar 生态。

⚠️ 历史教训：曾把 SelfLight 模块「不得新增实时 Light」的包描述错安到本方案头上，
据此得出过「立即停掉灯架」的错误结论 —— **那条已作废。**

## 2. 环境与硬约束

```
Unity 2022.3.22f1 / Built-in RP / Linear / D3D11 / Windows
NonToon = ShaderCore 着色器：13 个模块；phase_*.hlsl 被【文本拼接】进 frag() 函数体
  · 模块注入顺序按【模块名】排序（"RimShade" < "Shade" < "ShadowColor"）
  · 该版本 afters/befores 不可靠（声明 phase 会导致文件被注入两次）
  · 模块开关的【真身是 shader keyword】，不是属性数值
  · `Shader "名字"` 必须是 .scshader 的【第一行】
  · 核心属性靠 <路径>_properties.hlsl 注入，且该文件必须是【纯声明】
    （解析器不跳 // 注释、不跟 #include）
```

**硬约束**：① **默认零开销，且默认路径的编译产物里没有多余工作**（像素相等 ≠ 零开销）；
② 转换器**不得修改源 lilToon 材质**；③ 判定**必须靠测量**（逐像素 / 退出码 / 序列化字段直读），
不接受肉眼结论；④ 菜单同步预算 ≤ 30 bit（现 17 bit）。

**能力缺口**：没有 RenderDoc、没有 frame debugger、没有 GPU capture。
有 Unity 编辑器外挂 MCP、自建 headless-Unity 批处理管线（按退出码判定）、shell、node。

## 3. ⛔ 本项目真实发生过的「假绿」事故（审任何门禁时都拿这张表去对）

1. 探针**根本没产出报告**却被算成绿 ⇒ 判据改成只看退出码。
2. 改成退出码后：探针中途崩溃/被 kill，磁盘上留着**上一轮的**「==== 全部通过 ====」⇒
   现在**运行开始先删旧报告**。
3. **唯一一个「着色器真编译」门禁（装置 A）一条断言都没有** —— 只验证能编译。
4. 曾**为了让它变绿而改断言**（装置 C 测的是一条**已被删除**的路径 ⇒ 正确处置是**退役**它）。
5. **日志缓存**：MCP 的 `get_console_logs(source:"cache")` 会一直返回旧错误。
6. **「日志说对了、画面没变」**：转换器打印「阴影走上游 ramp」，画面跑的却是另一个模块
   （它排在后面、末尾是**赋值**不是相乘 ⇒ 整段覆盖）。**藏了两轮，因为门禁只查属性值、不查画面。**
7. **断言「因为错误的理由通过」**：子串匹配到了另一条也含该词的消息。
8. **用属性值判断模块开关** ⇒ 开关真身是 keyword，数值恒为 0 ⇒ 曾两次据此得出错误结论。
9. **用「属性 ≠ 默认值」当「功能被使用」的判据** ⇒ lilToon 把渲染模式/描边**编进着色器名**，
   专用变体里那些属性的默认值本身是另一种语义 ⇒ 第一遍扫描得出完全错误的结论
   （`_UseOutline` 非默认计数 6，但实际 **78%** 的材质画描边）。
10. `.gitignore` 的 `!/x` 是否定（重新纳入）；`git rm -r --cached .` **不加 `-f` 会被静默拒绝**。
11. **测量被夹取污染**：`ARGB32` 会把 >1 夹到 1，而「背景消去法」（渲黑底+白底相减）
    建立在**线性、不夹取**的读回上；第一版还把背景也算成饱和像素 ⇒ 四个变体报同一个数。
    另：项目是 **Linear**，而 `Camera.backgroundColor` **按 sRGB 解码**写入 ⇒
    仿射性检验残差 0.0991 看似失败，实际可被精确解释。
12. **「像素相等」不等于「零开销」**：惰性 pass 仍有提交/顶点开销。
13. **发布后**门禁 `audit-release` 在当前状态下 exit 1 是**预期**的（本地未发布 ⇒ 上游 404），
    容易被误读成「失败」。

## 4. 已确立、不要重新讨论的结论

1. **上游 Shade 的梯度 ramp 是一维颜色查找** ⇒ 承载不了 `fwidth` 抗锯齿；且 ramp 烘焙器
   只建模三层色带、**没烘 `lnB × _ShadowBorderColor` 渐变项**。而 fork 的 `ShadowColor` 模块
   是 lilToon 阴影的**逐行忠实移植**（`_ShadowBorderColor` 实测 **44/49** 材质用到 /
   `_ShadowBorderRange` 22 / `_ShadowMainStrength` 13 / 逐像素遮罩 / `fwidth` / `min(indirect,albedo)`）
   ⇒ 转换器已切回 ShadowColor 路径（`_ShadeGradientIndex = -1`）。
2. **`ShadowColor` 第 2/3 层的复合权重用的是【材质颜色】的 alpha**
   （`lns.y = _Shadow2ndColor.a - lns.y * _Shadow2ndColor.a`），**不是贴图 alpha**；
   贴图 alpha 只参与**颜色**的 lerp。
3. **`NonToon` 的 `sd.L` 与 lilToon 的 `fd.L` 根本不是同一个东西**：

```hlsl
// lilToon:  fd.L = input.lightDirection;                  // 直接取主光方向
// NonToon:  sd.L = normalize(lightSum.direction
//                  + (SHAr+SHAg+SHAb)/3    // ① 环境光 SH 混入   ← 无属性可控
//                  + vertex.Head*bias     // ② 有属性（转换器设 0）
//                  + float3(0,1,0));      // ③ 固定 +Y 偏置     ← 无属性可控
```
量化：即使 ② 设 0，明暗输入与真实光方向仍差 **0°–60°**（上游默认 0°–98°）
⇒ 「lilToon 素材转过来观感不塌」存在**结构性上限**，不能声称逐像素等价。
⚠️ `sd.L` 在同一函数里还被 `NdotL`/`fakerim` 用来算**环境 rim** ⇒ 改 ① 会连带改环境项。
4. **「泛红」的真正因果**：MA 菜单 `NT_ShadowStrength` **默认值是 1** ⇒ VRChat 里
   `_ShadowStrength` 一律被驱动到 **1.0**，而 lilToon 作者设的是 **0.10** ⇒
   作者有意设的暖粉阴影色被放大 ⇒ 泛红。现有两处「颜色中性化」补丁是在压这个放大。
5. **真实素材实测频次**（49 个 lilToon 材质 / 全工程 135 个 / 2 个 avatar）：
   `_UseShadow` 40、`_UseRim` 29、`_UseRimShade` 28、**`_UseReflection` 24**、`_UseMatCap` 11、
   `_UseBumpMap` 10、`_UseMatCap2nd` 6、`_UseOutline` 6、`_UseBump2ndMap` 5、`_UseEmission` 3；
   **其余全部 0/47**。⚠️ 样本只有 2 个 avatar ⇒ 只能当「本工程优先级」，不能外推 VRChat 总体。
   ⚠️ **NonToon 全包 `grep "Reflect|Cubemap|_EnvRim|Reflection"` = 零命中**（无环境反射代码）。
   ⚠️ `_BackfaceForceShadow`（6/49）**无法转换**：NonToon 的 `sd` 里没有 `facing` 等价字段。

6. **【已决定 2026-09-18：保持现状】`sd.L` 与 lilToon 不同构 ⇒ 接受为限制，不加编译期 keyword 变体。**
   量化：即使 `_ShadeDirectionBias = 0`，明暗输入仍偏离真实光方向 **0°–60°**（上游默认 0°–98°）。
   ⇒ **不得声称「lilToon 素材转换后阴影分界位置一致」**。
   若将来要改：正解是**编译期 keyword**（默认分支预处理后就是原表达式 ⇒ 不破坏"默认零开销"），
   但**必须同时处理 SH 那一项**（在另一个函数里），而 `sd.L` 还被 `NdotL`/`fakerim` 用来算
   **环境 rim** ⇒ 得先定"环境项是否跟着变"。**这不是一行改动。**
7. **【已决定 2026-09-18：保持现状】菜单 `NT_ShadowStrength` 默认 1.0 **与** 阴影色中性化，两者都保留。**
   原因：lilToon 作者的 `_ShadowStrength = 0.10` 在数学上 ≈「几乎没有阴影」，与用户「不够立体」
   的诉求相反 ⇒ **1.0 是有意选择，不是 bug**。
   已接受的代价：`[NT-FIX 26]` / `[NT-FIX 22]` 把作者有意调的暖粉色阴影**洗成灰**。
   ⇒ **不得声称「转换产物保留了作者的阴影色彩」**。

## 5. 给审计方的工作纪律（**必须遵守**）

1. **读到再断言。** 不许凭记忆说行号/常量；引用时给 `文件:行号`。
2. **明确区分**：你确定 / 你在推断 / 你不确定。若凭记忆，标注置信度。
3. **模型给的「源码片段」必须回到真源码逐字核对** —— 已经发生过一次：某模型断言
   `ShadowColor` 第 2/3 层权重用贴图 alpha，**结论被源码推翻**；若照它改，本库
   `_Shadow2ndColorTex` 未被指定（黑色 a=0）⇒ 第 2 层阴影**整层消失**。
4. **不能失败的断言等于没有。** 给建议时写清：它能因**什么**而失败、它**不能**证明什么。
5. 读文件优先用工具，不要用 shell 拼字符串（Windows 上路径与转义极易出错）。

## 6. 仓库布局

| 路径 | 内容 |
|---|---|
| `NonToon/` | 着色器包（0.3.10）+ `SCOPE.md`（支持范围与已知限制） |
| `nontoon-converter/` | Unity 编辑器转换器工具包（0.5.2） |
| `tools/make-twopass.mjs` | 两趟透明变体的生成器（派生文件必须有可复现来源） |
| `_notes/` | 笔记、探针、门禁脚本（**不进发布包、不进版本控制**） |
| `_notes/probes/*.cs` | 8 台验证装置的探针本体 |
| `_notes/verify-all.sh` | 一次跑完 8 台装置；判据是**每台的退出码** |
