// Repository health: one scan of every main repository, then confirmed cleanups per repository.
import { freed } from './actions.js';
import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, plural } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

on('gitHealth.item', (r) => update((s) => s.gitHealth.items.set(r.path, r)));

export const healthBusyKey = (repo) => `health:${repo.path}`;

export async function scanGitHealth() {
  if (state.gitHealth.status === 'scanning') return;
  update((s) => { s.gitHealth.status = 'scanning'; s.gitHealth.error = null; s.gitHealth.items = new Map(); delete s.progress.gitHealth; });
  try {
    const list = await call('gitHealth.scan');
    update((s) => { s.gitHealth.items = new Map(list.map((r) => [r.path, r])); s.gitHealth.status = 'done'; s.gitHealth.finishedAt = Date.now(); });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.gitHealth.status = 'error'; s.gitHealth.error = err.message; });
  }
}

export async function runGc(repo) {
  if (!repo.gc || state.busy.has(healthBusyKey(repo))) return;
  const reclaim = repo.gc.looseBytes + repo.gc.garbageBytes;
  const { confirmed } = await confirmSheet({
    title: `Run git gc in ${repo.name}?`,
    body: 'git packs loose objects and removes unreachable ones older than two weeks. Newer unreachable objects are kept, so no reachable history is lost.',
    items: [{ label: repo.name, sub: repo.path, size: bytes(reclaim) }],
    confirmLabel: `Run git gc (${bytes(reclaim)})`,
  });
  if (!confirmed) return;
  await withHealthBusy(repo, async () => {
    const r = await runOp(`git gc in ${repo.name}`, 'gitHealth.gc', { id: repo.path }, { key: repo.path });
    applyHealth(r.health);
    freed(Math.max(0, r.beforeBytes - r.health.gitBytes));
    toast(`git gc finished. .git went from ${bytes(r.beforeBytes)} to ${bytes(r.health.gitBytes)}.`);
  });
}

export async function pruneLfs(repo) {
  if (!repo.lfs?.toolInstalled || state.busy.has(healthBusyKey(repo))) return;
  const { confirmed } = await confirmSheet({
    title: `Prune the LFS cache in ${repo.name}?`,
    body: 'Deletes local copies of large files that no recent commit uses and that are already pushed. They download again when you check them out. Files that are not pushed yet are kept.',
    items: [{ label: repo.name, sub: 'Git LFS cache', size: bytes(repo.lfs.bytes) }],
    confirmLabel: `Prune LFS cache (${bytes(repo.lfs.bytes)})`,
  });
  if (!confirmed) return;
  await withHealthBusy(repo, async () => {
    const r = await runOp(`Pruning LFS in ${repo.name}`, 'gitHealth.lfsPrune', { id: repo.path }, { key: repo.path });
    applyHealth(r.health);
    freed(Math.max(0, r.beforeBytes - (r.health.lfs?.bytes ?? 0)));
    toast(`LFS cache went from ${bytes(r.beforeBytes)} to ${bytes(r.health.lfs?.bytes ?? 0)}.`);
  });
}

export async function deleteGoneBranches(repo) {
  const names = repo.goneBranches ?? [];
  if (names.length === 0 || state.busy.has(healthBusyKey(repo))) return;
  const n = names.length;
  const { confirmed } = await confirmSheet({
    title: n === 1 ? `Delete branch ${names[0]}?` : `Delete ${plural(n, 'branch')} gone from the remote?`,
    body: 'These branches were deleted on the remote, so only the local copies are left. git deletes a branch only when its commits are already in your current branch; any other branch is kept. The remote is not touched.',
    items: names.map((name) => ({ label: name, sub: `In ${repo.name}`, size: '' })),
    confirmLabel: n === 1 ? `Delete ${names[0]}` : `Delete ${plural(n, 'branch')}`,
  });
  if (!confirmed) return;
  await withHealthBusy(repo, async () => {
    const r = await runOp(`Deleting branches in ${repo.name}`, 'gitHealth.deleteBranches', { id: repo.path, branches: names }, { key: repo.path });
    applyHealth(r.health);
    const kept = r.kept ?? [];
    const message = [
      r.deleted.length ? `${plural(r.deleted.length, 'branch', 'branches')} deleted.` : 'No branch was deleted.',
      kept.length ? `Kept ${kept.length}: ${kept.map((k) => `${k.name} (${k.reason})`).join(' ')}` : '',
    ].filter(Boolean).join(' ');
    toast(message, r.deleted.length === 0 && kept.length > 0 ? 'bad' : 'good');
  });
}

function applyHealth(health) {
  update((s) => s.gitHealth.items.set(health.path, health));
}

async function withHealthBusy(repo, work) {
  const key = healthBusyKey(repo);
  update((s) => s.busy.add(key));
  try {
    await work();
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}
