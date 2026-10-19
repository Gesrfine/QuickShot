const status = document.getElementById('connection'), error = document.getElementById('error');
async function connect() {
  error.textContent = '';
  try {
    const result = await chrome.runtime.sendMessage({ type: 'connect' });
    if (!result?.ok) throw new Error(result?.error || '连接失败。');
    const state = await chrome.runtime.sendMessage({ type: 'getState' });
    if (state.connection) status.textContent = state.connection.message;
  } catch (failure) { error.textContent = failure.message; }
}
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.connection) status.textContent = changes.connection.newValue.message; });
document.getElementById('retry').addEventListener('click', connect);
await connect();
