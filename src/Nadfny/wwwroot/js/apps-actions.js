// Apps & games: scan, clear an app's cache, uninstall, then offer its leftovers. Every destructive step is confirmed.
import { freed } from './actions.js';
import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, plural } from './format.js';
import { runOp } from './ops.js';
import { animateOut, update } from './store.js';
import { setStreaming } from './views/common.js';

on('apps.item', (a) => update((s) => s.apps.items.set(a.id, a)));

const total = (a) => (a.appBytes ?? 0) + a.dataBytes;

export async function scanApps() {
  update((s) => { s.apps.status = 'scanning'; s.apps.error = null; s.apps.items = new Map(); delete s.progress.apps; });
  setStreaming(true);
  try {
    const list = await call('apps.scan');
    update((s) => { s.apps.items = new Map(list.map((a) => [a.id, a])); s.apps.status = 'done'; });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.apps.status = 'error'; s.apps.error = err.message; });
  } finally {
    setStreaming(false);
  }
}

export async function clearAppCache(app) {
  const caches = app.caches.filter((c) => c.deletable);
  const { confirmed } = await confirmSheet({
    title: `Clear ${app.name}'s cache?`,
    body: `${app.name} rebuilds these files when it needs them. Sign-ins, settings and saved files are kept. Close it first for the best result: files it has open are skipped.`,
    items: caches.map((c) => ({ label: c.path.split('\\').slice(-2).join('\\'), sub: c.path, size: bytes(c.bytes) })),
    confirmLabel: `Clear ${app.name}'s cache (${bytes(app.cacheBytes)})`,
  });
  if (!confirmed) return;
  const key = `app:${app.id}`;
  update((s) => s.busy.add(key));
  try {
    const r = await runOp(`Clearing ${app.name}'s cache`, 'apps.clearCache', { id: app.id }, { key });
    freed(r.freed);
    update((s) => s.apps.items.set(app.id, {
      ...app,
      cacheBytes: Math.max(0, app.cacheBytes - r.freed),
      dataBytes: Math.max(0, app.dataBytes - r.freed),
      caches: app.caches.map((c) => ({ ...c, bytes: 0 })),
    }));
    toast(r.wasRunning
      ? `${bytes(r.freed)} freed. ${app.name} is running, so files it had open were kept.`
      : `${app.name}'s cache cleared. ${bytes(r.freed)} freed.`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

const UNINSTALL_TEXT = {
  win32: (a) => `Nadfny starts ${a.name}'s own uninstaller, the same one Windows Settings runs. Follow its steps; afterwards Nadfny shows anything it left behind.`,
  store: (a) => `Windows removes ${a.name} and the data it keeps in its package folder.`,
  steam: (a) => `Steam asks you to confirm, then removes ${a.name}. Your cloud saves stay in Steam.`,
  epic: (a) => `Opens your Epic Games library, where you remove ${a.name}.`,
  folder: (a) => `${a.name} was not installed by a setup program, so its folder is moved to the Recycle Bin. Saved games kept elsewhere are not touched.`,
};

export async function uninstallApp(app) {
  const isFolder = app.source === 'folder';
  const { confirmed } = await confirmSheet({
    title: isFolder ? `Remove ${app.name}?` : `Uninstall ${app.name}?`,
    body: (UNINSTALL_TEXT[app.source] ?? UNINSTALL_TEXT.win32)(app),
    items: [{ label: app.name, sub: app.installLocation ?? app.publisher ?? '', size: bytes(total(app)) }],
    confirmLabel: isFolder ? `Move ${app.name} to the Recycle Bin (${bytes(app.appBytes ?? 0)})` : `Uninstall ${app.name}`,
  });
  if (!confirmed) return;

  const key = `app:${app.id}`;
  update((s) => s.busy.add(key));
  try {
    const r = await runOp(`Uninstalling ${app.name}`, 'apps.uninstall', { id: app.id }, { key });
    if (!r.removed) {
      toast(app.source === 'epic' ? `Finish in the Epic Games launcher, then rescan.` : `${app.name} is still installed. The uninstaller was closed or did not finish.`, 'bad');
      return;
    }
    freed(app.appBytes ?? 0);
    animateOut([app.id], (s) => s.apps.items.delete(app.id));
    toast(`${app.name} uninstalled.`);
    await offerLeftovers(app, r.leftovers);
  } catch (err) {
    if (err.cancelled) toast(`Stopped waiting. If ${app.name}'s uninstaller is still open, finish it and rescan.`, 'bad');
    else toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

export const stopWaiting = () => call('scan.cancel', { scope: 'uninstall' });

async function offerLeftovers(app, leftovers) {
  const removable = leftovers.filter((f) => f.deletable && f.bytes > 0);
  const locked = leftovers.filter((f) => !f.deletable);
  if (locked.length) toast(`${plural(locked.length, 'folder')} in protected locations (${locked.map((f) => f.path).join(', ')}) need administrator rights to remove.`, 'bad');
  if (!removable.length) return;

  const size = removable.reduce((a, f) => a + f.bytes, 0);
  const { confirmed } = await confirmSheet({
    title: `${app.name} left ${bytes(size)} behind`,
    body: 'These folders still carry its name. They go to the Recycle Bin, so you can restore them.',
    items: removable.map((f) => ({ label: f.path.split('\\').pop(), sub: f.path, size: bytes(f.bytes) })),
    warning: app.isGame ? 'Games often keep saved games and settings in these folders.' : null,
    ack: app.isGame ? 'I do not need these saves' : null,
    cancelLabel: 'Keep them',
    confirmLabel: `Move ${plural(removable.length, 'folder')} to the Recycle Bin (${bytes(size)})`,
  });
  if (!confirmed) return;
  try {
    const r = await runOp(`Removing ${app.name}'s leftovers`, 'apps.removeLeftovers', { id: app.id, paths: removable.map((f) => f.path) });
    freed(size);
    toast(r.failed.length ? `${r.removed} removed, ${r.failed.length} could not be moved (in use?).` : `Leftovers moved to the Recycle Bin.`, r.failed.length ? 'bad' : 'good');
  } catch (err) {
    toast(err.message, 'bad');
  }
}
