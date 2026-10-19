import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import * as shared from '../extension/shared.mjs';
const binding = () => ({ id: '11111111-2222-4333-8444-555555555555', key: 'C', name: '测试聊天', url: 'https://chat.example.test/', origin: 'https://chat.example.test', locator: { selector: '#chat', tag: 'textarea' } });

test('canonical shortcuts accept up to three keys without capture-tool reservations', () => {
  for (const [input, expected] of [[' c ', 'C'], ['shift+control+x', 'Ctrl+Shift+X'], ['alt+ctrl+F12', 'Ctrl+Alt+F12'], ['1', '1'], ['Win+Left', 'Win+Left'], ['Alt+Shift+D', 'Alt+Shift+D'], ['printscreen', 'PrintScreen']]) assert.equal(shared.bindingKey(input), expected);
  for (const key of ['', 'CC', '中', 'Ctrl', 'Ctrl+Ctrl+C', 'C+D', 'Ctrl+Alt+Shift+C', 'Esc']) assert.throws(() => shared.bindingKey(key));
  assert.equal(shared.keyFromEvent({ code: 'KeyC', ctrlKey: true, altKey: true, key: 'Process', isComposing: true }), 'Ctrl+Alt+C');
  assert.equal(shared.keyFromEvent({ code: 'ControlLeft', ctrlKey: true }), null);
  assert.equal(shared.keyFromEvent({ code: 'KeyD', altKey: true, shiftKey: true }), 'Alt+Shift+D');
  assert.equal(shared.keyFromEvent({ code: 'PrintScreen' }), 'PrintScreen');
  assert.throws(() => shared.keyFromEvent({ code: 'KeyC', ctrlKey: true, altKey: true, shiftKey: true }));
});
test('URL permissions and binding identity are validated', () => {
  assert.equal(shared.permissionPattern('http://localhost:9012/chat'), 'http://localhost/*');
  for (const url of ['edge://settings', 'file:///tmp/a', 'javascript:alert(1)', 'https://user:password@example.test']) assert.throws(() => shared.webUrl(url));
  assert.equal(shared.validateBindings([binding()]).length, 1);
  assert.throws(() => shared.validateBindings([binding(), { ...binding(), id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee' }]));
  assert.throws(() => shared.validateBindings([{ ...binding(), url: 'https://other.test' }]));
  assert.throws(() => shared.validateBindings([{ ...binding(), locator: null }]));
  assert.deepEqual(Object.keys(shared.targetSummary([binding()])[0]).sort(), ['id', 'key', 'name', 'url']);
});
test('candidate resolution counts the target site across conversations, windows, and exact ports', () => {
  const one = { id: 1, url: binding().origin + '/chat/one', incognito: false };
  assert.equal(shared.restoredTab([one, { ...one, id: 2, url: 'https://other.test/' }], binding().origin).id, 1);
  assert.equal(shared.restoredTab([{ ...one, incognito: true }], binding().origin), null);
  assert.throws(() => shared.restoredTab([one, { ...one, id: 2, url: binding().origin + '/chat/two', windowId: 8 }], binding().origin));
  assert.equal(shared.restoredTab([{ ...one, url: 'http://localhost:9001/' }], 'http://localhost:9002'), null);
  assert.equal(shared.restoredTab([{ id: 9, url: 'about:blank', pendingUrl: binding().origin + '/chat/new' }], binding().origin).id, 9);
  assert.throws(() => shared.restoredTab([one, { id: 9, url: 'about:blank', pendingUrl: binding().origin + '/chat/new' }], binding().origin));
});

function event() {
  const listeners = [];
  return { addListener: fn => listeners.push(fn), fire: (...args) => listeners.map(fn => fn(...args)) };
}
async function harness(savedBindings = [binding()], tabOverrides = [], schemaVersion = 2, savedSession = {}, storageHooks = {}) {
  const outgoing = [], content = [], ports = [], trace = [];
  const tabs = new Map([[7, { id: 7, windowId: 1, url: binding().origin + '/chat/one', status: 'complete', active: true, incognito: false }], ...tabOverrides.map(tab => [tab.id, tab])]);
  const local = { bindings: structuredClone(savedBindings), schemaVersion, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee' }, session = structuredClone(savedSession);
  const storageArea = data => ({
    get: async keys => Object.fromEntries((Array.isArray(keys) ? keys : [keys]).filter(key => key in data).map(key => [key, structuredClone(data[key])])),
    set: async values => { if (data === session) await storageHooks.beforeSessionSet?.(structuredClone(values)); Object.assign(data, structuredClone(values)); }, remove: async key => { delete data[key]; }
  });
  const runtimeMessage = event(), tabUpdated = event(), tabRemoved = event();
  let contentFocus = true, windowFocus = true, nextTab = 20, nextWindow = 2;
  const windowTypes = new Map();
  const chrome = {
    runtime: {
      id: 'abcdefghijklmnopabcdefghijklmnop', getURL: path => 'chrome-extension://abcdefghijklmnopabcdefghijklmnop/' + path,
      connectNative: () => {
        const port = { onMessage: event(), onDisconnect: event(), postMessage: message => { outgoing.push(structuredClone(message)); trace.push(message.type); }, disconnect: () => port.onDisconnect.fire() };
        ports.push(port); return port;
      }, onStartup: event(), onInstalled: event(), onMessage: runtimeMessage
    },
    storage: { local: storageArea(local), session: storageArea(session) },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    alarms: { clear: async () => true, onAlarm: event() },
    permissions: { contains: async () => true, remove: async () => true },
    scripting: { getRegisteredContentScripts: async () => [], registerContentScripts: async () => {}, unregisterContentScripts: async () => {}, executeScript: async () => {} },
    windows: {
      get: async id => ({ id, type: windowTypes.get(id) || 'normal', focused: windowFocus, state: 'normal' }),
      getAll: async ({ windowTypes: included } = {}) => [...new Set([...tabs.values()].map(tab => tab.windowId))]
        .map(id => ({ id, type: windowTypes.get(id) || 'normal', focused: windowFocus, state: 'normal' }))
        .filter(window => !included || included.includes(window.type)),
      create: async ({ url, type, focused }) => {
        const id = nextWindow++, tab = { id: nextTab++, url, windowId: id, status: 'complete', active: true, incognito: false };
        windowTypes.set(id, type); tabs.set(tab.id, tab); trace.push('createWindow:' + url);
        return { id, type, focused, state: 'normal', tabs: [structuredClone(tab)] };
      },
      update: async () => { trace.push('activateWindow'); }
    },
    tabs: {
      get: async id => { if (!tabs.has(id)) throw new Error('missing tab'); return structuredClone(tabs.get(id)); },
      query: async () => [...tabs.values()].map(tab => structuredClone(tab)),
      create: async ({ url }) => { const tab = { id: nextTab++, url, windowId: 1, status: 'complete', active: true, incognito: false }; tabs.set(tab.id, tab); trace.push('create:' + url); return structuredClone(tab); },
      update: async (id, update) => { trace.push('activateTab:' + id); return Object.assign(tabs.get(id), update); },
      remove: async id => { tabs.delete(id); trace.push('close:' + id); tabRemoved.fire(id); },
      sendMessage: async (id, message) => {
        content.push({ id, ...structuredClone(message) });
        if (message.type === 'prepare') { tabs.get(id).title = `[QuickShot:${message.requestId}] Test chat`; return { ok: true, url: tabs.get(id).url }; }
        return { ok: message.type === 'verifyFocus' ? contentFocus === true : true, needsContentFocus: message.type === 'verifyFocus' && contentFocus === 'needs', error: 'focus lost', pasteObserved: true };
      },
      onUpdated: tabUpdated, onRemoved: tabRemoved
    }
  };
  const source = (await readFile(new URL('../extension/background.js', import.meta.url), 'utf8')).replace(/^import[^\n]+\n/, 'const { HOST_NAME, PROTOCOL_VERSION, bindingKey, permissionPattern, restoredTab, targetSummary, validateBindings, webUrl } = __shared;\n');
  vm.runInNewContext(source, { chrome, crypto: webcrypto, URL, TextEncoder, setTimeout, clearTimeout, console, __shared: shared });
  const wait = async predicate => { for (let i = 0; i < 3000; i++) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 1)); } throw new Error('timed out waiting for worker'); };
  const ui = (message, sender = { id: chrome.runtime.id, url: chrome.runtime.getURL('options.html') }) => new Promise(resolve => runtimeMessage.fire(message, sender, resolve));
  await wait(() => session.connection);
  const h = { chrome, tabs, outgoing, content, local, session, ports, trace, ui, wait, tabUpdated, tabRemoved, windowTypes,
    get port() { return ports.at(-1); }, setFocus: value => { contentFocus = value; }, setWindowFocus: value => { windowFocus = value; } };
  h.connect = async (intent = 'connect') => { await ui({ type: intent === 'launch' ? 'reconnect' : 'connect' }); h.port.onMessage.fire({ type: 'helloAck', version: 3 }); await wait(() => outgoing.some(message => message.type === 'ready')); };
  return h;
}
const requestId = '1234567890abcdef1234567890abcdef';
async function prepare(h) {
  await h.connect(); h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => ['focusPrepared', 'deliveryError'].includes(message.type)));
}

test('worker startup, browser startup and state reads never start a native host', async () => {
  const h = await harness();
  h.chrome.runtime.onStartup.fire(); h.chrome.runtime.onInstalled.fire(); h.chrome.alarms.onAlarm.fire({ name: 'quickshot-reconnect' });
  await h.ui({ type: 'getState' }); await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.ports.length, 0); assert.equal(h.session.connection.state, 'stopped');
});
test('passive connect does not request launch, stopped hosts do not retry', async () => {
  const h = await harness(); await h.ui({ type: 'connect' });
  assert.equal(h.outgoing[0].intent, 'connect'); assert.equal(h.outgoing[0].version, shared.PROTOCOL_VERSION);
  assert.equal(h.outgoing[0].bindings[0].locator, undefined);
  h.port.onMessage.fire({ type: 'appStopped', message: 'stopped' });
  await h.wait(() => h.session.connection.state === 'stopped');
  await h.ui({ type: 'getState' }); assert.equal(h.ports.length, 1);
  await h.ui({ type: 'reconnect' }); assert.equal(h.outgoing.at(-1).intent, 'launch');
});
test('connection handshake retains its helper until a unique receiving page is ready', async () => {
  const h = await harness(); const sender = { id: h.chrome.runtime.id, url: h.chrome.runtime.getURL('connect.html'), tab: { id: 80 } };
  h.tabs.set(80, { id: 80, windowId: 2, url: sender.url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, sender); h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  assert.equal(h.trace.includes('close:80'), false);
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.ok(h.trace.indexOf('ready') < h.trace.indexOf('close:80'));
  assert.ok(h.trace.indexOf('close:80') < h.trace.indexOf('focusPrepared'));
  assert.equal(h.tabs.has(7), true);
});
test('cold startup and repeated connect never close the last tab and reuse it as the configured default', async () => {
  const custom = { ...binding(), url: binding().origin + '/default' };
  const h = await harness([custom]); h.tabs.clear();
  const sender = { id: h.chrome.runtime.id, url: h.chrome.runtime.getURL('connect.html'), tab: { id: 80 } };
  h.tabs.set(80, { id: 80, windowId: 1, url: sender.url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, sender); h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  await h.ui({ type: 'connect' }, sender);
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(80).url, sender.url); assert.equal(h.ports.length, 1);
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: custom.id });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(80).url, custom.url);
  assert.equal(h.content.find(message => message.type === 'prepare').id, 80);
  assert.equal(h.trace.some(item => item.startsWith('create:') || item.startsWith('close:')), false);
});
test('claimed connection page waits for its pending web navigation to commit', async () => {
  const h = await harness(); h.tabs.clear();
  const sender = { id: h.chrome.runtime.id, url: h.chrome.runtime.getURL('connect.html'), tab: { id: 80 } };
  h.tabs.set(80, { id: 80, windowId: 1, url: sender.url, status: 'complete', active: true });
  const update = h.chrome.tabs.update;
  h.chrome.tabs.update = async (id, change) => {
    if (!change.url) return update(id, change);
    const tab = h.tabs.get(id); tab.pendingUrl = change.url; tab.status = 'loading';
    setTimeout(() => { tab.url = change.url; delete tab.pendingUrl; tab.status = 'complete'; }, 30);
    return structuredClone(tab);
  };
  await h.ui({ type: 'connect' }, sender); h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => ['focusPrepared','deliveryError'].includes(message.type)));
  assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
  assert.equal(h.tabs.get(80).url, binding().url); assert.equal(h.tabs.size, 1);
});
test('multiple bootstrap tabs hand off one tab before removing only the remaining helper', async () => {
  const h = await harness(); h.tabs.clear();
  const url = h.chrome.runtime.getURL('connect.html');
  for (const id of [80, 81]) {
    h.tabs.set(id, { id, windowId: 1, url, status: 'complete', active: true });
    await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id } });
  }
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  assert.equal(h.tabs.size, 2);
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(80).url, binding().url);
  assert.equal(h.trace.includes('close:80'), false); assert.equal(h.trace.includes('close:81'), true);
});
test('a helper navigated by the user is neither overwritten nor removed', async () => {
  const h = await harness(); const url = h.chrome.runtime.getURL('connect.html');
  h.tabs.set(80, { id: 80, windowId: 2, url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id: 80 } });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  h.tabs.get(80).pendingUrl = 'https://user.example.test/';
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.equal(h.tabs.get(80).pendingUrl, 'https://user.example.test/');
  assert.equal(h.trace.includes('close:80'), false);
});
test('failed bootstrap navigation preserves the browser and can be retried', async () => {
  const h = await harness(); h.tabs.clear(); const url = h.chrome.runtime.getURL('connect.html');
  h.tabs.set(80, { id: 80, windowId: 1, url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id: 80 } });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  const update = h.chrome.tabs.update;
  h.chrome.tabs.update = async (id, change) => { if (change.url) throw new Error('navigation failed'); return update(id, change); };
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(80).url, url);
  h.outgoing.length = 0; h.chrome.tabs.update = update;
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.equal(h.tabs.get(80).url, binding().url); assert.equal(h.trace.includes('close:80'), false);
});
test('cancel during bootstrap resolution cannot navigate or close the helper afterwards', async () => {
  const h = await harness(); h.tabs.clear(); const url = h.chrome.runtime.getURL('connect.html');
  h.tabs.set(80, { id: 80, windowId: 1, url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id: 80 } });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  const query = h.chrome.tabs.query;
  let resume;
  h.chrome.tabs.query = async options => { await new Promise(resolve => { resume = resolve; }); return query(options); };
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => !!resume);
  h.port.onMessage.fire({ type: 'cancel', requestId });
  await new Promise(resolve => setImmediate(resolve)); resume();
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  assert.equal(h.tabs.get(80).url, url); assert.equal(h.tabs.size, 1);
  assert.equal(h.trace.includes('close:80'), false); assert.equal(h.content.length, 0);
});
test('cancel while claimed helper navigation is pending preserves the target and never prepares paste', async () => {
  const h = await harness(); h.tabs.clear(); const url = h.chrome.runtime.getURL('connect.html');
  h.tabs.set(80, { id: 80, windowId: 1, url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id: 80 } });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  const update = h.chrome.tabs.update;
  let resume;
  h.chrome.tabs.update = async (id, change) => {
    const tab = await update(id, change);
    if (change.url) await new Promise(resolve => { resume = resolve; });
    return structuredClone(tab);
  };
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => !!resume);
  assert.equal(h.tabs.get(80).url, binding().url, 'navigation was already accepted by Edge');
  h.port.onMessage.fire({ type: 'cancel', requestId });
  await new Promise(resolve => setImmediate(resolve)); resume();
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(80).url, binding().url);
  assert.equal(h.trace.some(item => item.startsWith('close:') || item.startsWith('create:')), false);
  assert.equal(h.content.some(message => message.type === 'prepare'), false);
  assert.equal(h.outgoing.some(message => ['focusPrepared', 'pasteNow'].includes(message.type)), false);
});
test('cancel during helper cleanup keeps the claimed target and remaining helper without enabling paste', async () => {
  const h = await harness(); h.tabs.clear(); const url = h.chrome.runtime.getURL('connect.html');
  for (const id of [80, 81]) {
    h.tabs.set(id, { id, windowId: 1, url, status: 'complete', active: true });
    await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id } });
  }
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  const get = h.chrome.tabs.get;
  let resume;
  h.chrome.tabs.get = async id => {
    if (id === 81 && h.content.some(message => message.type === 'prepare'))
      await new Promise(resolve => { resume = resolve; });
    return get(id);
  };
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => !!resume);
  assert.equal(h.tabs.get(80).url, binding().url);
  h.port.onMessage.fire({ type: 'cancel', requestId });
  await h.wait(() => h.content.some(message => message.type === 'cancel'));
  resume();
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.tabs.size, 2); assert.equal(h.tabs.get(80).url, binding().url); assert.equal(h.tabs.get(81).url, url);
  assert.equal(h.trace.some(item => item.startsWith('close:')), false);
  assert.equal(h.outgoing.some(message => ['focusPrepared', 'pasteNow'].includes(message.type)), false);
});
test('ambiguous targets do not close the connection page while reporting the error', async () => {
  const h = await harness([binding()], [{ id: 8, windowId: 2, url: binding().origin + '/second', status: 'complete' }]);
  const url = h.chrome.runtime.getURL('connect.html');
  h.tabs.set(80, { id: 80, windowId: 3, url, status: 'complete', active: true });
  await h.ui({ type: 'connect' }, { id: h.chrome.runtime.id, url, tab: { id: 80 } });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => h.outgoing.some(message => message.type === 'ready'));
  h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  assert.match(h.outgoing.at(-1).message, /多个/); assert.equal(h.tabs.get(80).url, url);
  assert.equal(h.trace.some(item => item.startsWith('close:') || item.startsWith('create:')), false);
});
test('v1 migration uses homepage defaults and retains an original backup', async () => {
  const old = { ...binding(), url: binding().origin + '/chat/last' };
  const h = await harness([old], [], 1);
  assert.equal(h.local.bindings[0].url, binding().origin + '/');
  assert.equal(h.local.v1BindingsBackup[0].url, old.url); assert.equal(h.local.schemaVersion, 2);
});
test('invalid saved data is retained for recovery instead of overwritten', async () => {
  const invalid = { ...binding(), key: '???' }; const h = await harness([invalid]);
  assert.equal(h.local.bindings[0].key, '???'); assert.equal(h.session.connection.state, 'error');
});
test('picker validates sender, records three-key shortcut and defaults to the homepage', async () => {
  const h = await harness([]);
  assert.equal((await h.ui({ type: 'beginBinding', tabId: 7, key: 'Ctrl+Alt+C', name: '我的聊天' })).ok, true);
  const picked = { type: 'bindingPicked', pickId: h.session.pendingPick.id, locator: binding().locator };
  assert.equal((await h.ui(picked, { id: h.chrome.runtime.id, tab: { id: 8 }, frameId: 0 })).ok, false);
  assert.equal((await h.ui(picked, { id: h.chrome.runtime.id, tab: { id: 7 }, frameId: 0 })).ok, true);
  assert.equal(h.local.bindings[0].key, 'Ctrl+Alt+C'); assert.equal(h.local.bindings[0].url, binding().url);
  assert.equal((await h.ui({ type: 'beginBinding', tabId: 7, key: 'Alt+Ctrl+C', name: '重复' })).ok, false);
});
test('bindings allow an editable same-site default URL but reject cross-site defaults', async () => {
  const h = await harness();
  assert.equal((await h.ui({ type: 'editBinding', id: binding().id, key: 'Ctrl+C', name: '工作', url: binding().origin + '/default' })).ok, true);
  assert.equal(h.local.bindings[0].url, binding().origin + '/default');
  assert.equal((await h.ui({ type: 'editBinding', id: binding().id, key: 'D', name: '工作', url: 'https://other.test/' })).ok, false);
  assert.equal(h.local.bindings[0].key, 'Ctrl+C');
});
test('old helloAck connection-state writes cannot ready or override a replacement connection', async () => {
  let releaseConnected, delayFirst = true;
  const h = await harness([], [], 2, {}, { beforeSessionSet: async values => {
    if (values.connection?.state === 'connected' && delayFirst) {
      delayFirst = false; await new Promise(resolve => { releaseConnected = resolve; });
    }
  } });
  await h.ui({ type: 'connect' });
  h.port.onMessage.fire({ type: 'helloAck', version: 3 });
  await h.wait(() => !!releaseConnected);
  const reconnect = h.ui({ type: 'reconnect' });
  await new Promise(resolve => setImmediate(resolve));
  releaseConnected(); await reconnect;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.outgoing.some(message => message.type === 'ready'), false);
  assert.equal(h.session.connection.state, 'connecting');
});
test('one target-site tab plus unrelated tabs delivers to that unique chat', async () => {
  const h = await harness([binding()], [{ id: 8, windowId: 2, url: 'https://other.test/', active: true }]); await prepare(h);
  assert.equal(h.content.find(message => message.type === 'prepare').id, 7);
  assert.equal(h.trace.includes('activateWindow'), false, 'desktop activation must wait for released shortcut keys');
  h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
  h.port.onMessage.fire({ type: 'pasteDispatched', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'deliveryResult'));
  assert.equal(h.outgoing.at(-1).pasteObserved, true); assert.equal(h.content.some(message => ['send', 'submit'].includes(message.type)), false);
});
test('a unique Edge app or popup chat is reused and never replaced with a normal tab', async () => {
  for (const type of ['app', 'popup']) {
    const h = await harness(); h.windowTypes.set(1, type);
    const query = h.chrome.tabs.query;
    h.chrome.tabs.query = async options => { assert.equal(options.windowType, undefined); return query(options); };
    await prepare(h);
    assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
    assert.equal(h.content.find(message => message.type === 'prepare').id, 7);
    assert.equal(h.trace.some(item => item.startsWith('create:')), false);
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
  }
});
test('normal and app conversations count as ambiguous together, while devtools and incognito are excluded', async () => {
  const h = await harness([binding()], [{ id: 8, windowId: 2, url: binding().origin + '/chat/app', active: true }]);
  h.windowTypes.set(2, 'app'); await prepare(h);
  assert.match(h.outgoing.at(-1).message, /多个/);
  assert.equal(h.content.length, 0); assert.equal(h.trace.some(item => item.startsWith('activate')), false);
  const isolated = await harness([binding()], [{ id: 8, windowId: 2, url: binding().origin + '/chat/devtools' }, { id: 9, windowId: 3, url: binding().origin + '/private', incognito: true }]);
  isolated.windowTypes.set(2, 'devtools'); await prepare(isolated);
  assert.equal(isolated.outgoing.at(-1).type, 'focusPrepared');
  assert.equal(isolated.content.find(message => message.type === 'prepare').id, 7);
});
test('multiple conversations in different windows do nothing, even after previously using one', async () => {
  const h = await harness(); await h.ui({ type: 'openBinding', id: binding().id }); h.trace.length = 0;
  h.tabs.set(8, { id: 8, windowId: 2, url: binding().origin + '/chat/two', active: false }); await prepare(h);
  assert.match(h.outgoing.at(-1).message, /多个/); assert.equal(h.content.length, 0);
  assert.equal(h.trace.some(item => item.startsWith('activate') || item.startsWith('create:')), false);
});
test('no target site opens the configured default, never the last conversation', async () => {
  const custom = { ...binding(), url: binding().origin + '/default' }; const h = await harness([custom]);
  h.tabs.get(7).url = 'https://other.test/'; await prepare(h);
  assert.ok(h.trace.includes('create:' + custom.url));
  assert.equal(h.content.find(message => message.type === 'prepare').id, 20);
});
test('connected background Edge without a normal window creates a receiving window at the configured default', async () => {
  const custom = { ...binding(), url: binding().origin + '/configured-default' };
  const h = await harness([custom]); h.tabs.clear();
  const calls = [], create = h.chrome.windows.create;
  h.chrome.tabs.create = async options => { calls.push({ type: 'tab', ...structuredClone(options) }); throw new Error('No current window'); };
  h.chrome.windows.create = async options => { calls.push({ type: 'window', options: structuredClone(options) }); return create(options); };
  await prepare(h);
  assert.deepEqual(calls, [
    { type: 'tab', url: custom.url, active: true },
    { type: 'window', options: { url: custom.url, type: 'normal', focused: false } }
  ]);
  assert.equal(h.tabs.size, 1); assert.equal(h.tabs.get(20).url, custom.url);
  assert.equal(h.content.find(message => message.type === 'prepare').id, 20);
  assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
  assert.equal(h.trace.some(item => item.startsWith('close:')), false);
  assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false, 'window creation still waits for native focus verification');
});
test('newly opened chats wait for navigation completion before preparing the editor', async () => {
  const h = await harness(); h.tabs.get(7).url = 'https://other.test/';
  const create = h.chrome.tabs.create;
  h.chrome.tabs.create = async options => {
    const created = await create(options); Object.assign(h.tabs.get(created.id), { status: 'loading', pendingUrl: options.url }); return created;
  };
  await h.connect(); h.port.onMessage.fire({ type: 'prepare', requestId, bindingId: binding().id });
  await h.wait(() => h.tabs.has(20));
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(h.content.some(message => message.type === 'prepare'), false);
  Object.assign(h.tabs.get(20), { status: 'complete', pendingUrl: undefined });
  await h.wait(() => h.outgoing.some(message => message.type === 'focusPrepared'));
  assert.equal(h.content.find(message => message.type === 'prepare').id, 20);
});
test('activation waits for the browser to publish the content document title marker', async () => {
  const h = await harness(); let markerReads = 0;
  const get = h.chrome.tabs.get;
  h.chrome.tabs.get = async id => {
    const tab = await get(id);
    if (tab.title?.startsWith('[QuickShot:') && markerReads++ < 2) return { ...tab, title: '正在加载网页标题' };
    return tab;
  };
  await prepare(h);
  assert.equal(markerReads >= 3, true); assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
});
test('same-origin hydration navigation retries preparation against the final document', async () => {
  const h = await harness(); const send = h.chrome.tabs.sendMessage; let attempts = 0;
  h.chrome.tabs.sendMessage = async (id, message) => {
    const result = await send(id, message);
    if (message.type === 'prepare' && attempts++ === 0) h.tabs.get(id).url = binding().origin + '/chat/hydrated';
    return result;
  };
  await prepare(h);
  assert.equal(attempts, 2); assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
});
test('same-site conversation navigation never overwrites the default URL', async () => {
  const h = await harness(); h.tabs.get(7).url = binding().origin + '/chat/two'; h.tabUpdated.fire(7, { url: h.tabs.get(7).url }); await prepare(h);
  assert.equal(h.local.bindings[0].url, binding().url); assert.equal(h.outgoing.at(-1).type, 'focusPrepared');
});
test('login or cross-site navigation during target preparation blocks paste', async () => {
  const h = await harness(); const update = h.chrome.tabs.update;
  h.chrome.tabs.update = async (...args) => { const result = await update(...args); h.tabs.get(7).url = 'https://login.other.test/'; return result; };
  await prepare(h); assert.equal(h.outgoing.at(-1).type, 'deliveryError'); assert.equal(h.content.some(message => message.type === 'prepare'), false);
});
test('window focus and editor verification both protect the real paste', async () => {
  for (const windowLost of [false, true]) {
    const h = await harness(); await prepare(h);
    if (windowLost) h.setWindowFocus(false); else h.setFocus(false);
    h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
    assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  }
});
test('a minimized unique target is restored before its window and editor are verified for paste', async () => {
  const h = await harness(); let state = 'minimized'; const updates = [];
  const get = h.chrome.windows.get, update = h.chrome.windows.update, send = h.chrome.tabs.sendMessage;
  h.chrome.windows.get = async id => { const window = await get(id); h.trace.push('windowState:' + state); return { ...window, state }; };
  h.chrome.windows.update = async (id, options) => {
    updates.push({ id, ...structuredClone(options) });
    if (options.state === 'normal') { h.trace.push('restoreWindow'); state = 'normal'; }
    return update(id, options);
  };
  h.chrome.tabs.sendMessage = async (id, message) => { if (message.type === 'verifyFocus') h.trace.push('verifyEditor:' + id); return send(id, message); };
  await prepare(h);
  assert.equal(state, 'minimized', 'preparation alone never restores a window');
  assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  h.trace.length = 0;
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
  assert.deepEqual(updates, [{ id: 1, focused: true, state: 'normal' }]);
  assert.ok(h.trace.indexOf('windowState:minimized') < h.trace.indexOf('restoreWindow'));
  assert.ok(h.trace.indexOf('restoreWindow') < h.trace.indexOf('activateTab:7'));
  assert.ok(h.trace.indexOf('activateTab:7') < h.trace.indexOf('windowState:normal'));
  assert.ok(h.trace.indexOf('windowState:normal') < h.trace.indexOf('verifyEditor:7'));
  assert.ok(h.trace.indexOf('verifyEditor:7') < h.trace.indexOf('pasteNow'));
});
test('restoring a minimized target never bypasses failed window or editor focus checks', async () => {
  for (const failure of ['windowUnfocused', 'stillMinimized', 'editorUnfocused']) {
    const h = await harness(); let state = 'minimized'; let restored = false;
    const get = h.chrome.windows.get;
    h.chrome.windows.get = async id => ({ ...await get(id), state });
    h.chrome.windows.update = async (id, options) => {
      assert.equal(id, 1); assert.deepEqual(structuredClone(options), { focused: true, state: 'normal' });
      restored = true; if (failure !== 'stillMinimized') state = 'normal';
    };
    if (failure === 'windowUnfocused') h.setWindowFocus(false);
    if (failure === 'editorUnfocused') h.setFocus(false);
    await prepare(h);
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
    assert.equal(restored, true, failure + ': restore was attempted');
    assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false, failure + ': paste remains forbidden');
    assert.equal(h.content.some(message => message.type === 'verifyFocus'), failure === 'editorUnfocused', failure + ': editor is checked only after window verification');
  }
});
test('missing browser content focus requests a native fallback and still forbids paste until verified', async () => {
  const h = await harness(); await prepare(h); h.setFocus('needs');
  h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'contentFocusNeeded'));
  assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  h.setFocus(true); h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
});
test('a second target tab appearing during delivery prevents activation and paste', async () => {
  const h = await harness(); await prepare(h); h.trace.length = 0;
  h.tabs.set(8, { id: 8, windowId: 2, url: binding().origin + '/chat/two', active: false });
  h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  assert.equal(h.trace.some(item => item.startsWith('activate')), false);
});
test('cold-start navigation after focus preparation waits and prepares the final document before paste', async () => {
  const h = await harness(); h.tabs.clear(); await prepare(h);
  const tab = h.tabs.get(20), finalUrl = binding().origin + '/chat/created';
  tab.pendingUrl = finalUrl; tab.status = 'loading';
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)), false);
  assert.equal(h.content.filter(message => message.type === 'prepare').length, 1);
  Object.assign(tab, { url: finalUrl, status: 'complete', pendingUrl: undefined, title: 'New chat' });
  await h.wait(() => h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)));
  assert.equal(h.outgoing.at(-1).type, 'pasteNow');
  assert.equal(h.content.filter(message => message.type === 'prepare').length, 2);
  assert.equal(h.content.at(-1).type, 'verifyFocus');
  assert.equal(h.local.bindings[0].url, binding().url);
});
test('same-origin navigation and reload events before paste invalidate preparation without canceling delivery', async () => {
  for (const sameUrl of [false, true]) {
    const h = await harness(); await prepare(h);
    const tab = h.tabs.get(7), nextUrl = sameUrl ? tab.url : binding().origin + '/chat/hydrated';
    Object.assign(tab, { status: 'loading', pendingUrl: nextUrl });
    h.tabUpdated.fire(7, { status: 'loading', ...(sameUrl ? {} : { url: nextUrl }) });
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)), false);
    Object.assign(tab, { url: nextUrl, status: 'complete', pendingUrl: undefined, title: 'Hydrated chat' });
    h.tabUpdated.fire(7, { status: 'complete' });
    await h.wait(() => h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)));
    assert.equal(h.outgoing.at(-1).type, 'pasteNow');
    assert.equal(h.content.filter(message => message.type === 'prepare').length, 2);
    assert.equal(h.outgoing.filter(message => message.type === 'pasteNow').length, 1);
  }
});
test('navigation during focus verification repeats preparation and verifies only the new document', async () => {
  const h = await harness(); await prepare(h); const send = h.chrome.tabs.sendMessage; let navigated = false;
  h.chrome.tabs.sendMessage = async (id, message) => {
    if (message.type === 'verifyFocus' && !navigated) {
      navigated = true; const tab = h.tabs.get(id);
      Object.assign(tab, { url: binding().origin + '/chat/settled', title: 'New document' });
      h.tabUpdated.fire(id, { url: tab.url });
      return { ok: false, error: 'old document was replaced' };
    }
    return send(id, message);
  };
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await h.wait(() => h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)));
  assert.equal(h.outgoing.at(-1).type, 'pasteNow');
  assert.equal(h.content.filter(message => message.type === 'prepare').length, 2);
});
test('recovery still refuses a cross-origin pending destination, multiple target tabs, cancellation, or closed tab', async () => {
  for (const failure of ['crossOrigin', 'ambiguous', 'cancel', 'closed']) {
    const h = await harness(); await prepare(h); const tab = h.tabs.get(7);
    Object.assign(tab, { status: 'loading', pendingUrl: binding().origin + '/chat/next' });
    if (failure === 'crossOrigin') tab.pendingUrl = 'https://login.other.test/';
    if (failure === 'ambiguous') h.tabs.set(8, { id: 8, windowId: 2, url: binding().origin + '/chat/two', status: 'complete' });
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await new Promise(resolve => setTimeout(resolve, 20));
    if (failure === 'cancel') h.port.onMessage.fire({ type: 'cancel', requestId });
    if (failure === 'closed') { h.tabs.delete(7); h.tabRemoved.fire(7); }
    await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError') || h.content.some(message => message.type === 'cancel'));
    Object.assign(tab, { status: 'complete', pendingUrl: undefined });
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false, failure);
  }
});
test('a second target or cross-site destination appearing while recovery waits still blocks paste', async () => {
  for (const crossSite of [false, true]) {
    const h = await harness(); await prepare(h); const tab = h.tabs.get(7);
    Object.assign(tab, { status: 'loading', pendingUrl: binding().origin + '/chat/next' });
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(h.outgoing.some(message => ['pasteNow', 'deliveryError'].includes(message.type)), false);
    if (crossSite) {
      tab.url = 'https://login.other.test/'; h.tabUpdated.fire(7, { url: tab.url });
    } else h.tabs.set(8, { id: 8, windowId: 2, url: binding().origin + '/chat/two', status: 'complete' });
    Object.assign(tab, { status: 'complete', pendingUrl: undefined });
    await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
    assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  }
});
test('duplicate concurrent focus verification never authorizes two pastes', async () => {
  const h = await harness(); await prepare(h); const send = h.chrome.tabs.sendMessage; let resume;
  h.chrome.tabs.sendMessage = async (id, message) => {
    if (message.type === 'verifyFocus') await new Promise(resolve => { resume = resolve; });
    return send(id, message);
  };
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await h.wait(() => !!resume);
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await new Promise(resolve => setImmediate(resolve)); resume();
  await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
  assert.equal(h.content.filter(message => message.type === 'verifyFocus').length, 1);
  assert.equal(h.outgoing.filter(message => message.type === 'pasteNow').length, 1);
});
test('a stable verified document can be reverified but navigation after paste authorization never retries', async () => {
  for (const navigate of [false, true]) {
    const h = await harness(); await prepare(h);
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
    const preparedCount = h.content.filter(message => message.type === 'prepare').length;
    if (navigate) {
      h.tabs.get(7).url = binding().origin + '/chat/other'; h.tabUpdated.fire(7, { url: h.tabs.get(7).url });
      await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
    }
    h.port.onMessage.fire({ type: 'verifyFocus', requestId });
    if (!navigate) await h.wait(() => h.outgoing.filter(message => message.type === 'pasteNow').length === 2);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.content.filter(message => message.type === 'prepare').length, preparedCount);
    assert.equal(h.outgoing.filter(message => message.type === 'pasteNow').length, navigate ? 1 : 2);
    if (!navigate) {
      h.port.onMessage.fire({ type: 'pasteDispatched', requestId });
      await h.wait(() => h.outgoing.some(message => message.type === 'deliveryResult'));
      h.port.onMessage.fire({ type: 'verifyFocus', requestId });
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(h.outgoing.filter(message => message.type === 'pasteNow').length, 2);
    }
  }
});
test('pending navigation after paste authorization stops re-verification even without a navigation event', async () => {
  const h = await harness(); await prepare(h);
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await h.wait(() => h.outgoing.some(message => message.type === 'pasteNow'));
  h.tabs.get(7).pendingUrl = binding().origin + '/chat/late';
  h.port.onMessage.fire({ type: 'verifyFocus', requestId });
  await h.wait(() => h.outgoing.some(message => message.type === 'deliveryError'));
  assert.equal(h.outgoing.filter(message => message.type === 'pasteNow').length, 1);
  assert.equal(h.content.filter(message => message.type === 'prepare').length, 1);
});
test('canceled or cross-site navigated requests cannot later enable paste', async () => {
  for (const navigation of [false, true]) {
    const h = await harness(); await prepare(h);
    if (navigation) { h.tabs.get(7).url = 'https://login.other.test/'; h.tabUpdated.fire(7, { url: h.tabs.get(7).url }); }
    else h.port.onMessage.fire({ type: 'cancel', requestId });
    await h.wait(() => h.content.some(message => message.type === 'cancel'));
    h.port.onMessage.fire({ type: 'verifyFocus', requestId }); await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.outgoing.some(message => message.type === 'pasteNow'), false);
  }
});

test('2.0 allows the old capture chord and PrintScreen as target shortcuts', async () => {
  for (const key of ['Alt+Shift+D', 'PrintScreen']) {
    const h = await harness([{ ...binding(), key }]);
    await prepare(h);
    assert.equal((await h.ui({ type: 'getState' })).captureShortcut, undefined);
    assert.ok(h.outgoing.some(message => message.type === 'focusPrepared'));
  }
});

test('an older native protocol never marks the 2.0 extension ready', async () => {
  const h = await harness(); await h.ui({ type: 'connect' });
  h.port.onMessage.fire({ type: 'helloAck', version: 2 });
  await h.wait(() => h.session.connection.state === 'error');
  assert.ok(!h.outgoing.some(message => message.type === 'ready'));
});
