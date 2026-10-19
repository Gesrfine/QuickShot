// Real, isolated Edge sole-connection-page lifecycle regression. The copied
// extension first reuses the only blank startup tab for a test control page,
// waits for its real extension context, then navigates that same tab to connect.html;
// the native handshake starts with exactly one connection page and one window.
// This does not assert persistence of a preinstalled extension or direct external
// command-line navigation to it. Native messages are stubbed; no registered host,
// clipboard, or user's tabs are used.
import { spawn } from 'node:child_process';
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { cdpJson } from './cdp-http.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const baseline = process.argv.includes('--regression-baseline');
const navigationBaseline = process.argv.includes('--navigation-regression-baseline');
const lateNavigation = process.argv.includes('--late-navigation') || navigationBaseline;
assert.ok(!(baseline && lateNavigation), 'close-last-tab and late-navigation baselines run separately');
const output = path.join(root, 'artifacts/cold-start', (baseline ? 'baseline-' : navigationBaseline ? 'navigation-baseline-' : lateNavigation ? 'late-navigation-' : 'fixed-') + randomUUID());
const extensionPath = path.join(output, 'extension'), profile = path.join(output, 'profile');
await mkdir(output, { recursive: true });
const events = [], checks = [];
const html = await readFile(path.join(root, 'tests/fixtures/chat.html'));
const server = createServer(async (request, response) => {
  if (request.url === '/events' && request.method === 'POST') {
    let body = '';
    for await (const part of request) body += part;
    try { events.push({ ...JSON.parse(body), receivedAt: Date.now() }); }
    catch { response.writeHead(400); response.end(); return; }
    response.writeHead(204); response.end(); return;
  }
  response.setHeader('Content-Type', 'text/html; charset=utf-8');
  if (request.url === '/late-navigation') {
    // Commit the new same-origin document while deliberately leaving its body
    // loading. The test injects verifyFocus during this actual browser transition.
    response.flushHeaders();
    setTimeout(() => response.end(html), 400);
    return;
  }
  response.end(html);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = 'http://127.0.0.1:' + server.address().port;
const defaultUrl = origin + '/configured-default';
const bindingId = randomUUID(), requestId = randomUUID().replaceAll('-', '');
const binding = { id: bindingId, key: 'C', name: '隔离冷启动测试', origin, url: defaultUrl, locator: { selector: '#chat', tag: 'textarea' } };
await cp(path.join(root, 'extension'), extensionPath, { recursive: true });
const manifestPath = path.join(extensionPath, 'manifest.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
// Even if the stub breaks, this copy cannot call a real Native Messaging host.
manifest.permissions = manifest.permissions.filter(permission => permission !== 'nativeMessaging');
manifest.permissions.push('tabs');
manifest.host_permissions = ['http://127.0.0.1/*'];
await writeFile(manifestPath, JSON.stringify(manifest));
const sharedPath = path.join(extensionPath, 'shared.mjs');
await writeFile(sharedPath, (await readFile(sharedPath, 'utf8')).replace("'com.clipboardsender.edge'", "'com.quickshot.test.coldstart'"));
const workerPath = path.join(extensionPath, 'background.js');
let worker = await readFile(workerPath, 'utf8');
const storageRead = "const saved = await chrome.storage.local.get(['bindings', 'profileId', 'schemaVersion']);";
assert.ok(worker.includes(storageRead), 'isolated binding seed hook exists');
worker = worker.replace(storageRead, 'const saved = ' + JSON.stringify({ bindings: [binding], profileId: randomUUID(), schemaVersion: 2 }) + ';');
if (baseline) {
  const hook = "if (message.type === 'helloAck') {";
  assert.ok(worker.includes(hook), 'helloAck regression hook exists');
  // Reproduce the previous implementation in this copy only: close the temporary
  // connection page before announcing ready or creating a target page.
  worker = worker.replace(hook, hook + `
    const oldConnectionPages = (await chrome.tabs.query({})).filter(tab => tab.url === chrome.runtime.getURL('connect.html'));
    for (const tab of oldConnectionPages) await chrome.tabs.remove(tab.id);
  `);
}
if (navigationBaseline) {
  const hook = 'async function handleNative(message) {';
  assert.ok(worker.includes(hook), 'navigation regression hook exists');
  // Restore the previous early failure in this test copy only. It must fail on
  // the real loading tab, rather than accepting a simulated error as evidence.
  worker = worker.replace(hook, hook + `
    await initialized;
    if (message.type === 'verifyFocus' && delivery?.requestId === message.requestId) {
      const oldTab = await chrome.tabs.get(delivery.tabId);
      if (oldTab.status !== 'complete' || oldTab.pendingUrl)
        throw new Error('目标页面正在导航，本次未粘贴。');
    }
  `);
}
await writeFile(workerPath, "import './cold-start-boot.mjs';\n" + worker);
await writeFile(path.join(extensionPath, 'cold-start-boot.mjs'), `
const eventUrl = ${JSON.stringify(origin + '/events')};
const listeners = new Set(), disconnectListeners = new Set();
let portClosed = false;
const event = set => ({ addListener: fn => set.add(fn), removeListener: fn => set.delete(fn), hasListener: fn => set.has(fn) });
async function record(kind, message = null) {
  const tabs = await chrome.tabs.query({});
  const windows = await chrome.windows.getAll();
  await fetch(eventUrl, { method: 'POST', body: JSON.stringify({ kind, message, tabs, windows, at: Date.now() }) });
}
async function incoming(message) {
  if (portClosed) return;
  await record('native-in', message);
  for (const listener of listeners) listener(message);
}
globalThis.__quickshotColdStartIncoming = incoming;
chrome.runtime.connectNative = () => {
  void record('native-connected');
  return {
    onMessage: event(listeners), onDisconnect: event(disconnectListeners),
    postMessage(message) {
      void record('native-out', message).then(() => {
        if (message.type === 'hello') setTimeout(() => incoming({type:'helloAck', version:3}), 350);
        if (message.type === 'ready') setTimeout(() => incoming({type:'prepare', requestId:${JSON.stringify(requestId)}, bindingId:${JSON.stringify(bindingId)}, titleToken:${JSON.stringify('[QuickShot:' + requestId + ']')}}), 100);
      });
    },
    disconnect() { portClosed = true; void record('native-disconnected'); for (const listener of disconnectListeners) listener(); }
  };
};
chrome.tabs.onRemoved.addListener(tabId => { void record('tab-removed', {tabId}); });
void record('boot');
`);
// This test-only extension page has no application script or native handshake.
// Reusing the startup tab avoids creating a second tab that could hide a close-last-tab bug.
await writeFile(path.join(extensionPath, 'cold-start-control.html'), '<!doctype html><meta charset="utf-8"><title>QuickShot cold-start controller</title><body>Isolated test controller</body>');

const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const connectUrl = 'chrome-extension://' + extensionId + '/connect.html';
const controlUrl = 'chrome-extension://' + extensionId + '/cold-start-control.html';
const edgePath = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
// A fresh temporary profile must not import the Windows account or show a
// first-sign-in dialog. The user's profile and account preferences are untouched.
await mkdir(path.join(profile, 'Default'), { recursive: true });
await writeFile(path.join(profile, 'Default/Preferences'), JSON.stringify({ signin: { allowed: false }, sync: { requested: false } }));
const edgeArgs = [
  '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-background-mode',
  '--disable-features=msImplicitSignin,msEdgeSignin,msEdgeSync',
  '--remote-debugging-port=0', '--start-minimized', '--window-size=900,700', '--user-data-dir=' + profile,
  '--load-extension=' + extensionPath, '--disable-extensions-except=' + extensionPath
];
let socket;
const sockets = new Set();
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const edge = spawn(edgePath, [...edgeArgs, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let stderr = '', launchError, page, debuggingPort;
edge.stderr.on('data', part => { stderr += part; });
edge.on('error', error => { launchError = error; });
const check = (value, name) => { assert.ok(value, name); checks.push(name); console.log('PASS: ' + name); };
async function connect(url) {
  const connection = new WebSocket(url);
  socket = connection; sockets.add(connection);
  connection.addEventListener('close', () => sockets.delete(connection), { once: true });
  await new Promise((resolve, reject) => { connection.addEventListener('open', resolve, { once: true }); connection.addEventListener('error', reject, { once: true }); });
  let next = 1; const pending = new Map();
  connection.addEventListener('message', event => {
    const message = JSON.parse(event.data), waiter = pending.get(message.id); if (!waiter) return;
    pending.delete(message.id); clearTimeout(waiter.timer);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error))); else waiter.resolve(message.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = next++, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 3000);
    pending.set(id, { resolve, reject, timer }); connection.send(JSON.stringify({ id, method, params }));
  });
  return { send, eval: async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  } };
}
let failure, baselineReproduced = false, navigationBaselineReproduced = false, diagnostics, controlReadiness, navigationLoading;
try {
  let workerTarget, startupPage;
  const startupDeadline = Date.now() + 15000;
  while (Date.now() < startupDeadline) {
    if (launchError) throw launchError;
    if (edge.exitCode !== null) throw new Error('Owned Edge exited before its isolated test extension loaded.');
    try {
      debuggingPort = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
      const targets = await cdpJson('http://127.0.0.1:' + debuggingPort + '/json/list', { timeout: 1000 });
      workerTarget = targets.find(target => target.type === 'service_worker' && target.url === 'chrome-extension://' + extensionId + '/background.js');
      const pages = targets.filter(target => target.type === 'page');
      startupPage = pages.length === 1 && pages[0].url === 'about:blank' ? pages[0] : null;
      if (workerTarget && startupPage) break;
    } catch { /* The owned debugging endpoint is still starting. */ }
    await delay(100);
  }
  check(!!workerTarget, 'isolated real Edge loads the test extension');
  check(!!startupPage, 'owned browser starts with exactly one blank page');
  const controller = await connect(startupPage.webSocketDebuggerUrl);
  await controller.send('Page.enable');
  const navigation = await controller.send('Page.navigate', { url: controlUrl });
  assert.ok(!navigation.errorText, 'test control page navigation succeeds: ' + (navigation.errorText || ''));
  // A CDP target can exist before its extension API world is initialized. Probe a
  // real extension page rather than evaluating chrome.tabs in an early worker world.
  const controlDeadline = Date.now() + 10000;
  while (Date.now() < controlDeadline) {
    if (launchError) throw launchError;
    if (edge.exitCode !== null) throw new Error('Owned Edge exited before the extension controller became ready.');
    try {
      controlReadiness = await controller.eval("({url:location.href,readyState:document.readyState,extensionId:globalThis.chrome?.runtime?.id,tabsAvailable:typeof globalThis.chrome?.tabs?.query==='function'})");
      if (controlReadiness.url === controlUrl && controlReadiness.readyState === 'complete'
          && controlReadiness.extensionId === extensionId && controlReadiness.tabsAvailable) break;
    } catch (error) {
      // Only context replacement during the navigation above is transient. API or
      // application errors still fail the test instead of being retried away.
      if (!/Execution context was destroyed|Cannot find context with specified id|Cannot find default execution context/.test(error.message)) throw error;
    }
    await delay(50);
  }
  check(controlReadiness?.url === controlUrl && controlReadiness.readyState === 'complete'
    && controlReadiness.extensionId === extensionId && controlReadiness.tabsAvailable,
  'test controller waits for its loaded page and expected chrome.runtime.id');
  check(await controller.eval(`(async()=>{const tabs=await chrome.tabs.query({});const windows=await chrome.windows.getAll();if(tabs.length!==1||windows.length!==1||tabs[0].url!==${JSON.stringify(controlUrl)})return false;await chrome.tabs.update(tabs[0].id,{url:${JSON.stringify(connectUrl)}});return true;})()`),
    'extension navigates its only control tab in the only window to the connection page');
  socket?.close(); socket = null;
  const deadline = Date.now() + 20000;
  let first, prepared;
  while (Date.now() < deadline) {
    if (launchError) throw launchError;
    first = events.find(event => event.kind === 'native-out' && event.message?.type === 'hello');
    if (first && events.some(event => event.kind === 'tab-removed' && event.message.tabId === first.tabs[0]?.id))
      throw new Error('The only connection tab was closed before the default target was established.');
    prepared = events.find(event => event.kind === 'native-out' && event.message?.type === 'focusPrepared');
    if (prepared) break;
    const error = events.find(event => event.kind === 'native-out' && event.message?.type === 'deliveryError');
    if (error) throw new Error('Delivery failed: ' + error.message.message);
    if (edge.exitCode !== null) throw new Error('Owned Edge exited before focusPrepared.');
    await delay(50);
  }
  check(!!first, 'native handshake occurs');
  check(first.tabs.length === 1 && first.tabs[0].url === connectUrl && first.windows.length === 1, 'initial native handshake has exactly one connection tab and one Edge window');
  check(!!prepared, 'prepare reaches focusPrepared within 20 seconds');
  check(edge.exitCode === null, 'Edge remains running after helloAck and prepare');
  check(prepared.tabs.length === 1 && prepared.windows.length === 1, 'cold start does not create an extra tab or window');
  check(prepared.tabs[0].id === first.tabs[0].id && prepared.tabs[0].windowId === first.tabs[0].windowId, 'connection tab and window are reused');
  check(prepared.tabs[0].url === defaultUrl, 'reused tab loads the configured default URL');
  check(prepared.tabs[0].title.startsWith('[QuickShot:' + requestId + '] '), 'stable page publishes the expected activation marker');
  check(events.some(event => event.kind === 'native-out' && event.message?.type === 'ready'), 'native ready handshake is sent');
  check(!events.some(event => event.kind === 'native-disconnected' || event.message?.type === 'deliveryError'), 'connection remains intact with no delivery error');
  if (baseline) throw new Error('Baseline unexpectedly passed; the old close-last-tab bug was not reproduced.');
  debuggingPort = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
  const targets = await cdpJson('http://127.0.0.1:' + debuggingPort + '/json/list');
  const target = targets.find(item => item.type === 'page' && item.url === defaultUrl);
  check(!!target, 'default page is still present in the actual browser');
  page = await connect(target.webSocketDebuggerUrl);
  check(await page.eval("document.querySelector('#chat').value === '保留这段草稿'"), 'existing draft is preserved');
  check(await page.eval("document.querySelector('#count').textContent === '已发送 0 次'"), 'no message is sent');
  if (!lateNavigation) {
    check(!events.some(event => ['pasteNow', 'deliveryResult'].includes(event.message?.type)), 'test stops before requesting any clipboard paste');
  } else {
    const workerTarget = targets.find(item => item.type === 'service_worker' && item.url === 'chrome-extension://' + extensionId + '/background.js');
    check(!!workerTarget, 'real extension worker remains available for late-navigation verification');
    const workerDebug = await connect(workerTarget.webSocketDebuggerUrl);
    check(await workerDebug.eval("typeof globalThis.__quickshotColdStartIncoming === 'function'"), 'only the copied test worker exposes its native-input stub');
    await page.send('Page.bringToFront');
    const lateUrl = origin + '/late-navigation';
    const navigation = await page.send('Page.navigate', { url: lateUrl });
    assert.ok(!navigation.errorText, 'late same-origin navigation succeeds: ' + (navigation.errorText || ''));
    navigationLoading = await workerDebug.eval(`(async()=>{
      const tab=await chrome.tabs.get(${JSON.stringify(prepared.tabs[0].id)});
      const snapshot={id:tab.id,url:tab.url,pendingUrl:tab.pendingUrl,status:tab.status};
      if(tab.status==='loading'||tab.pendingUrl)
        await globalThis.__quickshotColdStartIncoming({type:'verifyFocus',requestId:${JSON.stringify(requestId)}});
      return snapshot;
    })()`);
    check(navigationLoading?.status === 'loading' || !!navigationLoading?.pendingUrl, 'verifyFocus is injected while the actual target tab is loading or has a pending URL');
    const loadingVerification = events.find(event => event.kind === 'native-in' && event.message?.type === 'verifyFocus');
    check(loadingVerification?.tabs.some(tab => tab.id === prepared.tabs[0].id && (tab.status === 'loading' || tab.pendingUrl)),
      'the native-input stub records the real tab still loading when verification is delivered');
    const verificationDeadline = Date.now() + 10000;
    let handledContentFocus = 0;
    while (Date.now() < verificationDeadline) {
      const error = events.find(event => event.kind === 'native-out' && event.message?.type === 'deliveryError');
      if (error) throw new Error('Late-navigation delivery failed: ' + error.message.message);
      if (events.some(event => event.kind === 'native-out' && event.message?.type === 'pasteNow')) break;
      if (edge.exitCode !== null) throw new Error('Owned Edge exited during late-navigation verification.');
      const contentFocusRequests = events.filter(event => event.kind === 'native-out' && event.message?.type === 'contentFocusNeeded').length;
      if (contentFocusRequests > handledContentFocus) {
        handledContentFocus = contentFocusRequests;
        // Restore only this owned page's WebContents keyboard focus, then ask the
        // real worker to repeat its complete validation. No paste is simulated.
        await page.send('Page.bringToFront');
        await page.eval("document.querySelector('#chat')?.focus()");
        await workerDebug.eval(`globalThis.__quickshotColdStartIncoming({type:'verifyFocus',requestId:${JSON.stringify(requestId)}})`);
      }
      await delay(50);
    }
    await delay(150);
    const authorizations = events.filter(event => event.kind === 'native-out' && event.message?.type === 'pasteNow');
    check(authorizations.length === 1 && authorizations[0].message.requestId === requestId, 'late navigation produces exactly one final paste authorization');
    check(authorizations[0].tabs.length === 1 && authorizations[0].tabs[0].id === prepared.tabs[0].id
      && authorizations[0].tabs[0].url === lateUrl && authorizations[0].tabs[0].status === 'complete'
      && authorizations[0].tabs[0].title.startsWith('[QuickShot:' + requestId + '] '),
    'replacement document is loaded and prepared with its own activation marker');
    check(await page.eval(`location.href===${JSON.stringify(lateUrl)}&&document.title.startsWith(${JSON.stringify('[QuickShot:' + requestId + '] ')})`), 'actual replacement document contains the refreshed preparation marker');
    check(await page.eval("document.querySelector('#chat').value === '保留这段草稿'"), 'late navigation preserves the replacement page draft');
    check(await page.eval("document.querySelector('#count').textContent === '已发送 0 次'"), 'late navigation sends no message');
    check(!events.some(event => event.message?.type === 'deliveryError'), 'late same-origin navigation no longer reports an early delivery error');
    check(!events.some(event => event.message?.type === 'pasteDispatched' || event.message?.type === 'deliveryResult'), 'test never dispatches a paste or accesses the clipboard');
    if (navigationBaseline) throw new Error('Navigation baseline unexpectedly passed; the old loading-tab failure was not reproduced.');
  }
} catch (error) {
  failure = error;
  const first = events.find(event => event.kind === 'native-out' && event.message?.type === 'hello');
  baselineReproduced = baseline && first?.tabs.length === 1 && first.tabs[0].url === connectUrl && /only connection tab was closed|Owned Edge exited before focusPrepared/.test(error.message);
  navigationBaselineReproduced = navigationBaseline && (navigationLoading?.status === 'loading' || !!navigationLoading?.pendingUrl)
    && events.some(event => event.kind === 'native-out' && event.message?.type === 'deliveryError' && event.message.message === '目标页面正在导航，本次未粘贴。')
    && !events.some(event => event.message?.type === 'pasteNow') && /Late-navigation delivery failed/.test(error.message);
  if (baselineReproduced) console.log('PASS: isolated baseline reproduces closing the only connection tab: ' + error.message);
  else if (navigationBaselineReproduced) console.log('PASS: isolated baseline reproduces premature failure during actual same-origin navigation: ' + error.message);
  else console.error(error);
} finally {
  if (!page && edge.exitCode === null) {
    try {
      debuggingPort ??= Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
      if (failure && !baselineReproduced && !navigationBaselineReproduced) {
        const targets = await cdpJson('http://127.0.0.1:' + debuggingPort + '/json/list', { timeout: 1500 });
        diagnostics = { targets };
        const ownPage = targets.find(target => target.type === 'page' && target.url === connectUrl) || targets.find(target => target.type === 'page');
        if (ownPage) {
          const probe = await connect(ownPage.webSocketDebuggerUrl);
          diagnostics.page = await probe.eval("({url:location.href,title:document.title,readyState:document.readyState,extension:globalThis.chrome?.runtime?.id})");
          socket?.close();
        }
      }
      const version = await cdpJson('http://127.0.0.1:' + debuggingPort + '/json/version', { timeout: 1500 });
      page = await connect(version.webSocketDebuggerUrl);
    } catch { /* The regression can close the browser before debugging is available. */ }
  }
  if (page) { try { await page.send('Browser.close'); } catch { /* Already closed. */ } }
  for (const connection of sockets) connection.close();
  if (edge.exitCode === null) {
    await Promise.race([new Promise(resolve => edge.once('exit', resolve)), delay(2000)]);
    if (edge.exitCode === null) edge.kill();
  }
  edge.stderr.destroy(); edge.unref();
  await new Promise(resolve => server.close(resolve));
  await writeFile(path.join(output, 'events.json'), JSON.stringify(events, null, 2));
  await writeFile(path.join(output, 'result.json'), JSON.stringify({ passed: baseline ? baselineReproduced : navigationBaseline ? navigationBaselineReproduced : !failure, baseline, baselineReproduced, navigationBaseline, navigationBaselineReproduced, lateNavigation, navigationLoading, testKind: lateNavigation ? 'sole-connection-page-late-navigation' : 'sole-connection-page-lifecycle', externalColdLaunchVerified: false, nativeTransport: 'stub', checks, controlReadiness, error: failure?.message, clipboardTouched: false, diagnostics, stderr: stderr.slice(-1500) }, null, 2));
  console.log('Evidence: ' + output);
}
if (failure && !baselineReproduced && !navigationBaselineReproduced) process.exitCode = 1;
