# 任务（第三批 · 把症状探针修好并取得像素证据）

> 上一批（`_notes/_task-tools-rectify-2.md`）**合格交付**：三个症状都给了"定位不到 + 已排除什么 + 下一步"。
> 用户侧已**独立复核通过**其中两条断言（你自己也识别出探针判定不可采信，这点做得对）：
> - 探针 keyword 写错是**探针自身 bug**：源码 `nontoon-converter/Editor/LilToonToNonToonConverter.cs:1431-1441`
>   用的是 `material.EnableKeyword(keyword + "_1")`（`keyword = property.ToUpperInvariant()`）⇒ 真实产物是
>   `_JP_LILXYZW_NONTOON_DETAILS_ENABLE_1`，你查的 `..._ENABLE_` 不存在。
> - 源 `Hair.mat:768` / `:773` 的两层阴影色**确是等 RGB 中性灰**，转换产物原值保留 ⇒
>   "偏紫 = 我们的阴影色中性化"这条排除**成立**。
>
> ⇒ 本批**不再重跑这些已确认的东西**，只做"修探针 + 拿像素证据"。

## 1. 你要做的（按顺序）

### 1.1 修探针自身的两处 bug（这是本批的前置，不修就还是无效测量）

1. **keyword 判定**：不要再自己拼 keyword。**从产物材质实际的 keyword 列表里读**
   （或至少用正确的 `<PROP>_1` / `<PROP>_0` 形式）。你上一批已承认"MatCap 第 76 行、Rim 第 77 行也用了错
   keyword / 产品没有的 enable keyword" —— 一并修掉：
   - MatCap 的 enable keyword 按 1.1.1 的方式从产物读；
   - Rim 在本产品里**没有** enable keyword，要用**临时克隆材质的 `_RimLightColor` 置零**做隔离，
     不能调用不存在的开关（这点你上一批自己写了，照做）。
2. **`NTSymptomDiagnostic.cs:91` 的空引用**：`GetComponentsInChildren<Behaviour>(true)` 返回项里
   可能有 **Missing Script**（`null`）⇒ 遍历前判 null / 过滤掉，别让整个探针死在几何提取之前。
3. **顺手修部署脚本**：上一批它重建包副本时漏拷 `NTConverterProbe.cs`，导致日志里出现
   `CS2001 NTConverterProbe.cs could not be found`。部署后**自检**：验证工程里 B 探针必须存在。

### 1.2 跑像素实验，把 (a) 夹克 与 (b) 头发 从"定位不到"推进

**按你自己上一批写的"下一个实验"做**，不要另发明：

- **(a) 夹克**：`clothes_bk` 真实子网格，固定相机 + 斜向白光；源产物 vs 当前转换产物，
  消融矩阵 = 第一法线关 / 第二法线关 / 全部法线关 / MatCap 关 / Specular 关。
  **临时克隆材质**改，禁止动源资产。固定几何 ROI 读 **浮点**回读，统计相邻像素高频残差与成对 RMSE；
  **必须先量化复现"噪点增加"**，不能把总 RMSE 下降当根因。
- **(b) 头发**：真实 Hair 子网格、统一 ROI、相同光照；消融 MatCap / Rim / ShadowColor / 法线，
  保存浮点 RGB 均值与 `B-R`、`B-G` 的**有符号变化**，**先确认基线确实偏紫**，再看哪个干预能恢复。
  （全图 RMSE 判断不了颜色方向——你自己写的，照做。）
- **(c) 透明容器内含物体**：**本批不做**（上一批连对象身份都没确认）。
  **例外**：若你能在工程里定位到旧 `aburaage` 的袋体/内物两个 renderer，就按你写的
  "仅袋体 / 仅内物 / 二者共存 + 浮点黑白底读回 + 记录队列/ZWrite/Cull/pass/bounds"做；
  否则如实写"缺对象身份，等用户确认"，**不要**用别的物体顶替。

### 1.3 判据（**必须能失败**）

每个消融臂都要有：**该臂必须真的改变了它声称改变的东西**（keyword 实态 / 属性值读回），
否则该臂**判为无效**并如实标出（这正是上一批的教训：探针自己的判定错了，整个臂就白跑）。

## 2. 硬约束（违反即返工）

1. ⛔ **不许改断言 / 判据让结果变绿**；探针自身出错就如实报失败，**不要**第二次运行掩盖。
2. ⛔ **只诊断**：不改 `NonToon/` 与 `nontoon-converter/` 的**实现**（探针与部署脚本除外）。
3. ⛔ 不碰 `_VPM-nontoon-fork/docs/`；**不要**动这几个文件（用户侧统一口径）：
   `nontoon-converter/SelfLight.md`、`NonToon/SCOPE.md`、`README.md`、`_VPM-nontoon-fork/README.md`、
   `_notes/目标契约.md`。
4. ⛔ **不要**改工作区里那 4 个未提交文件（上一批的自渲路径移除）：
   `Runtime/NTSelfRealtimeShadow.cs`、`Editor/NTSelfRealtimeSetup.cs`、
   `Editor/NTSelfRealtimeShadowEditor.cs`、`nontoon-converter/package.json`。
5. ⛔ **用户侧同时在改 `NonToon/Shaders/Modules/Shade/`**（T1 原生阴影增强）——**你不要碰那个目录**，
   也别跑 `verify-all.sh`（避免撞锁与抢 `_verify-proj`）。需要跑 Unity 就用你自己的诊断工程/流程。
6. `_notes/` 被 gitignore，新增/改动要用 `git add -f`；**不要提交**（结束给 `git status --short`）。
7. 省流：按需读文件、控制单次输出量（上一批 33 万字节尚可，但别再翻倍）。

## 3. 交付格式

每个症状给**二选一**：
- **① 拿到像素因果证据**：哪个模块/通道/属性、哪张 ROI 数值、消融前后怎么变、**能独立失败的最小复现**
  （命令 + 退出码 + 关键输出）
- **② 仍定位不到**：已排除哪些（每条附"怎么排除的"）+ 下一个该做的实验

并逐条标 **已验证 / 推断 / 未验证**。无效臂**必须**单独列出来说明为什么无效。
