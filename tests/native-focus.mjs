// Tests only owned Edge windows, with a separate browser profile and local page. Never reads/writes clipboard.
import { spawn } from 'node:child_process';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/native-focus'); await mkdir(output, { recursive: true });
const profile = path.join(output, 'profile-' + process.pid);
const controllerPath = path.join(output, 'controller-' + process.pid); await mkdir(controllerPath, { recursive: true });
const manifest = JSON.parse(await readFile(path.join(root, 'extension/manifest.json'), 'utf8'));
await writeFile(path.join(controllerPath, 'manifest.json'), JSON.stringify({ manifest_version: 3, name: 'QuickShot focus test', version: '1.0', key: manifest.key, permissions: ['tabs', 'scripting'], host_permissions: ['http://127.0.0.1/*'] }));
await writeFile(path.join(controllerPath, 'control.html'), '<!doctype html><title>Owned focus test controller</title>');
await writeFile(path.join(controllerPath, 'content.js'), await readFile(path.join(root, 'extension/content.js')));
const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const html = await readFile(path.join(root, 'tests/fixtures/chat.html'));
const server = createServer((_request, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const edgePath = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const edge = spawn(edgePath, ['--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--load-extension=' + controllerPath, '--disable-extensions-except=' + controllerPath, '--remote-debugging-port=0', '--start-minimized', '--window-size=900,700', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let stderr = ''; edge.stderr.on('data', data => { stderr += data; });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const sockets = [];
async function connect(url) {
  const socket = new WebSocket(url); sockets.push(socket);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  let next = 1; const pending = new Map();
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data), waiter = pending.get(message.id); if (!waiter) return;
    pending.delete(message.id); clearTimeout(waiter.timer);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error))); else waiter.resolve(message.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = next++, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 10000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
  return { send, eval: async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  } };
}
let cdp, helper, appLaunch;
async function boundedExit(process, milliseconds, name) {
  if (process.exitCode !== null) return process.exitCode;
  let timer;
  try {
    return await Promise.race([
      new Promise((resolve, reject) => { process.once('error', reject); process.once('exit', resolve); }),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(name + ' exit timed out')), milliseconds); })
    ]);
  } finally { clearTimeout(timer); }
}
try {
  let port;
  for (let i = 0; i < 150; i++) {
    try { port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; } catch { await delay(100); }
  }
  if (!port) throw new Error('Owned Edge failed to start: ' + stderr.slice(-1200));
  const target = await (await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('http://127.0.0.1:' + server.address().port + '/')}`, { method: 'PUT' })).json();
  cdp = await connect(target.webSocketDebuggerUrl);
  for (let i = 0; i < 100 && !await cdp.eval("document.readyState==='complete'&&!!document.querySelector('#chat')"); i++) await delay(50);
  const controllerTarget = await (await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('chrome-extension://' + extensionId + '/control.html')}`, { method: 'PUT' })).json();
  const controller = await connect(controllerTarget.webSocketDebuggerUrl);
  for (let i = 0; i < 100 && !await controller.eval("!!globalThis.chrome?.runtime?.id"); i++) await delay(50);
  let fixtureTab = await controller.eval(`chrome.tabs.query({}).then(tabs=>tabs.find(tab=>tab.url?.startsWith('http://127.0.0.1:')))`);
  await controller.eval(`chrome.tabs.update(${fixtureTab.id},{active:true})`);
  await controller.eval(`chrome.scripting.executeScript({target:{tabId:${fixtureTab.id}},files:['content.js']})`);
  const content = message => controller.eval(`chrome.tabs.sendMessage(${fixtureTab.id},${JSON.stringify({quickshot:true,...message})})`);
  // Intercept Ctrl+V before its default action: verify real SendInput without reading or changing the clipboard.
  await cdp.eval(`globalThis.pasteProbe=[];document.addEventListener('keydown',event=>{if(event.keyCode===86||event.code==='KeyV'||event.key.toLowerCase()==='v'){event.preventDefault();event.stopImmediatePropagation();pasteProbe.push({trusted:event.isTrusted,target:event.target.id,ctrl:event.ctrlKey,code:event.code});}},true);`);
  // Model a page the user has visited before switching away; CDP-created pages otherwise begin with no view focus.
  await cdp.send('Page.bringToFront');
  for (const mode of ['minimized', 'background', 'app']) {
    if (mode === 'app') {
      await controller.eval(`chrome.tabs.remove(${fixtureTab.id})`);
      const appUrl = 'http://127.0.0.1:' + server.address().port + '/app-chat';
      appLaunch = spawn(edgePath, ['--user-data-dir=' + profile, '--app=' + appUrl, '--no-first-run'], { windowsHide: true, stdio: 'ignore' });
      await boundedExit(appLaunch, 10000, 'app launcher'); appLaunch = null;
      let appTarget;
      for (let i = 0; i < 100; i++) {
        const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        appTarget = targets.find(item => item.type === 'page' && item.url === appUrl);
        if (appTarget) break;
        await delay(100);
      }
      assert.ok(appTarget, 'Edge --app opens an owned app page');
      cdp = await connect(appTarget.webSocketDebuggerUrl);
      for (let i = 0; i < 100 && !await cdp.eval("document.readyState==='complete'&&!!document.querySelector('#chat')"); i++) await delay(50);
      fixtureTab = await controller.eval(`chrome.tabs.query({}).then(tabs=>tabs.find(tab=>tab.url===${JSON.stringify(appUrl)}))`);
      const appWindow = await controller.eval(`chrome.windows.get(${fixtureTab.windowId})`);
      assert.ok(['normal','popup','app'].includes(appWindow.type), 'app window is an eligible Edge window type');
      console.log('App window type: ' + appWindow.type);
      await controller.eval(`chrome.scripting.executeScript({target:{tabId:${fixtureTab.id}},files:['content.js']})`);
      await cdp.eval(`globalThis.pasteProbe=[];document.addEventListener('keydown',event=>{if(event.keyCode===86||event.code==='KeyV'||event.key.toLowerCase()==='v'){event.preventDefault();event.stopImmediatePropagation();pasteProbe.push({trusted:event.isTrusted,target:event.target.id,ctrl:event.ctrlKey,code:event.code});}},true);`);
      await cdp.send('Page.bringToFront');
      await controller.send('Page.bringToFront');
    }
    const window = await cdp.send('Browser.getWindowForTarget');
    const id = { minimized: '11111111111111111111111111111111', background: '22222222222222222222222222222222', app: '33333333333333333333333333333333' }[mode];
    const token = '[QuickShot:' + id + ']';
    if (mode === 'background') {
      const other = await cdp.send('Target.createTarget', { url: 'about:blank', newWindow: true });
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const otherPage = await connect(targets.find(item => item.id === other.targetId).webSocketDebuggerUrl);
      await otherPage.send('Page.bringToFront');
    }
    assert.equal((await content({type:'prepare',requestId:id,locator:{selector:'#chat',tag:'textarea'}})).ok, true);
    if (mode !== 'minimized') {
      // Chat sites hydrate after opening and overwrite document.title. The native
      // window marker must survive, including in a real Edge --app window.
      await cdp.eval(`document.title='动态聊天 · 最新标题'`);
      await delay(150);
      assert.equal(await cdp.eval(`document.title.startsWith(${JSON.stringify(token + ' ')})`), true, mode + ': title marker survives a site rewrite');
    }
    if (mode === 'minimized') await cdp.send('Browser.setWindowBounds', { windowId: window.windowId, bounds: { windowState: 'minimized' } });
    await delay(200);
    const resultPath = path.join(output, mode + '-' + process.pid + '.json');
    helper = spawn(path.join(root, '.tools/dotnet/dotnet.exe'), [path.join(root, 'tests/QuickShot.App.Tests/bin/Release/net10.0-windows/QuickShot.App.Tests.dll'), token, resultPath], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet') } });
    let helperError = ''; helper.stderr.on('data', chunk => { helperError += chunk; });
    const completed = new Promise((resolve, reject) => { helper.on('error', reject); helper.on('exit', resolve); });
    let result;
    for (let i = 0; i < 200; i++) { try { result = JSON.parse(await readFile(resultPath, 'utf8')); break; } catch { await delay(25); } }
    if (!result) throw new Error('Focus helper failed: ' + helperError);
    // Use the same public Edge focus APIs as the extension's verifyFocus phase.
    await controller.eval(`(async()=>{const w=await chrome.windows.get(${fixtureTab.windowId});await chrome.windows.update(${fixtureTab.windowId},w.state==='minimized'?{focused:true,state:'normal'}:{focused:true});await chrome.tabs.update(${fixtureTab.id},{active:true});})()`);
    await writeFile(resultPath + '.activate', 'confirm exact window after browser focus');
    let activation;
    for (let i = 0; i < 100; i++) { try { activation = JSON.parse(await readFile(resultPath + '.activation-result', 'utf8')); break; } catch { await delay(25); } }
    assert.ok(activation?.activated && activation.expected, mode + ': exact Edge window is foreground after native/browser activation');
    let verified = await content({type:'verifyFocus',requestId:id});
    if (verified.needsContentFocus) {
      await writeFile(resultPath + '.focus', 'focus content');
      let focusResult;
      for (let i = 0; i < 200; i++) { try { focusResult = JSON.parse(await readFile(resultPath + '.focus-result', 'utf8')); break; } catch { await delay(25); } }
      assert.ok(focusResult?.contentFocused && focusResult.expected, mode + ': unique page document receives UI Automation focus');
      verified = await content({type:'verifyFocus',requestId:id});
    }
    if (!verified.ok) console.log('Focus diagnostics: ' + JSON.stringify({ native: result, page: await cdp.eval(`({focused:document.hasFocus(),visibility:document.visibilityState,active:document.activeElement?.id})`) }));
    assert.equal(verified.ok, true, mode + ': document and editor focus verify');
    await cdp.eval('pasteProbe=[]');
    await writeFile(resultPath + '.paste', 'probe');
    for (let i = 0; i < 100 && !await cdp.eval('pasteProbe.length'); i++) await delay(25);
    const pasteResult = JSON.parse(await readFile(resultPath + '.paste-result', 'utf8'));
    assert.equal(pasteResult.dispatched, true, mode + ': Windows accepts Ctrl+V');
    assert.deepEqual(await cdp.eval('pasteProbe'), [{ trusted: true, target: 'chat', ctrl: true, code: 'KeyV' }], mode + ': real Ctrl+V reaches only the configured editor');
    assert.equal(await cdp.eval(`document.querySelector('#chat').value`), '保留这段草稿');
    assert.equal(await cdp.eval(`document.querySelector('#count').textContent`), '已发送 0 次');
    await content({type:'cancel',requestId:id});
    if (mode !== 'minimized') assert.equal(await cdp.eval('document.title'), '动态聊天 · 最新标题', mode + ': cleanup preserves the latest website title');
    await writeFile(resultPath + '.release', 'done');
    assert.equal(await Promise.race([completed, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('native helper did not exit')), 5000); timer.unref(); })]), 0, mode + ': native activation returned failure'); helper = null;
    console.log('PASS: ' + mode + ' Edge activation, editor focus, trusted Ctrl+V, draft preservation, no send.');
  }
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: true, modes: ['minimized', 'background', 'app'], dynamicTitles: true, clipboardTouched: false }, null, 2));
} finally {
  if (cdp) { try { await cdp.send('Browser.close'); } catch { /* Already closed. */ } }
  for (const socket of sockets) socket.close();
  edge.kill(); edge.stderr.destroy(); edge.unref(); server.close();
  helper?.kill();
  appLaunch?.kill();
}
