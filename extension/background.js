import { HOST_NAME, PROTOCOL_VERSION, bindingKey, permissionPattern, restoredTab, targetSummary, validateBindings, webUrl } from './shared.mjs';

let bindings = [], profileId, nativePort = null, nativeReady = false, pendingPick = null, delivery = null;
let connectionQueue = Promise.resolve();
const connectionTabs = new Set();
let mutationQueue = Promise.resolve();
const queue = task => { const result = mutationQueue.then(task); mutationQueue = result.catch(() => {}); return result; };
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function deliveryCall(operation) {
  let timer;
  try {
    return await Promise.race([operation, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('目标浏览器响应超时，本次未粘贴。请刷新目标页后重新复制图片。')), 3000);
    })]);
  } finally { clearTimeout(timer); }
}

const initialized = (async () => {
  const saved = await chrome.storage.local.get(['bindings', 'profileId', 'schemaVersion']);
  profileId = saved.profileId || crypto.randomUUID();
  let invalid = false;
  try {
    bindings = validateBindings(saved.bindings || []);
    if (saved.schemaVersion !== 2) {
      if (bindings.length) await chrome.storage.local.set({ v1BindingsBackup: bindings });
      bindings = bindings.map(binding => ({ ...binding, url: binding.origin + '/' }));
      await chrome.storage.local.set({ bindings, schemaVersion: 2 });
    }
  } catch { invalid = true; bindings = []; }
  const sessionState = await chrome.storage.session.get('pendingPick');
  pendingPick = sessionState.pendingPick || null;
  await chrome.storage.local.set({ profileId });
  await syncContentScripts();
  await chrome.alarms.clear('quickshot-reconnect');
  await setConnection(invalid ? 'error' : 'stopped', invalid ? '绑定数据无效，原数据已保留。请重新绑定接收目标。' : '尚未连接。手动运行 QuickShot，或点击“启动 / 连接 QuickShot”。');
})();

function setConnection(state, message, sourcePort = null) {
  const result = connectionQueue.then(async () => {
    if (sourcePort && nativePort !== sourcePort) return;
    await chrome.storage.session.set({ connection: { state, message } });
    if (sourcePort && nativePort !== sourcePort) return;
    await chrome.action.setBadgeText({ text: state === 'connected' ? '' : '!' });
    await chrome.action.setBadgeBackgroundColor({ color: '#6750e6' });
  });
  connectionQueue = result.catch(() => {});
  return result;
}
function nativeSend(message) {
  if (!nativePort) throw new Error('托盘程序未连接。请手动运行 QuickShot，或点击“启动 / 连接 QuickShot”。');
  nativePort.postMessage(message);
}
async function syncContentScripts() {
  const origins = [...new Set(bindings.map(binding => binding.origin))];
  const desired = [];
  for (const origin of origins) {
    if (await chrome.permissions.contains({ origins: [permissionPattern(origin)] })) {
      const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(origin));
      const id = 'quickshot-' + [...new Uint8Array(digest)].slice(0, 8).map(x => x.toString(16).padStart(2, '0')).join('');
      desired.push({ id, matches: [permissionPattern(origin)], js: ['content.js'], runAt: 'document_idle', persistAcrossSessions: true });
    }
  }
  const old = await chrome.scripting.getRegisteredContentScripts();
  const remove = old.filter(script => !desired.some(item => item.id === script.id)).map(script => script.id);
  if (remove.length) await chrome.scripting.unregisterContentScripts({ ids: remove });
  const add = desired.filter(item => !old.some(script => script.id === item.id));
  if (add.length) await chrome.scripting.registerContentScripts(add);
}
async function saveBindings(next) {
  validateBindings(next); bindings = next;
  await chrome.storage.local.set({ bindings, schemaVersion: 2 });
  await syncContentScripts();
  if (nativePort) nativeSend({ type: 'bindings', bindings: targetSummary(bindings) });
}
function isConnectionPage(tab) {
  const url = chrome.runtime.getURL('connect.html');
  return !tab.incognito && tab.url === url && (!tab.pendingUrl || tab.pendingUrl === url);
}
async function closeConnectionTabs(targetTabId, requestId = null) {
  for (const id of [...connectionTabs]) {
    if (requestId) assertCurrent(requestId);
    if (id === targetTabId) { connectionTabs.delete(id); continue; }
    let tab, target;
    try { tab = await chrome.tabs.get(id); } catch { connectionTabs.delete(id); continue; }
    if (!isConnectionPage(tab)) { connectionTabs.delete(id); continue; }
    try { target = await chrome.tabs.get(targetTabId); } catch { return; }
    if (!target || target.incognito || connectionTabs.has(targetTabId)) return;
    if (requestId) assertCurrent(requestId);
    try { await chrome.tabs.remove(id); connectionTabs.delete(id); } catch {}
  }
}
async function connectNative(intent = 'connect', connectionTabId = null) {
  await initialized;
  if (Number.isInteger(connectionTabId)) connectionTabs.add(connectionTabId);
  if (nativePort) {
    if (nativeReady) nativeSend({ type: 'ready' });
    return;
  }
  await setConnection('connecting', '正在连接 Windows 托盘程序…');
  if (nativePort) return;
  const port = chrome.runtime.connectNative(HOST_NAME); nativePort = port;
  let handshakeReady = Promise.resolve();
  port.onMessage.addListener(message => {
    if (nativePort !== port) return;
    const result = handshakeReady.then(() => handleNative(message, port));
    if (message.type === 'helloAck') handshakeReady = result.catch(() => {});
    result.catch(error => {
      if (nativePort !== port) return;
      if (message.requestId) failDelivery(message.requestId, error.message).catch(() => {});
      else setConnection('error', error.message, port).catch(() => {});
    });
  });
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError?.message || '本地连接已断开。';
    if (nativePort !== port) return;
    nativePort = null; nativeReady = false;
    cancelDelivery().catch(() => {});
    setConnection('disconnected', '连接已断开。请手动启动 QuickShot 或重新连接。详情：' + reason).catch(() => {});
  });
  port.postMessage({ type: 'hello', version: PROTOCOL_VERSION, intent, profileId, bindings: targetSummary(bindings) });
}

async function ensureContent(tabId) { await deliveryCall(chrome.scripting.executeScript({ target: { tabId }, files: ['content.js'], injectImmediately: true })); }
async function sendContent(tabId, message) { return deliveryCall(chrome.tabs.sendMessage(tabId, { quickshot: true, ...message }, { frameId: 0 })); }
async function cancelDelivery() {
  const current = delivery; delivery = null;
  if (current?.tabId != null) {
    sendContent(current.tabId, { type: 'cancel', requestId: current.requestId }).catch(() => {});
  }
}
async function failDelivery(requestId, message) {
  if (delivery?.requestId === requestId) await cancelDelivery();
  if (nativePort) nativeSend({ type: 'deliveryError', requestId, message });
}
function assertCurrent(requestId) { if (delivery?.requestId !== requestId) throw new Error('本次投递已取消。'); }

async function targetTabs(origin) {
  const tabs = await deliveryCall(chrome.tabs.query({ url: permissionPattern(origin) }));
  const windows = new Map();
  await Promise.all([...new Set(tabs.filter(tab => !tab.incognito).map(tab => tab.windowId))].map(async id => {
    windows.set(id, await deliveryCall(chrome.windows.get(id)));
  }));
  return tabs.filter(tab => !tab.incognito && !windows.get(tab.windowId)?.incognito && ['normal', 'popup', 'app'].includes(windows.get(tab.windowId)?.type));
}
async function resolveTab(binding, requestId = null) {
  const candidates = await targetTabs(binding.origin);
  if (requestId) assertCurrent(requestId);
  const existing = restoredTab(candidates, binding.origin);
  if (existing) return existing;
  for (const id of [...connectionTabs]) {
    let tab;
    try { tab = await chrome.tabs.get(id); } catch { connectionTabs.delete(id); continue; }
    if (!isConnectionPage(tab)) { connectionTabs.delete(id); continue; }
    if (requestId) assertCurrent(requestId);
    connectionTabs.delete(id);
    try { return { ...await chrome.tabs.update(id, { url: binding.url, active: true }), quickshotConnectionPage: true }; }
    catch (error) {
      try { if (isConnectionPage(await chrome.tabs.get(id))) connectionTabs.add(id); } catch {}
      throw error;
    }
  }
  if (requestId) assertCurrent(requestId);
  const windows = (await deliveryCall(chrome.windows.getAll({ windowTypes: ['normal'] }))).filter(window => !window.incognito);
  if (requestId) assertCurrent(requestId);
  const window = windows.find(item => item.focused) || windows[0];
  if (window) {
    if (window.state === 'minimized') await deliveryCall(chrome.windows.update(window.id, { state: 'normal', focused: false }));
    if (requestId) assertCurrent(requestId);
    return deliveryCall(chrome.tabs.create({ windowId: window.id, url: binding.url, active: true }));
  }
  if (requestId) {
    nativeSend({ type: 'openDefault', requestId });
    const deadline = Math.min(delivery.deadline, Date.now() + 10000);
    while (Date.now() < deadline) {
      assertCurrent(requestId);
      const opened = restoredTab(await targetTabs(binding.origin), binding.origin);
      assertCurrent(requestId);
      if (opened) return opened;
      await sleep(100);
    }
    throw new Error('Edge 未能打开默认接收页面，请确认程序与扩展已同时更新。');
  }
  const opened = await deliveryCall(chrome.windows.create({ url: binding.url, type: 'normal', focused: false }));
  const tab = opened.tabs?.[0];
  if (!tab) throw new Error('Edge 未能创建接收页面，请重新复制图片。');
  return tab;
}
async function assertUniqueTarget(bindingId, tabId) {
  const binding = bindings.find(item => item.id === bindingId);
  if (!binding) throw new Error('接收目标已删除。');
  const candidate = restoredTab(await targetTabs(binding.origin), binding.origin);
  if (candidate?.id !== tabId) throw new Error('目标网站的标签页已变化，请重新复制图片。');
}
function assertSameSite(tab, binding, allowBootstrap = false) {
  for (const url of [tab.url, tab.pendingUrl].filter(Boolean)) {
    if (allowBootstrap && (url === 'about:blank' || url === chrome.runtime.getURL('connect.html'))) continue;
    if (webUrl(url).origin !== binding.origin) throw new Error('网页发生登录跳转或跨站导航，本次未粘贴。');
  }
}
function documentChanged(current, tab) {
  return current.needsPrepare || current.preparedNavigation !== current.navigation ||
    !!tab.pendingUrl || tab.url !== current.preparedUrl;
}
async function prepareDocument(current, binding, deadline) {
  const { requestId, tabId } = current;
  let lastError = '网页或聊天输入框未就绪，请确认账号已登录。';
  while (Date.now() < deadline) {
    assertCurrent(requestId);
    const latest = await chrome.tabs.get(tabId); assertCurrent(requestId);
    assertSameSite(latest, binding, !current.preparedUrl);
    if (!latest.url || latest.url === 'about:blank' || latest.url === chrome.runtime.getURL('connect.html') || latest.pendingUrl) {
      await sleep(250); continue;
    }
    const navigation = current.navigation;
    try {
      await ensureContent(tabId); assertCurrent(requestId);
      const result = await sendContent(tabId, { type: 'prepare', requestId, locator: binding.locator });
      assertCurrent(requestId);
      if (result?.ok) {
        const markerDeadline = Math.min(deadline, Date.now() + 1500);
        while (Date.now() < markerDeadline) {
          const marked = await chrome.tabs.get(tabId); assertCurrent(requestId);
          assertSameSite(marked, binding);
          if (current.navigation !== navigation || marked.pendingUrl || marked.url !== result.url) break;
          if (marked.title?.startsWith(`[QuickShot:${requestId}] `)) {
            current.preparedUrl = marked.url; current.preparedNavigation = navigation; current.needsPrepare = false;
            return;
          }
          await sleep(50);
        }
        lastError = '网页仍在加载或切换会话，请稍后重新截图。';
      }
      lastError = result?.error || lastError;
    } catch (error) {
      assertCurrent(requestId);
      const latest = await chrome.tabs.get(tabId); assertCurrent(requestId);
      assertSameSite(latest, binding);
      lastError = error.message;
    }
    await sleep(250);
  }
  throw new Error(lastError);
}

async function handleNative(message, sourcePort) {
  await initialized;
  if (nativePort !== sourcePort) return;
  if (message.type === 'helloAck') {
    if (message.version !== PROTOCOL_VERSION) throw new Error('程序与扩展版本不一致，请更新程序并重新加载扩展。');
    await setConnection('connected', 'Windows 托盘程序已连接。', sourcePort);
    if (nativePort !== sourcePort) return;
    nativeReady = true;
    nativeSend({ type: 'ready' }); return;
  }
  if (message.type === 'rejected' || message.type === 'appStopped') {
    const old = nativePort; nativePort = null; nativeReady = false;
    await cancelDelivery();
    await setConnection(message.type === 'appStopped' ? 'stopped' : 'error', message.message);
    old?.disconnect(); return;
  }
  if (!/^[a-f0-9]{32}$/.test(message.requestId || '')) return;
  if (message.type === 'cancel') { if (delivery?.requestId === message.requestId) await cancelDelivery(); return; }
  if (message.type === 'prepare') {
    await cancelDelivery();
    const binding = bindings.find(item => item.id === message.bindingId);
    if (!binding) throw new Error('接收目标不存在，请重新绑定。');
    bindingKey(binding.key);
    delivery = { requestId: message.requestId, bindingId: binding.id, tabId: null, phase: 'preparing', navigation: 0, needsPrepare: true, deadline: Date.now() + 30000 };
    const requestId = message.requestId;
    if (!await chrome.permissions.contains({ origins: [permissionPattern(binding.origin)] })) throw new Error('该网站访问权限已被撤销，请重新绑定。');
    const tab = await resolveTab(binding, requestId); assertCurrent(requestId); delivery.tabId = tab.id;
    const targetWindow = await deliveryCall(chrome.windows.get(tab.windowId)); assertCurrent(requestId);
    if (targetWindow.state === 'minimized') {
      await deliveryCall(chrome.windows.update(tab.windowId, { state: 'normal', focused: false })); assertCurrent(requestId);
    }
    await chrome.tabs.update(tab.id, { active: true }); assertCurrent(requestId);
    await prepareDocument(delivery, binding, Math.min(delivery.deadline, Date.now() + 22000));
    await assertUniqueTarget(binding.id, tab.id);
    await closeConnectionTabs(tab.id, requestId);
    assertCurrent(requestId); delivery.phase = 'ready';
    nativeSend({ type: 'focusPrepared', requestId }); return;
  }
  if (delivery?.requestId !== message.requestId) return;
  if (message.type === 'verifyFocus' && ['ready', 'verified'].includes(delivery.phase) && !delivery.verifying) {
    const current = delivery;
    const binding = bindings.find(item => item.id === current.bindingId), canRecover = current.phase !== 'verified';
    const deadline = Math.min(current.deadline, Date.now() + 8000);
    current.verifying = true;
    try {
      while (Date.now() < deadline) {
        assertCurrent(message.requestId);
        let tab = await chrome.tabs.get(current.tabId); assertCurrent(message.requestId);
        assertSameSite(tab, binding);
        await assertUniqueTarget(current.bindingId, current.tabId); assertCurrent(message.requestId);
        if (documentChanged(current, tab)) {
          if (!canRecover) throw new Error('目标页面已导航，本次投递停止。');
          await prepareDocument(current, binding, deadline);
          continue;
        }
        const currentWindow = await chrome.windows.get(tab.windowId); assertCurrent(message.requestId);
        await chrome.windows.update(tab.windowId, currentWindow.state === 'minimized' ? { focused: true, state: 'normal' } : { focused: true }); assertCurrent(message.requestId);
        await chrome.tabs.update(tab.id, { active: true }); assertCurrent(message.requestId);
        const window = await chrome.windows.get(tab.windowId); assertCurrent(message.requestId);
        tab = await chrome.tabs.get(current.tabId); assertCurrent(message.requestId);
        assertSameSite(tab, binding);
        if (documentChanged(current, tab)) continue;
        if (!tab.active || !window.focused || window.state === 'minimized') throw new Error('目标标签页失去焦点，本次未粘贴。');
        let result, contentError;
        try { result = await sendContent(tab.id, { type: 'verifyFocus', requestId: message.requestId }); }
        catch (error) { contentError = error; }
        assertCurrent(message.requestId);
        tab = await chrome.tabs.get(current.tabId); assertCurrent(message.requestId);
        assertSameSite(tab, binding);
        if (documentChanged(current, tab)) continue;
        if (contentError) throw contentError;
        if (result?.needsEditor) {
          if (!canRecover) throw new Error('粘贴授权后输入框已变化，本次投递停止。');
          await prepareDocument(current, binding, Math.min(deadline, Date.now() + 2000));
          continue;
        }
        if (result?.needsContentFocus) { nativeSend({ type: 'contentFocusNeeded', requestId: message.requestId }); return; }
        if (!result?.ok) throw new Error(result?.error || '聊天输入框失去焦点，本次未粘贴。');
        await assertUniqueTarget(current.bindingId, current.tabId); assertCurrent(message.requestId);
        tab = await chrome.tabs.get(current.tabId); assertCurrent(message.requestId);
        assertSameSite(tab, binding);
        if (documentChanged(current, tab)) continue;
        current.phase = 'verified'; nativeSend({ type: 'pasteNow', requestId: message.requestId }); return;
      }
      throw new Error('网页仍在加载或切换会话，请稍后重新截图。');
    } finally { current.verifying = false; }
  }
  if (message.type === 'pasteDispatched' && delivery.phase === 'verified') {
    const current = delivery;
    current.phase = 'finishing';
    const result = await sendContent(current.tabId, { type: 'finish', requestId: message.requestId });
    assertCurrent(message.requestId); delivery = null;
    nativeSend({ type: 'deliveryResult', requestId: message.requestId, pasteObserved: result?.pasteObserved === true });
  }
}
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if ((change.url || change.status === 'loading') && delivery?.tabId === tabId) {
    const current = delivery; current.navigation++; current.needsPrepare = true;
    if (['verified', 'finishing'].includes(current.phase))
      failDelivery(current.requestId, '目标页面已导航，本次投递停止。').catch(() => {});
    else if (change.url) {
      const binding = bindings.find(item => item.id === current.bindingId);
      try { assertSameSite({ url: change.url }, binding, current.phase === 'preparing' && !current.preparedUrl); }
      catch (error) { failDelivery(current.requestId, error.message).catch(() => {}); }
    }
  }
});
chrome.tabs.onRemoved.addListener(tabId => {
  connectionTabs.delete(tabId);
  if (delivery?.tabId === tabId) failDelivery(delivery.requestId, '目标标签页已关闭，本次投递停止。').catch(() => {});
});

function defaultUrl(value, origin) {
  const url = webUrl(value || origin + '/');
  if (url.origin !== origin) throw new Error('默认网址必须与绑定输入框属于同一个网站。');
  return url.href;
}
async function handleUi(message, sender) {
  await initialized;
  if (message.type === 'bindingPicked') {
    return queue(async () => {
      const pick = pendingPick;
      if (sender.id !== chrome.runtime.id || !pick || pick.expires < Date.now() || message.pickId !== pick.id || sender.tab?.id !== pick.tabId || sender.frameId !== 0) throw new Error('本次输入框选择已失效，请重新绑定。');
      const tab = await chrome.tabs.get(pick.tabId);
      if (webUrl(tab.url).origin !== pick.origin) throw new Error('选择期间网页已离开原网站。');
      bindingKey(pick.key);
      if (bindings.some(binding => binding.key === pick.key && binding.id !== pick.editId)) throw new Error('此快捷键已有接收目标。');
      const next = bindings.filter(binding => binding.id !== pick.editId);
      next.push({ id: pick.editId || crypto.randomUUID(), key: pick.key, name: pick.name, url: pick.url, origin: pick.origin, locator: message.locator });
      await saveBindings(next.sort((a, b) => a.key.localeCompare(b.key)));
      pendingPick = null; await chrome.storage.session.remove('pendingPick');
      return { ok: true };
    });
  }
  if (!sender.url?.startsWith(chrome.runtime.getURL('')) || sender.id !== chrome.runtime.id) throw new Error('无效的扩展请求。');
  switch (message.type) {
    case 'getState': return { ok: true, bindings, ...(await chrome.storage.session.get('connection')) };
    case 'connect': {
      const temporary = sender.url === chrome.runtime.getURL('connect.html') ? sender.tab?.id : null;
      await connectNative('connect', temporary); return { ok: true };
    }
    case 'reconnect': {
      if (nativePort) { const old = nativePort; nativePort = null; nativeReady = false; old.disconnect(); await cancelDelivery(); }
      await connectNative('launch'); return { ok: true };
    }
    case 'openBinding':
    case 'reselectBinding': {
      const binding = bindings.find(item => item.id === message.id);
      if (!binding) throw new Error('接收目标不存在。');
      if (!await chrome.permissions.contains({ origins: [permissionPattern(binding.origin)] })) throw new Error('该网站权限已撤销，请重新允许访问或重新绑定。');
      const tab = await resolveTab(binding);
      await closeConnectionTabs(tab.id);
      await chrome.tabs.update(tab.id, { active: true });
      const window = await chrome.windows.get(tab.windowId);
      await chrome.windows.update(tab.windowId, window.state === 'minimized' ? { focused: true, state: 'normal' } : { focused: true });
      if (message.type === 'reselectBinding') {
        const deadline = Date.now() + 20000;
        while (Date.now() < deadline) {
          const latest = await chrome.tabs.get(tab.id);
          if (latest.url && webUrl(latest.url).origin !== binding.origin) throw new Error('页面已离开原网站，请重新绑定。');
          if (latest.status === 'complete') break;
          await sleep(250);
        }
        return handleUi({ type: 'beginBinding', tabId: tab.id, editId: binding.id, key: binding.key, name: binding.name, url: binding.url }, sender);
      }
      return { ok: true };
    }
    case 'beginBinding': return queue(async () => {
      const key = bindingKey(message.key), name = String(message.name || '').trim();
      if (!name || name.length > 80) throw new Error('请填写 1–80 字的目标名称。');
      const editId = message.editId || null;
      if (editId && !bindings.some(binding => binding.id === editId)) throw new Error('原接收目标已不存在。');
      if (bindings.some(binding => binding.key === key && binding.id !== editId)) throw new Error('此快捷键已被其他目标使用，请设置其他快捷键。');
      const tab = await chrome.tabs.get(message.tabId);
      if (tab.incognito) throw new Error('首版不支持无痕窗口。');
      const origin = webUrl(tab.url).origin, url = defaultUrl(message.url, origin);
      if (!await chrome.permissions.contains({ origins: [permissionPattern(origin)] })) throw new Error('请先允许访问当前网站。');
      if (delivery) await failDelivery(delivery.requestId, '正在重新绑定接收目标，本次投递已取消。');
      pendingPick = { id: crypto.randomUUID(), tabId: tab.id, key, name, origin, url, editId, expires: Date.now() + 120000 };
      await chrome.storage.session.set({ pendingPick });
      await ensureContent(tab.id);
      await sendContent(tab.id, { type: 'pick', pickId: pendingPick.id, key, name });
      return { ok: true };
    });
    case 'editBinding': return queue(async () => {
      const target = bindings.find(binding => binding.id === message.id);
      if (!target) throw new Error('接收目标不存在。');
      const key = bindingKey(message.key), name = String(message.name || '').trim(), url = defaultUrl(message.url || target.url, target.origin);
      if (bindings.some(binding => binding.id !== target.id && binding.key === key)) throw new Error('此快捷键已有接收目标。');
      if (delivery?.bindingId === target.id) await failDelivery(delivery.requestId, '接收目标已修改，请重新复制图片。');
      await saveBindings(bindings.map(binding => binding.id === target.id ? { ...binding, key, name, url } : binding));
      return { ok: true };
    });
    case 'deleteBinding': return queue(async () => {
      const removed = bindings.find(binding => binding.id === message.id);
      if (!removed) return { ok: true };
      if (delivery?.bindingId === removed.id) await failDelivery(delivery.requestId, '接收目标已删除，本次投递已取消。');
      await saveBindings(bindings.filter(binding => binding.id !== removed.id));
      if (!bindings.some(binding => permissionPattern(binding.origin) === permissionPattern(removed.origin))) await chrome.permissions.remove({ origins: [permissionPattern(removed.origin)] });
      return { ok: true };
    });
    default: throw new Error('未知操作。');
  }
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  handleUi(message, sender).then(respond).catch(error => respond({ ok: false, error: error.message }));
  return true;
});
