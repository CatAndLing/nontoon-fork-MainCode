// 验证 GitHub Pages 落地页 + 用户手册：
//   1. 落地页必须含**可点击的 vcc:// 深链**（GitHub README 渲染器会过滤它，页面不会）
//   2. 落地页必须能引导到用户手册，且手册在线上真的能打开、含关键章节
//   3. listing 必须照旧可用
//
// [NT-AUDIT-FIX] 改动：
//   · 裸 https.get 不认代理（本机必须走代理）→ 改用 fetch，并在检测到代理时自动以 NODE_USE_ENV_PROXY=1 重启自己
//   · 原来 `hasListing` 是 `hasButton` 的**子串**，永远不会独立失败（空跑子检查）→ 换成独立判据
//   · 新增：落地页不许再出现"请把 Language 切成简体中文"这类 0.3.7 已废弃的提示
//   · 新增：用户手册在线可达 + 关键章节齐全
import { spawnSync } from 'node:child_process';

const proxy = process.env.HTTPS_PROXY || process.env.https_proxy || process.env.HTTP_PROXY || process.env.http_proxy;
if (proxy && !process.env.NODE_USE_ENV_PROXY) {
  console.log(`检测到代理 ${proxy}，自动以 NODE_USE_ENV_PROXY=1 重启本脚本…\n`);
  const r = spawnSync(process.execPath, [process.argv[1], ...process.argv.slice(2)],
    { stdio: 'inherit', env: { ...process.env, NODE_USE_ENV_PROXY: '1' } });
  process.exit(r.status ?? 1);
}

const PAGE = 'https://catandling.github.io/VPM-nontoon-fork/';
const LISTING = 'https://catandling.github.io/VPM-nontoon-fork/vpm.json';
// ⚠️ 手册**不能**用 raw.githubusercontent 读：它有 CDN 缓存，且加 `?cb=` 查询串**绕不过**
//（2026-09-18 实测：推完手册后 50 秒仍取到旧内容 ⇒ 门禁会拿**旧内容**做判断，
// 既是假红也是**假绿**）。改走 GitHub API 的 raw 媒体类型 —— 实测拿到的是最新提交。
const README_API = 'https://api.github.com/repos/CatAndLing/VPM-nontoon-fork/contents/README.md';
async function getReadme() {
  const res = await fetch(README_API, {
    headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/vnd.github.raw' },
    redirect: 'follow',
  });
  return { status: res.status, body: await res.text() };
}
const NEEDLE = 'href="vcc://vpm/addRepo?url=https://catandling.github.io/VPM-nontoon-fork/vpm.json"';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function get(u) {
  const res = await fetch(u, { headers: { 'User-Agent': 'Mozilla/5.0' }, redirect: 'follow' });
  return { status: res.status, body: await res.text() };
}

let ok = true;
const bad = (s) => { ok = false; console.log('  ❌ ' + s); };

let html = null;
for (let i = 1; i <= 10; i++) {
  try {
    const r = await get(PAGE);
    if (r.status === 200 && r.body.includes('NonToon')) { html = r.body; console.log(`[${i}] 落地页可访问 ✓ (HTTP 200, ${r.body.length} 字节)`); break; }
    console.log(`[${i}] HTTP ${r.status} ... 等 Pages 构建`);
  } catch (e) {
    console.log(`[${i}] 网络失败：${e.message}${proxy ? '' : '（没设 HTTPS_PROXY？）'}`);
  }
  await sleep(15000);
}
if (!html) { console.log('\n❌ 落地页始终不可访问'); process.exit(1); }

console.log('\n--- vcc:// 按钮 ---');
const hasButton = html.includes(NEEDLE);
console.log(`  期望 href: ${NEEDLE}`);
if (hasButton) console.log('  页面上找到: ✓'); else bad('页面上没有 vcc:// 深链（按钮会点不动）');

console.log('\n--- 落地页的独立判据 ---');
if (html.includes('vpm.json')) console.log('  页面同时给出 listing 地址: ✓'); else bad('页面没有列出 listing 地址');
if (html.includes('NonToon (Fork)')) console.log('  页面标注为 NonToon (Fork): ✓'); else bad('页面没有标注 NonToon (Fork)');

console.log('\n--- 过期文案检查（0.3.7 起不需要手动切语言）---');
if (html.includes('Language 切成') || html.includes('Language 下拉框里选')) bad('落地页仍在要求用户手动切语言（0.3.7 已自动生效）');
else console.log('  没有过时的「请手动切语言」提示: ✓');

console.log('\n--- 用户手册（已并入 README）---');
if (html.includes('#readme')) console.log('  落地页有 README（用户手册）入口: ✓'); else bad('落地页没有手册入口');
const m = await getReadme();
if (m.status !== 200) bad(`README 线上打不开：HTTP ${m.status}`);
else {
  console.log(`  README 可访问 ✓ (${m.body.length} 字节)`);
  for (const [what, needle] of [
    ['普通用户：安装', '## 1. 安装'],
    ['普通用户：五分钟上手', '## 2. 五分钟上手'],
    ['普通用户：光源组件详解', '## 3. 光源组件详解'],
    ['普通用户：亮度插件用法', '### 3.4 亮度自适应与防煤（编辑器插件）'],
    ['普通用户：功能总览', '## 4. 材质面板功能总览'],
    ['普通用户：性能与代价', '## 5. 性能与代价'],
    ['普通用户：AAO/MA 注意事项', '### 5.4 与 AAO / MA 等工具共用时的注意事项'],
    ['普通用户：Quest', '### 5.5 Quest'],
    ['普通用户：已知限制', '## 6. 已知限制'],
    ['普通用户：常见问题', '## 7. 常见问题'],
    ['开发者：包结构', '## 8. 包结构与依赖'],
    ['开发者：技术细节', '## 9. 着色器技术细节'],
    ['开发者：实测性能数据', '## 10. 实测性能数据'],
    ['开发者：平台约束', '## 11. 平台与运行时约束'],
    ['开发者：兼容性与集成', '## 12. 兼容性与集成'],
    ['开发者：验证方法', '## 13. 验证方法与质量标准'],
    ['开发者：修改指引与坑', '## 14. 修改指引与已知实现坑'],
    ['两部分的划分', '第一部分　普通用户'],
    ['VRChat 白名单事实', 'whitelisted-avatar-components'],
    ['LightLimitChanger 出处', 'LightLimitChanger'],
  ]) {
    if (m.body.includes(needle)) console.log(`    含「${what}」: ✓`); else bad(`README 缺少「${what}」`);
  }
  if (m.body.includes('MANUAL.md')) bad('README 里还指向已删除的 MANUAL.md');
  else console.log('    没有指向已删除的 MANUAL.md: ✓');

  // 手册**不得出现状态符号**（用户 2026-09-18 要求）。
  // 为什么值得单独一道闸：本手册早有决定 `a4f303b README 重写：…去除所有状态符号与圈码`，
  // 而 2026-09-18 的重写里我又把 ⬜/✅/🔒 塞了回去（基线实测这三种符号本来就是 0 个）。
  // ⇒ 这类"风格回归"靠记性必然复发，靠命令才拦得住。
  console.log('\n--- 手册不得出现状态符号 ---');
  const BANNED = ['⬜', '✅', '❌', '🔒', '🔄', '🟡'];
  const hit = BANNED.filter((s) => m.body.includes(s));
  if (hit.length) bad(`README 里出现了状态符号：${hit.join(' ')}（本手册的既有风格是纯文字；警告用 ⚠️／⛔ 是原有的，不在禁止之列）`);
  else console.log('  未出现状态符号（⬜ ✅ ❌ 🔒 🔄 🟡）: ✓');
}

console.log('\n--- listing（身份已切分为自有 id） ---');
const r = await get(LISTING);
let listingOk = false, listingName = '';
try {
  const j = JSON.parse(r.body.replace(/^\uFEFF/, ''));
  const ids = Object.keys(j.packages ?? {}).sort();
  const EXPECT = ['com.catandling.nontoon', 'com.catandling.nontoon-converter'];
  const latestOf = (id) => {
    const vs = Object.keys(j.packages?.[id]?.versions ?? {});
    return vs.length ? vs[vs.length - 1] : null;
  };
  const ntVer = latestOf('com.catandling.nontoon');
  const cvVer = latestOf('com.catandling.nontoon-converter');
  // 期望版本取自源码 package.json（唯一真源），不硬编码 —— 否则每次发版这条都要手改。
  const fs = await import('node:fs');
  const srcDir = new URL('../', import.meta.url);
  const srcNt = JSON.parse(fs.readFileSync(new URL('NonToon/package.json', srcDir), 'utf8')).version;
  const srcCv = JSON.parse(fs.readFileSync(new URL('nontoon-converter/package.json', srcDir), 'utf8')).version;
  const nameOk = j.packages?.['com.catandling.nontoon']?.versions?.[ntVer]?.displayName === 'NonToon (Fork)';
  // 仓库 id：2026-09-18 由 io.github.123cy321.vpm 改为 io.github.catandling.vpm（摆脱旧账号名）。
  // 单独断言 —— 这类"身份漂移"正是本仓库刚犯过一次的错，加一道闸免得再犯。
  const REPO_ID = 'io.github.catandling.vpm';
  const repoIdOk = j.id === REPO_ID;
  // 每条判据都能单独失败：仓库 id / 包 id 集合恰好两个新身份 / 线上版本 == 源码版本 / displayName 正确
  listingOk = ids.length === EXPECT.length && EXPECT.every((k) => ids.includes(k))
    && ntVer === srcNt && cvVer === srcCv && nameOk && repoIdOk;
  listingName = `仓库 id=${j.id}${repoIdOk ? '' : '（应为 ' + REPO_ID + '）'} ids=[${ids.join(', ')}]`
    + ` nontoon=${ntVer}(源码 ${srcNt}) converter=${cvVer}(源码 ${srcCv})`
    + ` displayName=${nameOk ? 'ok' : 'BAD'}`;
} catch (e) { listingName = 'parse error: ' + e.message; }
if (listingOk) console.log(`  HTTP ${r.status}, ${listingName}: ✓`); else bad(`listing 异常（${listingName}）`);

console.log(ok ? '\n==== 落地页与手册核验全部通过 ====' : '\n==== 落地页/手册核验失败（见上面 ❌）====');
process.exit(ok ? 0 : 1);
