// Single app state + batched re-render (one render per animation frame).

const scan = () => ({ status: 'idle', error: null, items: new Map(), finishedAt: null });

export const state = {
  view: 'overview',
  info: { roots: [], drives: [], allDrives: false },
  progress: {},
  worktrees: { ...scan(), filter: 'all' },
  docker: { status: 'idle', error: null, report: null, tab: 'images' },
  artifacts: { ...scan(), kind: 'all', age: 0, expanded: new Set() },
  caches: scan(),
  apps: { ...scan(), filter: 'all', query: '', expanded: new Set(), queueRunning: false },
  windows: { status: 'idle', error: null, items: [] },
  recycle: { status: 'idle', error: null, info: null },
  system: { status: 'idle', error: null, info: null },
  update: { status: 'idle', error: null, info: null },
  explore: { path: null, status: 'idle', error: null, items: new Map() },
  selection: { worktrees: new Set(), artifacts: new Set(), apps: new Set() },
  busy: new Set(),
  ops: new Map(), // running host actions with live progress (see ops.js)
  batch: null, // { label, count, done, failed, freedBytes } while a multi-item removal runs
  leaving: new Set(),
  freed: 0,
};

let renderer = () => {};
let queued = false;

export function setRenderer(fn) { renderer = fn; }

export function update(mutator) {
  if (mutator) mutator(state);
  if (queued) return;
  queued = true;
  requestAnimationFrame(() => {
    queued = false;
    renderer();
  });
}

/** Plays the row collapse, then drops the keys from state. */
export function animateOut(keys, drop) {
  const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
  update((s) => keys.forEach((k) => s.leaving.add(k)));
  setTimeout(() => update((s) => {
    keys.forEach((k) => s.leaving.delete(k));
    drop(s);
  }), reduce ? 0 : 240);
}
