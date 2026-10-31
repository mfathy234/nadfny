// Temp breakdown: what is in Temp by name pattern, per-group clearing and the user's keep list.
// Clearing goes through confirmSheet first; keep-list changes are not destructive and skip it.
import { call } from './bridge.js';
import { freed } from './actions.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, plural } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

export async function loadTempBreakdown() {
  update((s) => { s.tempBreakdown.status = 'loading'; s.tempBreakdown.error = null; });
  try {
    const data = await call('temp.breakdown');
    update((s) => { s.tempBreakdown.data = data; s.tempBreakdown.status = 'done'; });
  } catch (err) {
    update((s) => { s.tempBreakdown.status = 'error'; s.tempBreakdown.error = err.message; });
  }
}

export function toggleTempBreakdown() {
  const open = !state.tempBreakdown.open;
  update((s) => { s.tempBreakdown.open = open; });
  if (open && state.tempBreakdown.status !== 'loading') loadTempBreakdown();
}

export async function clearTempGroup(group) {
  const key = `tempgroup:${group.id}`;
  if (state.busy.has(key) || state.caches.status === 'scanning') return;
  const { confirmed } = await confirmSheet({
    title: `Clear ${group.pattern}?`,
    body: `${plural(group.count, 'item')} in Temp match this name.${group.recentCount ? ` ${plural(group.recentCount, 'item')} changed in the last 24 hours (${bytes(group.recentBytes)}) will be kept.` : ''}`,
    warning: group.kind === 'links'
      ? 'Only the broken links are removed. Nothing they used to point to is touched.'
      : 'Files are deleted permanently. Files in use are skipped.',
    items: [{ label: group.pattern, sub: state.tempBreakdown.data?.path, size: bytes(group.bytes) }],
    confirmLabel: `Clear ${group.pattern} (${bytes(group.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(key));
  try {
    const n = await runOp(`Clearing ${group.pattern}`, 'temp.clearGroup', { id: group.id }, { key });
    freed(n);
    update((s) => {
      const temp = s.caches.items.get('temp');
      if (temp) s.caches.items.set('temp', { ...temp, bytes: Math.max(0, temp.bytes - n) });
    });
    toast(`${group.pattern} cleared. ${bytes(n)} freed.`);
    await loadTempBreakdown();
  } catch (err) {
    toast(err.message, 'bad');
    loadTempBreakdown();
  } finally {
    update((s) => s.busy.delete(key));
  }
}

async function changeKeepList(method, pattern, message) {
  try {
    await call(method, { pattern });
    toast(message);
    await loadTempBreakdown();
  } catch (err) {
    toast(err.message, 'bad');
  }
}

export const keepTempPattern = (pattern) => changeKeepList('temp.keep.add', pattern, `${pattern} will always be kept. Rescan to update the size.`);
export const unkeepTempPattern = (pattern) => changeKeepList('temp.keep.remove', pattern, `${pattern} is no longer kept. Rescan to update the size.`);
