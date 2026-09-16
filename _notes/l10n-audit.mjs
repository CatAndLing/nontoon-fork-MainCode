// NonToon 本地化审计 v2：抽取全部可本地化字符串（属性/提示/折叠/枚举标签/ShaderLab 属性/模块名）
// 用法: node _notes/l10n-audit.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve('NonToon/Shaders');
const SHADERCORE = path.resolve('_verify-proj/Packages/jp.lilxyzw.shadercore');

function splitArgs(s) {
  const out = []; let depth = 0, cur = '', inStr = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inStr) { cur += c; if (c === '"') inStr = false; continue; }
    if (c === '"') { inStr = true; cur += c; continue; }
    if (c === '(' || c === '[') depth++;
    if (c === ')' || c === ']') depth--;
    if (c === ',' && depth === 0) { out.push(cur.trim()); cur = ''; continue; }
    cur += c;
  }
  if (cur.trim()) out.push(cur.trim());
  return out;
}
const unq = s => (s && s.startsWith('"') && s.endsWith('"')) ? s.slice(1, -1) : null;

function walk(dir, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, acc); else acc.push(p);
  }
  return acc;
}
function readPo(p) {
  const map = new Map();
  if (!fs.existsSync(p)) return map;
  const re = /msgid\s+"((?:[^"\\]|\\.)*)"\s*\nmsgstr\s+"((?:[^"\\]|\\.)*)"/g;
  let m; const txt = fs.readFileSync(p, 'utf8');
  while ((m = re.exec(txt))) {
    // [NT-AUDIT-FIX] 跳过 PO 头部的空 msgid（msgid "" / msgstr ""）。原来它被当成一个真实条目，
    // 于是 zh-Hans.po 永远"缺 1 条"→ 发版清单里「l10n-audit 缺失为 0」这条长期不成立，
    // 典型的"狼来了"：看久了就会把真缺失一起忽略。
    if (m[1] === '') continue;
    map.set(m[1], m[2]);
  }
  return map;
}

const add = (list, scope, key, kind, where) => { if (key && !key.startsWith('__')) list.push({ scope, key, kind, where }); };
const entries = [];

// ---- 1. hlsl 属性 / 折叠 / 枚举标签 ----
for (const f of walk(ROOT)) {
  if (!f.endsWith('.hlsl')) continue;
  const base = path.basename(f);
  let scope = null;
  if (base.endsWith('_properties.hlsl')) scope = 'Shaders';
  else if (base === 'properties.hlsl') scope = path.basename(path.dirname(f));
  if (!scope) continue;
  const rel = path.relative(process.cwd(), f);
  fs.readFileSync(f, 'utf8').split(/\r?\n/).forEach((line, i) => {
    const m = /^\s*SC_(\w+)\s*\((.*)\)\s*$/.exec(line);
    if (!m) return;
    const type = m[1], args = splitArgs(m[2]);
    if (type === 'Foldout') { add(entries, scope, unq(args[0]), 'foldout', `${rel}:${i + 1}`); return; }
    if (['SamplerState', 'ScaleOffset', 'Box', 'BoxEnd', 'FoldoutEnd'].includes(type)) return;
    // 枚举标签
    for (const a of args) {
      const em = /\[SCEnum\(([^\]]*)\)\]/.exec(a);
      if (!em) continue;
      const parts = splitArgs(em[1]);
      // SCEnum(Label,0,Label,1,...) 或 SCEnum(UnityEngine.Xxx)
      if (parts.length === 1 && !/^\d+$/.test(parts[0])) continue; // 系统枚举，取不到标签
      for (let k = 0; k + 1 < parts.length; k += 2) add(entries, scope, unq(parts[k]), 'enum', `${rel}:${i + 1}`);
    }
    if (args.length < 5) return;
    add(entries, scope, unq(args[3]), 'display', `${rel}:${i + 1}`);
    add(entries, scope, unq(args[4]), 'tooltip', `${rel}:${i + 1}`);
  });
}

// ---- 2. .scshader 里的手工 ShaderLab 属性 ----
for (const f of walk(ROOT).filter(f => f.endsWith('.scshader'))) {
  const rel = path.relative(process.cwd(), f);
  fs.readFileSync(f, 'utf8').split(/\r?\n/).forEach((line, i) => {
    const m = /^\s*(?:\[[^\]]*\]\s*)*(_\w+)\s*\(\s*"([^"]*)"\s*,/.exec(line);
    if (m && m[2]) add(entries, 'Shaders', m[2], 'shaderlab', `${rel}:${i + 1}`);
  });
}

// ---- 3. 对照 po ----
const scopes = [...new Set(entries.map(e => e.scope))].sort();
const report = [];
let miss = 0, tot = 0;
for (const scope of scopes) {
  const dir = scope === 'Shaders' ? path.join(ROOT, 'lang') : path.join(ROOT, 'Modules', scope, 'lang');
  const po = readPo(path.join(dir, 'zh-Hans.po'));
  const keys = [...new Set(entries.filter(e => e.scope === scope).map(e => e.key))].sort();
  const missing = keys.filter(k => !po.has(k) || !po.get(k).trim());
  tot += keys.length; miss += missing.length;
  report.push({
    scope, po: path.relative(process.cwd(), path.join(dir, 'zh-Hans.po')),
    total: keys.length, missing: missing.length,
    missingDetail: missing.map(k => `${k}  <- ${entries.filter(e => e.scope === scope && e.key === k).map(e => e.kind + '@' + e.where).join(', ')}`)
  });
}
console.log(JSON.stringify({ totalKeys: tot, totalMissing: miss, report }, null, 2));

// ---- 4. shadercore 核心键覆盖 ----
const coreEn = readPo(path.join(SHADERCORE, 'lang/en-US.po'));
const coreZh = readPo(path.join(SHADERCORE, 'lang/zh-Hans.po'));
const coreMissing = [...coreEn.keys()].filter(k => !coreZh.has(k) || !coreZh.get(k).trim());
console.error(`\n=== shadercore core: ${coreEn.size} keys, zh missing ${coreMissing.length} ===`);
console.error(coreMissing.join('\n'));

// ---- 5. scmodule 名称 ----
console.error('\n=== scmodule names ===');
for (const f of walk(path.join(ROOT, 'Modules')).filter(f => f.endsWith('.scmodule'))) {
  const j = JSON.parse(fs.readFileSync(f, 'utf8'));
  console.error(`${j.uniqueID}\t${j.name}`);
}

// ---- 6. zh-CN.po 必须与 zh-Hans.po 一致（2026-09-17 实机事故）----
// Unity 中文编辑器解析出的语言码是 **zh-CN**，ShaderCore 会优先读 `zh-CN.po`，
// 并且**不会**对缺失的键回退到 `zh-Hans.po`（实测：缺失的键直接显示英文）。
// 实机现场：`zh-CN.po` 是 65 键的旧快照、`zh-Hans.po` 是 71 键，差的正好是 ⑥ 的
// 6 个 `Realtime *` 标签 ⇒ 面板上那 6 行全是英文。而旧审计只比 zh-Hans，
// 报「缺失 0」—— 一条典型的**假绿**。
const staleCn = [];
for (const zh of walk(ROOT).filter(f => /lang[\\/]zh-Hans\.po$/.test(f))) {
  const cn = zh.replace(/zh-Hans\.po$/, 'zh-CN.po');
  if (!fs.existsSync(cn)) continue;            // 没有 zh-CN.po 时不判（那种情况会回退 zh-Hans）
  const a = readPo(zh), b = readPo(cn);
  const onlyHans = [...a.keys()].filter(k => !b.has(k));
  const diff = [...a.keys()].filter(k => b.has(k) && b.get(k) !== a.get(k));
  if (onlyHans.length || diff.length)
    staleCn.push(`${path.relative(ROOT, cn)}: 缺 ${onlyHans.length} 键、内容不一致 ${diff.length} 键`
      + (onlyHans.length ? `（例：${onlyHans.slice(0, 3).join(' / ')}）` : ''));
}
if (staleCn.length) {
  console.error('\n=== ❌ zh-CN.po 与 zh-Hans.po 不一致（中文编辑器会显示英文） ===');
  staleCn.forEach(s => console.error('  ' + s));
  console.error('  修法：删除过期的 zh-CN.po，或把它重新同步成 zh-Hans.po 的副本');
}

// ---- 7. 退出码 ----
// [NT-AUDIT-FIX] 以前整个脚本没有 process.exit：实测把 po 全删掉（缺失 100%）仍然 EXIT=0，
// 任何判退出码的用法都会放行。另外"一条键都没抽到"也判失败，避免空跑通过。
const noKeys = tot === 0;
if (noKeys) console.error('\n❌ 一条可本地化字符串都没抽到（路径不对？包被删了？）—— 这不算通过');
console.error(`\n=== 汇总：可本地化键 ${tot} 条，缺失 ${miss} 条，shadercore 核心缺 ${coreMissing.length} 条 ===`);
const FAILED = miss > 0 || noKeys || staleCn.length > 0;
console.error(FAILED ? '>>> 本地化审计失败' : '>>> 本地化审计通过（缺失 0）');
process.exit(FAILED ? 1 : 0);
