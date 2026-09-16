// 发布前的机械核验（自己做，不外包）。
// 目的：挡住本仓库自己复现过的假绿链 —— "声明 0.3.11 却指向旧内容的 zip"。
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const VPM = '_VPM-nontoon-fork';
const DOCS = path.join(VPM, 'docs');
const NL = String.fromCharCode(10);
let bad = 0;
const fail = (m) => { console.log('  ❌ ' + m); bad++; };

const sha256 = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

let hasUnzip = true;
try { execFileSync('unzip', ['-v'], { stdio: 'ignore' }); } catch { hasUnzip = false; }
console.log('unzip 可用 = ' + hasUnzip);
if (!hasUnzip) { console.log('  ⛔ 没有 unzip，无法验 zip 内部内容 —— 中止'); process.exit(1); }

const vpm = JSON.parse(fs.readFileSync(path.join(DOCS, 'vpm.json'), 'utf8'));
console.log(NL + '════ vpm.json 的包与版本 ════');
const keys = Object.keys(vpm.packages || {});
console.log('  packages 键 = ' + keys.join(', '));
const EXPECT = ['com.catandling.nontoon', 'com.catandling.nontoon-converter'];
if (keys.length !== EXPECT.length || !EXPECT.every((k) => keys.includes(k))) {
  fail('packages 键不是恰好两个新身份（旧 id 可能残留）');
}
for (const k of keys) {
  if (!EXPECT.includes(k)) fail('出现意外包 id: ' + k);
}

for (const id of keys) {
  const pkg = vpm.packages[id];
  console.log(NL + '════ ' + id + ' ════');
  for (const [ver, e] of Object.entries(pkg.versions || {})) {
    const urlName = String(e.url || '').split('/').pop();
    const zipPath = path.join(DOCS, urlName);
    console.log('  ' + ver + '  url = ' + urlName);
    if (!fs.existsSync(zipPath)) { fail('zip 不存在: ' + urlName); continue; }
    const size = fs.statSync(zipPath).size;

    // ① 文件名里的版本 vs 声明版本
    const verInName = /-(\d+\.\d+\.\d+)\.zip$/.exec(urlName);
    if (!verInName) fail('文件名里没有版本号: ' + urlName);
    else if (verInName[1] !== ver) fail('声明版本 ' + ver + ' 与 url 文件名里的 ' + verInName[1] + ' 不一致');

    // ② 自己重算哈希 vs 声明
    const real = sha256(zipPath);
    const declared = String(e.zipSHA256 || '').toLowerCase();
    if (!declared) fail('没有声明 zipSHA256');
    else if (declared !== real) fail('zipSHA256 不符：声明 ' + declared.slice(0, 16) + '… 实际 ' + real.slice(0, 16) + '…');
    else console.log('    ✅ SHA256 自算一致 ' + real.slice(0, 16) + '…');

    // ③ zip 内部 package.json 的 name/version
    let inner;
    try { inner = JSON.parse(execFileSync('unzip', ['-p', zipPath, 'package.json'], { encoding: 'utf8' })); }
    catch (err) { fail('读不出 zip 内 package.json: ' + String(err.message).slice(0, 60)); continue; }
    console.log('    zip 内 name=' + inner.name + '  version=' + inner.version + '  (' + size + ' B)');
    if (inner.name !== id) fail('zip 内 name 与声明 id 不一致');
    if (inner.version !== ver) fail('zip 内 version 与声明版本不一致');

    // ④ 着色器包：内部 .scshader 的第一行（是否已是新名字）
    if (id === 'com.catandling.nontoon') {
      const list = execFileSync('unzip', ['-Z1', zipPath], { encoding: 'utf8' }).split(NL).filter((f) => f.endsWith('.scshader'));
      for (const f of list) {
        const first = execFileSync('unzip', ['-p', zipPath, f], { encoding: 'utf8' }).split(NL)[0];
        const okName = first.includes('nontoon-fork');
        console.log('    ' + (okName ? '✅' : '❌') + ' ' + f + ' -> ' + first);
        if (!okName) fail('zip 内着色器名未改名: ' + f);
      }
      if (!list.length) fail('zip 内找不到任何 .scshader');
    }
    // ⑤ 依赖声明
    if (inner.vpmDependencies) console.log('    依赖 = ' + JSON.stringify(inner.vpmDependencies));
  }
}

console.log(NL + (bad ? '⛔ 核验失败 ' + bad + ' 项 —— 不许推送' : '✅ 全部核验通过 —— 机械层面可推（但步骤 3 的安装测试仍未做）'));
process.exit(bad ? 1 : 0);
