// 着色器静态审计：pass 数、采样器数、关键字（变体）、编译目标、平台风险点。
// 用法: node _notes/audit-shader.mjs [生成的源码文件]
//
// 为什么看"生成的源码"而不是我们的 .scshader：ShaderCore 会把模块与相位拼进去，
// 真正被编译的是生成后的东西。用 _e2e-proj 的 dump 或 _verify-proj 的 dump 都行。
import fs from 'node:fs';
// 归档的生成源码在 _notes/evidence/（会话收尾时把 _e2e-proj 删了，只留 dump）。
// 要重新生成：node _notes/e2e-setup.mjs 建工程 → 跑 NTE2EProbe3.Run（它会把 dump 写进工程根）。
const candidates = process.argv.length > 2 ? process.argv.slice(2) : [
  '_notes/evidence/shader-source-NonToon.txt',
  '_notes/evidence/shader-source-NonToonFur.txt',
  '_e2e-proj/e2e-shader-source-NonToon.txt',
  '_e2e-proj/e2e-shader-source-NonToonFur.txt',
];

const files = candidates.filter((f) => fs.existsSync(f));
let bad = 0;
if (process.argv.length > 2) {
  for (const f of candidates.filter(f => !fs.existsSync(f))) {
    console.error(`❌ 指定的生成源码不存在：${f}`);
    bad++;
  }
}
if (!files.length) {
  console.error('找不到生成源码。先跑一次 NTE2EProbe3.Run（它会 dump 到 _e2e-proj/e2e-shader-source-*.txt）');
  process.exit(1);
}

console.log('='.repeat(70));
console.log('着色器静态审计');
console.log('='.repeat(70));

for (const f of files) {
  let src;
  try { src = fs.readFileSync(f, 'utf8'); }
  catch (e) { console.error(`❌ ${f}: ${e.message}`); bad++; continue; }
  const st = fs.statSync(f);
  console.log(`\n### ${f}`);
  console.log(`    生成源码 ${src.length} 字符，文件时间 ${st.mtime.toISOString().slice(0, 19)}`);
  console.log('    ⚠️ 若这个 dump 早于最近的着色器改动，数字要重新生成后再看。');

  // ---- Pass ----
  const passes = (src.match(/^\s*Pass\s*\{/gm) || []).length;
  const subshaders = (src.match(/^\s*SubShader\b/gm) || []).length;
  console.log(`\n  [结构]`);
  console.log(`    SubShader ${subshaders} 个，Pass ${passes} 个`);

  // ---- 编译目标 ----
  const targets = {};
  for (const m of src.matchAll(/#pragma\s+target\s+([0-9.]+)/g)) targets[m[1]] = (targets[m[1]] || 0) + 1;
  console.log(`    #pragma target 分布：` + Object.entries(targets).sort().map(([k, v]) => `${k}×${v}`).join(', '));

  // ---- 着色器阶段 ----
  const stages = {};
  for (const m of src.matchAll(/#pragma\s+(vertex|fragment|geometry|hull|domain)\b/g)) stages[m[1]] = (stages[m[1]] || 0) + 1;
  console.log(`    阶段：` + Object.entries(stages).map(([k, v]) => `${k}×${v}`).join(', '));

  // ---- 变体关键字 ----
  const kw = { multi_compile: 0, shader_feature: 0, multi_compile_local: 0, shader_feature_local: 0, others: 0 };
  const kwNames = new Set();
  for (const m of src.matchAll(/#pragma\s+(multi_compile\w*|shader_feature\w*)\s+([^\n\r]+)/g)) {
    const kind = m[1].startsWith('multi_compile') ? (kind => kind)(m[1]) : m[1];
    kw[m[1]] = (kw[m[1]] || 0) + 1;
    // 关键字里的每一段都算一个可能值
    for (const name of m[2].trim().split(/\s+/)) kwNames.add(name);
  }
  const kwTotal = Object.entries(kw).filter(([k]) => k !== 'others').reduce((a, [, v]) => a + v, 0);
  console.log(`\n  [变体]`);
  console.log(`    关键字指令 ${kwTotal} 条：` +
    Object.entries(kw).filter(([, v]) => v).map(([k, v]) => `${k}×${v}`).join(', '));
  console.log(`    关键字名 ${kwNames.size} 个（理论组合数是 2^k，实际由 Unity 的变体剥离削减）`);

  // ---- 采样器 / 纹理 ----
  const samplers = [...src.matchAll(/\bSamplerState\s+(\w+)/g)].map((m) => m[1]);
  const uniqSamplers = [...new Set(samplers)];
  const texDecls = [...src.matchAll(/\b(Texture2D|Texture2DArray|TextureCube|Texture3D)\s*<[^>]*>\s*(\w+)/g)];
  const uniqTex = [...new Set(texDecls.map((m) => m[2]))];
  console.log(`\n  [采样器与纹理声明（Quest/GLES3 要重点看）]`);
  console.log(`    SamplerState 声明 ${samplers.length} 次，去重 ${uniqSamplers.length} 个`);
  console.log(`    Texture 对象声明 ${texDecls.length} 次，去重 ${uniqTex.length} 个`);
  const kinds = {};
  for (const m of texDecls) kinds[m[1]] = (kinds[m[1]] || 0) + 1;
  console.log(`    纹理类型：` + Object.entries(kinds).map(([k, v]) => `${k}×${v}`).join(', '));

  // ---- 每像素采样点（grep 采样调用）----
  const sampleCalls = (src.match(/\.(Sample|SampleLevel|SampleGrad|tex2D|tex2Dlod)\s*\(/g) || []).length;
  console.log(`\n  [采样调用点] 源码里共 ${sampleCalls} 处（含不同 pass/分支；实际每像素次数取决于分支）`);

  // ---- 平台风险点 ----
  console.log(`\n  [平台风险点]`);
  const risks = [
    ['Texture2DArray', /Texture2DArray/],
    ['SampleLevel', /SampleLevel/],
    ['ddx/ddy', /\bddx\s*\(|\bddy\s*\(/],
    ['SV_VertexID', /SV_VertexID|unity_VertexID/],
    ['SV_InstanceID', /SV_InstanceID|unity_InstanceID/],
    ['几何着色器', /#pragma\s+geometry/],
    ['顶点色（COLOR 语义）', /:\s*COLOR\b/],
    ['纹理 LOD 偏导', /tex2Dlod|SampleGrad/],
  ];
  for (const [name, re] of risks) {
    const c = (src.match(new RegExp(re.source, 'g')) || []).length;
    console.log(`    ${c ? '⚠️ ' : '✅ '}${name}: ${c} 处`);
  }

  // 确定性结构错误才阻断；跨 pass 的声明总数、Quest 风险不是 D3D11 编译错误。
  // 去掉注释，避免注释里的 Pass/pragma/占位符让断言因错误的理由通过。
  const code = src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\r\n]*/g, '');
  const count = re => (code.match(re) || []).length;
  const passCount = count(/^\s*Pass\s*\{/gm);
  const problems = [
    [!code.trim(), '源码为空或只有注释'],
    [count(/^\s*Shader\s+"[^"\r\n]+"\s*\{/gm) !== 1, '必须恰有一个 Shader 声明'],
    [!count(/^\s*SubShader\s*\{/gm) || !passCount, '缺少 SubShader/Pass'],
    [count(/^\s*#pragma\s+target\s+[0-9.]+\b/gm) < passCount, 'Pass 缺少显式编译目标'],
    [count(/^\s*#pragma\s+vertex\s+\w+/gm) < passCount ||
     count(/^\s*#pragma\s+fragment\s+\w+/gm) < passCount, 'Pass 缺少顶点/片元入口'],
    [/\b__SC_\w*__\b/.test(code), '残留 ShaderCore 未展开占位符'],
    [/^\s*#\s*error\b/m.test(code), '存在显式 #error'],
  ];
  for (const [failed, reason] of problems) {
    if (failed) { console.log(`    ❌ ${reason}`); bad++; }
  }
}

console.log('\n' + '='.repeat(70));
console.log('判定要点');
console.log('='.repeat(70));
console.log(`  · GLES3/Quest 的**片元纹理单元下限是 16**（OpenGL ES 3.0 规范）；`);
console.log(`    去重后的 SamplerState 数如果 > 16，就要确认目标设备/引擎的实际上限。`);
console.log(`  · #pragma target 5.0 要求 DX11 / ES3.1+AEP / Vulkan；Quest 的 GLES3.0 路径存在风险，`);
console.log(`    本机没装 Android 构建支持，**无法实测**（需在装了 Android 模块的机器上构建一次 Android bundle）。`);
console.log(`  · 参考：lilToon 主要用 target 3.5（32 处）、4.5（24 处）、5.0（10 处）——比本包保守。`);
console.log('  · 退出判据：输入非空且结构检查无问题；平台风险仅提示，不等同真实编译失败。');
console.log(`>>> 静态审计${bad ? '失败' : '通过'}（源码 ${files.length}，问题 ${bad}）`);
process.exit(bad ? 1 : 0);
