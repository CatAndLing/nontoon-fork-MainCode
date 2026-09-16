# NonToon (Fork) Converter

lilToon → NonToon 材质转换器。作为 **NonToon (Fork)** 的**辅助包**单独分发。

## 为什么单独一个包

- 主着色包 `com.catandling.nontoon` 保持**纯库**：只有着色器 + 少量编辑器辅助（描边平滑、模块注册、渲染模式），
  不掺工具代码、不掺 VRChat 相关逻辑
- 转换器需要知道 VRChat 的概念（复制 avatar 时剥离 Blueprint ID），这类逻辑只应该待在工具包里
- **本包不依赖 VRChat SDK**：`VRC.Core.PipelineManager` 通过反射访问，
  所以在**非 VRChat 工程**里也能正常编译和使用
- 本包**不引用**主包的程序集（只通过 `Shader.Find` / 材质属性字符串交互），
  因此两边可以各自演进；依赖方向是**转换器 → 着色器**

## 安装

通过 NonToon (Fork) 的 VPM 仓库安装：

```
https://catandling.github.io/VPM-nontoon-fork/vpm.json
```

它会自动带上 `com.catandling.nontoon >= 0.3.11`
（**0.1.11 起转换器已从着色包移出**，两边不会重复注册菜单）。

> 反向不成立：装着色包**不会**自动装本包 —— 转换器是可选的工具。

## 用法

**两种用法，任选一种**

1. **拖进去** —— 把模型（Hierarchy 里的，或 Project 里的 fbx / prefab）、材质、文件夹
   拖到窗口**最上面的方框**里，再点「转换拖入的对象」
2. **选中再转** —— 在 Project 或 Hierarchy 里选中，点「转换所选对象」

菜单：`Tools ▸ LilToon to NonToon Converter`
（也可以右键 Hierarchy 里的模型 → `Convert lilToon to NonToon`）

**会发生什么**

- 生成**新的** NonToon 材质，**原 lilToon 材质一个都不动**
- 输出到 `Assets/NonToonConverted/`；报告写到 `Assets/NonToonConverted/LilToonToNonToonReport.txt`，
  并在 `Assets/NonToonConversionLogs/` 留一份带环境信息的日志
- 选 Hierarchy 里的模型时默认会**复制一份** `模型名_NonToon` 并把原对象禁用（`Ctrl+Z` 可撤销）
- 阴影走**上游 Shade 梯度 Ramp**（= 原版 NonToon 行为，保真 lilToon 的 border/blur/strength/1st·2nd·3rd）；
  `ShadowColor` 模块只在 ramp 不可用时回退，**且默认被明确关闭**（`[NT-FIX 28]` + `[NT-FIX 30]`）

**转换前先看窗口里「检测到的 lilToon 材质」这个数字**：是 0 就说明拖/选的对象里没有 lilToon 材质
（只认「着色器名里含 lilToon」的材质）。

## 实时自阴影（⑥，**仅 PC / 仅 Poor**）

除了上面这条**烘焙**路径，工具包还带一个**实时**选项：
`Tools ▸ NonToon ▸ ⑥ 实时自阴影` 会往 avatar 的**单一容器** `_NonToonLight/` 下装一盏
**锚在骨骼上的 Spot Light**，并让 NonToon **天然接受 Unity 的阴影贴图**
（默认 `pcssQuality = 0`：**1 次硬件比较采样、0 额外 pass、0 额外 RT**），
再用 Modular Avatar **声明式**铺出一个子菜单（**不改你的 FX 控制器 / 参数 / 菜单**）：

```
Action Menu ▸ NonToon 光影 ▸ 光照上限 / 阴影强度 / 阴影开关
```

同步开销合计 **17 bit**（2 个 `Float` × 8 + 1 个 `Bool` × 1）。**阴影开关默认关** ——
关掉时整套 ⑥ 停（那盏灯也被关掉），观感**等于原版 NonToon**。想在游戏里看实时影要自己打开。
**⚠️ 编辑器里不播放时这些轮盘不会有任何反应**：NDMF 要等进入 Play 才把层合并进去，
所以请用 **进 Play** / `NDM Framework/Manual bake avatar` / 上传 VRChat 来测
（**但 ⑥ 组件本身带 `[ExecuteAlways]`**，所以在编辑器里直接勾 `pcssOn` 就能看到灯亮/灭）。

**用之前请先读 `SelfLight.md` 的 v6 一节**（菜单结构、参数预算、区间为什么是这些值都在 §6.7）。
最要紧的三条：

- **只能放在 `Poor`**（VRChat 限制：Excellent/Good/Medium 的 Lights 必须为 0），**Quest 不可用**
- 灯和参数都是**本地**的 ⇒ **影质量取决于观看者**，不是同步特性
- **锥外区域只能靠环境光**；场景没环境光时会渲染成纯黑（面板会给警告）

代价**约 3 × 材质槽**的 draw call（ForwardAdd + OutlineAdd + 我们自己的深度图 pass）。
默认路径仍然是烘焙（④），⑥ 只给"我就要真实时影"的场合。

## 环境光色温探测（避免"暖色地图里的自己是一盏冷光"）

`Tools ▸ NonToon ▸ ③ 亮度自适应与防煤` 窗口的第 ⑦ 节会读**当前场景**的环境光，
按 `ambientMode`（Flat / Trilight / Skybox）取出平均环境色，再**在着色器自己的色温曲线上反推**
出相关色温（往返精确到 0 K），并如实告诉你这个环境色**在不在黑体轨迹上**
（偏绿 / 偏品红的色相是任何色温都表达不了的）。

四条落点：**匹配环境色**（运行时跟随，推荐）/ **写入色温 N K**（烘焙）/ 屏蔽地图环境光 /
⑥ 的那盏实时灯色相跟随。详见 `SelfLight.md` §6.8。

## 许可

- 原始工具 **LilToonToNonToonConverter 1.1.4** —— MIT 许可，来源 https://vrc-levanilla.booth.pm/
- 相对上游的改动（阴影默认走上游 Shade ramp、Int 属性写入修正、拖放区、VRChat 反射化、中文化）
  由本 fork 维护，同样以 MIT 发布。详见 `LICENSE.md`。
