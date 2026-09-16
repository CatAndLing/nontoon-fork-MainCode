# SelfLight —— 角色「自己的光源 + 自阴影」

给 avatar 一路**只照自己**的光，并让它给自己投影。

## 为什么不挂真实的 Unity Light（有官方出处）

| 事实 | 出处 |
| --- | --- |
| VRChat 性能等级里 **Lights 在 PC 上 Excellent / Good / Medium 都要求 0**，只有 Poor 允许 1 —— 挂一盏实时光直接掉档 | [Performance Ranks](https://creators.vrchat.com/avatars/avatar-performance-ranking-system/) |
| 真实光会**照亮周围世界与其他玩家**，做不到"只照自己"。VRChat 的层是固定的：`Player(9)` 是"除本地玩家以外的玩家"，`PlayerLocal(10)` 才是本地玩家 | [Unity Layers in VRChat](https://creators.vrchat.com/worlds/layers/) |
| Unity 的 light culling mask 对自定义层本来就不可靠，而且 Quest 端实时光基本不可用 | Unity 社区结论 |

所以本功能**不产生任何 Light**：全部在着色器里完成 ⇒ 不进 VRChat 的 Lights 计数、不外溢、Quest 也能用。

## 它是怎么工作的

1. **私有光**（`Shaders/Modules/SelfLight/phase_customlight.hlsl`）：
   在 `customlight` 阶段往 `lightSum` 里加一路由材质参数定义的光（方向 / 颜色 / 强度）。
   - `Only This Light` 打开时，**丢掉世界光与环境光**并把 Shade ramp 的方向也换成它 ——
     avatar 在任何世界里长得都一样。
   - 运行时开销：1 次 `dot(N, L)`。
2. **自阴影**：一张**光照空间的正交深度图**。像素里 `worldPos` 投到光源空间后与贴图比较，
   多 1 次贴图采样 + 几个点乘。
   - 烘焙由 **`NTSelfLight` 组件**在编辑器里完成（纯 CPU 光线投射，**不需要相机 / RenderTexture / GPU**）。
3. **写入材质**：所有参数（含阴影贴图的基与范围）都写进材质属性。
   组件**运行时没有任何行为** ⇒ 烘焙完可以把它删掉，运行时零额外负担。

## 怎么用

1. 在 avatar 根节点上 **Add Component → NonToon → Self Light**。
2. `Light Source` 指定你要的那盏光源（**只读它的方向/颜色**，不会真的把它挂上去）。
3. 需要的话打开 `Only This Light`（只由它照亮）。
4. 点 **「烘焙自阴影并写入材质」**。会：
   - 采集该层级下所有 `MeshRenderer` / `SkinnedMeshRenderer`（蒙皮的按**当前姿态** `BakeMesh`）；
   - 用 `HideAndDontSave` 的临时 `MeshCollider` 逐像素朝光源方向投射射线，记录最近命中距离；
   - 写出 `Assets/NonToonSelfLight/SelfShadow_*.png`（关闭 sRGB / 压缩 / mipmap，否则深度会被改坏）；
   - 把方向、基、范围、强度、偏移写进该层级里所有带 `_UseSelfLight` 的材质。
5. 想只更新参数（不重新烘焙）就点 **「只同步光源参数」**。

## ⚠️ 注意事项

1. **渲染跟随姿态** —— 阴影是**烘焙那一刻的姿态**。换了姿势、改了网格、改了光源方向，都要**重新烘焙**。
   （VRChat 里 avatar 的姿势千变万化，所以自阴影适合"变化不大"的部位：头发的投影、脸部的立体感等。）
2. **只写进材质** —— 材质是共享资源；如果同一材质被多个 avatar/对象引用，它们会共享同一份烘焙结果。
   需要各自独立就复制一份材质。
3. **本组件运行时无行为**，烘焙后删除即可；删了也能正常显示（数据在材质里）。
4. **分辨率与耗时**：256³（默认）足够，512 更细腻但射线数 ×4；烘焙是 CPU 的，几十万条射线量级，
   大 avatar 请耐心等几秒。
5. **阴影痤疮**（表面出现条纹噪点）→ 调大 `Shadow Bias`；**漏光**（本该有阴影处发亮）→ 调大分辨率或检查光源方向。
6. **贴图必须保持** 关闭 sRGB / 不压缩 / 无 mipmap —— 这是深度数据，不是颜色。烘焙器已自动设置，
   但不要手动改 Importer。
7. **需要 NonToon (Fork) ≥ 0.2.0**（`SelfLight` 模块是 0.2.0 新增的）。
8. **和世界光的关系**：不开 `Only This Light` 时，世界光/环境光**照旧**影响 avatar，这一路光只是**额外加上去**的；
   想要"完全由它决定"就打开那个开关（代价：avatar 不再随世界变化，夜里也依然亮）。
9. **它不是阴影投射器** —— 别的物体（世界、其他玩家）**不会**被这盏光投影。这是"只照自己"的另一面。
10. **性能**：运行时不增加光照计算量，只是每像素多 1 次贴图采样 + 1 个 dot（`Only This Light` 打开时反而更省：
    世界光被丢弃了）。

---

## v2（NonToon Fork 0.3.0 / 工具包 0.4.0）：类 PCSS 软阴影

参考 **nHaruka 的 PCSS4VRC「真实影システム」**（<https://github.com/nHaruka-git/PCSS4VRC>，
PCSS 算法本身参考 <https://github.com/TheMasonX/UnityPCSS>）的思路，但把"挂一盏实时光"换成
**着色器侧**的实现，所以在 VRChat 里的代价完全不同：

| | PCSS4VRC（真实影システム） | 本分支 SelfLight v2 |
|---|---|---|
| 手段 | 真·Spot Light（+ 自定义 lilToon/Poiyomi 着色器） | 私有光 + 烘焙深度图，**没有 Light 组件** |
| VRChat 性能等级 | Lights = 1 → PC 上最高只能到 **Poor** | Lights 保持 **0**，不拖累等级 |
| 影响他人/世界 | 会照亮周围（因此需要遮罩/开关去压） | 不外溢（只作用于本材质），但**所有人看到的观感一致** —— 数据都在材质里 |
| Quest | 官方说明**不支持** | 可用（PCSS **关闭**时：1 次采样、无循环；开启 PCSS 后是动态循环，Quest 上未实测） |
| 费用 | 付费资产 | 免费（本仓库） |
| 软阴影 | PCSS | PCSS（同样先 blocker search 再变半径 PCF） |

### 新增的材质属性（SelfLight 模块）

| 属性 | 说明 |
|---|---|
| `PCSS Soft Shadow` | 开 = 8 次 blocker search + 12 次变半径 PCF；关 = 1 次采样的硬阴影（最省） |
| `Softness` | 半影大小（柔度），只在 PCSS 开时有效 |
| `Shadow Density` | 阴影浓度：1 = 该黑就黑，0 = 完全没有自阴影 |
| `Shadow Clamp` | 把 PCSS 的软边**压成硬边**（动画风），0 = 保持软边 |
| `Shadow Distance` | 离相机超过该距离**自动关闭**自阴影（PCSS4VRC 默认也是 10m）；0 = 不限制 |
| `Receive Mask` + 通道 + 强度 | 逐像素控制"哪里接收阴影"（对应 PCSS4VRC 的 **ReceiveMask**），白色 = 正常接收 |
| `Shadow Texels` | 深度图分辨率，PCSS 用它换算一个纹素的大小（组件会自动写入） |

### 采样预算（我们自己定的上限，参考 PCSS4VRC 的建议值）

- 官方建议：`Test Samplers ≤ 12`、`Filter Samplers ≤ 24`。
- 本实现的**低档**与官方建议一致：blocker **8** + filter **12** = 20 次；中 / 高 / 极高分别是
  12+24 / 20+40 / 32+64。
- 这两个循环的**上界来自材质属性** `_SelfLightPCSSQuality`（一个 uniform 三元式），**不是编译期常量**
  ⇒ 它们是**动态循环**，不会被展开成固定长度。实测（D3D11）功能与画面正确；
  **Quest / GLES3 上未实测**（打开 PCSS 时），PCSS 关掉则是 1 次采样、无循环。
- 只有 `_UseSelfLight` 打开 **且** 像素落在光源包围盒内 **且** 在 `Shadow Distance` 之内才采样，
  否则一次都不采。PCSS 关掉时退化成 1 次采样。

### 还没做的（诚实说明）

- **CastMask**（遮住"不投影的部件"，如眼白周围的刘海）没做 —— 我们的深度图是在**烘焙时**由 CPU 光栅化生成的，
  要做到"按遮罩跳过遮挡物"就得在烘焙器里逐像素查被遮挡物的遮罩，成本与精度都不划算。
  替代做法：把不想投影的部件**排除出烘焙**（组件烘焙时只传需要的 Renderer），或直接调小 `Shadow Distance`。
- **实时性**：仍然是"烘焙那一刻的姿态"。VRChat 内做不到实时自阴影（avatar 不能带相机/RT，实时光又会掉性能等级）。
- `_ShadowAOShift` 之类 lilToon 专属的边界遮罩重映射没有对应项。

---

## v3（0.3.1 / 0.4.1）：性能不是约束之后放开的两件事

前提变了：**性能等级无所谓**（avatar 本来就是极高负载）。于是把之前主动放弃的两件事拿回来。

### 3.1 PCSS 画质档位

| 档位 | Blocker | PCF | 合计 |
|---|---|---|---|
| 低 | 8 | 12 | 20 |
| 中 | 12 | 24 | 36 |
| 高 | 20 | 40 | 60 |
| 极高 | 32 | 64 | 96 |

默认档是**低**（材质属性 `_SelfLightPCSSQuality = 0`）。

- 材质属性 `_SelfLightPCSSQuality`，组件面板上是「PCSS 画质档位」下拉。
- 烘焙分辨率上限提到 **2048**（4M 条射线，慢，但边缘更干净）。
- 实现细节：深度图采样改成**显式 LOD 0**（`SampleLevel(sampler, uv, 0)`）。
  原来用隐式导数的 `tex2D`，在动态循环里会被编译器抱怨
  `gradient instruction used in a loop with varying iteration` —— 深度图本来不需要 mip，
  改完之后警告消失，也不会因为"unroll 不动"而退化。

### 3.2 实时光源模式（真·实时自阴影）

`Self Light` 组件的 **自阴影来源** 有两个选项；实时光源就是 PCSS4VRC 走的那条路：

| | 烘焙阴影图（默认） | 实时光源 |
|---|---|---|
| 影子 | 烘焙那一刻的姿态 | **实时**，改姿势立刻变，不用烘焙 |
| Lights 计数 | 不占 | **占 1**（PC 上最高 Poor） |
| 别人看得到吗 | 永远可见 | 取决于**观看者**的 Shadow Quality；别人关了阴影就看不到 |
| Quest | 可用 | 基本不可用 |
| 叠加 | 无 | 多盏这种光叠加会把 avatar 照白（PCSS4VRC 的已知问题） |

「创建 / 同步实时光源」会做的事：

1. 建/更新一盏 **Spot Light**（软阴影；角度、范围、颜色、强度、阴影强度都取自组件字段）；
2. **Culling Mask 默认第 10 层 `PlayerLocal`**。VRChat 的层是固定的（`Player(9)` = 其他玩家、
   `PlayerLocal(10)` = 本地玩家），每端只有"自己的 avatar"在 `PlayerLocal` 上 →
   这盏光不会照世界、也不会照其他玩家；
3. 把层级里所有 Renderer 的 **Receive Shadows / Cast Shadows 打开**
   （PCSS4VRC 的 FAQ 专门提过：有些 avatar 默认是关的，关了就不出影子）；
4. 把材质的 `_UseSelfLight` 关掉 —— 否则"真光源 + 着色器私有光"会**双份打光**；
5. Culling Mask 没选 `PlayerLocal`、或照射范围 > 8m 时，面板上给**警告**。

**换模式时的自动收尾**：切到实时光源会禁用已存在的实时光源对象并关掉 `_UseSelfLight`；
切回烘焙会重新打开 `_UseSelfLight` 并写入烘焙参数。

> 其他玩家看到的是什么：**实时光源模式下**，光跟着"每端自己的本地玩家"，所以你在别人那边看到的是
> 世界光 + 他们自己的光，**你的这盏实时光照不到他们的画面**（这也是它与下面 v4 讲的自有光最大的区别）。
> 这是这个方案本身的边界（PCSS4VRC 同样如此）。

---

## v4（0.3.8 / 0.4.9）：PCSS 取样点 clamp（真 bug 修复）

### 4.1 修了什么

`v2`~`v3` 的 PCSS 取样点直接对 `UV + 螺旋偏移` 采样，**没有 clamp**。而包围盒内判定只保证
`UV ∈ (0,1)`，偏移最大可到半径（256² 下 24 纹素 = 9.4%，高档 48 纹素 = 18.75%）——
一旦越过边界就会**读到深度图对面的纹素**。

用真 GPU 装置实测（`_notes/probes/NTPcssProbe.cs`，13 项断言）：

| 测什么 | 结果 |
|---|---|
| 深度图的采样 filter / address | 点采样 + **Repeat 一类**；**与贴图导入设置无关**（同一张图按 Clamp / Repeat 导入，结果完全一致）⇒ 烘焙器里写的 `wrapMode = Clamp` 对采样无效 |
| 对面是背景（远） | 贴右边界内侧的遮挡量 1.000 → **0.642**（漏光 36%） |
| 对面是几何（近） | 贴左边界凭空多出一条**假阴影线**：亮度 0.93 → **0.000**，约 7 纹素宽 |
| 判别实验 | 把"对面"也铺成遮挡物后贴边遮挡 **0.642 → 1.000** ⇒ 确认是越界读到了对面 |

**修法**：blocker 与 filter 两处采样前 `clamp(uv, 0, 1)`。零成本（每 tap 两个 min/max），
把行为从"由 Unity 绑定 / 平台 / 用户换图决定"变成"由代码决定"——顺带消除 Quest / Vulkan 上的同类风险。
硬阴影通路不受影响（只采 1 个点，UV 本来就在盒内）。

### 4.2 已知问题（写在这里免得当成 bug）

1. ~~**半影宽度随烘焙分辨率变化**~~ —— **已在着色器 0.3.9 修好**。
   修前：半径以"纹素"为单位、而包围盒与分辨率无关 ⇒ 世界半径 ∝ 1/分辨率，
   实测同一场景 256² → 512² 世界尺度只剩 71%、1024² 只剩 57%。
   修法：判别实验证明漂移由**半径**驱动、且实际半影宽度对纹素半径是**超线性**的（≈`R^1.55`），
   所以按实测标定的 `g(s)=s^0.8`（`s = Shadow Texels / 256`）换算半径与上下限，并同步按 `s²` 补
   采样数（封顶 64+32）。**256² 时 `g = 1`，与旧版逐像素一致** ⇒ 存量 avatar 零变化；
   512²/1024² 的世界宽度已与 256² 一致（实测 86%）。
   ⇒ **不需要**为了软影去调烘焙分辨率。
2. **`Softness` 是无量纲的 0–1 数**（**仍未修**）：实际半径 = `clamp(半影比 × Softness × 0.5, 1.5, 24/48)` 纹素。
   默认 0.5 时，半影比 < 6 就落在**下限 1.5 纹素**上。实测头尺度（接收面直径 0.3 m、遮挡物在前 5 cm）
   半影比约 4.4 ⇒ 默认档只比硬阴影柔化约 2.5 mm —— **真 avatar 上观感几乎看不出软影，真正的瓶颈是这一条**
   （而不是分辨率）。想更软**请把「柔度」拉大**；调高分辨率**不会**更软。
3. **`Shadow Bias` 是归一化深度上的常数**（默认 0.02 = 包围盒深度的 2%）⇒ 它的**世界含义随包围盒深度变**：
   包围盒 2.1 m 时是 4.2 cm，头部尺度（0.42 m）时是 8 mm。同一个数值在不同烘焙之间表现不同。
   （`Shadow Texels` 是「纹素 ↔ UV」的换算基准，**必须等于 Shadow Map 的分辨率**，别手改。）

### 4.3 与常用改模工具的联动（注意事项）

- **PhysBones / 动骨 / 固定在世界的道具**：自阴影是**烘焙那一刻**的深度图，纵深方向（光源轴）是固定的。
  头发/欧派等被 PhysBone 驱动、或部件被 `MA WorldFixedObject` 固定到世界之后，影子不会跟着动
  ⇒ 这类部件建议**不要参与烘焙**，或者只烘焙相对静止的部分（脸、躯干）。
- **Modular Avatar**：`MA Scale Adjuster` / `MA Merge Armature` / `MA MoveTo` 会在**构建时**改变 transform。
  如果你的 avatar 用它们调整过比例/位置，**请在建好之后再烘焙**（否则材质里的世界空间基与最终姿态不符）。
  实时光源模式同样受比例影响（聚光灯角度与范围是按米写的）。
- **VRCFury / AAO（Avatar Optimizer）**：AAO 的材质合并/UV 打包只对注册过 `ShaderInformation` 的着色器生效，
  本着色器**没有注册** ⇒ 不会被合并（安全，但也享受不到那份优化）。AAO 也不会动我们的深度图
  （它不是 `_MainTex` 那类常规贴图，且没被合并的材质不会参与贴图图集）。
- **Quest**：见 §v2 表格与「采样预算」两处说明 —— 关掉 PCSS 是安全的，打开则未实测。




---

## v5（工具包 0.5.0）：④「自带光照与阴影」——不需要地图光源的一键形态

### 5.1 为什么要它

② 是**手动模式**：光源必须来自场景里的一盏 `Light`（`Light Source` 字段），而且它其实**只读那盏灯的方向**
（颜色/强度一直取自组件字段）。也就是说，为了烘焙一张自阴影贴图，用户得先在场景里摆一盏灯 ——
这是纯粹的门槛，不是需求。

④ 把这件事变成一个组件 + 一个按钮：**光的方向写在组件上**（环绕角 + 俯仰角），
顺带把「只由它照亮」「亮度下限（防煤）」一起配好，一键写完材质并烘焙。

### 5.2 它是怎么做的（不新增第二套实现）

* 烘焙器新增一个入口：`Bake(Vector3 forward, Vector3 right, Vector3 up, string nameForFile, …)`，
  原来的 `Bake(Light, …)` 变成它的三行包装（只取 `light.transform` 的三轴）。
* 材质属性写入抽成**唯一一份** `NTSelfLightWriter`（② 与 ④ 共用），避免两套实现漂移；
  Int 属性仍走 `SetInteger`（`SetFloat` 写 Int 是静默无效的）。
* 正交基由 `OrthonormalBasis(forward)` 生成（`forward` 与参考轴夹角过大时自动换参考轴）。

### 5.3 实测：虚拟光 ≡ 真 Light

装置 `_notes/probes/NTSelfLitShadowProbe.cs`（35 项断言，exit 0）：

| 断言 | 结果 |
|---|---|
| 角度→方向换算 | `yaw=25°, pitch=40°` → `(0.324, 0.643, 0.694)`，与解析值点积 1.00000 |
| **虚拟光烘焙 vs 真 Light 同方向烘焙** | **逐像素一致**：16384 个像素最大差 **0.00001**，无一超过 1/255 |
| 两次烘焙的光轴 | 点积 1.00000 |
| 写入材质 | `_UseSelfLight`/`_SelfLightOnly`/`_LightMinLimit 0.35`/`_LightMaxLimit 0.9`/方向/PCSS 开关与档位/`Shadow Texels` 全部正确（Int 走 SetInteger） |
| 烘焙基 | halfX 0.157 / far 0.359（头尺度 0.3 m 符合预期） |
| 关掉自阴影 | 强度写 0、材质上的深度图引用被清掉（**探针抓到的 bug**：以前会照样烘焙并留下引用） |
| 与 ② 冲突 | 同时挂 ② 和 ④ 时给出警告 |

### 5.4 与常用改模工具

* 被 PhysBone 驱动、或被 `MA WorldFixedObject` 固定到世界的部件不要参与烘焙（影子是烘焙那一刻的纵深）
* 用 `MA Scale Adjuster` / `Merge Armature` / `MoveTo` 调过比例位置的 avatar：**先调完再烘焙**
* 混用 lilToon / Poiyomi 时本组件只对 NonToon 部位生效，面板会列出被跳过的材质
* 与 ② 写同一批属性 ⇒ 同一个目标上不要同时挂（面板会警告）

---

## v6（待发布）：⑥ 实时自阴影（PCSS，**仅 PC**）

④ 把阴影**烘焙进贴图**：零运行时成本，但光的**方向、形状、软硬在烘焙那一刻就冻结了**，
而且改一次要重烘一次。⑥ 是另一条路：**真挂一盏 Spot Light，自己渲一张深度图，逐像素做 PCSS**。
光是活的 —— 转动身体、灯随骨骼走，影子实时跟着变。

> **⑥ 不是 ④ 的替代品，是另一种取舍。**④ 仍是**默认**路径；⑥ 只给"我就要真实时影"的人。

### 6.1 为什么不用 Unity 自己的实时阴影（要自己渲深度图）

PCSS 的 blocker 搜索需要读**遮挡物的真实距离**。D3D11 上 Unity 只把 shadow map 暴露成
**比较采样器**（`SampleCmp`），拿不到距离值 —— 这是硬限制，不是配置问题。
所以 ⑥ 走**架构 A**：

1. 组件用 `CommandBuffer` + `NTRTDepth.shader` 把遮挡物渲进**自己的 RFloat 线性深度图**
   （存 `(距离-近)/(远-近)`，背景清 1.0）；
2. 归约**不用深度缓冲**：`Blend One One` + `BlendOp Min` 直接取最近面 ——
   这样与平台的 **reversed-Z** 方向彻底解耦（踩过：`ZTest LEqual` 在 reversed-Z 上会反过来，
   把球整个拒绝掉）；
3. 用 `Shader.SetGlobalTexture` 绑给着色器（ShaderCore 会把模块 include 展开在
   `__SC_BIRP_properties__` **之前**，所以内核看不见 `SC_*` 属性，也不能用 `Material.SetTexture` 绑）；
4. 在 `__SC_PHASE_customlight__` 里乘进 `lightSum.color`（**不碰 `env`** ⇒ 环境光不会被误压暗）。

### 6.2 怎么用

* 菜单 `Tools ▸ NonToon ▸ ⑥ 实时自阴影 ▸ 安装到所选 avatar`
  （另有"包含其他玩家"版本，见下）；或选中 avatar 后点组件面板里的安装按钮
* 装出来是这样（**全部是非破坏式声明**，可一键卸载）：

```
<Avatar>
  └── NonToon_RealtimeShadow          ← NTSelfRealtimeShadow + PositionConstraint(Neck)
        │                                + AimConstraint(Chest)   ← 灯锚在骨骼上
        ├── Spot Light                ← Spot / ForcePixel / 组件会把 Unity 阴影关掉
        ├── Menu_ShadowFloor          ← MA MenuItem + MenuInstaller
        └── Menu_OnOff                ← MA MenuItem + MenuInstaller
```

* **必须靠约束锚在骨骼上**，不能裸挂在 avatar 根下：灯和相机同侧（或在背后）时画面里
  **根本没有可见自遮挡**，看起来就像"功能没生效"。这是我们踩过的坑，PCSS4VRC 的
  `AutoLighting`/`AimSphere` 也是为了同一件事。
* MA 是**声明式**的：只往 avatar 根挂 `ModularAvatarMergeAnimator` /
  `ModularAvatarParameters` / `ModularAvatarMenuItem` / `ModularAvatarMenuInstaller`，
  **不动你的 FX 控制器 / ExpressionParameters / ExpressionsMenu**，NDMF 构建时合并。
  没装 MA 也能用（灯和组件照常工作，只是没有菜单控制项）。
* 菜单里会出现三项（根菜单放不下时 MA 会自动分到 `More` 子菜单）：
  **实时阴影 软硬**（径向）/ **实时阴影 下限**（径向）/ **实时阴影 开关**（Toggle）。

### 6.3 ⚠️ 硬约束（先读这一节）

| 约束 | 说明 |
|---|---|
| **只能放在 Poor** | VRChat 限制：`Excellent`/`Good`/`Medium` 的 Lights 必须为 **0**，**只有 `Poor` 允许 1 盏** |
| **Quest 不可用** | ⑥ 只支持 PC（Quest 上连光都不允许） |
| **光影质量取决于观看者** | 灯是**本地**的、参数是 `localOnly` 的 ⇒ **别人看到的影子和你的不一样**，不是同步特性 |
| **Unity 阴影必须是 Hard** | 我们的深度图是 PCSS 的输入，Unity 的 Soft/PCF 会**先把深度糊掉**，blocker 搜索随之失效。组件会强制 `shadows = None` |
| **锥外区域只能靠环境光 / 世界光** | 这是**照明设置**，不是着色器 bug。场景里没有环境光时，锥外会渲染成**纯黑** —— 我们自己踩过（把场景环境光设成 `Flat + 黑`，用户看到"无光的地方全黑"）。组件面板会在环境光接近黑时给红灯警告 |
| **阴影下限（Shadow Floor）** | 默认 `0.25`。全遮蔽处保留一点灯色，观感像"阴影里的环境反射"。设成 0 会真的到纯黑 |
| **别同时挂两份组件** | 两个 `NTSelfRealtimeShadow` 会每帧抢写同一批属性、并渲**两遍**深度图 |

### 6.4 代价（诚实版）

NonToon 的 `OutlineAdd` pass **也带** `fwdadd_fullshadows` ⇒ 附加光下**每个材质跑 2 次**。
以 16 材质槽的 avatar 为例（**推算，不是实测**）：

| 项 | draw call |
|---|---|
| ForwardAdd | +16 |
| OutlineAdd | +16 |
| ⑥ 自己的深度图 pass（每个参与投射的 Renderer 一次） | +16 |
| 该灯的 Unity ShadowCaster | **±0**（组件把 `light.shadows` 关成 `None`，我们不付这笔） |
| 合计 | **≈ 3 × 材质槽** |

**杠杆优先级**（按性价比）：

1. **减材质槽** —— 这是最大的一项（AAA 的 ShaderInformation / 材质合并就是干这个的）
2. 实时模式下**关描边** —— 直接砍掉 1/3
3. 用 `CullingMatrixOverride` / `BoundingSphereOverride` 收窄照射体积
4. 降 PCSS 档位（面板上的 `Realtime Quality 0-3`）

### 6.5 还没做的（诚实说明）

* `Light Size` 现在的量纲是"512 纹素基准下的纹素半径"，等价于世界半径但不好理解；
  计划改成**世界空间灯半径（米）**：`r_filter_uv = w_penumbra_world / (2·d_r·tan(spotAngle/2))`
* 常数 bias 计划换成 **normal offset**（按纹素世界尺寸缩放）+ 少量常数
* PhysBone 抓灯、更多菜单项、`CullingMatrix/BoundingSphere` 的封装
* AAO 的 `ShaderInformation` 注册（让 AAO 知道 ⑥ 的额外 pass）

### 6.6 与常用改模工具

* ⑥ 的灯会被 PhysBone 拖走（如果 P 骨覆盖到灯架）——用约束锚定可以缓解；
  实机还见过**编辑期动画（GestureManager）把灯拖走**，这也是必须锚定的原因之一
* 卸载走组件面板 / 菜单，会把灯架、约束、MA 四组件一起摘掉（只删我们自己挂的那些）

### 6.7 ★ 游戏内菜单：**一个子菜单 + 4 个独立控制项**（已定稿）

`Tools ▸ NonToon ▸ ⑥ 实时自阴影（PCSS）` 安装时，`NTLightMenuSetup` 会铺出这套菜单：

```
<Avatar>                     [ModularAvatarParameters] [MergeAnimator]
  └── NT_Menu_NonToon        [MenuItem(type=SubMenu, MenuSource=Children) + MenuInstaller]
        ├── NT_Menu_MinLimit        径向  NT_MinLimit        → 材质 _LightMinLimit
        ├── NT_Menu_MaxLimit        径向  NT_MaxLimit        → 材质 _LightMaxLimit
        ├── NT_Menu_ShadowStrength  径向  NT_ShadowStrength  → ⑥ 组件 shadowFloor（**反向**）
        └── NT_Menu_ShadowOn        开关  NT_ShadowOn        → ⑥ 组件 pcssOn（**默认关**）
```

在游戏里的路径是 **Action Menu ▸ NonToon 光影 ▸ …**。

**为什么是"子菜单 + 独立项"而不是"一个观感轮盘"**：早期版本用**一个** 0–7 档的轮盘同时驱动
亮度下限、上限、灯强度、影子下限（§6.7 旧版）。这样只占 8 bit，但用户无法单独调"只要影子更重"或
"只要上限低一点" —— 实测反馈原话是**「我需要单独可以调整阴影强度和阴影开关，和光照的上限和下限，
而不是一个不明不白的轮盘。应该是创建一个子菜单然后将开关轮盘放入」**。于是改成上图。

**同步预算 = 25 bit / 256**（用户指标："30 以内"）：

| 控制项 | 参数 | 类型 | bit |
|---|---|---|---|
| 光照下限（防煤） | `NT_MinLimit` | Float | 8 |
| 光照上限（压过曝） | `NT_MaxLimit` | Float | 8 |
| 阴影强度 | `NT_ShadowStrength` | Float | 8 |
| 阴影开关 | `NT_ShadowOn` | **Bool** | **1** |
| **合计** | | | **25** ✅ |

> VRChat 位宽：`Bool` = 1 bit、`Int` / `Float` = 8 bit。**三条连续量最少 24 bit** ——
> 所以"独立可调"和"≤20 bit"在数学上不可兼得（迭代史：1 轮盘被否 → 5 项 33 bit →
> 要 ≤20 → 上限降级成 Bool 得 18 → 放宽到 30 → 恢复连续滑块得 25）。

**区间**（都是实机调出来的，别随便改）：

| 项 | 范围 | 默认 | 为什么 |
|---|---|---|---|
| 光照下限 | 0 – 0.6 | 0.25 | 有效区间；`_LightMinLimit` 本身是 0–1 |
| 光照上限 | **0.2** – 1.0 | 1.0 | `clamp` 只在上限**低于实际渲染亮度**时才生效。深色套装渲染亮度普遍 < 0.4，0.4 起步等于白拉 |
| 阴影强度 | 0 – **0.95** | 0.25 | 弱端给到 0.95 = "几乎无影"；旧的 0.6 只淡 40%，实测"拉了不变" |
| 阴影开关 | — | **关** | ⑥ 是重功能（多一层深度图 + 一盏 Spot），装完不该默默生效 |

**⚠️ 自己实现时最容易踩的两个坑**：

1. **径向控件的参数要放 `subParameters[0]`，`parameter` 留空**
   （VRCSDK 的 `ExpressionsControlOptions.cs`、MA 的 `MAUtil.AddRadialMenu`、以及第三方
   NyaPPu LightController 都是这么写的）。写成 `parameter` 会让 GestureManager 抛
   `IndexOutOfRangeException`，游戏里轮盘完全没反应。
2. **子项不要各挂一个 `MenuInstaller`** —— 子菜单靠父 `MenuItem` 的 `MenuSource = Children`
   自动收进去；子项再挂 installer 会**重复装到根菜单**。MA 的必要条件是
   `Control.type == SubMenu` **且** `MenuSource == Children` **且** 子项在该物体的**直接子级**。

**⚠️⚠️ 编辑器里不播放时，轮盘"完全不起作用"是必然的**：
`Application.isPlaying = False` 时 NDMF 还没烘焙，当前 FX 层仍是用户自己的那份，
我们的 4 条层**要等进入 Play 的那一刻**才合并进去。测法三选一：
**进 Play** / Avatar 右键 `NDM Framework/Manual bake avatar` / 上传 VRChat 实测。

**实现归口**：`NTLightMenuSetup.Install(avatarRoot, addMenu)` 是**唯一**的菜单拥有者
（无对话框、可被脚本调用）。⑤「亮度调整」窗口里的"游戏内可调"和 ⑥ 的安装器都**转发**给它，
避免两条 FX 层同时动画 `_LightMinLimit`。清旧残留走
`Tools ▸ NonToon ▸ ⑤ 清理旧的非 MA 残留`（连所有 `VRCExpressionParameters` 资源里的同名参数一并清）。

> ⚠️ **⑤ 的亮度必须同步**（`saved = true`）：`_LightMinLimit` / `_LightMaxLimit` 是**材质属性** ——
> 只在自己这边抬亮的话，别人眼里的你还是暗的（反过来你压低上限，别人看到的是过曝的）。

### 6.8 环境光色温探测：别让"暖色地图里的自己是一盏冷光"

用户需求原话：**「探测环境光色温，避免在一个暖色地图而自身光是冷光」**。

入口：`Tools ▸ NonToon ▸ ③ 亮度自适应与防煤` 窗口的**第 ⑦ 节**（点「探测当前场景环境光」）。

**两个空间约定**（搞混就全错）：

* `RenderSettings.ambient*` 是**线性**空间。
* 着色器的 `NT_SELFLIGHT_KELVIN`（Tanner Helland 表）算出来的 RGB 是**当作线性光色直接相乘**的。
  ⇒ 反推色温必须在**同一个空间**里比色度（亮度不参与）。给人看的色板才转 sRGB。

**怎么取环境色**（`Runtime/NTAmbient.cs`，`Read()`）：

| `ambientMode` | 取法 |
|---|---|
| `Flat` | `RenderSettings.ambientLight` |
| `Trilight` | 天 / 地平 / 地 **0.5 / 0.3 / 0.2** 加权（角色朝上的面收得最多，是"看起来什么颜色"的主导项） |
| `Skybox` / `Custom` | `RenderSettings.ambientProbe` 的 **6 轴平均**（`SphericalHarmonicsL2.Evaluate`，数组**缓存**，避免运行时每帧 GC） |

**怎么反推色温**：**不**用 McCamy 之类的近似公式，而是**在我们自己的正向曲线上搜索**
（粗扫 10K + 1K 细化）—— 这样"选出来的 K 在着色器里渲出来"才真的最接近环境色，而不是最接近
某个教科书画布。实测：

* **往返精确**：2000 / 2700 / 3200 / 4000 / 5000 / 6500 / 8000 / 10000 / 15000 K
  全部 **Δ = 0 K、色度残差 0.00000**
* 暖色地图 `(1, 0.62, 0.32)` → **2616 K**，判定"落在黑体轨迹上" ⇒ 写色温就够
* 中性灰 → **6600 K**；准黑 → `valid = false`（黑没有色温）

**还给出"色温表达不了"的诚实判定**：环境色可能**不在黑体轨迹上**（偏绿/偏品红），
这时任何色温都还原不出来。返回的 `tint` 是残差的绿-品红分量：

* 偏绿 `(0.25, 0.55, 0.20)` → 残差 **0.265**、`tint +0.32` ⇒ 提示"常见于树林/植被，建议用
  **匹配环境色**而不是色温"
* 偏品红 → `tint −0.278` ⇒ "霓虹/紫调夜景"

**四条落点**（窗口里的按钮）：

| 落点 | 写什么 | 性质 |
|---|---|---|
| ④ **匹配环境色**（推荐） | 材质 `_SelfLightMatchAmbient = 强度` | **运行时跟随**：着色器把自有光颜色朝世界环境色 lerp，换地图自动适应，**还能表达偏绿** |
| ④ **写入色温 N K** | 材质 `_SelfLightUseTemperature = 1` + `_SelfLightTemperature = N` | **烘焙**：简单、零运行时开销，但换地图不会变 |
| ④ 屏蔽地图环境光 | 材质 `_SelfLightBlockAmbient` | 把环境光从结果里减掉 ⇒ 阴影更黑、对比更强（**⑥ 不读它**） |
| ⑥ 实时灯跟随 | 组件 `lightColor` + `followAmbient` | 那盏 Spot 的**色相**跟随，`RenderDepth()` 每帧重算 |

⚠️ **两条 ④ 路是互斥的**：`_SelfLightMatchAmbient = 1` 时着色器直接把 `outColor` 换成环境色，
色温完全不起作用。所以写入时会**显式把另一条清零**，并在结果里报告
**有多少材质的自有光没开**（`_UseSelfLight = 0` 时写色温/匹配都毫无效果 —— 不能静默）。

⚠️ **只取色相、不动亮度**：`HueOnly()` 把最亮通道归一化到 1，所以灯的 `intensity`
不会被环境光亮度拖动（实测：暖环境 `(1, 0.62, 0.32)`、灯强度仍是 `lightIntensity`）。
环境光太暗（线性亮度 < 1e-4）时 `valid = false`，**不拿噪声当色相**。

⚠️ **Built-in 管线没有 `Light.useColorTemperature`**（那是 URP / HDRP 的东西）
⇒ ⑥ 是**直接写 `light.color`**。

窗口里还有个 **「模拟暖色地图」** 按钮（环境光 + 主平行光一起调暖），用来肉眼确认
"暖色地图里自己还是不是冷光"。它和 ① 的「模拟全黑地图」共用一套保存/还原状态
（`dimmedLights` 已扩成 `(Light, intensity, Color)` 三元组，**连灯颜色一起还原**）。

⚠️ **只探测"当前打开的场景"**。VRChat 里地图光照是别人定的 ⇒ 所以推荐
**「匹配环境色」**（运行时跟随）而不是"写死色温"。
