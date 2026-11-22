import assert from 'node:assert/strict';
import { test } from 'node:test';
import { api } from '../src/api.ts';

// No server or MIDI access: verify the same-origin request contract directly.
test('uses the documented API routes and JSON mutation headers including bodyless POSTs', async () => {
  const originalFetch = globalThis.fetch;
  const requests: { url: string; method: string; body: unknown; headers: Headers }[] = [];
  globalThis.fetch = async (input, init) => {
    requests.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body, headers: new Headers(init?.headers) });
    return init?.method === 'DELETE' ? new Response(null, { status: 204 }) : Response.json({});
  };
  try {
    await api.parameters();
    await api.device();
    await api.connection();
    await api.connect();
    await api.setParameter('gain', 40);
    await api.commit();
    await api.presets();
    await api.savePreset('Clean');
    await api.renamePreset('id/with space', 'Crunch');
    await api.deletePreset('id/with space');
    await api.applyPreset('id/with space');
    assert.deepEqual(requests.map(({ url, method }) => [method, url]), [
      ['GET', '/api/parameters'], ['GET', '/api/device'],
      ['GET', '/api/device/connection'], ['POST', '/api/device/connect'],
      ['PUT', '/api/device/parameters/gain'], ['POST', '/api/device/slots/1/commit'], ['GET', '/api/presets'],
      ['POST', '/api/presets'], ['PUT', '/api/presets/id%2Fwith%20space'],
      ['DELETE', '/api/presets/id%2Fwith%20space'], ['POST', '/api/presets/id%2Fwith%20space/slots/1/apply'],
    ]);
    for (const request of requests) {
      if (request.method !== 'GET') assert.equal(request.headers.get('Content-Type'), 'application/json');
    }
    assert.equal(requests[4]!.body, JSON.stringify({ value: 40 }));
    assert.equal(requests[7]!.body, JSON.stringify({ name: 'Clean' }));
    assert.equal(requests[8]!.body, JSON.stringify({ name: 'Crunch' }));
    assert.equal(requests[3]!.body, undefined);
  } finally { globalThis.fetch = originalFetch; }
});

test('shows problem detail, title fallback and non-JSON HTTP failures', async () => {
  const originalFetch = globalThis.fetch;
  try {
    globalThis.fetch = async () => Response.json({ title: 'Unavailable', detail: 'No amp attached.', status: 503 }, { status: 503 });
    await assert.rejects(api.connect(), /No amp attached\./);
    globalThis.fetch = async () => Response.json({ title: 'Bad request', status: 400 }, { status: 400 });
    await assert.rejects(api.commit(), /Bad request/);
    globalThis.fetch = async () => new Response('Server unavailable', { status: 502 });
    await assert.rejects(api.device(), /Request failed \(502\)/);
  } finally { globalThis.fetch = originalFetch; }
});


test('heartbeat supplies a ten-second abort signal covering a stalled HTTP response', async () => {
  const originalFetch = globalThis.fetch;
  const originalTimeout = AbortSignal.timeout;
  let deadline = 0;
  AbortSignal.timeout = milliseconds => {
    deadline = milliseconds;
    return originalTimeout(5);
  };
  globalThis.fetch = async (_input, init) => await new Promise((_resolve, reject) => {
    init?.signal?.addEventListener('abort', () => reject(init.signal?.reason), { once: true });
    // Keep Node alive while its unref'd timeout fires.
    setTimeout(() => {}, 20);
  });
  try {
    await assert.rejects(() => api.connection(), /timeout/i);
    assert.equal(deadline, 10_000);
  } finally {
    globalThis.fetch = originalFetch;
    AbortSignal.timeout = originalTimeout;
  }
});
