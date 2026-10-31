import assert from 'node:assert/strict';
import { test } from 'node:test';

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
const storage = new Map();
globalThis.localStorage = { getItem: (k) => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: (k) => storage.delete(k) };

const { state } = await import('../../src/Nadfny/wwwroot/js/store.js');
const updates = await import('../../src/Nadfny/wwwroot/js/updates.js');

const info = (over = {}) => ({ currentVersion: '0.4.1', mode: 'installed', available: true, latestVersion: '0.4.2', releaseUrl: 'u', ...over });
const reply = (id, result) => receive({ data: { type: 'result', id, ok: true, result } });
const fail = (id, error) => receive({ data: { type: 'result', id, ok: false, error } });

function host({ check, download }) {
  requests.length = 0;
  respond = ({ id, method }) => {
    queueMicrotask(() => {
      if (method === 'update.check') reply(id, check);
      else if (method === 'update.download') (download === 'fail' ? fail(id, 'network') : reply(id, '0.4.2'));
    });
  };
}
const methods = () => requests.map((r) => r.method);
const reset = () => { state.update = { status: 'idle', error: null, info: null }; updates.resetUpdateChecks(); };

test('installed copy downloads automatically after a check, then shows Update ready', async () => {
  reset();
  host({ check: info() });
  await updates.checkForUpdates();
  assert.deepEqual(methods(), ['update.check', 'update.download']);
  assert.equal(state.update.status, 'ready');
});

test('portable copy only shows available', async () => {
  reset();
  host({ check: info({ mode: 'portable' }) });
  await updates.checkForUpdates();
  assert.deepEqual(methods(), ['update.check']);
  assert.equal(state.update.status, 'available');
});

test('failed automatic download goes back to available without an error and retries next check', async () => {
  reset();
  host({ check: info(), download: 'fail' });
  await updates.checkForUpdates();
  assert.equal(state.update.status, 'available');
  assert.equal(state.update.error, null);
  host({ check: info() });
  await updates.checkForUpdates();
  assert.equal(state.update.status, 'ready');
});

test('not now hides the card but a newer version shows it again', async () => {
  reset();
  host({ check: info({ mode: 'portable' }) });
  await updates.checkForUpdates();
  assert.ok(updates.updateCard());
  updates.dismissUpdate();
  assert.equal(updates.updateCard(), null);
  host({ check: info({ mode: 'portable', latestVersion: '0.4.3' }) });
  await updates.checkForUpdates();
  assert.ok(updates.updateCard());
});

test('a stored dismissed version from an older build does not block anything', async () => {
  reset();
  storage.set('nadfny.update.dismissed', '0.4.2');
  host({ check: info({ mode: 'portable' }) });
  await updates.checkForUpdates();
  assert.ok(updates.updateCard());
});

test('stale check is throttled to about an hour and never runs twice at once', async () => {
  reset();
  host({ check: info({ available: false }) });
  await updates.checkIfStale(10_000_000);
  await updates.checkIfStale(10_000_000 + 30 * 60_000);
  assert.deepEqual(methods(), ['update.check']);
  await updates.checkIfStale(10_000_000 + 61 * 60_000);
  assert.deepEqual(methods(), ['update.check', 'update.check']);
  reset();
  host({ check: info({ available: false }) });
  await Promise.all([updates.checkForUpdates(), updates.checkForUpdates()]);
  assert.deepEqual(methods(), ['update.check']);
});
