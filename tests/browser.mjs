// Real DOM checks in an isolated, headless Edge profile. No interaction with the user's tabs or clipboard.
import { spawn } from 'node:child_process';
import { readFile, mkdir, writeFile, cp } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';
import assert from 'node:assert/strict';
import { cdpJson } from './cdp-http.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'artifacts/browser-tests'); await mkdir(output, { recursive: true });
const profile = path.join(output, 'profile-' + process.pid);
const html = await readFile(path.join(root, 'tests/fixtures/chat.html'));
const server = createServer((request, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const edgePath = process.env.EDGE_PATH || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const extensionPath = path.join(output, 'extension-' + process.pid);
await cp(path.join(root, 'extension'), extensionPath, { recursive: true });
// Never contact the user's registered native host from this isolated test profile.
const sharedPath = path.join(extensionPath, 'shared.mjs');
await writeFile(sharedPath, (await readFile(sharedPath, 'utf8')).replace("'com.clipboardsender.edge'", "'com.quickshot.test.p" + process.pid + "'"));
const extensionId = (await readFile(path.join(root, 'extension-id.txt'), 'utf8')).trim();
const edge = spawn(edgePath, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + profile, '--load-extension=' + extensionPath, '--disable-extensions-except=' + extensionPath, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
let stderr = ''; edge.stderr.on('data', chunk => { stderr += chunk; });
let launchError; edge.on('error', error => { launchError = error; });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const sockets = [];
class Cdp {
  constructor(socket) {
    this.socket = socket; this.next = 1; this.pending = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (!message.id) return;
      const waiter = this.pending.get(message.id); if (!waiter) return;
      this.pending.delete(message.id); clearTimeout(waiter.timeout);
      if (message.error) waiter.reject(new Error(JSON.stringify(message.error))); else waiter.resolve(message.result);
    });
  }
  send(method, params = {}) {
    const id = this.next++;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { this.pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 15000);
      this.pending.set(id, { resolve, reject, timeout }); this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  async eval(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  }
}
async function connect(url) {
  const socket = new WebSocket(url); sockets.push(socket);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  return new Cdp(socket);
}
let checks = 0;
let mainCdp;
function check(value, expected, name) { assert.deepEqual(value, expected, name); console.log('PASS: ' + name); checks++; }
try {
  let port;
  for (let i = 0; i < 200; i++) {
    if (launchError) throw launchError;
    try { port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; } catch { await delay(100); }
  }
  if (!port) throw new Error('Edge did not start: ' + stderr.slice(-1500));
  const target = await cdpJson(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('http://127.0.0.1:' + server.address().port + '/')}`, { method: 'PUT' });
  const cdp = await connect(target.webSocketDebuggerUrl); mainCdp = cdp;
  await cdp.send('Page.enable'); await cdp.send('Page.bringToFront');
  for (let i = 0; i < 100 && !await cdp.eval("!!document.getElementById('chat')"); i++) await delay(50);
  await cdp.eval(`globalThis.savedPick = null; globalThis.chrome = { runtime: { onMessage: { addListener: fn => globalThis.quickshotListener = fn }, sendMessage: async message => { globalThis.savedPick = message; return {ok:true}; } } }; globalThis.callQuickShot = message => new Promise(resolve => quickshotListener({quickshot:true,...message}, {}, resolve));`);
  await cdp.eval(await readFile(path.join(root, 'extension/content.js'), 'utf8'));
  await cdp.eval(`callQuickShot({type:'pick',pickId:'test',key:'C',name:'测试聊天'})`);
  await cdp.eval(`document.querySelector('#chat').click()`);
  const locator = await cdp.eval('savedPick.locator');
  check(locator.selector, '#chat', 'picker identifies the actual textarea');
  check(await cdp.eval(`document.querySelector('[data-quickshot-ui="picker"]') === null`), true, 'picker removes its interception after binding');
  const id = '1234567890abcdef1234567890abcdef';
  await cdp.eval(`Object.defineProperty(document,'readyState',{configurable:true,get:()=> 'loading'})`);
  check((await cdp.eval(`callQuickShot({type:'prepare',requestId:'${id}',locator:${JSON.stringify(locator)}})`)).ok, false, 'loading document cannot publish an activation marker');
  await cdp.eval(`delete document.readyState`);
  await cdp.eval(`document.querySelector('#chat').setSelectionRange(0,3)`);
  check((await cdp.eval(`callQuickShot({type:'prepare',requestId:'${id}',locator:${JSON.stringify(locator)}})`)).ok, true, 'real DOM focus preparation');
  check(await cdp.eval(`document.querySelector('#chat').value`), '保留这段草稿', 'existing draft is untouched');
  check(await cdp.eval(`document.querySelector('#chat').selectionStart === document.querySelector('#chat').value.length`), true, 'caret moves to the end so paste cannot replace selected draft');
  check((await cdp.eval(`callQuickShot({type:'verifyFocus',requestId:'${id}'})`)).ok, true, 'focused input verifies');
  await cdp.eval(`document.title='动态网站的新标题'`);
  check(await cdp.eval(`document.title`), `[QuickShot:${id}] 动态网站的新标题`, 'hydration title updates retain the exact activation marker');
  await cdp.eval(`const title=document.createElement('title');title.textContent='替换后的最新标题';document.querySelector('title').replaceWith(title)`);
  check(await cdp.eval(`document.title`), `[QuickShot:${id}] 替换后的最新标题`, 'replacing the title element retains the activation marker');
  await cdp.eval(`document.head.replaceWith(document.head.cloneNode(true));document.title='替换后的最新标题'`);
  check(await cdp.eval(`document.title`), `[QuickShot:${id}] 替换后的最新标题`, 'replacing the document head reconnects the scoped title observer');
  check((await cdp.eval(`callQuickShot({type:'prepare',requestId:'${id}',locator:${JSON.stringify(locator)}})`)).ok, true, 'repeated preparation keeps the same live document');
  check(await cdp.eval(`document.title.split('[QuickShot:').length-1`), 1, 'repeated preparation never duplicates the title marker');
  await cdp.eval(`document.querySelector('#other').focus()`);
  check((await cdp.eval(`callQuickShot({type:'verifyFocus',requestId:'${id}'})`)).ok, true, 'activation re-focuses the configured input');
  check(await cdp.eval(`document.activeElement.id`), 'chat', 'caret ends in the target input');
  await cdp.eval(`document.hasFocus=()=>false`);
  check((await cdp.eval(`callQuickShot({type:'verifyFocus',requestId:'${id}'})`)).ok, false, 'unfocused document still blocks paste');
  await cdp.eval(`delete document.hasFocus;callQuickShot({type:'cancel',requestId:'${id}'});document.hasFocus=()=>false`);
  check((await cdp.eval(`callQuickShot({type:'prepare',requestId:'${id}',locator:${JSON.stringify(locator)}})`)).ok, true, 'background page prepares without requiring foreground focus');
  check(await cdp.eval(`document.title.startsWith('[QuickShot:')`), true, 'background preparation publishes a native activation marker');
  await cdp.eval(`delete document.hasFocus`);
  check((await cdp.eval(`callQuickShot({type:'verifyFocus',requestId:'${id}'})`)).ok, true, 'focus verifies after activation');
  await cdp.eval(`history.pushState({},'', '/changed-chat')`);
  check((await cdp.eval(`callQuickShot({type:'verifyFocus',requestId:'${id}'})`)).ok, false, 'conversation navigation during delivery blocks paste');
  await cdp.eval(`callQuickShot({type:'cancel',requestId:'${id}'})`);
  check(await cdp.eval(`document.title.includes('[QuickShot:')`), false, 'cancel restores the page title');
  check(await cdp.eval(`document.title`), '替换后的最新标题', 'cancel preserves the latest title from the website');
  await cdp.eval(`document.title='投递结束后的标题'`);
  check(await cdp.eval(`document.title`), '投递结束后的标题', 'title observer is disconnected after cancellation');
  await cdp.eval(`document.querySelector('#chat').id='hidden-chat';document.querySelector('#hidden-chat').style.display='none'`);
  check((await cdp.eval(`callQuickShot({type:'inspectInput',locator:{selector:'#hidden-chat',tag:'textarea'}})`)).ok, false, 'hidden inputs are rejected');
  await cdp.eval(`document.querySelector('#hidden-chat').id='chat';document.querySelector('#chat').style.display='block';const duplicate=document.createElement('textarea');duplicate.id='chat';document.body.append(duplicate)`);
  check((await cdp.eval(`callQuickShot({type:'inspectInput',locator:${JSON.stringify(locator)}})`)).ok, false, 'ambiguous selectors are rejected');
  await cdp.eval(`document.querySelectorAll('#chat')[1].remove();callQuickShot({type:'pick',pickId:'rich',key:'D',name:'富文本'})`);
  await cdp.eval(`document.querySelector('#rich span').click()`);
  const richLocator = await cdp.eval('savedPick.locator');
  check(richLocator.selector, '#rich', 'nested rich-text click binds the editable root');
  check((await cdp.eval(`callQuickShot({type:'prepare',requestId:'${id}',locator:${JSON.stringify(richLocator)}})`)).ok, true, 'rich-text input prepares');
  check(await cdp.eval(`document.querySelector('#rich').textContent`), '富文本草稿', 'rich-text draft remains intact');
  await cdp.eval(`const transfer=new DataTransfer();transfer.items.add(new File(['fake'],'test.png',{type:'image/png'}));document.querySelector('#rich').dispatchEvent(new ClipboardEvent('paste',{bubbles:true,clipboardData:transfer}));`);
  check((await cdp.eval(`callQuickShot({type:'finish',requestId:'${id}'})`)).pasteObserved, false, 'untrusted synthetic paste is never reported as a real paste');
  check(await cdp.eval(`document.querySelector('#count').textContent`), '已发送 0 次', 'no chat message is submitted');
  const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  await writeFile(path.join(output, 'fixture.png'), Buffer.from(screenshot.data, 'base64'));
  const optionsTarget = await cdpJson(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('chrome-extension://' + extensionId + '/options.html')}`, { method: 'PUT' });
  const options = await connect(optionsTarget.webSocketDebuggerUrl);
  await options.send('Page.enable'); await options.send('Page.bringToFront');
  await options.send('Emulation.setDeviceMetricsOverride', { width: 1100, height: 850, deviceScaleFactor: 1, mobile: false });
  for (let i = 0; i < 100 && !await options.eval("!!document.querySelector('#connection')"); i++) await delay(50);
  check(await options.eval("location.protocol"), 'chrome-extension:', 'actual unpacked extension loads with its stable ID');
  const state = await options.eval("chrome.runtime.sendMessage({type:'getState'})");
  check(state.ok, true, 'actual MV3 worker responds through Chrome messaging');
  check(state.bindings.length, 0, 'isolated Edge profile starts without user bindings');
  await delay(200);
  check(await options.eval("document.querySelector('#empty').hidden"), false, 'Chinese onboarding renders in the actual extension');
  // Render populated controls independently of host permissions. The real worker above is exercised separately.
  const demo = [
    { id: '11111111-2222-4333-8444-555555555555', key: 'C', name: 'DeepSeek 工作聊天', url: 'https://chat.deepseek.com/' },
    { id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', key: 'Ctrl+Alt+F12', name: '另一个接收目标', url: 'https://example.test/default' }
  ];
  await options.send('Page.addScriptToEvaluateOnNewDocument', { source: `globalThis.demoBindings=${JSON.stringify(demo)};globalThis.savedUi=[];chrome.runtime.sendMessage=async message=>{savedUi.push(message);if(message.type==='editBinding')demoBindings=demoBindings.map(item=>item.id===message.id?{...item,...message}:item);return message.type==='getState'?{ok:true,bindings:demoBindings,connection:{state:'connected',message:'Windows 托盘程序已连接。'}}:{ok:true};};` });
  await options.send('Page.reload');
  for (let i = 0; i < 100 && await options.eval("document.querySelectorAll('.target').length") !== 2; i++) await delay(50);
  check(await options.eval("document.querySelectorAll('.target').length"), 2, 'multiple shortcut and default URL controls render');
  check(await options.eval("document.querySelectorAll('input.key')[1].value"), 'Ctrl+Alt+F12', 'long three-key shortcut remains visible');
  await options.eval(`document.querySelector('input.key').dispatchEvent(new KeyboardEvent('keydown',{code:'KeyD',key:'Process',ctrlKey:true,altKey:true,bubbles:true,cancelable:true}))`);
  check(await options.eval("document.querySelector('input.key').value"), 'Ctrl+Alt+D', 'recorder recognizes physical keys during Chinese composition');
  await options.eval(`document.querySelector('input.key').dispatchEvent(new KeyboardEvent('keydown',{code:'KeyD',ctrlKey:true,altKey:true,shiftKey:true,bubbles:true,cancelable:true}))`);
  check(await options.eval("document.querySelector('#error').textContent.includes('三个')"), true, 'recorder rejects four-key combinations');
  await options.eval(`document.querySelector('input[type=url]').value='https://chat.deepseek.com/default';document.querySelector('.target button.primary').click()`);
  for (let i = 0; i < 100 && !await options.eval("savedUi.some(message=>message.type==='editBinding')"); i++) await delay(20);
  check(await options.eval("savedUi.find(message=>message.type==='editBinding').url"), 'https://chat.deepseek.com/default', 'save includes the independently configured default URL');
  const optionsScreenshot = await options.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  await writeFile(path.join(output, 'options.png'), Buffer.from(optionsScreenshot.data, 'base64'));
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ checks, passed: true }, null, 2));
  console.log(`${checks} browser checks passed.`);
} finally {
  if (mainCdp) { try { await mainCdp.send('Browser.close'); } catch { /* Already closed. */ } }
  for (const socket of sockets) socket.close();
  edge.kill(); edge.stderr.destroy(); edge.unref(); server.close();
}
