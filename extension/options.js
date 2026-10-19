import { recordShortcut } from './shortcut-input.mjs';
const $ = id => document.getElementById(id);
async function action(message) {
  const result = await chrome.runtime.sendMessage(message);
  if (!result?.ok) throw new Error(result?.error || '操作失败。');
  return result;
}
function button(text, callback, className = '') {
  const node = document.createElement('button'); node.textContent = text; node.className = className;
  node.addEventListener('click', async () => {
    node.disabled = true; $('error').textContent = '';
    try { await callback(); } catch (error) { $('error').textContent = error.message; }
    finally { node.disabled = false; }
  }); return node;
}
async function refresh() {
  const state = await action({ type: 'getState' });
  showConnection(state.connection);
  $('empty').hidden = state.bindings.length > 0;
  $('targets').replaceChildren();
  for (const binding of state.bindings) {
    const card = document.createElement('section'); card.className = 'target';
    const row = document.createElement('div'); row.className = 'row';
    const key = document.createElement('input'); key.className = 'key'; key.value = binding.key; key.setAttribute('aria-label', '投递快捷键');
    recordShortcut(key, text => { $('error').textContent = text; });
    const name = document.createElement('input'); name.className = 'name grow'; name.maxLength = 80; name.value = binding.name; name.setAttribute('aria-label', '接收目标名称');
    row.append(key, name);
    const urlLabel = document.createElement('label'); urlLabel.textContent = '默认网址 · 没有目标网站标签页时打开';
    const url = document.createElement('input'); url.type = 'url'; url.value = binding.url; url.setAttribute('aria-label', '默认网址');
    const actions = document.createElement('div'); actions.className = 'actions';
    actions.append(
      button('保存修改', async () => { await action({ type: 'editBinding', id: binding.id, key: key.value, name: name.value, url: url.value }); await refresh(); }, 'primary'),
      button('打开目标', () => action({ type: 'openBinding', id: binding.id })),
      button('重新选择输入框', () => action({ type: 'reselectBinding', id: binding.id })),
      button('删除绑定', async () => { if (confirm(`删除 ${binding.key} → ${binding.name} 的绑定？`)) { await action({ type: 'deleteBinding', id: binding.id }); await refresh(); } }, 'danger')
    );
    card.append(row, urlLabel, url, actions); $('targets').append(card);
  }
}
$('reconnect').addEventListener('click', async () => { try { await action({ type: 'reconnect' }); await refresh(); } catch (error) { $('error').textContent = error.message; } });
function showConnection(connection) {
  $('connection').textContent = connection?.message || '请手动运行 QuickShot，或点击“启动 / 连接 QuickShot”。';
  $('connection').dataset.state = connection?.state || 'stopped';
}
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.bindings) refresh().catch(() => {});
  if (area === 'session' && changes.connection) showConnection(changes.connection.newValue);
});
await refresh().catch(error => { $('error').textContent = error.message; });
action({ type: 'connect' }).catch(error => { $('error').textContent = error.message; });
