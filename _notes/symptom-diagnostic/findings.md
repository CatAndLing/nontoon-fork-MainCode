# 第二批：三个症状根因定位结果（本轮未定位）

## 本轮执行与阻塞

- 命令（仓库根目录，Git Bash）：`bash _notes/probes/run-symptom-diagnostic.sh`
- **已验证：Unity exit 1，驱动 exit 1**。`exit.txt`、`report.txt` 是本次新生成的文件；run-id `2002f330-c636-4935-a3b2-b94e57ca631c`。
- 关键输出：`FAIL clothes_bk Details keyword enabled`；随后 `FAIL System.NullReferenceException`，位置为 `NTSymptomDiagnostic.cs:91`。
- **这两项是本轮新探针的问题，不是产品根因**。探针第 64 行查了不存在的 `_JP_LILXYZW_NONTOON_DETAILS_ENABLE_`；真实产物为 `_JP_LILXYZW_NONTOON_DETAILS_ENABLE_1`。转换器 `LilToonToNonToonConverter.cs:1433` 明确拼接 `_1`。
- 第 91 行直接访问 `GetComponentsInChildren<Behaviour>(true)` 返回项的 `enabled`；该次运行在这里空引用。**推断**是副本 prefab 有缺失脚本，未逐组件验证。几何提取与渲染均未完成，**没有任何本轮像素统计可以使用**。
- 另外，尚未执行的 MatCap 消融第 76 行也用了错误 keyword；Rim 第 77 行用了产品没有的 enable keyword（真实属性见 `NonToon/Shaders/Modules/RimLight/properties.hlsl:1`）。这两臂不能作为未来有效实验直接使用。
- 原始 Unity 日志本地保留于 `unity.log`（约 906 KB）；相关错误摘录见 `unity-errors.txt`。日志还出现过 `CS2001 NTConverterProbe.cs could not be found`：本轮部署脚本重建包副本时漏复制现有 B 探针。Unity 后续仍进入本轮方法并写出上述失败报告，不能把初始编译消息冒充最终根因。现有 B 探针已从 `_notes/probes/NTConverterProbe.cs` 恢复到验证工程副本。
- **遵守失败即停：没有修改断言刷绿，没有第二次运行。当前新增探针作为失败实验归档，不是可用门禁。** 修复上述探针自身问题后才能执行下列后续实验。

## (a) 夹克噪点 / 针织异常：② 定位不到

已排除：

- **已验证（引用既有结论，未重跑）**：贴图烘焙路径；`_notes/对照与转换器状态-20260918.md:135`。历史更具体的依据：`_notes/会话断点-20260917.md:514` 起说明该材质反照率未烘焙。
- **已验证（本轮资产/字段）**：并非“未映射第二法线”。`report.txt` 记录新转产物的 `Detail1NormalMap=.../nuno.png`，对象身份与源 `_Bump2ndMap` 相等，第一、第二法线 scale 都为 1。
- **已验证（本轮序列化）**：并非“Details keyword 关闭”。新转产物 `.mat:15` 为 `_JP_LILXYZW_NONTOON_DETAILS_ENABLE_1`，见 `serialized-fields.txt`。失败探针对此的判定错误，不能采信。**未验证**遮罩像素是否为零、GPU 中实际参与程度。
- **已验证（本轮源字段）**：第一法线启用值为 1，所以转换器 `LilToonToNonToonConverter.cs:307` 未检查 `_UseBumpMap` 的问题，不能解释本素材“误启用第一法线”。

候选链：

- **已验证（源码事实）**：转换器 `LilToonToNonToonConverter.cs:309` 把第一法线放入 Details；`:502` 放入第二法线。`NonToon/Shaders/Modules/Details/phase_base.hlsl:7`、`:12` 只合成到 `sd.N_detail`。
- **已验证（源码事实）**：`NonToon/Shaders/birp.hlsl:112` 的 `sd.N` 另取核心 `_NormalMap`；`NonToon/Shaders/Modules/ShadowColor/phase_shade.hlsl:31` 使用 `sd.N`。MatCap 则在 `NonToon/Shaders/Modules/MatCaps/phase_reflection.hlsl:12` 取 `sd.N_detail`；Specular 在 `NonToon/Shaders/Modules/Specular/phase_light.hlsl:3` 使用它。
- **推断，未验证因果**：阴影分界与 MatCap/高光吃到不同法线，可能参与“针织消失 / 噪点”；这只是候选，不足以认定用户症状根因。scale 都为 1 也不能证明两套法线输入输出相同。

下一个实验：

- 修复并单独验证探针自身，再运行同一命令；用实际 `clothes_bk` 子网格固定相机/斜向白光，原版与当前转换产物各做第一法线关、第二法线关、全部法线关、MatCap 关、Specular 关矩阵。采用临时克隆材质，禁止改源资产。
- 对固定几何 ROI 读 ARGBFloat/RGBAFloat，统计相邻像素高频残差、成对 RMSE；必须先量化复现“噪点增加”，不能把总 RMSE 下降等同根因。检测细节遮罩 R/G 像素及法线解包向量；再隔离 `sd.N` 与 `sd.N_detail` 的使用差异。
- 失败条件：缺子网格、缺 keyword、GPU 输出无效、消融未改变对应通道、不能复现噪点增加，均必须报告失败/未复现。不能证明其他机位、光照或材质。

## (b) 头发灰紫：② 定位不到

已排除：

- **已验证（引用既有结论，未重跑）**：贴图烘焙路径，依据同上。
- **已验证（本轮字段复核）**：源 `Hair.mat:768`、`:773` 的二层与一层阴影色分别为等 RGB 的 `0.53773564`、`0.8509804`，见 `serialized-fields.txt`；与历史 `_notes/会话断点-20260917.md:501` 的“阴影色中性化对这两个颜色为空操作”一致。排除范围仅为这两个颜色被中性化，并不排除阴影强度/其他着色项。
- **已验证（本轮字段）**：`Hair.mat:672` 第一法线启用值为 1；不能归因为转换器误启用本来关闭的第一法线。

未验证：

- 本轮在夹克阶段中止，**没有运行 Hair 的转换及像素矩阵**。源 `Hair.mat:680` MatCap=1、`:685` Reflection=1 是使用状态，不是灰紫根因证据。
- 当前找到的是 `C:/VRC-File/kaguya/Assets/辉夜/kaguya` 素材；与历史 `Assets/IKUSIA/kaguya` 的 GUID/内容完全一致性、旧场景相机与光照均未证明。

下一个实验：

- 修复探针后，以真实 Hair 子网格、统一几何 ROI、相同光照与环境反射输入，分别消融 MatCap、Rim、ShadowColor、法线。保存浮点 RGB 均值及 `B-R`、`B-G` 的有符号变化，并量化当前基线确实产生偏紫，再判断哪个单独干预能恢复；全图 RMSE 不足以判断颜色方向。
- 用真实 `_ENABLE_1` keyword 验证 MatCap 开关；Rim 通过临时克隆材质的 `_RimLightColor` 置零隔离，不能调用不存在的开关。若消融未改变实际像素，该实验必须失败，不能据此排除模块。
- 若当前固定光照未复现灰紫，应恢复历史光照/场景做下一轮，不能声称已定位。

## (c) 透明容器内含物体：② 定位不到

已排除：

- **本轮没有排除任何该实报场景的渲染假设**。对象身份、内物 renderer 与复现场景尚未获得；没有运行两个 mesh 的像素实验。
- **已知线索（引用，不当作排除证据）**：转换器未接两趟见 `_notes/对照与转换器状态-20260918.md:133`；单素材验证限制见 `NonToon/SCOPE.md:45`；队列复制实现见 `LilToonToNonToonConverter.cs:1246`。队列字段被复制不等于跨 renderer 的遮挡正确。

下一个实验：

- 先确认用户实报是否为旧 `aburaage`，并定位袋体/内物两个 renderer。分别测“仅袋体 / 仅内物 / 二者共存”，使用相同相机与浮点黑白背景读回，记录 renderer 的实际队列、排序层/顺序、ZWrite、Cull、shader/pass 与 bounds。
- 对原版 lilToon、当前单趟 NonToon、**仅诊断临时材质**指定两趟变体并类型正确地设置 `_Pre*` 做矩阵；随后隔离队列顺序影响。不改转换器接线。先固定 blend 与 cutoffs，再做排序干预，避免把两种效应混在一起。
- 必须在内物独有的像素 ROI 比较共存时的遮挡/透射误差；同 mesh 正背面正确而内物仍消失就能独立失败。两趟与队列叠加是否足够仍为**未验证**。

## 工作区范围

- 没改 `NonToon/`、`nontoon-converter/` 源实现；用户原有四个未提交改动保留。没碰禁止修改的文档与发布目录，没执行全盘门禁，没提交。
- 本次只新增诊断代码、驱动、失败报告与本文；验证工程内使用素材/包副本。
- `serialized-fields.txt` 中源夹克 SHA256 与报告运行前副本 SHA256 相等；源 kaguya 工程未被本轮 Unity 打开。原始 Hair 只作文件读取。
