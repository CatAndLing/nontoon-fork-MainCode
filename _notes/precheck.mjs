// 预检 SCProperty 语法（严格模仿 ShaderCore 的 SCProperty.Parse 正则），避免又出现
// "Property error." 这种整个着色器导入失败的低级错误。
import fs from 'node:fs';
import path from 'node:path';

const REG_START = '^\\s*SC_';
const REG_END = '\\s*$';
const REG_PAR_START = '\\s*\\(\\s*';
const REG_PAR_END = '\\s*\\)';
const REG_SEPARATOR = '\\s*,\\s*';
const REG_variable = '\\w+';
const REG_num = '[\\d\\.\\-]+';
const REG_vector = '\\([\\d\\.\\-,\\s]*\\)';
const REG_string = '"[^"]*"';
const REG_type = '(' + REG_variable + ')';
const REG_name = '(' + REG_variable + ')';
const REG_defaultvalue = '(' + REG_num + '|' + REG_vector + '|' + REG_string + ')';
const REG_attributes = '(\\[[^\\[\\]]*\\]\\s*)*';
const REG_displayname = '(' + REG_string + ')';
const REG_description = '(' + REG_string + ')';
const REG_Property = new RegExp(REG_START + REG_type + REG_PAR_START + REG_name + REG_SEPARATOR + REG_defaultvalue + REG_SEPARATOR + REG_attributes + REG_SEPARATOR + REG_displayname + REG_SEPARATOR + REG_description + REG_PAR_END + REG_END);
const REG_SamplerState = new RegExp(REG_START + 'SamplerState\\(\\s*(' + REG_variable + ')\\s*\\)' + REG_END);
const REG_ScaleOffset = new RegExp(REG_START + 'ScaleOffset\\(\\s*(' + REG_variable + ')\\s*\\)' + REG_END);
const REG_Box = new RegExp(REG_START + 'Box' + REG_END);
const REG_BoxEnd = new RegExp(REG_START + 'BoxEnd' + REG_END);
const REG_Foldout = new RegExp(REG_START + 'Foldout\\(([^(())]*)\\)' + REG_END);
const REG_FoldoutEnd = new RegExp(REG_START + 'FoldoutEnd' + REG_END);

function walk(d, a = []) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); e.isDirectory() ? walk(p, a) : a.push(p); } return a; }

let bad = 0, total = 0, seenModule = 0, seenPo = 0;
for (const f of walk('NonToon/Shaders').filter(f => f.endsWith('.hlsl'))) {
  const base = path.basename(f);
  if (base !== 'properties.hlsl' && !base.endsWith('_properties.hlsl')) continue;
  fs.readFileSync(f, 'utf8').split(/\r?\n/).forEach((line, i) => {
    if (!line.trim()) return;
    total++;
    if (REG_Property.test(line) || REG_SamplerState.test(line) || REG_ScaleOffset.test(line) ||
        REG_Box.test(line) || REG_BoxEnd.test(line) || REG_Foldout.test(line) || REG_FoldoutEnd.test(line)) return;
    console.log(`✗ ${f}:${i + 1}  ${line}`);
    bad++;
  });
}
console.log(bad ? `\n>>> ${bad} 行不符合 SCProperty 语法（共 ${total} 行）` : `\n>>> 全部 ${total} 行属性声明语法通过`);

// .scmodule JSON 校验
for (const f of walk('NonToon/Shaders/Modules').filter(f => f.endsWith('.scmodule'))) {
  seenModule++;
  try { const j = JSON.parse(fs.readFileSync(f, 'utf8')); if (!j.name || !j.uniqueID) throw new Error('缺少 name/uniqueID'); }
  catch (e) { console.log('✗ ' + f + ': ' + e.message); bad++; }
}

// po 校验（重复 key 会让 ShaderCore 的 POParser 直接抛异常）
for (const f of [...walk('NonToon').filter(f => f.endsWith('.po')), ...walk('nontoon-converter').filter(f => f.endsWith('.po'))]) {
  seenPo++;
  const keys = new Set();
  const lines = fs.readFileSync(f, 'utf8').split(/\r?\n/);
  let key = null;
  for (const line of lines) {
    if (!line.trim() || line.startsWith('"') || line.startsWith('#')) continue;
    let m = /^\s*msgid\s*"(.*)"\s*$/.exec(line);
    if (m) { key = m[1]; continue; }
    m = /^\s*msgstr\s*"(.*)"\s*$/.exec(line);
    if (m) {
      if (keys.has(key) && key !== '') { console.log(`✗ ${f}: 重复 key ${JSON.stringify(key)}`); bad++; }
      keys.add(key); key = null; continue;
    }
    console.log(`✗ ${f}: 无法解析 ${JSON.stringify(line)}`); bad++;
  }
}
console.log(bad ? '>>> 存在问题' : '>>> po / scmodule / 属性语法全部通过');

// [NT-AUDIT-FIX] 以前整个脚本**没有 process.exit**：报了多少问题，进程一律 exit 0，
// 于是 `node precheck.mjs && 下一步` 或任何 CI 判退出码的用法都会放行（实测：1 行不符仍 EXIT=0）。
// 另外，如果**什么都没检查到**（路径变了 / 包被删了），原来也会打印"全部通过" —— 那是空跑通过。
if (total === 0) console.log('❌ 一个属性文件都没扫到（路径不对？包被删了？）—— 这不算通过');
if (seenModule === 0) console.log('❌ 一个 .scmodule 都没扫到');
if (seenPo === 0) console.log('❌ 一个 .po 都没扫到');
const FAILED = bad > 0 || total === 0 || seenModule === 0 || seenPo === 0;
console.log(`>>> 预检${FAILED ? '失败' : '通过'}（属性行 ${total}，模块 ${seenModule}，po ${seenPo}，问题 ${bad}）`);
process.exit(FAILED ? 1 : 0);
