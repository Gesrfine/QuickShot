// Microsoft's Blob API supports explicit x-ms-range even when a proxy strips HTTP Range.
import { spawn } from 'node:child_process';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, stat, writeFile } from 'node:fs/promises';
import { pipeline } from 'node:stream/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const size = 300755826, chunkSize = 4 * 1024 * 1024;
const expectedHash = 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd';
const url = 'https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip';
const directory = path.join(root, '.tools/sdk-parts-10.0.402'); await mkdir(directory, { recursive: true });
const pieces = Array.from({ length: Math.ceil(size / chunkSize) }, (_, index) => ({ index, start: index * chunkSize, end: Math.min(size, (index + 1) * chunkSize) - 1, file: path.join(directory, String(index).padStart(3, '0')) }));
async function correctSize(file, length) { try { return (await stat(file)).size === length; } catch { return false; } }
// An explicitly supplied partial ZIP can seed complete chunks; the final SHA512 still verifies every byte.
const seed = process.argv[2];
if (seed) {
  const length = (await stat(seed)).size;
  for (const piece of pieces) if (piece.end < length && !await correctSize(piece.file, piece.end - piece.start + 1))
    await pipeline(createReadStream(seed, { start: piece.start, end: piece.end }), createWriteStream(piece.file));
}
let next = 0, completed = 0;
async function worker() {
  while (next < pieces.length) {
    const piece = pieces[next++];
    if (!await correctSize(piece.file, piece.end - piece.start + 1)) {
      await new Promise((resolve, reject) => {
        const child = spawn('curl.exe', ['--fail', '--silent', '--show-error', '--retry', '3', '--max-time', '240', '--header', 'x-ms-version: 2023-11-03', '--header', `x-ms-range: bytes=${piece.start}-${piece.end}`, '--output', piece.file, url], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
        let error = ''; child.stderr.on('data', data => { error += data; }); child.on('error', reject);
        child.on('close', code => code === 0 ? resolve() : reject(new Error(`Chunk ${piece.index}: ${error}`)));
      });
      if (!await correctSize(piece.file, piece.end - piece.start + 1)) throw new Error('Invalid partial response: ' + piece.index);
    }
    completed++; if (completed % 8 === 0 || completed === pieces.length) console.log(`SDK chunks: ${completed}/${pieces.length}`);
  }
}
await Promise.all(Array.from({ length: 24 }, worker));
const archive = path.join(root, '.tools/dotnet-sdk-10.0.402.zip'); await writeFile(archive, Buffer.alloc(0));
for (const piece of pieces) await pipeline(createReadStream(piece.file), createWriteStream(archive, { flags: 'a' }));
const hash = createHash('sha512'); for await (const data of createReadStream(archive)) hash.update(data);
if (hash.digest('hex') !== expectedHash) throw new Error('SDK SHA512 mismatch.');
console.log('SDK SHA512 verified: ' + archive);
