(() => {
  if (globalThis.__quickshotContentLoaded) return;
  globalThis.__quickshotContentLoaded = true;
  let picker = null, prepared = null;
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

  function editable(element) {
    if (!(element instanceof HTMLElement) || element.closest('[data-quickshot-ui]')) return false;
    if (element.matches('textarea')) return !element.disabled && !element.readOnly;
    return element.isContentEditable && element.getAttribute('contenteditable') !== 'false';
  }
  function inputFrom(element) {
    if (!(element instanceof Element)) return null;
    const candidate = element.closest('textarea, [contenteditable="true"], [contenteditable=""], [contenteditable="plaintext-only"]');
    return editable(candidate) ? candidate : null;
  }
  function visible(element) {
    return !!element && editable(element) && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden' && getComputedStyle(element).display !== 'none';
  }
  function makeLocator(element) {
    const tag = element.tagName.toLowerCase();
    const unique = selector => document.querySelectorAll(selector).length === 1;
    if (element.id) {
      const selector = '#' + CSS.escape(element.id);
      if (unique(selector)) return { selector, tag };
    }
    for (const attribute of ['data-testid', 'name', 'aria-label', 'placeholder', 'role']) {
      const value = element.getAttribute(attribute);
      if (value) {
        const selector = `${tag}[${attribute}="${CSS.escape(value)}"]`;
        if (unique(selector)) return { selector, tag };
      }
    }
    const parts = [];
    let current = element;
    while (current && current !== document.documentElement) {
      let part = current.tagName.toLowerCase();
      if (current.id) { parts.unshift('#' + CSS.escape(current.id)); break; }
      const siblings = [...current.parentElement.children].filter(child => child.tagName === current.tagName);
      if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
      parts.unshift(part); current = current.parentElement;
    }
    return { selector: parts.join(' > '), tag };
  }
  function locate(locator) {
    if (!locator || typeof locator.selector !== 'string' || locator.selector.length > 2000) return null;
    try {
      const matches = document.querySelectorAll(locator.selector);
      if (matches.length !== 1 || matches[0].tagName.toLowerCase() !== locator.tag) return null;
      return visible(matches[0]) ? matches[0] : null;
    } catch { return null; }
  }
  function toast(text, color = '#272839') {
    const node = document.createElement('div'); node.dataset.quickshotUi = 'toast'; node.textContent = text;
    node.style.cssText = `position:fixed;z-index:2147483647;left:50%;top:24px;transform:translateX(-50%);padding:14px 22px;border-radius:12px;background:${color};color:white;font:14px/1.6 system-ui,sans-serif;box-shadow:0 6px 24px #0003;pointer-events:none;max-width:80vw`;
    document.documentElement.append(node); setTimeout(() => node.remove(), 5000);
  }
  function stopPicker() {
    if (!picker) return;
    document.removeEventListener('click', picker.click, true); document.removeEventListener('keydown', picker.keydown, true);
    picker.banner.remove(); clearTimeout(picker.timeout); picker = null;
  }
  function startPicker(message) {
    stopPicker(); cleanup();
    const banner = document.createElement('div'); banner.dataset.quickshotUi = 'picker';
    banner.textContent = `QuickShot · ${message.key} → ${message.name}：请点击聊天输入框完成绑定；Esc 取消。`;
    banner.style.cssText = 'position:fixed;z-index:2147483647;left:24px;right:24px;top:20px;padding:18px 24px;border-radius:14px;background:#6750e6;color:white;font:15px/1.6 system-ui,sans-serif;box-shadow:0 8px 32px #0003;pointer-events:none';
    const click = async event => {
      const element = inputFrom(event.target);
      event.preventDefault(); event.stopImmediatePropagation();
      if (!element || !visible(element)) { banner.textContent = '请点击可编辑的聊天输入框（支持普通文本框或富文本输入区）。Esc 取消。'; return; }
      const locator = makeLocator(element); stopPicker();
      try {
        const result = await chrome.runtime.sendMessage({ type: 'bindingPicked', pickId: message.pickId, locator });
        if (!result?.ok) throw new Error(result?.error || '绑定失败，请重试。');
        toast(`已绑定 ${message.key} → ${message.name}。复制图片后按 ${message.key} 即可投递。`, '#226746');
      } catch (error) { toast(error.message); }
    };
    const keydown = event => { if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); stopPicker(); } };
    document.documentElement.append(banner);
    picker = { banner, click, keydown, timeout: setTimeout(stopPicker, 120000) };
    document.addEventListener('click', click, true); document.addEventListener('keydown', keydown, true);
  }
  function cleanup(requestId) {
    if (!prepared || (requestId && prepared.requestId !== requestId)) return;
    const current = prepared; prepared = null; clearTimeout(current.timeout); current.titleObserver.disconnect();
    document.removeEventListener('paste', current.paste, true);
    if (document.title.startsWith(current.token + ' ')) document.title = document.title.slice(current.token.length + 1);
  }
  async function prepare(message) {
    if (prepared?.requestId === message.requestId && prepared.url === location.href && document.readyState !== 'loading' && visible(prepared.element)) {
      const current = prepared; maintainTitle(current); return { ok: prepared === current, url: current.url };
    }
    cleanup(); stopPicker();
    if (document.readyState === 'loading') return { ok: false, error: '网页仍在加载，请稍后重试。' };
    const element = locate(message.locator);
    if (!element) return { ok: false, error: '找不到已绑定的聊天输入框，请确认已登录，或重新选择输入框。' };
    focusAtEnd(element);
    const token = '[QuickShot:' + message.requestId + ']';
    const current = { requestId: message.requestId, element, token, url: location.href, observed: false, timeout: null, paste: null, titleObserver: null, head: undefined };
    current.titleObserver = new MutationObserver(() => maintainTitle(current));
    current.paste = event => {
      if (!event.isTrusted || !(event.target === element || element.contains(event.target))) return;
      current.observed = [...(event.clipboardData?.items || [])].some(item => item.type.startsWith('image/')) || [...(event.clipboardData?.files || [])].some(file => file.type.startsWith('image/'));
    };
    prepared = current;
    maintainTitle(current); document.addEventListener('paste', current.paste, true);
    current.timeout = setTimeout(() => cleanup(current.requestId), 30000);
    await Promise.race([new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))), sleep(100)]);
    return { ok: prepared === current && location.href === current.url && visible(current.element), url: current.url };
  }
  function maintainTitle(current) {
    if (prepared !== current) return;
    if (location.href !== current.url) { cleanup(current.requestId); return; }
    if (current.head !== document.head) {
      current.titleObserver.disconnect(); current.head = document.head;
      current.titleObserver.observe(document.documentElement, { childList: true });
      if (current.head) current.titleObserver.observe(current.head, { childList: true, subtree: true, characterData: true });
    }
    if (!document.title.startsWith(current.token + ' ')) document.title = current.token + ' ' + document.title;
  }
  function focusAtEnd(element) {
    element.focus({ preventScroll: true });
    if (element instanceof HTMLTextAreaElement) element.setSelectionRange(element.value.length, element.value.length);
    else {
      const range = document.createRange(); range.selectNodeContents(element); range.collapse(false);
      const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
    }
  }
  function verify(requestId) {
    const current = prepared;
    if (current) maintainTitle(current);
    if (current?.requestId === requestId && location.href === current.url && !current.element.isConnected)
      return { ok: false, needsEditor: true, error: '网页正在重建已绑定的输入框。' };
    if (current?.requestId === requestId && location.href === current.url && visible(current.element)) { window.focus(); focusAtEnd(current.element); }
    if (!current || current.requestId !== requestId || location.href !== current.url || document.activeElement !== current.element || !visible(current.element) || !document.title.startsWith(current.token + ' '))
      return { ok: false, error: '目标网页、输入框或焦点发生变化，本次未粘贴。' };
    if (!document.hasFocus() || document.visibilityState !== 'visible') return { ok: false, needsContentFocus: true, error: '网页内容尚未获得键盘焦点。' };
    return { ok: true };
  }
  async function finish(requestId) {
    if (!prepared || prepared.requestId !== requestId) return { ok: true, pasteObserved: false };
    const current = prepared;
    const deadline = Date.now() + 2000;
    while (!current.observed && prepared === current && Date.now() < deadline) await sleep(50);
    const observed = current.observed; cleanup(requestId);
    return { ok: true, pasteObserved: observed };
  }
  chrome.runtime.onMessage.addListener((message, _sender, respond) => {
    if (!message.quickshot) return false;
    try {
      switch (message.type) {
        case 'pick': startPicker(message); respond({ ok: true }); break;
        case 'inspectInput': respond({ ok: !!locate(message.locator) }); break;
        case 'prepare': prepare(message).then(respond).catch(error => respond({ ok: false, error: error.message })); return true;
        case 'verifyFocus': respond(verify(message.requestId)); break;
        case 'finish': finish(message.requestId).then(respond); return true;
        case 'cancel': cleanup(message.requestId); respond({ ok: true }); break;
        default: respond({ ok: false, error: '未知页面操作。' });
      }
    } catch (error) { respond({ ok: false, error: error.message }); }
    return false;
  });
  window.addEventListener('pagehide', () => { cleanup(); stopPicker(); });
})();
