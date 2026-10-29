// Scans and cleanup actions. Every destructive action goes through confirmSheet first.
import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, parseDockerSize, plural } from './format.js';
import { batchStep, endBatch, pool, runOp, startBatch } from './ops.js';
import { animateOut, state, update } from './store.js';
import { setStreaming } from './views/common.js';
import { isRisky } from './views/worktrees-model.js';
import { scanToolchains } from './toolchains-actions.js';

on('worktrees.item', (w) => update((s) => s.worktrees.items.set(w.path, w)));
on('artifacts.item', (a) => update((s) => s.artifacts.items.set(a.path, a)));
on('caches.item', (c) => update((s) => s.caches.items.set(c.id, c)));
on('scan.progress', (p) => update((s) => { s.progress[p.scope] = p; }));
on('explore.item', (e) => update((s) => {
  if (s.explore.path && parentOf(e.path) === trimDir(s.explore.path)) s.explore.items.set(e.path, e);
}));

const trimDir = (p) => p.replace(/[\\/]+$/, '').toLowerCase();
const parentOf = (p) => trimDir(p).replace(/\\[^\\]*$/, '');

let activeScans = 0;
function streaming(delta) {
  activeScans += delta;
  setStreaming(activeScans > 0);
}

async function runListScan(key, method) {
  update((s) => { s[key].status = 'scanning'; s[key].error = null; s[key].items = new Map(); delete s.progress[key]; });
  streaming(1);
  try {
    const list = await call(method);
    const idOf = key === 'caches' ? (x) => x.id : (x) => x.path;
    update((s) => { s[key].items = new Map(list.map((x) => [idOf(x), x])); s[key].status = 'done'; s[key].finishedAt = Date.now(); });
  } catch (err) {
    if (!err.cancelled) update((s) => { s[key].status = 'error'; s[key].error = err.message; });
  } finally {
    streaming(-1);
  }
}

export const scanWorktrees = () => {
  update((s) => s.selection.worktrees.clear());
  return runListScan('worktrees', 'worktrees.scan');
};
export const scanArtifacts = () => {
  update((s) => s.selection.artifacts.clear());
  return runListScan('artifacts', 'artifacts.scan');
};
export const scanCaches = () => runListScan('caches', 'caches.scan');

export async function scanWindows() {
  update((s) => { s.windows.status = 'scanning'; s.windows.error = null; });
  try {
    const items = await call('windows.scan');
    update((s) => { s.windows.items = items; s.windows.status = 'done'; });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.windows.status = 'error'; s.windows.error = err.message; });
  }
}

export async function scanDocker() {
  update((s) => { s.docker.status = 'scanning'; s.docker.error = null; });
  try {
    const report = await call('docker.scan');
    update((s) => { s.docker.report = report; s.docker.status = 'done'; });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.docker.status = 'error'; s.docker.error = err.message; });
  }
}

export async function loadInfo() {
  const info = await call('app.info');
  update((s) => { s.info = info; });
}

export async function refreshDrives() {
  const drives = await call('drives.list');
  update((s) => { s.info = { ...s.info, drives }; });
}

export function scanAll() {
  return Promise.allSettled([scanWorktrees(), scanDocker(), scanArtifacts(), scanCaches(), scanToolchains(), scanWindows(), queryRecycleBin()]);
}

/* ---------- Recycle Bin ---------- */

export async function queryRecycleBin() {
  update((s) => { s.recycle.status = 'scanning'; s.recycle.error = null; });
  try {
    const info = await call('recycle.query');
    update((s) => { s.recycle.info = info; s.recycle.status = 'done'; });
  } catch (err) {
    update((s) => { s.recycle.status = 'error'; s.recycle.error = err.message; });
  }
}

export async function emptyRecycleBin() {
  const info = state.recycle.info;
  if (!info?.items) return;
  const { confirmed } = await confirmSheet({
    title: 'Empty the Recycle Bin?',
    body: 'Everything in it is deleted permanently and cannot be restored, on every drive.',
    items: info.drives.map((d) => ({ label: `Drive ${d.drive.slice(0, 2)}`, sub: plural(d.items, 'item'), size: bytes(d.bytes) })),
    confirmLabel: `Empty Recycle Bin (${bytes(info.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add('recycle'));
  try {
    const n = await call('recycle.empty');
    freed(n);
    toast(`Recycle Bin emptied. ${bytes(n)} freed.`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete('recycle'));
    queryRecycleBin();
  }
}

export function freed(n) {
  update((s) => { s.freed += Math.max(0, n); });
  refreshDrives().catch(() => {});
  queryRecycleBin(); // deletes that go to the Bin show up there
}

/* ---------- Worktrees ---------- */

export async function removeWorktrees(paths) {
  const items = paths.map((p) => state.worktrees.items.get(p)).filter(Boolean);
  if (items.length === 0) return;
  const total = items.reduce((a, w) => a + w.bytes, 0);
  const risky = items.filter(isRisky);
  const single = items.length === 1 ? items[0] : null;

  const { confirmed, option } = await confirmSheet({
    title: single ? `Remove ${single.name}?` : `Remove ${items.length} worktrees?`,
    body: single
      ? 'The folder is deleted, including ignored files such as .env, and git forgets the worktree. Pushed or merged commits are not affected.'
      : 'The folders are deleted, including ignored files such as .env, and git forgets the worktrees. Pushed or merged commits are not affected.',
    items: items.map((w) => ({ label: w.name, sub: w.branch ?? 'detached HEAD', size: bytes(w.bytes) })),
    warning: risky.length > 0
      ? `${risky.map((w) => w.name).join(', ')} ${risky.length === 1 ? 'has' : 'have'} uncommitted, unpushed, locked or unverified work. It will be lost.`
      : null,
    ack: risky.length > 0 ? 'Delete the unsaved work too' : null,
    option: { label: single ? `Also delete the local branch ${single.branch ?? ''}`.trim() : 'Also delete their local branches' },
    confirmLabel: single ? `Remove ${single.name} (${bytes(single.bytes)})` : `Remove ${items.length} worktrees (${bytes(total)})`,
  });
  if (!confirmed) return;

  let removedBytes = 0;
  const done = [];
  update((s) => { items.forEach((w) => s.busy.add(w.path)); s.selection.worktrees.clear(); });
  if (items.length > 1) startBatch(`Removing ${plural(items.length, 'worktree')}`, items.length, total);
  // Three at a time: git steps are serialized per repository on the host; file deletion runs in parallel.
  await pool(items, 3, async (w) => {
    let gone = false;
    // Once git has let go (phase "Deleting files"), the worktree no longer exists as a worktree: drop the row
    // right away and let the file cleanup finish in the activity panel.
    const dropRow = () => {
      if (gone) return;
      gone = true;
      animateOut([w.path], (s) => { s.worktrees.items.delete(w.path); s.selection.worktrees.delete(w.path); });
    };
    try {
      // allowUnsaved only for items the user explicitly acknowledged; otherwise the host re-checks git status first.
      await runOp(w.name, 'worktrees.remove', { path: w.path, deleteBranch: option, allowUnsaved: isRisky(w) }, {
        key: w.path,
        onProgress: (p) => { if (p.phase === 'Deleting files') dropRow(); },
      });
      removedBytes += w.bytes;
      done.push(w.path);
      batchStep({ ok: true, bytes: w.bytes });
      dropRow();
    } catch (err) {
      batchStep({ ok: false });
      toast(`${w.name}: ${err.message}`, 'bad');
    } finally {
      update((s) => s.busy.delete(w.path));
    }
  });
  endBatch();
  if (done.length === 0) return;
  freed(removedBytes);
  toast(`${done.length === 1 ? items.find((w) => w.path === done[0]).name : plural(done.length, 'worktree')} removed. ${bytes(removedBytes)} freed.`);
}

/* ---------- Docker ---------- */

const PRUNE_COPY = {
  buildCache: { title: 'Clear the Docker build cache?', body: 'Layers are rebuilt or pulled again on the next build. Images and containers are not touched.', label: 'Clear build cache' },
  unusedImages: { title: 'Remove unused Docker images?', body: 'Images not used by any container are deleted. They are pulled or rebuilt again when needed.', label: 'Remove unused images' },
  danglingImages: { title: 'Remove dangling images?', body: 'Untagged leftovers from rebuilt images are deleted.', label: 'Remove dangling images' },
  stoppedContainers: { title: 'Remove stopped containers?', body: 'Every container that is not running is deleted, along with its writable layer.', label: 'Remove stopped containers' },
  unusedVolumes: { title: 'Remove unused volumes?', body: 'Volumes not attached to any container are deleted. Databases and other data stored in them are gone for good.', label: 'Remove unused volumes' },
};

export async function dockerPrune(kind, estimate, warning) {
  const copy = PRUNE_COPY[kind];
  const { confirmed } = await confirmSheet({
    title: copy.title,
    body: copy.body,
    warning,
    ack: kind === 'unusedVolumes' ? 'I understand this data cannot be recovered' : null,
    confirmLabel: estimate > 0 ? `${copy.label} (${bytes(estimate)})` : copy.label,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(`docker:${kind}`));
  try {
    const message = await runOp(copy.label, 'docker.prune', { kind });
    const n = parseDockerSize(message);
    freed(n);
    toast(n > 0 ? `${bytes(n)} reclaimed inside Docker. Compact the disk to return it to Windows.` : 'Done. Nothing else to reclaim.');
    await scanDocker();
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(`docker:${kind}`));
  }
}

export async function dockerRemove(kind, id, label, size) {
  const { confirmed } = await confirmSheet({
    title: `Remove ${label}?`,
    body: kind === 'volume' ? 'Data stored in this volume cannot be recovered.' : 'It can be pulled or rebuilt again later.',
    ack: kind === 'volume' ? 'I understand this data cannot be recovered' : null,
    confirmLabel: `Remove ${label}${size ? ` (${bytes(size)})` : ''}`,
  });
  if (!confirmed) return;
  const key = `docker:${kind}:${id}`;
  update((s) => s.busy.add(key));
  try {
    await runOp(`Removing ${label}`, 'docker.remove', { kind, id }, { key });
    freed(size ?? 0);
    toast(`${label} removed.`);
    await scanDocker();
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

export async function dockerCompact(disk, used) {
  const { confirmed } = await confirmSheet({
    title: 'Compact Docker’s disk?',
    body: `The disk file is ${bytes(disk.bytes)} but Docker only uses about ${bytes(used)} inside it. Compacting gives the empty space back to Windows.`,
    warning: 'Docker Desktop will be closed and WSL shut down. Windows will ask for administrator permission. This can take several minutes.',
    confirmLabel: `Compact disk (${bytes(disk.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add('docker:compact'));
  try {
    const before = disk.bytes;
    const message = await runOp('Compacting Docker disk', 'docker.compact');
    toast(message);
    await scanDocker();
    const after = state.docker.report?.disk?.bytes ?? before;
    freed(before - after);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete('docker:compact'));
  }
}

/* ---------- Build output ---------- */

export async function removeArtifacts(paths) {
  const items = paths.map((p) => state.artifacts.items.get(p)).filter(Boolean);
  if (items.length === 0) return;
  const total = items.reduce((a, x) => a + x.bytes, 0);
  const projects = new Set(items.map((x) => x.projectPath)).size;
  const { confirmed } = await confirmSheet({
    title: `Delete ${plural(items.length, 'folder')} of build output?`,
    body: `From ${plural(projects, 'project')}. These folders are regenerated by the next install or build, so no source code is touched. They are deleted permanently, not moved to the Recycle Bin.`,
    items: [
      ...items.slice(0, 200).map((x) => ({ label: `${x.project}\\${x.kind}`, sub: x.path, size: bytes(x.bytes) })),
      ...(items.length > 200
        ? [{ label: `and ${plural(items.length - 200, 'more folder')}`, size: bytes(items.slice(200).reduce((a, x) => a + x.bytes, 0)) }]
        : []),
    ],
    confirmLabel: items.length === 1 ? `Delete ${items[0].kind} (${bytes(total)})` : `Delete ${plural(items.length, 'folder')} (${bytes(total)})`,
  });
  if (!confirmed) return;
  update((s) => { items.forEach((x) => s.busy.add(x.path)); s.selection.artifacts.clear(); });
  try {
    const res = await runOp(`Build output (${plural(items.length, 'folder')})`, 'artifacts.remove', { paths: items.map((x) => x.path) });
    const failed = new Set(res.failed.map((f) => f.slice(0, f.indexOf(': ')).toLowerCase()));
    const done = items.filter((x) => !failed.has(x.path.toLowerCase())).map((x) => x.path);
    animateOut(done, (s) => done.forEach((p) => { s.artifacts.items.delete(p); s.selection.artifacts.delete(p); }));
    freed(res.bytes);
    toast(`${plural(res.removed, 'folder')} deleted. ${bytes(res.bytes)} freed.`);
    const kept = res.failed.filter((f) => f.includes('Kept:'));
    const errors = res.failed.filter((f) => !f.includes('Kept:'));
    if (kept.length) toast(`${plural(kept.length, 'folder')} kept because ${kept.length === 1 ? 'it holds' : 'they hold'} source code or tracked files. ${kept[0]}`, 'bad');
    if (errors.length) toast(`${plural(errors.length, 'folder')} could not be deleted (in use?). ${errors[0]}`, 'bad');
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => items.forEach((x) => s.busy.delete(x.path)));
  }
}

/* ---------- Caches ---------- */

export async function clearCache(c) {
  const { confirmed } = await confirmSheet({
    title: `Clear ${c.name}?`,
    body: c.note,
    warning: 'Cached files are deleted permanently. Any local edits or diagnostic history in this folder are lost.',
    items: [{ label: c.name, sub: c.path, size: bytes(c.bytes) }],
    confirmLabel: `Clear ${c.name} (${bytes(c.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(`cache:${c.id}`));
  try {
    const n = await runOp(`Clearing ${c.name}`, 'caches.clear', { id: c.id, path: c.path }, { key: `cache:${c.id}` });
    freed(n);
    update((s) => s.caches.items.set(c.id, { ...c, bytes: Math.max(0, c.bytes - n) }));
    toast(`${c.name} cleared. ${bytes(n)} freed.`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(`cache:${c.id}`));
  }
}

/* ---------- This PC ---------- */

export async function loadSystemInfo(refresh = false) {
  update((s) => { s.system.status = 'scanning'; s.system.error = null; });
  try {
    const info = await call('system.info', { refresh });
    update((s) => { s.system.info = info; s.system.status = 'done'; });
  } catch (err) {
    update((s) => { s.system.status = 'error'; s.system.error = err.message; });
  }
}

/** Copies through the native clipboard (reliable inside WebView2) and confirms with a toast. */
export async function copyText(text, message = 'Copied') {
  try {
    await call('clipboard.set', { text });
    toast(message);
  } catch (err) {
    toast(`Could not copy: ${err.message}`, 'bad');
  }
}

/* ---------- Windows update leftovers ---------- */

export async function cleanWindows(items) {
  const known = items.reduce((a, i) => a + (i.bytes ?? 0), 0);
  const warnings = items.map((i) => i.warning).filter(Boolean);
  const { confirmed } = await confirmSheet({
    title: 'Clean up Windows update files?',
    body: 'Windows Disk Cleanup removes these with its own safety rules. Windows asks for administrator permission, and it can take several minutes.',
    items: items.map((i) => ({ label: i.name, sub: i.note, size: i.bytes != null ? bytes(i.bytes) : 'Size unknown' })),
    warning: warnings.length ? warnings.join(' ') : null,
    confirmLabel: known > 0 ? `Clean up Windows files (${bytes(known)}+)` : 'Clean up Windows files',
  });
  if (!confirmed) return;
  update((s) => s.busy.add('windows'));
  try {
    const message = await runOp('Windows update cleanup', 'windows.clean', { ids: items.map((i) => i.id) });
    toast(message);
    refreshDrives().catch(() => {});
    await scanWindows();
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete('windows'));
  }
}

/* ---------- Explore ---------- */

export async function exploreList(path) {
  update((s) => { s.explore = { path, status: 'scanning', error: null, items: new Map() }; });
  try {
    const list = await call('explore.list', { path });
    if (state.explore.path !== path) return;
    update((s) => { s.explore.items = new Map(list.map((e) => [e.path, e])); s.explore.status = 'done'; });
  } catch (err) {
    if (!err.cancelled && state.explore.path === path) update((s) => { s.explore.status = 'error'; s.explore.error = err.message; });
  }
}

export async function exploreDelete(entry) {
  const { confirmed, option } = await confirmSheet({
    title: `Delete ${entry.name}?`,
    body: entry.isDirectory ? `This folder and everything in it (${plural(entry.files ?? 0, 'file')}) will be moved to the Recycle Bin.` : 'The file will be moved to the Recycle Bin.',
    items: [{ label: entry.name, sub: entry.path, size: bytes(entry.bytes ?? 0) }],
    option: { label: 'Delete permanently instead (faster, cannot be undone)' },
    confirmLabel: `Delete ${entry.name} (${bytes(entry.bytes ?? 0)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(entry.path));
  try {
    await runOp(entry.name, 'explore.delete', { path: entry.path, permanent: option }, { key: entry.path });
    animateOut([entry.path], (s) => s.explore.items.delete(entry.path));
    if (option) freed(entry.bytes ?? 0);
    else refreshDrives().catch(() => {});
    toast(option ? `${entry.name} deleted. ${bytes(entry.bytes ?? 0)} freed.` : `${entry.name} moved to the Recycle Bin. Empty it to free the space.`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(entry.path));
  }
}

export const reveal = (path) => call('shell.reveal', { path }).catch((err) => toast(err.message, 'bad'));

export async function addRoot() {
  const res = await call('settings.addRoot');
  if (!res.added) return;
  update((s) => { s.info = { ...s.info, roots: res.roots }; });
  await Promise.allSettled([scanWorktrees(), scanArtifacts(), scanToolchains()]);
}

export async function setAllDrives(enabled) {
  const res = await call('settings.scanAllDrives', { enabled });
  update((s) => { s.info = { ...s.info, roots: res.roots, allDrives: res.allDrives }; });
  await Promise.allSettled([scanWorktrees(), scanArtifacts(), scanToolchains()]);
}

export async function removeRoot(path) {
  const res = await call('settings.removeRoot', { path });
  update((s) => { s.info = { ...s.info, roots: res.roots }; });
  await Promise.allSettled([scanWorktrees(), scanArtifacts(), scanToolchains()]);
}
