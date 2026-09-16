// 手册重写的**客观核对**：把"事实原子"抽成集合作差分。
//
// 为什么需要：肉眼看 900 行必然漏；而"重写把某个数字/属性名弄丢了"正是本项目最忌讳的
// 那类静默退化（AGENTS.md §3 假绿表：日志说对了、画面没变）。
// 判据：**丢失**才是失败的；新增只提示（可能是合理的补充）。
//
// 用法：node _notes/readme-factdiff.mjs <旧.md> <新.md>
import fs from 'node:fs';

const [oldP, newP] = process.argv.slice(2);
if (!oldP || !newP) { console.error('用法: node readme-factdiff.mjs <旧.md> <新.md>'); process.exit(2); }
const rd = (p) => fs.readFileSync(p, 'utf8');
const A = rd(oldP), B = rd(newP);

const collect = (t) => {
  const c = { num: new Set(), id: new Set(), guid: new Set(), url: new Set(), head: new Set(), code: new Set() };
  // 版本号 / 数值（1.0 / 0.3.11 / 2450 …）
  for (const m of t.matchAll(/\d+(?:\.\d+){1,3}/g)) c.num.add(m[0]);
  // 属性名与包 id / 着色器名
  for (const m of t.matchAll(/_[A-Za-z][A-Za-z0-9_]{2,}/g)) c.id.add(m[0]);
  for (const m of t.matchAll(/\b(?:com|io|jp|nadena|de|vrchat|red)\.[A-Za-z0-9_.-]{2,}/g)) c.id.add(m[0]);
  for (const m of t.matchAll(/\b[0-9a-f]{32}\b/g)) c.guid.add(m[0]);
  for (const m of t.matchAll(/https?:\/\/[^\s)`"']+/g)) c.url.add(m[0].replace(/[.,)]+$/, ''));
  for (const m of t.matchAll(/^#{1,3}[ \u3000].+$/gm)) c.head.add(m[0].trim());
  // 反引号里的技术键（路径、命令、文件名）
  for (const m of t.matchAll(/`([^`\n]{2,80})`/g)) c.code.add(m[1]);
  return c;
};

const a = collect(A), b = collect(B);
const LABEL = { num: '数值/版本', id: '属性名·包id', guid: 'GUID', url: 'URL', head: '标题', code: '反引号技术键' };

let bad = 0;
console.log(`旧: ${A.length} 字符 / ${A.split('\n').length} 行`);
console.log(`新: ${B.length} 字符 / ${B.split('\n').length} 行\n`);

for (const k of Object.keys(a)) {
  const missing = [...a[k]].filter((x) => !b[k].has(x));
  const added = [...b[k]].filter((x) => !a[k].has(x));
  console.log(`── ${LABEL[k]}：旧 ${a[k].size} / 新 ${b[k].size}  |  丢失 ${missing.length}  新增 ${added.length}`);
  if (missing.length) {
    if (k !== 'head') bad += missing.length;
    console.log(`   ❌ 丢失: ${missing.slice(0, 40).join(' · ')}${missing.length > 40 ? ' …' : ''}`);
  }
  if (added.length && added.length <= 25) console.log(`   ＋ 新增: ${added.join(' · ')}`);
}

console.log('\n' + (bad === 0
  ? '✅ 未发现事实原子丢失（标题以外的丢失数 = 0）'
  : `⛔ 有 ${bad} 项事实原子在重写后消失 —— 逐条核对是"搬去附录"还是"真丢了"`));
process.exit(bad === 0 ? 0 : 1);
