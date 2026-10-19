import { generateKeyPairSync, createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const manifestPath = path.join(root, 'extension/manifest.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
if (manifest.key === '__PUBLIC_KEY__') {
  const { publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  manifest.key = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
}
const hash = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
const extensionId = [...hash].map(c => String.fromCharCode(97 + parseInt(c, 16))).join('');
await writeFile(path.join(root, 'extension-id.txt'), extensionId + '\n');
const protocolPath = path.join(root, 'src/QuickShot.Core/Protocol.cs');
const protocol = await readFile(protocolPath, 'utf8');
await writeFile(protocolPath, protocol.replace(/public const string ExtensionId = "[^"]+";/, `public const string ExtensionId = "${extensionId}";`));

// Small code-native icons; no external image assets or runtime dependencies.
const crcTable = Array.from({ length: 256 }, (_, i) => { let c = i; for (let n = 0; n < 8; n++) c = (c & 1) ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
function chunk(type, data) {
  const body = Buffer.concat([Buffer.from(type), data]); let crc = 0xffffffff;
  for (const byte of body) crc = crcTable[(crc ^ byte) & 255] ^ (crc >>> 8);
  const head = Buffer.alloc(4), tail = Buffer.alloc(4); head.writeUInt32BE(data.length); tail.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
  return Buffer.concat([head, body, tail]);
}
function segmentDistance(x, y, ax, ay, bx, by) {
  const t = Math.max(0, Math.min(1, ((x - ax) * (bx - ax) + (y - ay) * (by - ay)) / ((bx - ax) ** 2 + (by - ay) ** 2)));
  return Math.hypot(x - ax - t * (bx - ax), y - ay - t * (by - ay));
}
const lines = [[9, 14, 9, 9], [9, 9, 14, 9], [23, 18, 23, 23], [23, 23, 18, 23], [12, 20, 21, 11], [15, 11, 21, 11], [21, 11, 21, 17]];
await mkdir(path.join(root, 'extension/icons'), { recursive: true });
for (const size of [16, 32, 48, 128]) {
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let alpha = 0, white = 0;
    for (let sy = 0; sy < 4; sy++) for (let sx = 0; sx < 4; sx++) {
      const u = (x + (sx + .5) / 4) / size * 32, v = (y + (sy + .5) / 4) / size * 32;
      const dx = Math.max(9 - u, 0, u - 23), dy = Math.max(9 - v, 0, v - 23);
      if (Math.hypot(dx, dy) <= 8) { alpha++; if (lines.some(line => segmentDistance(u, v, ...line) <= 1.15)) white++; }
    }
    const offset = y * (size * 4 + 1) + 1 + x * 4;
    const mix = alpha ? white / alpha : 0;
    raw[offset] = 103 + 152 * mix; raw[offset + 1] = 80 + 175 * mix; raw[offset + 2] = 230 + 25 * mix; raw[offset + 3] = alpha / 16 * 255;
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
  const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
  await writeFile(path.join(root, `extension/icons/${size}.png`), png);
}
console.log(`Extension ID: ${extensionId}`);
