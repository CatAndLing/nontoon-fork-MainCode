# NDMF 构建期接入

本包以 `com.catandling.nontoon-converter` 注册 NDMF 插件。现有菜单和组件的编辑期行为不变；
转换器仍在编辑期生成新材质。本页描述新增的构建行为，不代表已完成 VRChat 实机验收。

## 构建行为与顺序

1. **Resolving，MA / AAO 之前**：读取 `NTLightAdjust.FixedAtBuild`，在构建材质副本上重放固定值。
   复用编辑期 `NTSelfLightWriter.SetNumber` 的 Int / Float 写入规则；共享材质在此 avatar 内仍共享。
   冲突的固定值、指向 avatar 外的目标引用会报构建错误，不按不确定顺序覆盖。
2. 同一 pass 重放 `NTAvatarLightCreator.Sync` 对应的 Light 字段和 Renderer 阴影标志。
   **① 没有材质写入**。只处理当前构建 avatar；不影响场景里其他 avatar。
   重放后移除构建副本中的这两种无运行时行为的 authoring 组件，保留 Light / MA 产物。
3. **Transforming，MA（含 late-transform-stages）之后，AAO 的 Optimizing 之前**：
   收集 Renderer 材质和 NDMF AnimatorServices 中动画引用的材质，按 `_RenderingMode` 对齐队列。
   0 → -1、1 → 2450、2 → 3000；复用菜单的 `IsModeDefaultQueue`，保留 2460 / 2900 / 3005 等自定义值。
   Fur 等无 `_RenderingMode` 属性的材质跳过。动画引用只通过 NDMF virtual clip API 更新。

这三项是已有 avatar 构建产物可能变化的范围：陈旧队列、与固定亮度/光源组件不一致的字段、
已消费的两个 authoring 组件。材质仅在需要写入时复制，不新增 shader keyword、运行时脚本、Animator 层或同步参数。

`NTLightAdjust.AdjustableInGame` 的现有生成动作只安装统一 MA 光影菜单，**不写** `initialValue` 到材质。
构建也不发明这项写入；缺统一菜单的 MergeAnimator 时明确报警，仍需先使用现有编辑期生成入口。
③ 的窗口设置没有序列化成构建组件，现有生成的 MA 组件由 MA 构建处理，不从 EditorPrefs 重建。

## 旧版残留

构建期读取 descriptor 引用的 FX、ExpressionParameters、ExpressionsMenu（递归含径向 subParameters）。
发现 `NonToon ` 层前缀或 `NT_Light` 时发出带稳定代码的警告：`LEGACY_FX` / `LEGACY_PARAMETERS` / `LEGACY_MENU`。
这些名称不足以证明资产内容归本工具所有，无法安全自动删除；也不调用会全工程写资产的旧清理命令。
请检查并迁移旧内容为 MA 声明。未被此 avatar 引用的孤立资产不在构建扫描范围内，也不会参与本次构建。

## 依赖

- 现有 `NTModularAvatarBridge` 仍用反射访问 MA；其文件头的“软依赖/无 MA 回退”是历史注释，
  当前 package.json 已把 MA 声明为必需依赖，实际编辑器生成入口没有破坏性回退。
- Editor asmdef 新增 **`nadena.dev.ndmf` 编译期引用**。未添加 versionDefines 或条件禁用插件的 defineConstraints。
  缺 NDMF 时程序集不能编译，不能在“没有构建保障”的状态下悄悄继续。
- package.json 新增直接 VPM 依赖 `nadena.dev.ndmf >=1.8.0 <2.0.0-a`，避免插件依赖仅隐藏在 MA 的传递依赖里。
  已查安装的 MA 1.18.7 自身要求 NDMF `>=1.14.7 <2.0.0-a`；验证使用 NDMF 1.14.8、AAO 1.9.19。
  更早版本的完整组合未实测。本包版本仍为 0.5.4。

## 验证边界

复用 `_notes/probes/NTAaoProbe.cs`，命令仅 `bash _notes/verify-all.sh F`。
真实构建检验陈旧队列的三个模式、自定义队列保留、固定亮度、光源、动画材质替换、重复构建、
源资产字节不变及旧 FX 警告；沿用原有 AAO 动画/参数/贴图断言。
停掉插件、漏换 Renderer/动画引用、写错队列、意外改源资产均可使新增断言失败。
这些字段测量不证明视觉像素等价、GPU 性能或 VRChat 上传/运行效果；未重跑其他装置。
