import { spawn } from 'node:child_process';
import { cp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { cdpJson } from './cdp-http.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const baseline = process.argv.includes('--regression-baseline');
const output = path.join(root, 'artifacts/window-placement', (baseline ? 'baseline-' : 'fixed-') + randomUUID());
const extension = path.join(output, 'extension'), profile = path.join(output, 'profile');
const helper = path.join(root, 'tests/QuickShot.App.Tests/bin/Release/net10.0-windows/QuickShot.App.Tests.exe');
const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const connectUrl = 'chrome-extension://' + extensionId + '/connect.html';
const requestId = randomUUID().replaceAll('-', ''), bindingId = randomUUID();
const checks = [], events = [], measurements = [], launches = [], sockets = new Set();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser, page, worker, port, mode = 'seed', failure, baselineReproduced = false;
const check = (condition, label) => { assert.ok(condition, label); checks.push(label); console.log('PASS: ' + label); };
const server = createServer(async (request, response) => {
  if (request.url === '/events' && request.method === 'POST') {
    let body = ''; for await (const chunk of request) body += chunk;
    const event = { ...JSON.parse(body), mode }; events.push(event);
    response.writeHead(204); response.end();
    if (event.message?.type === 'openDefault') {
      void launch(defaultUrl).catch(error => { failure = error; });
    }
    return;
  }
  response.setHeader('Content-Type', 'text/html; charset=utf-8');
  response.end(await readFile(path.join(root, 'tests/fixtures/chat.html')));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = 'http://127.0.0.1:' + server.address().port, defaultUrl = origin + '/configured-default';
await cp(path.join(root, 'extension'), extension, { recursive: true });
const manifest = JSON.parse(await readFile(path.join(extension, 'manifest.json'), 'utf8'));
manifest.permissions = manifest.permissions.filter(permission => permission !== 'nativeMessaging');
manifest.permissions.push('tabs'); manifest.host_permissions = ['http://127.0.0.1/*'];
await writeFile(path.join(extension, 'manifest.json'), JSON.stringify(manifest));
let source = await readFile(path.join(extension, 'background.js'), 'utf8');
const storageHook = "const saved = await chrome.storage.local.get(['bindings', 'profileId', 'schemaVersion']);";
assert.ok(source.includes(storageHook));
source = source.replace(storageHook, 'const saved = ' + JSON.stringify({ profileId: randomUUID(), schemaVersion: 2,
  bindings: [{ id: bindingId, name: '窗口恢复回归', key: 'C', origin, url: defaultUrl, locator: { selector: '#chat', tag: 'textarea' } }] }) + ';');
if (baseline) {
  const start = source.indexOf("  if (requestId) {\n    nativeSend({ type: 'openDefault', requestId });");
  const end = source.indexOf('  const opened = await deliveryCall(chrome.windows.create(', start);
  assert.ok(start >= 0 && end > start, 'old-window-creation regression hook exists');
  source = source.slice(0, start) + source.slice(end);
}
await writeFile(path.join(extension, 'background.js'), "import './placement-boot.mjs';\n" + source + '\nglobalThis.__placementConnect = connectNative;');
await writeFile(path.join(extension, 'placement-boot.mjs'), `
const listeners = new Set(), disconnected = new Set();
const event = set => ({addListener:fn=>set.add(fn),removeListener:fn=>set.delete(fn)});
async function incoming(message) { for (const listener of listeners) listener(message); }
globalThis.__placementIncoming = incoming;
chrome.runtime.connectNative = () => ({
  onMessage:event(listeners),onDisconnect:event(disconnected),disconnect(){},
  postMessage(message) {
    void (async()=>{
      const windows=await chrome.windows.getAll({populate:true});
      await fetch(${JSON.stringify(origin + '/events')},{method:'POST',body:JSON.stringify({message,windows})});
      if(message.type==='hello')await incoming({type:'helloAck',version:3});
      if(message.type==='ready')await incoming({type:'prepare',requestId:${JSON.stringify(requestId)},bindingId:${JSON.stringify(bindingId)}});
    })();
  }
});
`);
await mkdir(path.join(profile, 'Default'), { recursive: true });
await writeFile(path.join(profile, 'Default/Preferences'), JSON.stringify({ signin: { allowed: false }, sync: { requested: false } }));

async function launch(url, backgroundOnly = false) {
  const id = randomUUID(), config = path.join(output, id + '.json'), result = path.join(output, id + '.result.json');
  await writeFile(config, JSON.stringify({ url, profile, extension, output: result, backgroundOnly }));
  const process = spawn(helper, ['--edge-launch', config], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...globalThis.process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet'), DOTNET_ROOT_X64: path.join(root, '.tools/dotnet') } });
  let errors = ''; process.stderr.on('data', part => { errors += part; });
  const code = await new Promise((resolve, reject) => { process.once('error', reject); process.once('exit', resolve); });
  assert.equal(code, 0, 'production launch helper succeeds: ' + errors);
  const record = JSON.parse(await readFile(result, 'utf8')); launches.push({ mode, ...record });
  assert.ok(!record.arguments.some(argument => /^--(?:window-size|window-position|start-maximized|start-minimized)/.test(argument)), 'launcher supplies no placement overrides');
}
async function connect(url) {
  const ws = new WebSocket(url); sockets.add(ws);
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
  ws.addEventListener('close', () => sockets.delete(ws), { once: true });
  let next = 0; const pending = new Map();
  ws.addEventListener('message', event => {
    const message = JSON.parse(event.data), waiter = pending.get(message.id); if (!waiter) return;
    pending.delete(message.id); clearTimeout(waiter.timer);
    message.error ? waiter.reject(new Error(JSON.stringify(message.error))) : waiter.resolve(message.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++next, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 4000);
    pending.set(id, { resolve, reject, timer }); ws.send(JSON.stringify({ id, method, params }));
  });
  return { ws, send, eval: async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  } };
}
async function targets() { return cdpJson('http://127.0.0.1:' + port + '/json/list', { timeout: 1000 }); }
async function attach() {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    try {
      port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
      const version = await cdpJson('http://127.0.0.1:' + port + '/json/version', { timeout: 1000 });
      browser = await connect(version.webSocketDebuggerUrl);
      const list = await targets(), target = list.find(item => item.type === 'page');
      if (target) page = await connect(target.webSocketDebuggerUrl);
      return;
    } catch { await delay(100); }
  }
  throw new Error('owned Edge debugging endpoint did not start');
}
async function attachWorker() {
  const deadline = Date.now() + 10000;
  while (Date.now() < deadline) {
    const target = (await targets()).find(item => item.type === 'service_worker' && item.url === 'chrome-extension://' + extensionId + '/background.js');
    if (target) {
      worker = await connect(target.webSocketDebuggerUrl); await worker.send('Runtime.enable'); await worker.send('Runtime.runIfWaitingForDebugger');
      while (Date.now() < deadline) {
        if (await worker.eval("typeof globalThis.__placementConnect==='function'")) return;
        await delay(50);
      }
    }
    await delay(100);
  }
  throw new Error('owned placement worker did not initialize');
}
async function close() {
  const currentPort = port;
  if (browser) { try { await browser.send('Browser.close'); } catch { /* Browser closes its own socket. */ } }
  for (const ws of sockets) ws.close(); sockets.clear(); browser = page = worker = undefined;
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    try { await cdpJson('http://127.0.0.1:' + currentPort + '/json/version', { timeout: 300 }); }
    catch { await delay(400); return; }
    await delay(100);
  }
  throw new Error('owned Edge did not exit cleanly');
}
async function start(url, backgroundOnly = false) {
  await rm(path.join(profile, 'DevToolsActivePort'), { force: true });
  await launch(url, backgroundOnly); await attach();
}
try {
  for (const state of ['normal', 'maximized']) {
    mode = 'seed-' + state;
    await start('about:blank');
    const window = await page.send('Browser.getWindowForTarget');
    await browser.send('Browser.setWindowBounds', { windowId: window.windowId, bounds: { windowState: 'normal' } });
    await browser.send('Browser.setWindowBounds', { windowId: window.windowId, bounds: { left: 540, top: 110, width: 960, height: 650 } });
    if (state === 'maximized') await browser.send('Browser.setWindowBounds', { windowId: window.windowId, bounds: { windowState: state } });
    await delay(300);
    const expected = (await page.send('Browser.getWindowForTarget')).bounds;
    await close();
    for (const kind of ['cold', 'background']) {
      mode = kind + '-' + state; events.length = 0;
      await start(connectUrl, kind === 'background');
      if (kind === 'background') {
        await attachWorker();
        check((await worker.eval('chrome.windows.getAll()')).length === 0, mode + ': background Edge has no browser window');
        await worker.eval('globalThis.__placementConnect()');
      } else {
        const initial = (await page.send('Browser.getWindowForTarget')).bounds;
        assert.deepEqual(initial, expected, mode + ': production launcher retains persisted placement before connection');
        await attachWorker();
        await worker.eval(`(async()=>{const tabs=await chrome.tabs.query({});await chrome.tabs.update(tabs[0].id,{url:${JSON.stringify(connectUrl)}});})()`);
      }
      let prepared;
      const deadline = Date.now() + 20000;
      while (Date.now() < deadline) {
        if (failure) throw failure;
        const error = events.find(event => event.message.type === 'deliveryError');
        if (error) throw new Error(error.message.message);
        prepared = events.find(event => event.message.type === 'focusPrepared');
        if (prepared) break;
        await delay(50);
      }
      check(!!prepared, mode + ': production extension prepares the receiving page');
      const list = await targets(), target = list.find(item => item.type === 'page' && item.url === defaultUrl);
      check(!!target && list.filter(item => item.type === 'page').length === 1, mode + ': exactly one configured default page is open');
      page = await connect(target.webSocketDebuggerUrl);
      const actual = (await page.send('Browser.getWindowForTarget')).bounds;
      measurements.push({ mode, expected, actual });
      if (baseline && kind === 'background' && JSON.stringify(actual) !== JSON.stringify(expected)) {
        baselineReproduced = true;
        check(actual.windowState === 'normal' && actual.left < expected.left, 'old extension creation reproduces a default-sized window on the left');
        break;
      }
      assert.deepEqual(actual, expected, mode + ': saved size, position and show state survive delivery preparation');
      checks.push(mode + ': saved size, position and show state survive delivery preparation');
      check(events.filter(event => event.message.type === 'openDefault').length === (kind === 'background' ? 1 : 0), mode + ': native launch happens once only when needed');
      check(await page.eval("document.querySelector('#chat').value==='保留这段草稿'&&document.querySelector('#count').textContent==='已发送 0 次'"), mode + ': draft is preserved and no message is sent');
      check(!events.some(event => ['pasteNow', 'pasteDispatched'].includes(event.message.type)), mode + ': test stops before paste authorization');
      await close();
    }
    if (baselineReproduced) break;
  }
  if (baseline) assert.ok(baselineReproduced, 'old creation must reproduce the placement loss');
} catch (error) { failure = error; console.error(error); }
finally {
  try { await close(); } catch (error) { failure ??= error; }
  await new Promise(resolve => server.close(resolve));
  await writeFile(path.join(output, 'result.json'), JSON.stringify({ passed: !failure, baseline, baselineReproduced, checks, measurements, launches,
    error: failure?.stack, nativeTransport: 'stub; native open delegated to production ProcessStartInfo', externalExtensionPageColdNavigationVerified: false, clipboardTouched: false, events }, null, 2));
  console.log('Evidence: ' + output);
}
if (failure) process.exitCode = 1;
