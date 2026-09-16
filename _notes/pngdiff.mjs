// 逐像素比较两张 PNG（本机没有 PIL / ImageMagick；Unity 出的图都是非交错 8bit PNG）。
// 用法：node _notes/pngdiff.mjs <a.png> <b.png> [阈值]
// 输出：尺寸、最大单通道差、平均绝对差、超过阈值的像素数/占比、以及差异最大的几个像素位置。
// 为什么需要它：字节哈希对**编译器浮点重排**太敏感 —— 两张视觉相同的图哈希也会不同，
//   所以判断"是不是恒等变换"必须看**像素差**，不能看哈希。
import { readFileSync } from 'node:fs';
import { inflateSync } from 'node:zlib';

function decode(file) {
  const buf = readFileSync(file);
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error(file + ': 不是 PNG');
  let off = 8, w = 0, h = 0, depth = 0, ctype = 0, interlace = 0;
  const idat = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); depth = data[8]; ctype = data[9]; interlace = data[12]; }
    else if (type === 'IDAT') idat.push(data);
    else if (type === 'IEND') break;
    off += 12 + len;
  }
  if (depth !== 8 || interlace !== 0) throw new Error(file + ': 只支持 8bit 非交错');
  const ch = ctype === 6 ? 4 : ctype === 2 ? 3 : ctype === 0 ? 1 : ctype === 4 ? 2 : 0;
  if (!ch) throw new Error(file + ': 不支持的颜色类型 ' + ctype);
  const raw = inflateSync(Buffer.concat(idat));
  const stride = w * ch;
  if (w === 0 || h === 0 || raw.length !== h * (stride + 1)) throw new Error(file + ': 空图或像素数据长度不符');
  const img = Buffer.alloc(h * stride);
  const paeth = (a, b, c) => { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; };
  for (let y = 0; y < h; y++) {
    const ft = raw[y * (stride + 1)];
    if (ft > 4) throw new Error(file + ': 非法 PNG filter ' + ft);
    const src = raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride);
    const cur = img.subarray(y * stride, (y + 1) * stride);
    const prev = y > 0 ? img.subarray((y - 1) * stride, y * stride) : null;
    for (let i = 0; i < stride; i++) {
      const a = i >= ch ? cur[i - ch] : 0;
      const b = prev ? prev[i] : 0;
      const c = prev && i >= ch ? prev[i - ch] : 0;
      const x = src[i];
      cur[i] = ft === 0 ? x : ft === 1 ? (x + a) & 255 : ft === 2 ? (x + b) & 255
        : ft === 3 ? (x + ((a + b) >> 1)) & 255 : (x + paeth(a, b, c)) & 255;
    }
  }
  return { w, h, ch, img };
}

const [fa, fb, thrArg, ...extra] = process.argv.slice(2);
if (!fa || !fb || extra.length) { console.error('用法: node pngdiff.mjs <a.png> <b.png> [阈值=1]'); process.exit(2); }
const thr = Number(thrArg ?? 1);
if (!Number.isFinite(thr) || thr < 0 || thr > 255 || thrArg?.trim() === '') {
  console.error('阈值必须是 0..255 的有限数值'); process.exit(2);
}
console.log(`判据：最大 RGBA 单通道差 <= ${thr}/255 时 exit 0，超过时 exit 1；输入错误 exit 2（默认阈值 1）。`);
let A, B;
try { A = decode(fa); B = decode(fb); }
catch (e) { console.error(e.message); process.exit(2); }
console.log(`${fa}\n${fb}`);
if (A.w !== B.w || A.h !== B.h) { console.error(`尺寸不同: ${A.w}x${A.h} vs ${B.w}x${B.h}`); process.exit(2); }

let maxd = 0, sum = 0, over = 0, n = 0, worst = null;
// 灰度展开到 RGB，无 alpha 视为 255；两图分别索引，避免不同通道数错位/越界假绿。
function channel(image, pixel, k) {
  const i = pixel * image.ch;
  if (k === 3) return image.ch === 2 ? image.img[i + 1] : image.ch === 4 ? image.img[i + 3] : 255;
  return image.img[i + (image.ch <= 2 ? 0 : k)];
}
for (let y = 0; y < A.h; y++) {
  for (let x = 0; x < A.w; x++) {
    const pixel = y * A.w + x;
    let pd = 0;
    for (let k = 0; k < 4; k++) { const d = Math.abs(channel(A, pixel, k) - channel(B, pixel, k)); if (d > pd) pd = d; }
    sum += pd; n++;
    if (pd > maxd) { maxd = pd; worst = { x, y, a: channel(A, pixel, 0), b: channel(B, pixel, 0) }; }
    if (pd > thr) over++;
  }
}
console.log(`尺寸 ${A.w}x${A.h} ch=${A.ch}`);
console.log(`最大单通道差 = ${maxd}/255   平均差 = ${(sum / n).toFixed(4)}   超过 ${thr} 的像素 = ${over} / ${n} (${(100 * over / n).toFixed(3)}%)`);
if (worst) console.log(`差异最大处: (x=${worst.x}, y=${worst.y})  a=${worst.a}  b=${worst.b}`);
console.log(maxd <= 1 ? '⇒ 差异 ≤1/255：属于浮点/舍入噪声，可视作**恒等**' : (maxd <= 4 ? '⇒ 差异很小（≤4/255）：大概率只是编译器浮点重排' : '⇒ 差异明显：需要排查'));
console.log(`>>> 像素比较${maxd > thr ? '失败' : '通过'}（max=${maxd}，阈值=${thr}）`);
process.exit(maxd > thr ? 1 : 0);
