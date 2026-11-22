import assert from 'node:assert/strict';
import test from 'node:test';
import { ConnectionMonitor } from '../src/connectionMonitor.ts';

test('reports powered-off amp and backend/network failure, skips idle online updates', async () => {
  let mode = 'online';
  const offline: unknown[] = [];
  const monitor = new ConnectionMonitor(async () => {
    if (mode === 'error') throw new Error('Server unavailable');
    return { connected: mode === 'online' };
  }, () => false, failure => offline.push(failure));
  await monitor.check();
  assert.equal(offline.length, 0);
  mode = 'offline';
  await monitor.check();
  assert.deepEqual(offline, [undefined]);
  mode = 'error';
  await monitor.check();
  assert.equal((offline[1] as Error).message, 'Server unavailable');
});

test('no overlapping probes; skips writes and ignores result made stale by user action', async () => {
  let finish: ((value: { connected: boolean }) => void) | undefined;
  let calls = 0;
  let offline = 0;
  let blocked = true;
  const monitor = new ConnectionMonitor(() => {
    calls++;
    return new Promise(resolve => { finish = resolve; });
  }, () => blocked, () => { offline++; });
  await monitor.check();
  assert.equal(calls, 0);
  blocked = false;
  const pending = monitor.check();
  await monitor.check();
  assert.equal(calls, 1);
  monitor.invalidate();
  finish?.({ connected: false });
  await pending;
  assert.equal(offline, 0);
  const next = monitor.check();
  finish?.({ connected: false });
  await next;
  assert.equal(offline, 1);
});
