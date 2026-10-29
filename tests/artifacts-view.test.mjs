import assert from 'node:assert/strict';
import { test } from 'node:test';

globalThis.window = {};
// The view's imports (history hints, store) schedule renders; Node has no animation frames.
globalThis.requestAnimationFrame ??= (fn) => setTimeout(fn, 0);
const artifacts = await import('../src/Nadfny/wwwroot/js/views/artifacts.js');
const { visibleArtifacts } = artifacts;
const { state } = await import('../src/Nadfny/wwwroot/js/store.js');

function item(path, age, bytes, outputAge = 0) {
  return { path, projectPath: path, kind: 'build', tool: 'Flutter', bytes,
    projectLastUsedUtc: new Date(Date.now() - age * 864e5).toISOString(),
    lastWriteUtc: new Date(Date.now() - outputAge * 864e5).toISOString() };
}

test('age filters follow source activity even when output was rebuilt recently', () => {
  state.artifacts.kind = 'other';
  state.artifacts.items = new Map([
    ['old', item('old', 400, 10)],
    ['active', item('active', 2, 100, 500)],
    ['half', item('half', 200, 20)],
  ]);
  state.artifacts.age = 180;
  assert.deepEqual(visibleArtifacts().map(a => a.path).sort(), ['half', 'old']);
  state.artifacts.age = 365;
  assert.deepEqual(visibleArtifacts().map(a => a.path), ['old']);
});

test('unknown source activity is kept out of stale selections', () => {
  state.artifacts.age = 30;
  state.artifacts.items = new Map([['unknown', { ...item('unknown', 400, 10, 500), projectLastUsedUtc: null }]]);
  assert.deepEqual(visibleArtifacts(), []);
  state.artifacts.age = 0;
  assert.equal(visibleArtifacts().length, 1);
});

test('project groups sort by total size or oldest source use, with unknown dates last', () => {
  const items = [item('old', 400, 10), item('large', 20, 100), item('recent', 2, 1),
    { ...item('unknown', 0, 50), projectLastUsedUtc: null }];
  assert.deepEqual(artifacts.groupArtifacts(items).map(g => g.key), ['large', 'unknown', 'old', 'recent']);
  assert.deepEqual(artifacts.groupArtifacts(items, 'oldest').map(g => g.key), ['old', 'large', 'recent', 'unknown']);
  const sameProject = [{ ...item('a', 400, 20), projectPath: 'repo' }, { ...item('b', 400, 30), projectPath: 'repo' }];
  assert.equal(artifacts.groupArtifacts(sameProject)[0].total, 50);
});
