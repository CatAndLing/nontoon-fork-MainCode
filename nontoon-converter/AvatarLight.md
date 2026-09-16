# Avatar 光源插件（NTAvatarLight）

给 avatar 挂一盏**只照自己**的实时光。**着色器无关**：lilToon / Poiyomi / NonToon / 甚至 Standard 都能用，
它只经营一盏 Unity `Light`，**不碰任何材质属性**。

> 出处：思路参考 nHaruka 的 PCSS4VRC「真实影システム」（也是"给 avatar 挂一盏聚光灯"）。
> 那个插件的本体是 BOOTH 付费商品，GitHub 仓库里只有文档站与 FAQ，所以这里是我们自己的实现。

---

## 1. 怎么用

1. 选中 **avatar 的根节点**（Hierarchy 里那个带 Renderer 的根对象）。
2. 菜单 **`GameObject ▸ NonToon ▸ 创建 Avatar 光源插件`**
   （或 `Tools ▸ NonToon ▸ 创建 Avatar 光源插件`）。
3. 会在根节点下生成一个 `NTAvatarLight` 子节点，上面挂着 **Spot Light + 本组件**，并把层级里所有
   Renderer 的 **Receive Shadows / Cast Shadows 打开**。
4. 在组件面板上调颜色 / 光强 / 角度 / 范围 / 阴影浓度，点「同步到光源组件」生效。

重复点创建**不会**多出第二盏灯（会改为同步已有的那盏）。

## 2. 它默认做对了什么

| | 默认 | 为什么 |
|---|---|---|
| **Culling Mask** | 第 10 层 `PlayerLocal` | VRChat 里**只有本地玩家自己的 avatar** 在这一层（`Player(9)` 是其他玩家）。所以这盏光不会照世界、也不会照其他玩家。面板上有「只照自己（PlayerLocal）」一键按钮。 |
| **Receive Shadows / Cast Shadows** | 全部打开 | 有些 avatar 默认是关的，关了就不出影子（PCSS4VRC 的 FAQ 专门提过这一条）。 |
| **阴影类型** | `Soft` | 硬阴影在动画风 avatar 上很难看。 |
| **渲染模式** | `ForcePixel` | 强制逐像素，阴影质量最好。 |
| **着色器** | 完全不碰 | 所以 lilToon/Poiyomi 的 avatar 也能直接用；也不会和 NonToon 的材质属性打架。 |

## 3. 组件面板会主动警告的事

- Culling Mask 没选 `PlayerLocal` → 会去照世界/别人
- 照射范围 > 8m → 容易照到附近的人，且多盏光叠加会照白
- 没开阴影 / 光强 > 4
- **检测到同一 avatar 上还有 NonToon 材质开着「自有光源」（`_UseSelfLight = 1`）** → 会双份打光，
  请二选一（用本插件，或者用 `Self Light` 组件的「实时光源」模式统一管理）

## 4. 代价（必须知道）

1. **占 VRChat 性能等级的 Lights 计数** —— PC 上 `Lights = 1` 最高只能 **Poor**。
2. **影子能不能看到取决于观看者**：看的人 Shadow Quality 关了就看不到；Unity 的 Shadow Distance 之外也没有影子。
3. **多盏叠加**：每个玩家的这种光都会照"自己那一端的 PlayerLocal"，人挤人时可能把 avatar 照白
   （PCSS4VRC 的「白飛び」是同一个问题）。
4. **Quest 端实时光基本不可用**。

> 想同时有"影子永远可见 + Quest 可用"，就用 `Self Light` 组件的**烘焙阴影图**模式（着色器侧采样）。

## 5. 和 Self Light 组件的关系

| | **Avatar 光源插件**（本文件） | Self Light 组件 |
|---|---|---|
| 着色器 | **不限**（lilToon/Poiyomi 都行） | 只对 NonToon 材质有效 |
| 影子 | 真·实时（Unity 阴影） | 烘焙那一刻的姿态 / 或实时光源模式 |
| 是否碰材质 | **不碰** | 会把参数写进 NonToon 材质 |
| 适用 | 任意 avatar | 用 NonToon 材质的 avatar |

两个都开 = 双份打光，面板会警告。

## 6. 还没做的（要 VRCSDK，故意不代劳）

- **ExpressionMenu 控制**（开关 / 光强 / 颜色）：在 FX 层建 float 参数 → AnimationClip 写
  `Light.intensity` / `Light.color` → 挂到 ExpressionMenu。手写一次 5 分钟，代劳就得给本包加
  VRCSDK 依赖（现在整个包是**零 SDK 依赖**的，连 `PipelineManager` 都走反射）。
- **PhysBone 控制方向**：把 `NTAvatarLight` 节点挂到被 PhysBone 驱动的那根骨骼下即可，
  或用组件的「方向来源」字段指定那根骨骼。
