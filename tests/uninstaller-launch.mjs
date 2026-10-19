// Exercise the installed uninstallers from their own directory. Fixtures and registry keys are test-owned.
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { copyFile, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

assert.equal(process.platform, 'win32', 'This regression test requires Windows.');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = path.join(root, 'artifacts');
const release = process.env.QUICKSHOT_UNINSTALL_SOURCE || path.join(artifacts, 'staging/portable');
const id = randomUUID();
const testRoot = path.join(artifacts, 'uninstaller-launch-' + id);
const registryRoot = 'HKCU:\\Software\\QuickShot\\Tests\\' + id;
const registryHelper = path.join(testRoot, 'registry-fixture.ps1');
const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const commandProcessor = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'cmd.exe');
const taskkill = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'taskkill.exe');
const env = { ...process.env, QUICKSHOT_TEST_REGISTRY_ROOT: registryRoot };

async function exists(file) {
  try { await stat(file); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; }
}

function assertOwnedDirectory(directory) {
  const absolute = path.resolve(directory);
  assert.equal(path.dirname(absolute).toLowerCase(), artifacts.toLowerCase(), 'cleanup must stay directly inside workspace artifacts');
  assert.equal(path.basename(absolute), 'uninstaller-launch-' + id, 'cleanup must target this exact test GUID');
  assert.equal(absolute.toLowerCase(), testRoot.toLowerCase(), 'cleanup must not accept another directory');
  return absolute;
}

function run(executable, args, { cwd = testRoot, fixture, input, timeout = 30000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, {
      cwd, env: { ...env, QUICKSHOT_UNINSTALL_FIXTURE: fixture || '' }, windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    const chunks = [];
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      // Only this test's spawned process tree can be stopped; no process-name matching is used here.
      if (child.pid) {
        const killer = spawn(taskkill, ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
        killer.on('error', () => child.kill());
      } else child.kill();
    }, timeout);
    child.stdout.on('data', chunk => chunks.push(chunk));
    child.stderr.on('data', chunk => chunks.push(chunk));
    child.stdin.on('error', error => { if (error.code !== 'EPIPE' && error.code !== 'ERR_STREAM_DESTROYED') reject(error); });
    // A newline is buffered immediately (well within five seconds), so the batch pause needs no UI input.
    child.stdin.end(input || '');
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('close', (code, signal) => {
      clearTimeout(timer);
      const bytes = Buffer.concat(chunks);
      const output = bytes.toString('utf8');
      if (timedOut) reject(new Error('Timed out running owned uninstall fixture: ' + output));
      else resolve({ code, signal, bytes, output });
    });
  });
}

async function registry(mode, fixture) {
  const result = await run(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', registryHelper, '-Mode', mode], { fixture });
  assert.equal(result.code, 0, 'isolated registry ' + mode + ' failed:\n' + result.output);
}

async function makeInstalledFixture(name) {
  const directory = path.join(testRoot, name);
  await mkdir(path.join(directory, 'app'), { recursive: true });
  await Promise.all(['Uninstall.cmd', 'Uninstall.ps1'].map(file => copyFile(path.join(release, file), path.join(directory, file))));
  await writeFile(path.join(directory, 'quickshot.install.json'), JSON.stringify({ application: 'QuickShot', installDirectory: directory, version: 'test' }));
  // This is deliberately not executable. Its unique path cannot match any real QuickShot process.
  await writeFile(path.join(directory, 'app', 'QuickShot.exe'), 'Owned uninstaller test placeholder; never launch.');
  await writeFile(path.join(directory, 'native-host.json'), JSON.stringify({ name: 'com.quickshot.edge', path: path.join(directory, 'app', 'QuickShot.exe'), type: 'stdio' }));
  await registry('prepare', directory);
  return directory;
}

function assertNoMissingBatchFile(bytes) {
  const text = [bytes.toString('utf8')];
  // cmd's localized errors use the OEM code page; PowerShell's own output is UTF-8.
  for (const encoding of ['gb18030', 'utf-16le']) {
    try { text.push(new TextDecoder(encoding).decode(bytes)); } catch { /* UTF-8 checks remain available. */ }
  }
  assert.ok(!text.some(value => /batch file (?:cannot be|could not be|was not) found|(?:找不到|无法找到).{0,12}批处理文件/i.test(value)),
    'cmd tried to read its batch file after deleting it:\n' + text.join('\n'));
}

let helperWritten = false;
let testRootCreated = false;
const failures = [];
try {
  await Promise.all(['Uninstall.cmd', 'Uninstall.ps1'].map(file => readFile(path.join(release, file))));
  await mkdir(assertOwnedDirectory(testRoot), { recursive: false });
  testRootCreated = true;
  // This script lives outside either installation and only accepts our dedicated GUID namespace.
  await writeFile(registryHelper, String.raw`param([ValidateSet('prepare','assertRemoved','cleanup')][string]$Mode)
$ErrorActionPreference = 'Stop'
$testKey = $env:QUICKSHOT_TEST_REGISTRY_ROOT
if ($testKey -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-fA-F0-9-]{36}$') { throw 'Unsafe test registry namespace.' }
$nativeKey = $testKey + '\NativeHost'
$runKey = $testKey + '\Run'
if ($Mode -eq 'prepare') {
    if (-not $env:QUICKSHOT_UNINSTALL_FIXTURE) { throw 'Missing test fixture.' }
    New-Item -Path $nativeKey -Force | Out-Null
    Set-Item -LiteralPath $nativeKey -Value (Join-Path $env:QUICKSHOT_UNINSTALL_FIXTURE 'native-host.json')
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $runKey -Name QuickShot -Value ('"' + (Join-Path $env:QUICKSHOT_UNINSTALL_FIXTURE 'app\QuickShot.exe') + '" --background')
}
elseif ($Mode -eq 'assertRemoved') {
    if (Test-Path -LiteralPath $nativeKey) { throw 'Native host registration was not removed.' }
    if ((Test-Path -LiteralPath $runKey) -and $null -ne (Get-Item -LiteralPath $runKey).GetValue('QuickShot')) { throw 'Matching startup registration was not removed.' }
}
elseif (Test-Path -LiteralPath $testKey) {
    # The exact GUID namespace was checked above; never remove a parent registry key.
    Remove-Item -LiteralPath $testKey -Recurse -Force
}
`, 'utf8');
  helperWritten = true;

  const cmdDirectory = await makeInstalledFixture('CMD 快投 (自删)! installation');
  // The command argument has no spaces or shell metacharacters. The difficult path is passed as cwd,
  // so Node/cmd quote handling cannot accidentally bypass the installed batch file's own path logic.
  const batch = await run(commandProcessor, ['/d', '/c', 'Uninstall.cmd'], { cwd: cmdDirectory, input: '\r\n' });
  assert.equal(batch.code, 0, 'installed Uninstall.cmd must return 0:\n' + batch.output);
  assert.equal(await exists(cmdDirectory), false, 'batch launch must remove its current directory, its script and placeholder exe');
  assertNoMissingBatchFile(batch.bytes);
  await registry('assertRemoved');
  console.log('PASS: installed Uninstall.cmd self-deletes from a cwd containing spaces, Chinese, parentheses and !, without a missing-batch-file error.');

  const psDirectory = await makeInstalledFixture('PowerShell 快投 (自删)! installation');
  const direct = await run(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(psDirectory, 'Uninstall.ps1')], { cwd: psDirectory });
  assert.equal(direct.code, 0, 'installed Windows PowerShell 5.1 uninstaller must return 0:\n' + direct.output);
  assert.equal(await exists(psDirectory), false, 'direct PowerShell launch must remove its current directory, script and placeholder exe');
  await registry('assertRemoved');
  console.log('PASS: installed Uninstall.ps1 self-deletes when Windows PowerShell 5.1 starts inside the installation.');

  const interactiveDirectory = await makeInstalledFixture('Interactive CMD 快投 (自删)! installation');
  const alive = 'QUICKSHOT_INTERACTIVE_SURVIVED_' + randomUUID().replaceAll('-', '');
  const interactiveInput = ['Uninstall.cmd', '', 'echo ' + alive, 'exit 0', ''].join('\r\n');
  const interactive = await run(commandProcessor, ['/d', '/q'], { cwd: interactiveDirectory, input: interactiveInput });
  assert.equal(interactive.code, 0, 'interactive cmd must remain usable and reach its final exit command:\n' + interactive.output);
  assert.equal(await exists(interactiveDirectory), false, 'interactive launch must remove the installed directory');
  // A real echo result is either a standalone marker or follows cmd's prompt. The echoed input
  // "echo MARKER" cannot satisfy this expression, even if command echo is unexpectedly enabled.
  const aliveLine = new RegExp('(?:^|>)\\s*' + alive + '\\s*$');
  assert.ok(interactive.output.split(/\r?\n/).some(line => aliveLine.test(line)),
    'the existing interactive shell must execute echo after uninstall; input command echo is insufficient:\n' + interactive.output);
  assertNoMissingBatchFile(interactive.bytes);
  await registry('assertRemoved');
  console.log('PASS: an existing interactive cmd executes a unique echo after uninstall instead of being closed.');

  const invalidDirectory = path.join(testRoot, 'Invalid 快投 (不删除)! installation');
  await mkdir(invalidDirectory);
  await Promise.all(['Uninstall.cmd', 'Uninstall.ps1'].map(file => copyFile(path.join(release, file), path.join(invalidDirectory, file))));
  await writeFile(path.join(invalidDirectory, 'keep-my-file.txt'), 'Unmarked directories must never be deleted.');
  const invalid = await run(commandProcessor, ['/d', '/c', 'Uninstall.cmd'], { cwd: invalidDirectory, input: '\r\n' });
  assert.ok(Number.isInteger(invalid.code) && invalid.code !== 0, 'an unmarked installation must return a failure code:\n' + invalid.output);
  assert.equal(await exists(invalidDirectory), true, 'an unmarked installation directory must be kept');
  assert.equal(await readFile(path.join(invalidDirectory, 'keep-my-file.txt'), 'utf8'), 'Unmarked directories must never be deleted.');
  assert.ok(!invalid.output.includes('QuickShot removed'), 'a rejected uninstall must not claim success:\n' + invalid.output);
  await registry('assertRemoved');
  console.log('PASS: an unmarked installation returns nonzero, preserves its files and does not report success.');

  const delayedDirectory = await makeInstalledFixture('Delayed expansion CMD 快投 (自删)! installation');
  const delayed = await run(commandProcessor, ['/d', '/v:on', '/c', 'Uninstall.cmd'], { cwd: delayedDirectory, input: '\r\n' });
  assert.equal(delayed.code, 0, 'an incoming /v:on shell must preserve the ! in the script path after leaving the old batch context:\n' + delayed.output);
  assert.equal(await exists(delayedDirectory), false, 'delayed-expansion launch must remove the installed directory');
  assertNoMissingBatchFile(delayed.bytes);
  await registry('assertRemoved');
  console.log('PASS: incoming delayed expansion does not alter the ! in the installed script path.');
} catch (error) {
  failures.push(error);
} finally {
  if (helperWritten) {
    try { await registry('cleanup'); } catch (error) { failures.push(error); }
  }
  try { if (testRootCreated) await rm(assertOwnedDirectory(testRoot), { recursive: true, force: true, maxRetries: 4, retryDelay: 250 }); }
  catch (error) { failures.push(error); }
}
if (failures.length) throw new AggregateError(failures, 'Uninstaller launch regression failed.');
