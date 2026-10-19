import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const pageSource = await readFile(new URL('../extension/connect.js', import.meta.url), 'utf8');

function event() {
  const listeners = [];
  return { addListener: listener => listeners.push(listener), fire: (...args) => listeners.forEach(listener => listener(...args)) };
}

async function runPage(sendMessage) {
  const requests = [], changes = event(), clicks = {};
  const elements = Object.fromEntries(['connection', 'error', 'retry'].map(id => [id, {
    textContent: '', addEventListener: (type, listener) => { clicks[id + ':' + type] = listener; }
  }]));
  await vm.runInNewContext(`(async () => {\n${pageSource}\n})()`, {
    chrome: {
      runtime: { sendMessage: message => { requests.push(structuredClone(message)); return sendMessage(message); } },
      storage: { onChanged: changes }
    }, document: { getElementById: id => elements[id] }
  });
  return { requests, elements, changes, retry: () => clicks['retry:click']() };
}

test('the restored connection page connects passively and displays the current state', async () => {
  const h = await runPage(async message => message.type === 'connect' ? { ok: true } : {
    ok: true, connection: { message: 'Windows 托盘程序已连接。' }
  });
  assert.deepEqual(h.requests, [{ type: 'connect' }, { type: 'getState' }]);
  assert.equal(h.elements.connection.textContent, 'Windows 托盘程序已连接。');
  assert.equal(h.elements.error.textContent, '');
});

test('connection failures remain visible and require an explicit retry', async () => {
  let attempts = 0;
  const h = await runPage(async message => {
    if (message.type === 'connect') return ++attempts === 1 ? { ok: false, error: 'native host unavailable' } : { ok: true };
    return { ok: true, connection: { message: 'connected after retry' } };
  });
  assert.deepEqual(h.requests, [{ type: 'connect' }]);
  assert.equal(h.elements.error.textContent, 'native host unavailable');
  await h.retry();
  assert.deepEqual(h.requests, [{ type: 'connect' }, { type: 'connect' }, { type: 'getState' }]);
  assert.equal(h.elements.error.textContent, '');
  assert.equal(h.elements.connection.textContent, 'connected after retry');
});

test('a rejected worker request is displayed without implicit retries or app launch', async () => {
  const h = await runPage(async () => { throw new Error('worker unavailable'); });
  assert.deepEqual(h.requests, [{ type: 'connect' }]);
  assert.equal(h.elements.error.textContent, 'worker unavailable');
});

test('only session connection changes update the page status', async () => {
  const h = await runPage(async () => ({ ok: true }));
  h.changes.fire({ connection: { newValue: { message: 'local state' } } }, 'local');
  assert.equal(h.elements.connection.textContent, '');
  h.changes.fire({ connection: { newValue: { message: 'session state' } } }, 'session');
  assert.equal(h.elements.connection.textContent, 'session state');
  assert.deepEqual(h.requests, [{ type: 'connect' }, { type: 'getState' }]);
});

test('the 2.0 connection interface exposes status and an explicit retry', async () => {
  const html = await readFile(new URL('../extension/connect.html', import.meta.url), 'utf8');
  assert.match(html, /<title>连接剪切板快捷发送<\/title>/);
  assert.match(html, /href="ui\.css"/);
  assert.match(html, /id="connection"/);
  assert.match(html, /id="error"/);
  assert.match(html, /<button id="retry">重新连接<\/button>/);
  assert.match(html, /<script\s+type="module"\s+src="connect\.js"><\/script>/);
});
