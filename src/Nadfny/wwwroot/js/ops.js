// Long-running actions (deletes, prunes, cleanups) with live progress from the host.
import { call, on } from './bridge.js';
import { state, update } from './store.js';

let seq = 0;
const listeners = new Map(); // op id -> callback(progress)

on('op.progress', (p) => {
  update((s) => {
    const op = s.ops.get(p.id);
    if (op) s.ops.set(p.id, { ...op, phase: p.phase, done: p.done, total: p.total, current: p.current });
  });
  listeners.get(p.id)?.(p);
});

// Elapsed time on running operations ticks once a second while anything is running.
let ticker = null;
function ensureTicker() {
  if (ticker) return;
  ticker = setInterval(() => {
    if (state.ops.size === 0 && !state.batch) { clearInterval(ticker); ticker = null; return; }
    update();
  }, 1000);
}

/**
 * Runs one host action with a progress row. `key` ties it to a list row (e.g. a worktree path)
 * so that row can show its own percentage.
 */
export async function runOp(label, method, params = {}, { key, onProgress } = {}) {
  const id = `op${++seq}`;
  update((s) => s.ops.set(id, { id, key, label, phase: 'Starting', done: 0, total: 0, startedAt: Date.now() }));
  if (onProgress) listeners.set(id, onProgress);
  ensureTicker();
  try {
    return await call(method, { ...params, opId: id });
  } finally {
    listeners.delete(id);
    update((s) => s.ops.delete(id));
  }
}

/** A batch groups many operations under one heading ("Removing 8 worktrees"). */
export function startBatch(label, count, totalBytes) {
  update((s) => { s.batch = { label, count, done: 0, failed: 0, freedBytes: 0, totalBytes, startedAt: Date.now() }; });
  ensureTicker();
}

export function batchStep({ ok, bytes = 0 }) {
  update((s) => {
    if (!s.batch) return;
    s.batch = { ...s.batch, done: s.batch.done + 1, failed: s.batch.failed + (ok ? 0 : 1), freedBytes: s.batch.freedBytes + (ok ? bytes : 0) };
  });
}

export function endBatch() { update((s) => { s.batch = null; }); }

/** Runs fn over items with at most `limit` in flight. */
export async function pool(items, limit, fn) {
  const queue = [...items];
  const workers = Array.from({ length: Math.min(limit, queue.length) }, async () => {
    while (queue.length) await fn(queue.shift());
  });
  await Promise.all(workers);
}

export const opFor = (key) => [...state.ops.values()].find((o) => o.key === key);

export const opFraction = (op) => (op && op.total > 0 ? Math.min(1, op.done / op.total) : null);

export function elapsed(since) {
  const s = Math.max(0, Math.round((Date.now() - since) / 1000));
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
