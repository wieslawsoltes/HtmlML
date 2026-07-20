import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { htmlml as viteHtmlml } from '../vite.mjs';

const manifest = {
  schemaVersion: '1.0', id: 'dev.htmlml.integration', displayName: 'Integration', version: '1.0.0',
  profileVersion: '1.0', entryPoint: 'src/main.ts', assets: ['src/main.ts'], capabilities: ['dom']
};

test('Vite plugin checks sources and emits packaged assets', async () => {
  const root = await mkdtemp(join(tmpdir(), 'htmlml-vite-'));
  const manifestPath = join(root, 'htmlml-component.json');
  await writeFile(manifestPath, JSON.stringify(manifest));
  const plugin = viteHtmlml({ manifest: manifestPath });
  const emitted = [];
  const context = {
    error(value) { throw new Error(typeof value === 'string' ? value : value.message); },
    warn() {},
    emitFile(value) { emitted.push(value); }
  };
  await plugin.buildStart.call(context);
  assert.equal(plugin.transform.call(context, 'document.body.textContent = "ready";', join(root, 'src/main.ts')), null);
  plugin.generateBundle.call(context, {}, { 'dist/main.js': { type: 'chunk', isEntry: true, fileName: 'dist/main.js' } });
  const packaged = JSON.parse(emitted[0].source);
  assert.equal(packaged.entryPoint, 'dist/main.js');
  assert.deepEqual(packaged.assets, ['dist/main.js']);
});

test('runtime forwards abort to the native bridge request', async () => {
  let cancelled;
  globalThis.__htmlMlHostBridge = {
    invoke(request, resolve) { resolve(JSON.stringify({ requestId: JSON.parse(request).requestId, ok: true, result: 7 })); },
    cancel(requestId) { cancelled = requestId; }
  };
  const { htmlml } = await import(`../runtime.mjs?test=${Date.now()}`);
  assert.equal(await htmlml.host.commands.invoke('value'), 7);

  globalThis.__htmlMlHostBridge.invoke = () => {};
  const controller = new AbortController();
  void htmlml.host.commands.invoke('wait', {}, { signal: controller.signal });
  controller.abort();
  assert.equal(typeof cancelled, 'string');
});
