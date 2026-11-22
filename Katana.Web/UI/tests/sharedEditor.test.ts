import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { createServer } from 'vite';

const source = (path: string) => readFile(new URL(path, import.meta.url), 'utf8');

test('the standalone app injects its API into the editor', async () => {
  const standalone = await source('../src/App.vue');
  const editor = await source('../src/editor/KatanaEditor.vue');
  assert.match(standalone, /<KatanaEditor\s+:api="api"[\s\S]*?\/>/);
  assert.match(editor, /defineProps<\{[\s\S]*?api: ReturnType<typeof createKatanaApi>/);
  assert.match(editor, /const api = props\.api;/);
  assert.doesNotMatch(editor, /from ['"]\.\/api['"]/);
});

test('the app and editor resolve the same local Vue runtime', async () => {
  const server = await createServer({ server: { middlewareMode: true }, appType: 'custom' });
  try {
    const main = fileURLToPath(new URL('../src/main.ts', import.meta.url));
    const editor = fileURLToPath(new URL('../src/editor/KatanaEditor.vue', import.meta.url));
    const local = await server.pluginContainer.resolveId('vue', main);
    const shared = await server.pluginContainer.resolveId('vue', editor);
    assert.ok(local);
    assert.ok(shared);
    assert.equal(shared.id, local.id);
    assert.ok(local.id.startsWith(fileURLToPath(new URL('../node_modules/', import.meta.url))));
  } finally {
    await server.close();
  }
});
