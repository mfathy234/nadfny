import { freed } from './actions.js';
import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, plural } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

on('finder.item', (item) => {
  if (state.finder.status !== 'scanning' || item.mode !== state.finder.tab) return;
  update((s) => {
    if (item.file) s.finder.items.set(item.file.path, item.file);
    if (item.group) {
      s.finder.groups.push(item.group);
      item.group.files.forEach((f) => s.finder.items.set(f.path, f));
    }
  });
});

export async function scanFinder() {
  const st = state.finder;
  if (st.status === 'scanning' || state.busy.has('finder')) return;
  const mode = st.tab;
  const value = mode === 'large' ? st.threshold : mode === 'downloads' ? st.age : 0;
  update((s) => {
    s.finder.status = 'scanning'; s.finder.error = null;
    s.finder.items = new Map(); s.finder.groups = []; s.finder.selected.clear(); delete s.progress.finder;
  });
  try {
    const result = await call('finder.scan', { mode, value });
    update((s) => {
      s.finder.items = new Map(result.files.map((f) => [f.path, f]));
      s.finder.groups = result.groups; s.finder.status = 'done';
    });
  } catch (err) {
    update((s) => {
      s.finder.status = err.cancelled ? 'cancelled' : 'error';
      s.finder.error = err.cancelled ? null : err.message;
      s.finder.items.clear(); s.finder.groups = []; s.finder.selected.clear();
    });
  }
}

export function changeFinder(key, value) {
  if (state.finder.status === 'scanning' || state.busy.has('finder')) return;
  update((s) => { s.finder[key] = value; s.finder.status = 'idle'; s.finder.items.clear(); s.finder.groups = []; s.finder.selected.clear(); s.finder.error = null; });
}

export async function cancelFinder() {
  try { await call('scan.cancel', { scope: 'finder' }); }
  catch (err) { toast(err.message, 'bad'); }
}

export function toggleFinder(path, on) {
  const st = state.finder;
  if (st.status !== 'done' || state.busy.has('finder') || !st.items.has(path)) return;
  const group = st.groups.find((g) => g.files.some((f) => f.path === path));
  if (on && group && group.files.filter((f) => !st.selected.has(f.path)).length <= 1) {
    toast('Keep at least one copy of each duplicate group.');
    update();
    return;
  }
  update((s) => on ? s.finder.selected.add(path) : s.finder.selected.delete(path));
}

export async function recycleFinder() {
  const st = state.finder;
  if (st.status !== 'done' || state.busy.has('finder')) return;
  const mode = st.tab;
  const items = [...st.selected].map((p) => st.items.get(p)).filter(Boolean);
  if (!items.length) return;
  const total = items.reduce((n, f) => n + f.bytes, 0);
  // Hold the scan/filter controls while the confirmation is open too.
  update((s) => s.busy.add('finder'));
  try {
    const { confirmed } = await confirmSheet({
      title: `Move ${plural(items.length, 'item')} to the Recycle Bin?`,
      body: 'You can restore these items from the Recycle Bin. Space is released when the Bin is emptied.',
      items: items.map((f) => ({ label: f.name, sub: f.path, size: bytes(f.bytes) })),
      warning: 'These may be personal files or files your tools need. Review every path before removing them.',
      ack: 'I have reviewed these items and want to remove them',
      confirmLabel: `Move ${plural(items.length, 'item')} to the Recycle Bin (${bytes(total)})`,
    });
    if (!confirmed) return;
    const result = await runOp('Moving files to the Recycle Bin', 'finder.remove', { mode, paths: items.map((f) => f.path) }, { key: 'finder' });
    const removed = new Set(result.removed);
    update((s) => {
      removed.forEach((p) => { s.finder.items.delete(p); s.finder.selected.delete(p); });
      s.finder.groups = s.finder.groups.map((g) => {
        const files = g.files.filter((f) => !removed.has(f.path));
        return { ...g, files, wastedBytes: files.reduce((n, f) => n + f.bytes, 0) - Math.max(0, ...files.map((f) => f.bytes)) };
      }).filter((g) => g.files.length > 1).sort((a, b) => b.wastedBytes - a.wastedBytes);
      if (mode === 'duplicates') s.finder.items = new Map(s.finder.groups.flatMap((g) => g.files.map((f) => [f.path, f])));
    });
    freed(0); // Refresh drive and Bin totals; recycling has not freed space yet.
    toast(`${plural(result.removed.length, 'item')} moved to the Recycle Bin.${result.failed.length ? ` ${plural(result.failed.length, 'item')} could not be moved. Scan again before retrying.` : ''}`, result.failed.length ? 'bad' : 'good');
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete('finder'));
  }
}
