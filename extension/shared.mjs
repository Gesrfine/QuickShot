export const HOST_NAME = 'com.quickshot.edge';
export const PROTOCOL_VERSION = 2;
export const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function webUrl(value) {
  const url = new URL(value);
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) throw new Error('只支持普通 HTTP / HTTPS 网页。');
  return url;
}
export function bindingKey(value) {
  const parts = String(value).trim().split('+').map(part => part.trim());
  if (parts.length > 3 || parts.some(part => !part)) throw new Error('快捷键最多包含三个同时按下的键。');
  const aliases = { CTRL: 'Ctrl', CONTROL: 'Ctrl', ALT: 'Alt', SHIFT: 'Shift', WIN: 'Win', META: 'Win' };
  const names = ['Space', 'Tab', 'Enter', 'Backspace', 'Delete', 'Insert', 'Home', 'End', 'PageUp', 'PageDown', 'Left', 'Up', 'Right', 'Down', 'PrintScreen'];
  const modifiers = new Set(); let key = null;
  for (const part of parts) {
    const upper = part.toUpperCase(), modifier = aliases[upper];
    if (modifier) {
      if (modifiers.has(modifier)) throw new Error('快捷键中不能重复同一个修饰键。');
      modifiers.add(modifier); continue;
    }
    if (key) throw new Error('请使用修饰键加一个字母、数字或功能键，例如 Ctrl+Alt+C。');
    key = /^[A-Z0-9]$/.test(upper) || /^F([1-9]|1[0-2])$/.test(upper) ? upper : names.find(name => name.toUpperCase() === upper);
    if (!key) throw new Error('支持字母、数字、F1–F12、方向键等；Esc 始终用于取消。');
  }
  if (!key) throw new Error('快捷键需要包含一个字母、数字或功能键。');
  return [...['Ctrl', 'Alt', 'Shift', 'Win'].filter(name => modifiers.has(name)), key].join('+');
}
export function keyFromEvent(event) {
  const code = event.code || '';
  if (/^(Control|Alt|Shift|Meta)(Left|Right)$/.test(code)) return null;
  const named = { ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down' };
  const key = /^Key[A-Z]$/.test(code) ? code.slice(3) : /^Digit[0-9]$/.test(code) ? code.slice(5) : named[code] || code;
  return bindingKey([event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', event.metaKey && 'Win', key].filter(Boolean).join('+'));
}
export function permissionPattern(origin) {
  const url = webUrl(origin);
  // Extension match patterns do not distinguish TCP ports. Target checks still use the exact origin.
  return `${url.protocol}//${url.hostname}/*`;
}
export function validateBindings(bindings) {
  if (!Array.isArray(bindings) || bindings.length > 26) throw new Error('接收目标数据无效。');
  const keys = new Set(), ids = new Set();
  for (const binding of bindings) {
    if (!UUID.test(binding.id) || ids.has(binding.id)) throw new Error('接收目标 ID 无效或重复。');
    const key = bindingKey(binding.key);
    if (key !== binding.key || keys.has(key)) throw new Error('投递键重复或无效。');
    if (!binding.name || binding.name.length > 80) throw new Error('目标名称应为 1–80 个字符。');
    if (webUrl(binding.url).origin !== binding.origin) throw new Error('目标网址与授权网站不一致。');
    if (!binding.locator || typeof binding.locator.selector !== 'string' || !binding.locator.selector || binding.locator.selector.length > 2000) throw new Error('聊天输入框定位无效，请重新选择。');
    keys.add(key); ids.add(binding.id);
  }
  if (new TextEncoder().encode(JSON.stringify(targetSummary(bindings))).length > 48000) throw new Error('接收目标信息过长，请减少目标数量或使用较短的聊天网址。');
  return bindings;
}
export function targetSummary(bindings) {
  return bindings.map(({ id, key, name, url }) => ({ id, key, name, url }));
}
export function restoredTab(candidates, origin) {
  const matches = candidates.filter(tab => {
    if (tab.incognito) return false;
    // A newly opened app/tab can still have about:blank as its current URL.
    // Count its pending target too so a second delivery never opens a duplicate.
    return [tab.url, tab.pendingUrl].some(value => {
      try { return webUrl(value).origin === origin; } catch { return false; }
    });
  });
  if (matches.length > 1) throw new Error('目标网站有多个标签页，本次不操作。请只保留一个目标网站标签页后重新截图。');
  return matches[0] ?? null;
}

