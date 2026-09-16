// 生成"**推送前**干净安装门禁"用的本地 staging listing。
//
// 为什么需要：0.3.11/0.5.3 那次是**推完才补**干净安装门禁 —— 硬门禁在推送之后才跑，等于没有闸。
// 本脚本 + `verify-clean-install.sh` 让它在推送**之前**跑：vrc-get 从一个本地 listing 解析并安装，
// 装的 zip 与将要发布的**完全同一份文件**。
//
// ⚠️ 两条踩过的坑（都是"会造假的绿"）：
//   1) **形状必须照抄已发布的 `docs/vpm.json`**：手工构造的 listing 少字段时，vrc-get 会
//      `missing field 'repo'` 拒绝加载整个本地仓库，然后**静默回落到线上 listing** ——
//      于是门禁拿**旧版本**跑出绿色。所以这里以已发布条目为模板，只覆盖版本相关内容。
//   2) **zip 名从源码 package.json 推导**，不硬编码（"bump 了版本忘了改脚本"是本项目的老毛病）。
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const stage = path.resolve('_notes/clean-vpm-stage');
fs.mkdirSync(stage, { recursive: true });
// URL 基址：默认走**本地 HTTP**（`stage-server.mjs` 伺服）。
// 为什么不用 file://：vrc-get 1.9.2 的 `repo add` 对裸路径/file:// 都不接受 ⇒ 本地 HTTP 最稳。
const base = process.env.STAGE_BASE || `http://127.0.0.1:${process.env.STAGE_PORT || 8799}`;
const listingUrl = `${base}/vpm.json`;
const pub = JSON.parse(fs.readFileSync('_VPM-nontoon-fork/docs/vpm.json', 'utf8'));

const OWN = [
  ['com.catandling.nontoon', 'NonToon'],
  ['com.catandling.nontoon-converter', 'nontoon-converter'],
];

const out = { name: 'clean-local', id: 'local.clean', author: 'catandling', url: listingUrl, packages: {} };
console.log('== 本地 staging（形状照抄已发布 listing，url 指向本地 zip）==');

for (const [id, dir] of OWN) {
  const src = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
  const rel = `${id}-${src.version}.zip`;
  if (!fs.existsSync(rel)) { console.error(`❌ 仓库根缺 ${rel} —— 先打 zip（ziptool）`); process.exit(1); }
  fs.copyFileSync(rel, path.join(stage, rel));
  const zipAbs = path.join(stage, rel);

  const versions = pub.packages?.[id]?.versions;
  if (!versions || !Object.keys(versions).length) { console.error(`❌ 已发布 listing 里没有 ${id} 可作模板`); process.exit(1); }
  const templateVer = Object.keys(versions).pop();               // 以最新一条为模板 ⇒ 字段齐全
  const entry = JSON.parse(JSON.stringify(versions[templateVer]));
  const inner = JSON.parse(execFileSync('unzip', ['-p', zipAbs, 'package.json'], { encoding: 'utf8' }));

  entry.name = inner.name;
  entry.version = inner.version;
  entry.displayName = inner.displayName;
  entry.description = inner.description;
  entry.unity = inner.unity;
  entry.hideInEditor = inner.hideInEditor;
  entry.url = `${base}/${encodeURIComponent(rel)}`;
  entry.repo = listingUrl;
  entry.vpmDependencies = inner.vpmDependencies || {};
  entry.zipSHA256 = crypto.createHash('sha256').update(fs.readFileSync(zipAbs)).digest('hex');

  if (inner.version === templateVer) console.warn(`  ⚠️ ${id} 的版本与已发布相同（${templateVer}）—— 这次测试的是"重发同版本"？`);
  out.packages[id] = { versions: { [inner.version]: entry } };
  console.log(`  ${id} @ ${inner.version}   zip=${rel}   sha=${entry.zipSHA256.slice(0, 12)}…`);
}

fs.writeFileSync(path.join(stage, 'vpm.json'), JSON.stringify(out, null, 2) + '\n');
console.log(`\n== staging listing 已生成：${path.relative(process.cwd(), path.join(stage, 'vpm.json'))} ==`);
console.log('   第三方依赖（shadercore / MA / NDMF）不在本 listing 里 —— 由已注册的官方仓库解析，与真实发布一致。');
