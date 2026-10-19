import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const exe = process.env.QUICKSHOT_APP_PATH || path.join(root, 'artifacts/staging/QuickShot-2.0.1/QuickShot.exe');
const dataDirectory = path.join(root, 'artifacts/native-smoke', randomUUID()); await mkdir(dataDirectory, { recursive: true });
await writeFile(path.join(dataDirectory, 'settings.json'), JSON.stringify({ ShowNotifications: false }));
const env = { ...process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet'), DOTNET_ROOT_X64: path.join(root, '.tools/dotnet'), QUICKSHOT_DATA_DIR: dataDirectory, QUICKSHOT_INSTANCE_ID: randomUUID(), QUICKSHOT_NO_REGISTER: '1' };
let app, bridge;
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
function send(message) {
  const payload = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(4); header.writeUInt32LE(payload.length); bridge.stdin.write(Buffer.concat([header, payload]));
}
function host(version = 3) {
  bridge = spawn(exe, ['--native-host'], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let buffer = Buffer.alloc(0);
  const messages = [];
  bridge.stdout.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4 && buffer.length >= buffer.readUInt32LE(0) + 4) {
      const size = buffer.readUInt32LE(0); messages.push(JSON.parse(buffer.subarray(4, size + 4).toString('utf8'))); buffer = buffer.subarray(size + 4);
    }
  });
  send({ type: 'hello', version, intent: 'connect', profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', bindings: [] });
  return messages;
}
try {
  let messages = host();
  for (let i = 0; i < 100 && !messages.length; i++) await wait(100);
  assert.equal(messages[0]?.type, 'appStopped', 'passive native connection does not start the tray');
  bridge.stdin.end(); await wait(200);
  messages = host(2);
  for (let i = 0; i < 100 && !messages.length; i++) await wait(100);
  assert.equal(messages[0]?.type, 'rejected', 'old extensions cannot trigger an implicit startup');
  bridge.stdin.end(); await wait(200);
  app = spawn(exe, ['--background'], { env, windowsHide: true, stdio: 'ignore' });
  await wait(1300);
  assert.equal(app.exitCode, null, 'isolated tray process starts');
  messages = host();
  for (let i = 0; i < 100 && !messages.length; i++) await wait(100);
  assert.equal(messages[0]?.type, 'helloAck', 'published WinExe uses real native stdio and current-user named pipe');
  assert.equal(messages[0]?.version, 3);
  assert.equal(messages[0]?.captureShortcut, undefined, '2.0 contains no capture-tool configuration');
  send({ type: 'ready' });
  await wait(150);
  assert.ok(!messages.some(message => message.type === 'captureShortcut'));
  console.log('PASS: small framework-dependent app starts, passive connection stays stopped, legacy protocol is rejected, real native pipe handshake succeeds.');
  bridge.stdin.end();
  await wait(300);
} finally {
  bridge?.kill(); app?.kill();
}
