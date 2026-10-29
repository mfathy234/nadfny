import assert from 'node:assert/strict';
import { test } from 'node:test';

// Exercise the real actions through a fake WebView transport, without starting the native host.
let receive;
let respond;
const requests = [];
globalThis.window = { chrome: { webview: {
  addEventListener: (_, listener) => { receive = listener; },
  postMessage: (text) => {
    const request = JSON.parse(text);
    requests.push(request);
    respond?.(request);
  },
} } };
globalThis.requestAnimationFrame = () => 0;
globalThis.Node = class {};
const fakeElement = () => ({ classList: { add() {} }, append() {}, addEventListener() {}, setAttribute() {} });
globalThis.document = { createElement: fakeElement, createTextNode: (text) => text, getElementById: fakeElement };

const { state } = await import('../../src/Nadfny/wwwroot/js/store.js');
const { changeFinder, scanFinder, toggleFinder } = await import('../../src/Nadfny/wwwroot/js/finder-actions.js');

test('duplicate selection always leaves one copy and can change which copy is kept', () => {
  const timer = globalThis.setTimeout;
  globalThis.setTimeout = () => 0;
  try {
    const files = ['a', 'b', 'c'].map((path) => ({ path, bytes: 100 }));
    Object.assign(state.finder, { status: 'done', items: new Map(files.map((f) => [f.path, f])), groups: [{ id: 'same', files }], selected: new Set() });
    files.forEach((f) => toggleFinder(f.path, true));
    assert.deepEqual([...state.finder.selected], ['a', 'b']);
    toggleFinder('a', false);
    toggleFinder('c', true);
    assert.deepEqual([...state.finder.selected], ['b', 'c']);
  } finally { globalThis.setTimeout = timer; }
});

test('cancelled scan clears streamed items and does not retain selectable results', async () => {
  Object.assign(state.finder, { status: 'idle', tab: 'large' });
  respond = ({ id, method }) => {
    assert.equal(method, 'finder.scan');
    receive({ data: { type: 'event', name: 'finder.item', data: { mode: 'large', file: { path: 'streamed' } } } });
    assert.equal(state.finder.items.size, 1);
    receive({ data: { type: 'result', id, ok: false, error: 'Cancelled.', cancelled: true } });
  };
  await scanFinder();
  assert.equal(state.finder.status, 'cancelled');
  assert.equal(state.finder.items.size, 0);
  toggleFinder('streamed', true);
  assert.equal(state.finder.selected.size, 0);
});

test('scan sends the selected threshold and only accepts events for its own tab', async () => {
  Object.assign(state.finder, { status: 'idle', tab: 'large', threshold: 500 });
  respond = ({ id, params }) => {
    assert.deepEqual(params, { mode: 'large', value: 500 });
    changeFinder('tab', 'downloads');
    assert.equal(state.finder.tab, 'large');
    receive({ data: { type: 'event', name: 'finder.item', data: { mode: 'downloads', file: { path: 'wrong-tab' } } } });
    assert.equal(state.finder.items.size, 0);
    receive({ data: { type: 'result', id, ok: true, result: { files: [{ path: 'large-file' }], groups: [] } } });
  };
  await scanFinder();
  assert.equal(state.finder.status, 'done');
  assert.deepEqual([...state.finder.items.keys()], ['large-file']);
  toggleFinder('large-file', true);
  changeFinder('threshold', 1024);
  assert.equal(state.finder.status, 'idle');
  assert.equal(state.finder.items.size, 0);
  assert.equal(state.finder.selected.size, 0);
  assert.equal(requests.length, 2);
});
