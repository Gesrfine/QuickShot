import { request } from 'node:http';

// Edge chooses the debugging port. Node fetch rejects some otherwise valid local
// ports as browser-unsafe; native HTTP keeps these owned-browser tests reliable.
export function cdpJson(address, { method = 'GET', timeout = 10000 } = {}) {
  const url = new URL(address);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.pathname.startsWith('/json/'))
    throw new Error('CDP HTTP requests must target an owned loopback debugging endpoint.');
  return new Promise((resolve, reject) => {
    const req = request(url, { method }, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => {
        body += chunk;
        if (body.length > 2 * 1024 * 1024) req.destroy(new Error('Oversized CDP HTTP response.'));
      });
      response.on('error', reject);
      response.on('end', () => {
        if (response.statusCode !== 200) { reject(new Error('CDP HTTP status: ' + response.statusCode)); return; }
        try { resolve(JSON.parse(body)); } catch (error) { reject(error); }
      });
    });
    req.setTimeout(timeout, () => req.destroy(new Error('CDP HTTP request timed out.')));
    req.on('error', reject);
    req.end();
  });
}
