import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ParameterWriteQueue } from '../src/writeQueue.ts';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const tick = () => new Promise<void>(resolve => setImmediate(resolve));

test('serialises writes, coalesces each pending control and protects newer edits from replies', async () => {
  const requests: { id: string; value: number; result: ReturnType<typeof deferred<Record<string, number>>> }[] = [];
  const displays: Record<string, number>[] = [];
  let queue!: ParameterWriteQueue<Record<string, number>>;
  queue = new ParameterWriteQueue(
    (id, value) => {
      const result = deferred<Record<string, number>>();
      requests.push({ id, value, result });
      return result.promise;
    },
    response => displays.push({ ...response, ...queue.pendingValues }),
    () => assert.fail('Unexpected failure'),
    () => {},
  );
  queue.enqueue('gain', 10);
  queue.enqueue('gain', 20);
  queue.enqueue('gain', 30);
  queue.enqueue('bass', 40);
  queue.enqueue('bass', 50);
  assert.equal(requests.length, 1);
  assert.equal(queue.busy, true);
  assert.deepEqual(queue.pendingValues, { gain: 30, bass: 50 });
  requests[0]!.result.resolve({ gain: 10, bass: 0 });
  await tick();
  assert.deepEqual(displays[0], { gain: 30, bass: 50 });
  assert.deepEqual(requests.map(({ id, value }) => ({ id, value })), [{ id: 'gain', value: 10 }, { id: 'gain', value: 30 }]);
  requests[1]!.result.resolve({ gain: 30, bass: 0 });
  await tick();
  assert.deepEqual(displays[1], { gain: 30, bass: 50 });
  assert.equal(requests[2]!.id, 'bass');
  assert.equal(requests[2]!.value, 50);
  requests[2]!.result.resolve({ gain: 30, bass: 50 });
  await tick();
  assert.equal(queue.busy, false);
  assert.deepEqual(queue.pendingValues, {});
});

test('stops and discards pending edits on failure, reports once, and can restart after recovery', async () => {
  const first = deferred<number>();
  const errors: unknown[] = [];
  const responses: number[] = [];
  let writes = 0;
  let notifications = 0;
  const queue = new ParameterWriteQueue(
    async () => { writes++; return writes === 1 ? first.promise : 99; },
    response => responses.push(response),
    error => errors.push(error),
    () => { notifications++; },
  );
  queue.enqueue('gain', 10);
  queue.enqueue('bass', 20);
  const failure = new Error('Disconnected');
  first.reject(failure);
  await tick();
  assert.equal(writes, 1);
  assert.deepEqual(errors, [failure]);
  assert.equal(queue.busy, false);
  assert.deepEqual(queue.pendingValues, {});
  assert.ok(notifications >= 4);
  queue.enqueue('gain', 99);
  await tick();
  assert.deepEqual(responses, [99]);
  assert.equal(queue.busy, false);
});
