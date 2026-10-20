import { spawn } from 'node:child_process';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/native-hang', String(Date.now())); await mkdir(output, { recursive: true });
const ready = path.join(output, 'window.txt'), result = path.join(output, 'result.json');
const runtime = path.join(root, '.tools/dotnet/dotnet.exe');
const helper = path.join(root, 'tests/QuickShot.App.Tests/bin/Release/net10.0-windows/QuickShot.App.Tests.dll');
const env = { ...process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet') };
const blocked = spawn(runtime, [helper, '--unresponsive', ready], { windowsHide: true, stdio: 'ignore', env });
let probe;
try {
  let window;
  for (let i = 0; i < 100; i++) {
    try { window = (await readFile(ready, 'utf8')).trim(); break; } catch { await new Promise(resolve => setTimeout(resolve, 30)); }
  }
  assert.ok(window, 'owned blocked window starts');
  const started = Date.now();
  probe = spawn(runtime, [helper, '--activation-bound', window, result], { windowsHide: true, stdio: 'ignore', env });
  const finished = new Promise(resolve => probe.once('exit', resolve));
  const exit = await Promise.race([finished, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Activation blocked on another UI thread')), 2000); timer.unref(); })]);
  assert.equal(exit, 0); const recorded = JSON.parse(await readFile(result, 'utf8'));
  assert.ok(recorded.elapsedMilliseconds < 800);
  console.log(`PASS: activation of an unresponsive owned window returns in ${recorded.elapsedMilliseconds} ms; total helper time ${Date.now() - started} ms.`);
  console.log('Evidence: ' + output);
} finally { probe?.kill(); blocked.kill(); }
