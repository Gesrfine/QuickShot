import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { cdpJson } from './cdp-http.mjs';
import { restoredTab } from '../extension/shared.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/doubao', String(Date.now()));
const profile = path.join(output, 'profile'), extension = path.join(output, 'controller');
await mkdir(extension, { recursive: true });
const manifest = JSON.parse(await readFile(path.join(root, 'extension/manifest.json'), 'utf8'));
await writeFile(path.join(extension, 'manifest.json'), JSON.stringify({ manifest_version: 3, name: 'QuickShot isolated Doubao test', version: '1.0', key: manifest.key, permissions: ['tabs', 'scripting'], host_permissions: ['https://www.doubao.com/*'] }));
await writeFile(path.join(extension, 'control.html'), '<!doctype html><title>QuickShot isolated Doubao test</title>');
await writeFile(path.join(extension, 'content.js'), await readFile(path.join(root, 'extension/content.js')));
const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const edgePath = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const edge = spawn(edgePath, ['--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--remote-debugging-port=0', '--window-size=920,760', '--load-extension=' + extension, '--disable-extensions-except=' + extension, '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let stderr = ''; edge.stderr.on('data', chunk => { stderr += chunk; });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms)), sockets = [];
async function connect(url) {
  const socket = new WebSocket(url); sockets.push(socket);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  let next = 0; const pending = new Map();
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data), waiter = pending.get(message.id); if (!waiter) return;
    pending.delete(message.id); clearTimeout(waiter.timer);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error))); else waiter.resolve(message.result);
  });
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++next, timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 5000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
  return { send, eval: async expression => {
    const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  } };
}
let controller, page, appLaunch, nativeHelper, browserProcess;
async function inspectFocus(tabId, requestId, label) {
  await controller.eval(`(async()=>{const tab=await chrome.tabs.get(${tabId});await chrome.windows.update(tab.windowId,{state:'normal',focused:true});await chrome.tabs.update(tab.id,{active:true});})()`);
  const verify = () => controller.eval(`chrome.tabs.sendMessage(${tabId},${JSON.stringify({ quickshot: true, type: 'verifyFocus', requestId })},{frameId:0})`);
  let verified = await verify();
  if (verified.needsEditor) {
    const recoveryDeadline = Date.now() + 2000;
    let ready;
    do {
      ready = await controller.eval(`chrome.tabs.sendMessage(${tabId},${JSON.stringify({ quickshot: true, type: 'prepare', requestId, locator: result.locator })},{frameId:0})`);
      if (ready.ok) break;
      await delay(100);
    } while (Date.now() < recoveryDeadline);
    assert.equal(ready?.ok, true, 'same bound editor reappears within the preauthorization recovery limit');
    verified = await verify();
  }
  if (verified.needsContentFocus) {
    const focusPath = path.join(output, label + '-native-focus.json');
    const runtime = path.join(root, '.tools/dotnet/dotnet.exe');
    const helper = path.join(root, 'tests/QuickShot.App.Tests/bin/Release/net10.0-windows/QuickShot.App.Tests.dll');
    nativeHelper = spawn(runtime, [helper, '[QuickShot:' + requestId + ']', focusPath, String(browserProcess)], {
      windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'], env: { ...process.env, DOTNET_ROOT: path.join(root, '.tools/dotnet') }
    });
    let errors = ''; nativeHelper.stderr.on('data', chunk => { errors += chunk; });
    let activated;
    for (let attempt = 0; attempt < 120; attempt++) { try { activated = JSON.parse(await readFile(focusPath, 'utf8')); break; } catch {} await delay(50); }
    assert.ok(activated?.activated && activated.expected, 'owned native activation succeeds: ' + errors);
    verified = await verify();
    if (verified.needsContentFocus) {
      await writeFile(focusPath + '.focus', 'request production content focus');
      let focused;
      for (let attempt = 0; attempt < 110; attempt++) { try { focused = JSON.parse(await readFile(focusPath + '.focus-result', 'utf8')); break; } catch {} await delay(50); }
      assert.ok(focused?.contentFocused && focused.expected, 'production content focus succeeds on Doubao');
      verified = await verify();
    }
    result.nativeFocus ||= {}; result.nativeFocus[label] = activated;
    await writeFile(focusPath + '.release', 'no paste requested');
    const completed = await Promise.race([new Promise(resolve => nativeHelper.once('exit', resolve)), delay(2000).then(() => 'timeout')]);
    assert.equal(completed, 0, 'focus helper exits without a paste'); nativeHelper = null;
  }
  result.documentStates ||= {};
  result.documentStates[label] = await controller.eval(`chrome.scripting.executeScript({target:{tabId:${tabId}},injectImmediately:true,func:()=>({url:location.href,state:document.readyState,focused:document.hasFocus(),visibility:document.visibilityState,activeTag:document.activeElement?.tagName,activeTestId:document.activeElement?.getAttribute('data-testid'),inputs:document.querySelectorAll('textarea').length,dialogs:document.querySelectorAll('[role="dialog"]').length,titleMarked:document.title.startsWith('[QuickShot:')})}).then(results=>results[0]?.result)`);
  return verified;
}
const result = { url: 'https://www.doubao.com/chat/', scope: 'Live editor preparation and unique app resolution; native paste is tested separately on a local fixture.', passed: false, nativePasteValidated: false, clipboardTouched: false, pasteDispatched: false, messageSent: false, timings: {} };
try {
  let port;
  for (let attempt = 0; attempt < 100; attempt++) {
    try { port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; } catch { await delay(100); }
  }
  if (!port) throw new Error('Owned Edge failed to start: ' + stderr.slice(-800));
  const browser = await connect((await cdpJson(`http://127.0.0.1:${port}/json/version`)).webSocketDebuggerUrl);
  browserProcess = (await browser.send('SystemInfo.getProcessInfo')).processInfo.find(process => process.type === 'browser')?.id;
  assert.ok(browserProcess);
  const controlTarget = await cdpJson(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('chrome-extension://' + extensionId + '/control.html')}`, { method: 'PUT' });
  controller = await connect(controlTarget.webSocketDebuggerUrl);
  for (let attempt = 0; attempt < 100 && !await controller.eval('!!globalThis.chrome?.tabs'); attempt++) await delay(50);
  const normalWindow = (await controller.eval('chrome.windows.getAll({windowTypes:["normal"]})'))[0];
  assert.ok(normalWindow);
  await controller.eval(`chrome.windows.update(${normalWindow.id},{state:'minimized'})`);
  const started = Date.now();
  await controller.eval(`chrome.windows.update(${normalWindow.id},{state:'normal',focused:false})`);
  const tab = await controller.eval(`chrome.tabs.create({windowId:${normalWindow.id},url:${JSON.stringify(result.url)},active:true})`);
  let inspected;
  for (let attempt = 0; attempt < 160; attempt++) {
    try {
      inspected = await controller.eval(`chrome.scripting.executeScript({target:{tabId:${tab.id}},injectImmediately:true,func:()=>{
        const inputs=[...document.querySelectorAll('textarea,[contenteditable="true"],[contenteditable="plaintext-only"]')].filter(e=>e.getClientRects().length&&!e.disabled&&!e.readOnly);
        return {url:location.href,state:document.readyState,title:document.title,inputCount:inputs.length,inputs:inputs.map(e=>({tag:e.tagName.toLowerCase(),id:e.id,placeholder:e.getAttribute('placeholder'),testId:e.getAttribute('data-testid')}))};
      }}).then(results=>results[0]?.result)`);
      if (inspected?.inputCount === 1 && inspected.state !== 'loading') break;
    } catch {}
    if (Date.now() - started > 25000) break;
    await delay(150);
  }
  result.timings.editorReadyMilliseconds = Date.now() - started; result.page = inspected;
  console.log('Doubao readiness: ' + JSON.stringify(inspected));
  assert.equal(inspected?.inputCount, 1, 'Doubao exposes one editable chat input in the isolated profile');
  const input = inspected.inputs[0];
  const selector = input.id ? '#' + input.id.replace(/[^a-zA-Z0-9_-]/g, character => '\\' + character) : input.tag;
  const locator = { selector, tag: input.tag };
  const targets = await cdpJson(`http://127.0.0.1:${port}/json/list`);
  const pageTarget = targets.find(target => target.type === 'page' && target.url.startsWith('https://www.doubao.com/'));
  assert.ok(pageTarget); page = await connect(pageTarget.webSocketDebuggerUrl);
  await controller.eval(`chrome.scripting.executeScript({target:{tabId:${tab.id}},files:['content.js'],injectImmediately:true})`);
  const content = (message, tabId = tab.id) => controller.eval(`chrome.tabs.sendMessage(${tabId},${JSON.stringify({ quickshot: true, ...message })},{frameId:0})`);
  const requestId = 'dddddddddddddddddddddddddddddddd';
  const prepareStarted = Date.now();
  const prepared = await content({ type: 'prepare', requestId, locator });
  result.timings.prepareMilliseconds = Date.now() - prepareStarted;
  assert.equal(prepared.ok, true, 'Actual Doubao input prepares with the production content script');
  await page.send('Page.bringToFront');
  result.focus = await content({ type: 'verifyFocus', requestId });
  result.locator = locator;
  const screenshot = await page.send('Page.captureScreenshot', { format: 'png' });
  await writeFile(path.join(output, 'doubao.png'), Buffer.from(screenshot.data, 'base64'));
  await content({ type: 'cancel', requestId });
  assert.equal(result.focus.ok, true, 'Actual Doubao editor receives focus without a paste or send');
  await controller.eval(`chrome.windows.update(${tab.windowId},{state:'minimized'})`);
  const restoreStarted = Date.now();
  await controller.eval(`chrome.windows.update(${tab.windowId},{state:'normal',focused:false})`);
  assert.equal((await content({ type: 'prepare', requestId, locator })).ok, true);
  result.timings.existingMinimizedPrepareMilliseconds = Date.now() - restoreStarted;
  result.minimizedFocus = await inspectFocus(tab.id, requestId, 'minimized');
  assert.equal(result.minimizedFocus.ok, true, 'restored Doubao input receives focus');
  result.timings.existingMinimizedVerifiedMilliseconds = Date.now() - restoreStarted;
  await content({ type: 'cancel', requestId });
  await controller.eval(`chrome.tabs.remove(${tab.id})`);
  appLaunch = spawn(edgePath, ['--no-first-run', '--user-data-dir=' + profile, '--app=' + result.url], { windowsHide: true, stdio: 'ignore' });
  let appTab;
  const appStarted = Date.now();
  for (let attempt = 0; attempt < 160; attempt++) {
    appTab = await controller.eval(`chrome.tabs.query({url:'https://www.doubao.com/*'}).then(tabs=>tabs.length===1?tabs[0]:null)`);
    if (appTab?.url === result.url && appTab.status === 'complete') break;
    if (Date.now() - appStarted > 20000) break;
    await delay(100);
  }
  assert.ok(appTab);
  result.appWindowType = (await controller.eval(`chrome.windows.get(${appTab.windowId})`)).type;
  result.normalBrowserWindows = (await controller.eval('chrome.windows.getAll({windowTypes:["normal"]})')).length;
  assert.ok(result.normalBrowserWindows > 0, 'normal browser remains open beside the Doubao app');
  const appCandidates = await controller.eval(`(async()=>{const tabs=await chrome.tabs.query({url:'https://www.doubao.com/*'});const eligible=[];for(const tab of tabs){const window=await chrome.windows.get(tab.windowId);if(!tab.incognito&&!window.incognito&&['normal','popup','app'].includes(window.type))eligible.push(tab);}return eligible;})()`);
  assert.equal(restoredTab(appCandidates, 'https://www.doubao.com').id, appTab.id);
  const appTarget = (await cdpJson(`http://127.0.0.1:${port}/json/list`)).find(target => target.type === 'page' && target.url === result.url);
  assert.ok(appTarget); page = await connect(appTarget.webSocketDebuggerUrl);
  await controller.eval(`chrome.scripting.executeScript({target:{tabId:${appTab.id}},files:['content.js'],injectImmediately:true})`);
  do {
    result.appPrepare = await content({ type: 'prepare', requestId, locator }, appTab.id);
    if (result.appPrepare.ok) break;
    await delay(100);
  } while (Date.now() - appStarted < 15000);
  assert.equal(result.appPrepare.ok, true, 'Doubao app editor becomes ready: ' + result.appPrepare.error);
  result.appFocus = await inspectFocus(appTab.id, requestId, 'app');
  assert.equal(result.appFocus.ok, true, 'Doubao app input receives focus beside the normal browser');
  await content({ type: 'cancel', requestId }, appTab.id);
  result.timings.appReadyMilliseconds = Date.now() - appStarted;
  const appScreenshot = await page.send('Page.captureScreenshot', { format: 'png' });
  await writeFile(path.join(output, 'doubao-app.png'), Buffer.from(appScreenshot.data, 'base64'));
  result.passed = true;
  console.log('PASS: actual Doubao editor preparation, minimized restore and unique app resolution beside the normal browser; clipboard untouched; no paste, upload or send.');
} catch (error) {
  result.error = error.message; throw error;
} finally {
  await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log('Evidence: ' + output);
  try { await (page || controller)?.send('Browser.close'); } catch {}
  for (const socket of sockets) socket.close();
  edge.kill(); edge.stderr.destroy(); edge.unref();
  appLaunch?.kill();
  nativeHelper?.kill();
}
