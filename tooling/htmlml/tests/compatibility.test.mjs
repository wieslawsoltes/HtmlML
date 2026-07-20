import test from 'node:test';
import assert from 'node:assert/strict';
import { checkSource, validateManifest } from '../compatibility.mjs';

const manifest = {
  schemaVersion: '1.0', id: 'dev.htmlml.test', displayName: 'Test', version: '1.0.0',
  profileVersion: '1.0', entryPoint: 'main.js', assets: ['main.js'], capabilities: ['dom', 'host.commands']
};

test('manifest and supported source pass', () => {
  assert.deepEqual(validateManifest(manifest), []);
  assert.deepEqual(checkSource('htmlml.host.commands.invoke("save")', manifest), []);
});

test('unsupported and undeclared APIs produce stable diagnostics', () => {
  const diagnostics = checkSource('// localStorage\nnew Worker("x.js");\nhtmlml.host.files.invoke("open")', manifest, 'app.ts');
  assert.deepEqual(diagnostics.map(item => item.code), ['HTMLML1003', 'HTMLML2007']);
  assert.equal(diagnostics[0].line, 2);
});
