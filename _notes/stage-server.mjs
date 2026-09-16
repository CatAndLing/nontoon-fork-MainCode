// 把 `_notes/clean-vpm-stage/` 用本地 HTTP 伺服，供**推送前**干净安装门禁用。
//
// 为什么不用 file://：vrc-get 1.9.2 的 `repo add` 对本地路径/flie:// 都不接受
//（实测：裸路径 → "URL scheme is not allowed"；相对路径 → 时好时坏、"path not found"）。
// 而 http 一定被接受 ⇒ 本地 HTTP 是最稳的"真·listing 解析"测法。
//
// 用法：node _notes/stage-server.mjs [port]     （默认 8799；仅监听 127.0.0.1）
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const port = Number(process.argv[2] || 8799);
const root = path.resolve('_notes/clean-vpm-stage');

http.createServer((req, res) => {
  const rel = decodeURIComponent((req.url || '/').split('?')[0]).replace(/^\/+/, '');
  const file = path.join(root, rel || 'vpm.json');
  if (!path.resolve(file).startsWith(root)) { res.statusCode = 403; return res.end('forbidden'); }
  fs.readFile(file, (err, data) => {
    if (err) { res.statusCode = 404; return res.end('not found: ' + rel); }
    res.setHeader('Content-Type', file.endsWith('.json') ? 'application/json' : 'application/octet-stream');
    res.end(data);
  });
}).listen(port, '127.0.0.1', () => {
  console.log(`staging 伺服中: http://127.0.0.1:${port}/vpm.json  (root=${root})`);
});
