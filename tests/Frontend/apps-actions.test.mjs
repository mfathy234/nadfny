import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { SourceTextModule, SyntheticModule, createContext } from 'node:vm';

const app = (id) => ({ id, name: id, source: 'win32', appBytes: 10, dataBytes: 2, caches: [] });

async function harness(responses, runner) {
  const apps = [app('one'), app('two'), app('three')];
  const state = { apps: { items: new Map(apps.map((a) => [a.id, a])), queueRunning: false }, selection: { apps: new Set(apps.map((a) => a.id)) }, busy: new Set() };
  const sheets = [];
  const calls = [];
  const batches = [];
  const context = createContext({ Map });
  const dependencies = {
    './actions.js': { freed: () => {} },
    './bridge.js': { call: async (method, params) => { calls.push({ method, params }); return runner(method, params); }, on: () => {} },
    './dom.js': { toast: () => {}, confirmSheet: async (sheet) => { sheets.push(sheet); const response = responses.shift(); return typeof response === 'function' ? response(sheet) : response ?? { confirmed: false }; } },
    './format.js': { bytes: (n) => `${n} B`, plural: (n, noun) => `${n} ${noun}${n === 1 ? '' : 's'}` },
    './ops.js': {
      startBatch: (...args) => batches.push(['start', ...args]),
      batchStep: (result) => batches.push(['step', result]), endBatch: () => batches.push(['end']),
      runOp: async (label, method, params) => { calls.push({ method, params }); return runner(method, params); },
    },
    './store.js': { state, update: (fn) => fn?.(state), animateOut: (ids, fn) => fn(state) },
    './views/common.js': { setStreaming: () => {} },
  };
  const source = await readFile(new URL('../../src/Nadfny/wwwroot/js/apps-actions.js', import.meta.url), 'utf8');
  const module = new SourceTextModule(source, { context });
  await module.link((specifier) => {
    const exports = dependencies[specifier];
    return new SyntheticModule(Object.keys(exports), function () {
      for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
    }, { context });
  });
  await module.evaluate();
  return { apps, state, sheets, calls, batches, actions: module.namespace };
}

test('cancelling the queue confirmation is a no-op', async () => {
  const h = await harness([{ confirmed: false }], () => { throw new Error('Must not run'); });
  await h.actions.uninstallApps(h.apps);
  assert.equal(h.calls.length, 0);
  assert.equal(h.batches.length, 0);
  assert.equal(h.state.apps.items.size, 3);
});

test('queue runs sequentially, continues after failure and reviews leftovers once', async () => {
  let active = 0;
  let maximum = 0;
  const h = await harness([{ confirmed: true, option: true }, { confirmed: true }, { confirmed: false }], async (method, params) => {
    assert.equal(method, 'apps.uninstallQueue');
    assert.equal(params.quiet, true);
    maximum = Math.max(maximum, ++active);
    await Promise.resolve();
    active--;
    if (params.paths[0] === 'two') throw new Error('Failed');
    return [{ removed: true, leftovers: [{ path: `C:\\Temp\\${params.paths[0]}`, bytes: 5, deletable: true }] }];
  });
  await h.actions.uninstallApps(h.apps);
  assert.equal(maximum, 1);
  assert.deepEqual(h.calls.map((c) => c.params.paths[0]), ['one', 'two', 'three']);
  assert.equal(h.sheets.length, 3);
  assert.equal(h.sheets[0].option.checked, true);
  assert.equal(h.sheets[1].items[1].sub, 'Failed');
  assert.equal(h.sheets[2].items.length, 2);
  assert.equal(h.state.apps.items.size, 1);
  assert.equal(h.state.apps.queueRunning, false);
  assert.equal(h.state.busy.size, 0);
  assert.equal(h.batches.filter((b) => b[0] === 'step').length, 3);
});

test('an uncertain uninstall waits for acknowledgement before starting the next', async () => {
  const h = await harness([{ confirmed: true, option: false }, { confirmed: false }, { confirmed: true }], async () =>
    [{ removed: false, leftovers: [], needsReview: true, error: 'Still running' }]);
  await h.actions.uninstallApps(h.apps);
  assert.equal(h.calls.length, 1);
  assert.ok(h.sheets[1].ack);
  assert.equal(h.sheets[2].items.length, 3);
  assert.equal(h.sheets[2].items[1].sub, 'Not started. Queue stopped.');
  assert.equal(h.state.busy.size, 0);
});

test('forced removal names the backup and folders, and cancellation removes nothing', async () => {
  const h = await harness([{ confirmed: false }], async (method) => {
    assert.equal(method, 'apps.forcePreview');
    return { backupPath: 'C:\\Local\\Nadfny\\backups\\one-date.reg', needsAdmin: true,
      folders: [{ path: 'C:\\Temp\\one', bytes: 5, deletable: true }, { path: 'C:\\ProgramData\\one', bytes: 8, deletable: false }] };
  });
  await h.actions.forceRemoveApp(h.apps[0]);
  assert.equal(h.calls.length, 1);
  assert.match(h.sheets[0].body, /backups\\one-date.reg/);
  assert.match(h.sheets[0].body, /administrator prompt/);
  assert.ok(h.sheets[0].warning);
  assert.ok(h.sheets[0].ack);
  assert.equal(h.sheets[0].items[2].label, 'Needs administrator, kept');
  assert.equal(h.sheets[0].confirmLabel, 'Remove entry and leftovers (5 B)');
  assert.equal(h.state.apps.items.size, 3);
  assert.equal(h.state.busy.size, 0);
});

test('queue cannot start while an unselected app action is busy', async () => {
  const h = await harness([{ confirmed: true }], () => { throw new Error('Must not run'); });
  h.state.busy.add('app:unselected');
  await h.actions.uninstallApps(h.apps);
  assert.equal(h.sheets.length, 0);
  assert.equal(h.calls.length, 0);
});

test('queue keeps other app actions blocked through its result and leftover review', async () => {
  const checkLock = async () => {
    assert.equal(h.state.apps.queueRunning, true);
    const count = h.calls.length;
    await h.actions.forceRemoveApp(app('unselected'));
    await h.actions.clearAppCache(app('unselected'));
    await h.actions.uninstallApp(app('unselected'));
    assert.equal(h.calls.length, count);
    return { confirmed: false };
  };
  const h = await harness([{ confirmed: true }, checkLock, checkLock], async () =>
    [{ removed: true, leftovers: [{ path: 'C:\\Temp\\one', bytes: 5, deletable: true }] }]);
  await h.actions.uninstallApps([h.apps[0]]);
  assert.equal(h.sheets.length, 3);
  assert.equal(h.state.apps.queueRunning, false);
});
