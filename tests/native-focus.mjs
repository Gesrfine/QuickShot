// Tests only owned Edge windows, with a separate browser profile and local page. Never reads/writes clipboard.
import { spawn } from 'node:child_process';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/native-focus'); await mkdir(output, { recursive: true });
const runId = process.pid + '-' + Date.now();
const profile = path.join(output, 'profile-' + runId);
const controllerPath = path.join(output, 'controller-' + runId); await mkdir(controllerPath, { recursive: true });
const manifest = JSON.parse(await readFile(path.join(root, 'extension/manifest.json'), 'utf8'));
await writeFile(path.join(controllerPath, 'manifest.json'), JSON.stringify({ manifest_version: 3, name: 'QuickShot focus test', version: '1.0', key: manifest.key, permissions: ['tabs', 'scripting'], host_permissions: ['http://127.0.0.1/*'] }));
await writeFile(path.join(controllerPath, 'control.html'), '<!doctype html><title>Owned focus test controller</title>');
await writeFile(path.join(controllerPath, 'content.js'), await readFile(path.join(root, 'extension/content.js')));
const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const html = await readFile(path.join(root, 'tests/fixtures/chat.html'));
const server = createServer((_request, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const edgePath = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const edge = spawn(edgePath, ['--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--load-extension=' + controllerPath, '--disable-extensions-except=' + controllerPath, '--remote-debugging-port=0', '--window-size=900,700', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
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
let cdp, helper, appLaunch, cover;
const allModes = ['minimized', 'background', 'maximized', 'fullscreen', 'topmost', 'app'];
const modes = process.env.QUICKSHOT_FOCUS_MODES?.split(',') || allModes;
assert.ok(modes.length && modes.every(mode => allModes.includes(mode)), 'valid requested focus modes');
await writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: false, runId, modes, status: 'running' }, null, 2));
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
  const browserInfo = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  const browserControl = await connect(browserInfo.webSocketDebuggerUrl);
  const browserProcess = (await browserControl.send('SystemInfo.getProcessInfo')).processInfo.find(process => process.type === 'browser')?.id;
  assert.ok(browserProcess, 'owned browser process is identified through its private debugging session');
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
  for (const mode of modes) {
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
    const id = String(modes.indexOf(mode) + 1).repeat(32);
    const token = '[QuickShot:' + id + ']';
    if (mode === 'background') {
      const other = await cdp.send('Target.createTarget', { url: 'about:blank', newWindow: true });
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const otherPage = await connect(targets.find(item => item.id === other.targetId).webSocketDebuggerUrl);
      const otherWindow = await otherPage.send('Browser.getWindowForTarget');
      await otherPage.send('Browser.setWindowBounds', { windowId: otherWindow.windowId, bounds: { windowState: 'normal' } });
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
    const resultPath = path.join(output, mode + '-' + runId + '.json');
    const helperDll = path.join(root, 'tests/QuickShot.App.Tests/bin/Release/net10.0-windows/QuickShot.App.Tests.dll');
    const helperEnvironment = { ...process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet') };
    const covering = ['maximized', 'fullscreen', 'topmost'].includes(mode);
    let inputProcess = browserProcess;
    if (covering) {
      await cdp.send('Page.bringToFront');
      cover = spawn(path.join(root, '.tools/dotnet/dotnet.exe'), [helperDll, '--cover', mode, resultPath + '.cover', String(browserProcess)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: helperEnvironment });
      let ready;
      for (let i = 0; i < 100; i++) { try { ready = JSON.parse(await readFile(resultPath + '.cover', 'utf8')); if (ready.foreground) break; } catch {} await delay(50); }
      assert.ok(ready?.foreground && ready.process === cover.pid, mode + ': a separate owned application covers Edge');
      inputProcess = cover.pid;
    } else {
      const other = await cdp.send('Target.createTarget', { url: 'about:blank', newWindow: true });
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const otherPage = await connect(targets.find(item => item.id === other.targetId).webSocketDebuggerUrl);
      const otherWindow = await otherPage.send('Browser.getWindowForTarget');
      await otherPage.send('Browser.setWindowBounds', { windowId: otherWindow.windowId, bounds: { windowState: 'normal' } });
      await otherPage.send('Page.bringToFront');
    }
    helper = spawn(path.join(root, '.tools/dotnet/dotnet.exe'), [helperDll, token, resultPath, String(inputProcess)], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: helperEnvironment });
    let helperError = ''; helper.stderr.on('data', chunk => { helperError += chunk; });
    const completed = new Promise((resolve, reject) => { helper.on('error', reject); helper.on('exit', resolve); });
    let result;
    for (let i = 0; i < 200; i++) { try { result = JSON.parse(await readFile(resultPath, 'utf8')); break; } catch { await delay(25); } }
    if (!result) throw new Error('Focus helper failed: ' + helperError);
    assert.ok(result.activated && result.expected, mode + ': native activation succeeds before browser focus APIs');
    assert.ok(result.unobscured, mode + ': Edge is visibly above covering windows');
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
    if (mode === 'topmost') {
      await writeFile(resultPath + '.cover.activate', 'return focus to the owned fixture');
      let returned;
      for (let i = 0; i < 60; i++) { try { returned = JSON.parse(await readFile(resultPath + '.cover', 'utf8')); if (returned.foreground) break; } catch {} await delay(25); }
      assert.ok(returned?.foreground, 'topmost: fixture regains foreground');
      await writeFile(resultPath + '.restore-check', 'check temporary topmost cleanup');
      let restored;
      for (let i = 0; i < 100; i++) { try { restored = JSON.parse(await readFile(resultPath + '.restore-result', 'utf8')); break; } catch {} await delay(25); }
      assert.ok(restored?.restored && restored.hookReleased, 'topmost: leaving Edge restores its original layer and releases the event hook');
    }
    await writeFile(resultPath + '.release', 'done');
    assert.equal(await Promise.race([completed, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('native helper did not exit')), 5000); timer.unref(); })]), 0, mode + ': native activation returned failure'); helper = null;
    if (cover) { await writeFile(resultPath + '.cover.release', 'done'); await boundedExit(cover, 5000, 'cover fixture'); cover = null; }
    console.log('PASS: ' + mode + ' Edge activation, editor focus, trusted Ctrl+V, draft preservation, no send.');
  }
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: true, runId, modes, dynamicTitles: true, clipboardTouched: false, systemHotkeyForeground: true }, null, 2));
} catch (error) {
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ passed: false, runId, modes, interrupted: error.message.includes('Refusing test input'), error: error.message, clipboardTouched: false }, null, 2));
  throw error;
} finally {
  if (cdp) { try { await cdp.send('Browser.close'); } catch { /* Already closed. */ } }
  for (const socket of sockets) socket.close();
  edge.kill(); edge.stderr.destroy(); edge.unref(); server.close();
  helper?.kill();
  appLaunch?.kill();
  cover?.kill();
}
