// 组装"干净安装门禁"用的**本地 staging listing**（**推送前**跑，替代"先推再测"）。
//
// 为什么有这一步：0.3.11/0.5.3 那次发布是**推完才补**干净安装门禁的 —— 硬门禁在推送之后才跑，
// 等于没有闸。本脚本 + `verify-clean-install.sh` 让它能在**推送前**跑：vrc-get 从一个 `file://`
// 的本地 listing 解析并安装，装的 zip 与将要发布的**完全同一份文件**。
//
// ⚠️ 我们自己的两个包：zip 名从**源码 package.json** 推导，并把 zip 自动拷进 staging 目录。
//    绝不在脚本里硬编码版本号 ——"bump 了版本忘了改脚本"是本项目反复出现的毛病。
//    第三方依赖仍**固定版本**：那是测试床的已知输入，不随我们发版变化。
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const stage = path.resolve('_notes/clean-vpm-stage');
fs.mkdirSync(stage, { recursive: true });

// 我们自己的包：{id, 源目录} → zip 名由源码版本推导，并拷进 staging
const OWN = [
  ['com.catandling.nontoon', 'NonToon'],
  ['com.catandling.nontoon-converter', 'nontoon-converter'],
];
const own = OWN.map(([id, dir]) => {
  const v = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8')).version;
  const name = `${id}-${v}.zip`;
  if (!fs.existsSync(name)) { console.error(`❌ 仓库根缺 ${name} —— 先打 zip（ziptool）`); process.exit(1); }
  fs.copyFileSync(name, path.join(stage, name));
  return [id, name];
});
console.log('== 本地 staging：我们自己的包（zip 名由源码 package.json 推导）==');
for (const [id, name] of own) console.log(`  ${id}  <-  ${name}`);

// 第三方：固定版本（测试床的已知输入）
const files = [
  ...own,
  ['jp.lilxyzw.shadercore', 'jp.lilxyzw.shadercore-0.1.9.zip'],
  ['nadena.dev.modular-avatar', 'nadena.dev.modular-avatar-1.18.7.zip'],
  ['nadena.dev.ndmf', 'nadena.dev.ndmf-1.14.8.zip'],
];

const packages = {};
for (const [id, name] of files) {
  const z = path.join(stage, name);
  if (!fs.existsSync(z)) { console.error(`❌ staging 里缺 ${name}（第三方依赖需要预先放好）`); process.exit(1); }
  const p = JSON.parse(execFileSync('unzip', ['-p', z, 'package.json'], { encoding: 'utf8' }));
  packages[id] = { versions: { [p.version]: {
    name: p.name, version: p.version, displayName: p.displayName, description: p.description,
    unity: p.unity, hideInEditor: p.hideInEditor,
    url: 'file:///' + z.replaceAll('\\', '/'),
    vpmDependencies: p.vpmDependencies || {},
    zipSHA256: crypto.createHash('sha256').update(fs.readFileSync(z)).digest('hex'),
  } } };
}
fs.writeFileSync(path.join(stage, 'vpm.json'),
  JSON.stringify({ name: 'clean-local', id: 'local.clean', author: 'catandling', url: 'file:///clean-vpm-stage/vpm.json', packages }, null, 2) + '\n');

console.log('\n== staging listing 已生成（verify-clean-install.sh 用它做推送前门禁）==');
for (const [id, v] of Object.entries(packages)) {
  const ver = Object.keys(v.versions)[0];
  console.log(`  ${id} @ ${ver}  ${v.versions[ver].zipSHA256.slice(0, 12)}…`);
}
