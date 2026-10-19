import { spawn, execFile } from 'node:child_process';
import { mkdir, writeFile, stat, lstat, rm } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { promisify } from 'node:util';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = path.join(root, 'artifacts');
const executable = path.resolve(process.argv[2] || path.join(artifacts, 'staging/portable/QuickShot.exe'));
const resultPath = path.resolve(process.argv[3] || path.join(artifacts, 'bridge-resources.json'));
const fixtureName = `bridge-resource-smoke-${randomUUID().replaceAll('-', '')}`;
const fixture = path.resolve(artifacts, fixtureName);
assert(resultPath.startsWith(artifacts + path.sep) && path.extname(resultPath) === '.json', 'measurement result must be a workspace artifacts JSON file');
assert.equal(path.dirname(fixture), artifacts);
assert.match(path.basename(fixture), /^bridge-resource-smoke-[a-f0-9]{32}$/);
const file = await stat(executable);
assert(file.isFile(), 'measured executable must be a file');
await mkdir(fixture);

const env = {
  ...process.env,
  QUICKSHOT_DATA_DIR: fixture,
  QUICKSHOT_INSTANCE_ID: randomUUID(),
  QUICKSHOT_NO_REGISTER: '1',
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const execute = promisify(execFile);
let app, bridge;

async function stopOwnProcess(child) {
  if (!child || !Number.isSafeInteger(child.pid) || child.exitCode !== null || child.signalCode !== null) return;
  const closed = new Promise(resolve => child.once('close', resolve));
  child.kill();
  let deadline;
  try {
    await Promise.race([closed, new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('Own test process did not exit after termination.')), 5000); })]);
  } finally { clearTimeout(deadline); }
}

try {
  app = spawn(executable, ['--background'], { env, windowsHide: true, stdio: 'ignore' });
  let appError;
  app.on('error', error => { appError = error; });
  await pause(1300);
  if (appError) throw appError;
  assert.equal(app.exitCode, null, 'isolated tray must stay running');
  assert(Number.isSafeInteger(app.pid), 'tray PID must come from this spawned process');

  bridge = spawn(executable, ['--native-host'], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let buffered = Buffer.alloc(0), bridgeError;
  const messages = [];
  bridge.on('error', error => { bridgeError = error; });
  bridge.stdin.on('error', error => { bridgeError = error; });
  bridge.stderr.on('data', () => {});
  bridge.stdout.on('data', chunk => {
    try {
      buffered = Buffer.concat([buffered, chunk]);
      while (buffered.length >= 4) {
        const length = buffered.readUInt32LE(0);
        assert(length > 0 && length <= 65536, 'bridge response length must stay within the protocol limit');
        if (buffered.length < length + 4) break;
        messages.push(JSON.parse(buffered.subarray(4, length + 4).toString('utf8')));
        buffered = buffered.subarray(length + 4);
      }
    } catch (error) { bridgeError = error; }
  });
  const hello = Buffer.from(JSON.stringify({ type: 'hello', version: 2, intent: 'connect', profileId: randomUUID(), bindings: [] }));
  const header = Buffer.alloc(4);
  header.writeUInt32LE(hello.length);
  bridge.stdin.write(Buffer.concat([header, hello]));
  for (let i = 0; i < 120 && !messages.length && !bridgeError && bridge.exitCode === null; i++) await pause(100);
  if (bridgeError) throw bridgeError;
  assert.equal(messages[0]?.type, 'helloAck', 'isolated bridge must complete the real native-stdio and named-pipe handshake');
  assert.equal(messages[0]?.version, 2);
  assert(Number.isSafeInteger(bridge.pid), 'bridge PID must come from this spawned process');

  // Observe only the two exact child PIDs. Do not force GC, alter working sets,
  // touch the clipboard, register a native host, or use any real Edge profile.
  const command = `
$ErrorActionPreference = 'Stop'
$tray = [Diagnostics.Process]::GetProcessById(${app.pid})
$bridge = [Diagnostics.Process]::GetProcessById(${bridge.pid})
$samples = @()
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    foreach ($index in 0..4) {
        $tray.Refresh(); $bridge.Refresh()
        if ($tray.HasExited -or $bridge.HasExited) { throw 'An isolated test process exited during measurement.' }
        $samples += [pscustomobject]@{
            elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            tray = [pscustomobject]@{ workingSetBytes = $tray.WorkingSet64; privateBytes = $tray.PrivateMemorySize64; cpuMilliseconds = $tray.TotalProcessorTime.TotalMilliseconds; handles = $tray.HandleCount; threads = $tray.Threads.Count }
            bridge = [pscustomobject]@{ workingSetBytes = $bridge.WorkingSet64; privateBytes = $bridge.PrivateMemorySize64; cpuMilliseconds = $bridge.TotalProcessorTime.TotalMilliseconds; handles = $bridge.HandleCount; threads = $bridge.Threads.Count }
        }
        if ($index -lt 4) { Start-Sleep -Milliseconds 2000 }
    }
    $samples | ConvertTo-Json -Depth 5 -Compress
} finally { $tray.Dispose(); $bridge.Dispose() }
`;
  const powershell = path.join(process.env.WINDIR || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
  const measured = await execute(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], { windowsHide: true, timeout: 20000, maxBuffer: 1024 * 1024 });
  const samples = JSON.parse(measured.stdout.trim().replace(/^\uFEFF/, ''));
  assert.equal(samples.length, 5);
  const summarize = role => ({
    meanWorkingSetBytes: Math.round(samples.reduce((sum, sample) => sum + sample[role].workingSetBytes, 0) / samples.length),
    meanPrivateBytes: Math.round(samples.reduce((sum, sample) => sum + sample[role].privateBytes, 0) / samples.length),
    idleCpuMilliseconds: samples.at(-1)[role].cpuMilliseconds - samples[0][role].cpuMilliseconds,
    finalHandles: samples.at(-1)[role].handles,
    finalThreads: samples.at(-1)[role].threads,
  });
  const result = {
    app: executable,
    executableBytes: file.size,
    handshake: 'helloAck v2',
    observationSeconds: samples.at(-1).elapsedSeconds - samples[0].elapsedSeconds,
    registrationDisabled: true,
    realProfileUsed: false,
    clipboardUsed: false,
    forcedGcOrWorkingSetTrim: false,
    tray: summarize('tray'),
    bridge: summarize('bridge'),
    samples,
  };
  await mkdir(path.dirname(resultPath), { recursive: true });
  await writeFile(resultPath, JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 2));
} finally {
  // These references identify only children spawned above; never kill by name.
  await stopOwnProcess(bridge);
  await stopOwnProcess(app);
  const resolved = path.resolve(fixture);
  assert.equal(path.dirname(resolved), artifacts, 'cleanup must stay in the workspace artifacts directory');
  assert.equal(path.basename(resolved), fixtureName);
  assert.match(fixtureName, /^bridge-resource-smoke-[a-f0-9]{32}$/);
  const directory = await lstat(resolved);
  assert(directory.isDirectory() && !directory.isSymbolicLink(), 'cleanup refuses a linked fixture');
  await rm(resolved, { recursive: true });
}
