// 从 NonToon.scshader 生成 NonToonTwoPass.scshader（两趟透明变体）。
//
// 为什么用生成器而不是手抄一份：
//   · 变体与基底只差「一个 ForwardBack pass + 4 个 _Pre* 属性」，手抄 440 行必然漂移；
//   · 基底（NonToon.scshader）日后升级时，重跑本脚本即可同步，差异永远只有那几处；
//   · 「两趟透明」是 fork 为自研能力（上游 13 条 issue 里没有任何一条涉及 TwoPass），
//     用生成器能把"我们到底改了什么"固定成可审计的一小段。
//
// 用法：node tools/make-twopass.mjs
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const SRC = join(root, 'NonToon', 'Shaders', 'NonToon.scshader');
const DST = join(root, 'NonToon', 'Shaders', 'NonToonTwoPass.scshader');

// ── 插入的 4 个属性：与 lilToon 的 `_Pre*` 一一对应，便于转换器 1:1 拷贝 ──
//    默认值取「无操作」：ZWrite 0 + Blend Zero One ⇒ 即使这个 pass 被画到，也等于没画。
//    （Cull 默认 2 = Back，与 lilToon 的 _PreCull 默认不同，但两趟开启时转换器会覆盖它。）
//
// ⛔ `_PreSrcBlend` 的换算规则 —— **与 `_SrcBlend` 完全一致，不能 1:1 照抄**：
//    lilToon 透明模式**一律在片元里预乘**（`lil_common_frag.hlsl:552-560`
//    `#define LIL_PREMULTIPLY fd.col.rgb *= fd.col.a;`），所以它的 `_PreSrcBlend = One(1)`；
//    而 NonToon **全包零预乘**（`rgb *= …a` 零匹配）。
//    ⇒ 本变体的片元输出的是**直出颜色**，`Blend One` 会把颜色原样叠上去（源色错、理论上还可能在 8bit RT 上饱和）。
//    ⇒ 转换器必须把 `_PreSrcBlend` 里的 `One` 换算成 `SrcAlpha(5)` —— 这正是它对 `_SrcBlend` 已经在做的事
//      （`LilToonToNonToonConverter.cs:1138`：`mode == 2 ? 5f : 1f`）。
//    实测（`aburaage_open`，同几何）：One vs SrcAlpha 的 T 为 0.4801 / 0.4792、
//    与 lilToon 白底图平均|Δ| 为 33.34 / 33.35 ⇒ **这张资产上测不出差异**，
//    两条臂的饱和像素都是 0/45354。所以这是一条**零风险的理论修正**，不是已复现的 bug。
const PRE_PROPS = `            [SCToggle]                                _PreZWrite       ("PrePass ZWrite", Int) = 0
            [SCEnum(Off, 0, Front, 1, Back, 2)]       _PreCull         ("PrePass Cull", Int) = 2
            [SCEnum(UnityEngine.Rendering.BlendMode)] _PreSrcBlend     ("PrePass SrcBlendRGB", Int) = 0
            [SCEnum(UnityEngine.Rendering.BlendMode)] _PreDstBlend     ("PrePass DstBlendRGB", Int) = 1
`;

function passBlock(pipeline) {
  const inc = pipeline === 'urp'
    ? 'Packages/jp.lilxyzw.shadercore/ShaderLibrary/urp_forward.hlsl'
    : 'Packages/jp.lilxyzw.shadercore/ShaderLibrary/birp_forward.hlsl';
  const hlsl = pipeline === 'urp' ? 'urp.hlsl' : 'birp.hlsl';
  const lightMode = pipeline === 'urp' ? 'UniversalForward' : 'ForwardBase';
  return `        Pass
        {
            Name "ForwardBack"
            Tags { "LightMode" = "${lightMode}" }

            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                Pass [_StencilPass]
            }
            Cull [_PreCull]
            ZWrite [_PreZWrite]
            Blend [_PreSrcBlend] [_PreDstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]

            HLSLPROGRAM
            #pragma target 5.0
            // ★ 与 lilToon 的 LIL_TRANSPARENT_PRE 同构：**只在本 pass 里**定义，
            //   让 sc_common.hlsl / urp.hlsl / birp.hlsl 的透明分支改走"背面趟"语义
            //   （乘 _PreColor + 用**自己的** _PreCutoff 裁剪，而不是 _Cutoff）。
            //   没有这一条，本 pass 就等于把 Forward 原样画第二遍 —— 实测混合量会多出近一倍
            //   （"第二次混合落地率" 0.80 vs lilToon 的 0.43）。
            #define NT_FORWARDBACK
            #include "${inc}"
            #include "${hlsl}"
            ENDHLSL
        }

`;
}

let src = readFileSync(SRC, 'utf8');

// 1) 改着色器名
src = src.replace(/^Shader "NonToon"/m, 'Shader "NonToonTwoPass"');
if (!src.includes('Shader "NonToonTwoPass"')) throw new Error('着色器名替换失败');

// 2) 追加 _Pre* 属性（挂在 __Rendering 折叠块里，紧跟 _AlphaToMask 之后）
const anchor = /(\[SCToggle\]\s+_AlphaToMask\s+\("AlphaToMask", Int\) = 0\n)/;
if (!anchor.test(src)) throw new Error('找不到 _AlphaToMask 锚点，基底结构可能变了');
src = src.replace(anchor, `$1${PRE_PROPS}`);

// 3) 两个 SubShader 各插一个 ForwardBack pass（必须在 Forward 之前）
const lines = src.split('\n');
const inserts = [];
lines.forEach((ln, i) => {
  if (ln.trim() !== 'Name "Forward"') return;
  // 找它所在的 Pass 起始行
  let p = i;
  while (p >= 0 && lines[p].trim() !== 'Pass') p--;
  if (p < 0) throw new Error(`第 ${i + 1} 行的 Forward 找不到所属 Pass`);
  // 判管线：往后找 include
  let pipeline = null;
  for (let k = i; k < Math.min(lines.length, i + 40); k++) {
    if (lines[k].includes('urp_forward.hlsl')) { pipeline = 'urp'; break; }
    if (lines[k].includes('birp_forward.hlsl')) { pipeline = 'birp'; break; }
  }
  if (!pipeline) throw new Error(`第 ${i + 1} 行的 Forward 判不出管线`);
  inserts.push({ at: p, text: passBlock(pipeline), pipeline });
});
if (inserts.length !== 2) throw new Error(`期望 2 个 Forward（URP+BiRP），实际 ${inserts.length}`);

// 倒序插入，避免索引位移
for (const ins of inserts.sort((a, b) => b.at - a.at)) {
  lines.splice(ins.at, 0, ...ins.text.split('\n').slice(0, -1));
}

// ⛔ ShaderCore 用 `new Regex(@"^\s*Shader\s+""([\w\s\/]+)""")`（**没有 Multiline**）取着色器名
//    （shadercore/Editor/ProjectSettings.cs:36）⇒ `Shader "名字"` **必须是文件的第一行**，
//    前面不能有任何注释（实测：加 19 行注释头就报 `Shader name not defined` 导入失败）。
//    所以注释块要放在 `Shader "..."` 之后。
const header = [
  '// ⚠️ 本文件由 tools/make-twopass.mjs 从 NonToon.scshader **生成**，请勿手改。',
  '//    重跑：node tools/make-twopass.mjs',
  '//    （上面那行 `Shader "NonToonTwoPass"` 必须是本文件第一行 —— ShaderCore 的正则没有 Multiline。）',
  '//',
  '// 为什么需要这个变体（[NT-FEAT 26] 两趟透明）：',
  '//   lilToon 的 TwoPass 透明（Hidden/lilToonTwoPassTransparentOutline）用 **两个 pass**',
  '//   —— FORWARD_BACK（Cull [_PreCull] / ZWrite [_PreZWrite]）→ FORWARD（Cull [_Cull] / ZWrite [_ZWrite]）。',
  '//   两遍在同深度处都能通过 ZTest LEqual ⇒ **同一像素混合两次**，有效不透明度 ≈ 1-(1-a)^2。',
  '//   这对「半透明包装袋 ↔ 袋内内容物」的遮挡/透视关系是必需的：',
  '//   实测（辉夜模型的 aburaage_open 油揚げ包装袋）单趟时黑布会渲染成灰白幽灵、袋体发虚。',
  '//   已实测否掉便宜替代：把 _ZWrite 改成 0（黑布回来但过深、且多出灰色晕圈）。',
  '//',
  '// 为什么是**独立变体**而不是给主着色器加一个常驻 pass：',
  '//   ShaderLab 无法按材质属性条件编译掉整个 Pass ⇒ 常驻 pass 会给**每个** NonToon 材质',
  '//   多一次 draw call。独立变体让只有需要的材质付这个代价，符合本包「默认零开销」原则；',
  '//   这也与 lilToon 同构 —— 它本来就是靠**专用变体着色器**表达渲染模式的。',
  '//',
  '// 模块登记是**自动**的：ShaderCore 的 ProjectSettings.GetShaderModules() 会在本文件所在目录',
  '// 扫 *.scmodule（shadercore/Editor/ProjectSettings.cs:28）⇒ 本变体自动获得与 NonToon 相同的模块集。',
  '//',
  '// 上游依据：lilToon 的 lts.shader / lts_onetrans.shader / lts_twotrans.shader 等变体；',
  '//           NonToon 上游 13 条 issue 中**没有任何一条涉及 TwoPass** ⇒ 这是 fork 自研能力。',
  '',
  '',
];

// ⛔ `Shader "..."` 必须留在**第一行**：注释块插在它之后。
const out = [lines[0], ...header, ...lines.slice(1)].join('\n');
writeFileSync(DST, out, 'utf8');
console.log(`✅ 已生成 ${DST}`);
console.log(`   第一行 = ${lines[0]}`);
console.log(`   插入的 ForwardBack pass：${inserts.map(x => x.pipeline).join(', ')}`);
console.log(`   着色器名：NonToonTwoPass  新增属性：_PreZWrite / _PreCull / _PreSrcBlend / _PreDstBlend`);

// ── 核心属性文件：**逐字复制**基底 + 追加两趟专用属性 ────────────────────────
// ⛔ 三条硬约束（都是踩出来的）：
//   1. ShaderCore 的属性解析器**不跳 `//` 注释、也不认 `#include`**
//      （`SCShaderImporter` 报 `Exception: Property error. // ...`）⇒ 本文件必须是**纯声明**；
//   2. 路径靠**文件名约定** `<去掉扩展名的着色器路径>_properties.hlsl`
//      （`SCModule.cs:26` / `SCShaderImporter.cs:107`）⇒ 变体必须自己有一份，
//      否则 `_RenderingMode` / `_SharedGradients` / `_NTShadowBias` 会**全部缺失**；
//   3. 所以"顺手复制"必须做成生成器的一部分 —— 手工 cp 一定会漂移。
const PROPS_SRC = join(root, 'NonToon', 'Shaders', 'NonToon_properties.hlsl');
const PROPS_DST = join(root, 'NonToon', 'Shaders', 'NonToonTwoPass_properties.hlsl');
// 追加的两条 = lilToon `_PreColor` / `_PreCutoff` 的对应物（默认值与 lilToon 逐字一致）。
// 只落在本变体里，**不动基底** NonToon_properties.hlsl（基底永远保持"纯声明、0 注释"）。
const PRE_CORE = [
  'SC_color(_PreColor, (1,1,1,1), [], "Pre Color", "")',
  'SC_float(_PreCutoff, 0.5, [SCRange(-0.001,1.001)], "Pre Cutoff", "")',
  '',
].join('\n');
const propsBody = readFileSync(PROPS_SRC, 'utf8');
writeFileSync(PROPS_DST, (propsBody.endsWith('\n') ? propsBody : propsBody + '\n') + PRE_CORE, 'utf8');
console.log(`✅ 已生成 ${PROPS_DST}（基底逐字复制 + 2 行两趟专用属性 _PreColor / _PreCutoff）`);
