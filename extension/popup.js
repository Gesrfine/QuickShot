import { permissionPattern, webUrl } from './shared.mjs';
import { recordShortcut } from './shortcut-input.mjs';
const $ = id => document.getElementById(id);
let currentTab = null;
function showConnection(connection) {
  $('connection').textContent = connection?.message || '请手动运行剪切板快捷发送，或点击“启动 / 连接剪切板快捷发送”。';
  $('connection').dataset.state = connection?.state || 'disconnected';
}
try {
  const [state, tabs] = await Promise.all([chrome.runtime.sendMessage({ type: 'getState' }), chrome.tabs.query({ active: true, currentWindow: true })]);
  currentTab = tabs[0];
  showConnection(state.connection);
  $('name').value = (currentTab?.title || '').slice(0, 80);
  const used = new Set((state.bindings || []).map(binding => binding.key));
  $('key').value = !used.has('C') ? 'C' : [...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'].find(key => !used.has(key)) || '';
  if (!currentTab?.url || currentTab.incognito) throw new Error('请在普通 Edge 窗口中打开需要接收图片的聊天网页。');
  $('default-url').value = webUrl(currentTab.url).origin + '/';
} catch (error) { $('error').textContent = error.message; $('bind').disabled = true; }
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.connection) showConnection(changes.connection.newValue); });
$('manage').addEventListener('click', () => chrome.runtime.openOptionsPage());
recordShortcut($('key'), text => { $('error').textContent = text; });
$('reconnect').addEventListener('click', async () => {
  const result = await chrome.runtime.sendMessage({ type: 'reconnect' });
  if (!result?.ok) $('error').textContent = result?.error || '启动失败。';
});
chrome.runtime.sendMessage({ type: 'connect' }).catch(error => { $('error').textContent = error.message; });
$('bind-form').addEventListener('submit', async event => {
  event.preventDefault(); $('error').textContent = '';
  try {
    const origin = webUrl(currentTab.url).origin;
    const granted = await chrome.permissions.request({ origins: [permissionPattern(origin)] });
    if (!granted) throw new Error('绑定需要允许扩展访问当前网站。');
    $('bind').disabled = true;
    const result = await chrome.runtime.sendMessage({ type: 'beginBinding', tabId: currentTab.id, key: $('key').value, name: $('name').value, url: $('default-url').value });
    if (!result?.ok) throw new Error(result?.error || '无法开始绑定。');
    window.close();
  } catch (error) { $('error').textContent = error.message; $('bind').disabled = false; }
});
