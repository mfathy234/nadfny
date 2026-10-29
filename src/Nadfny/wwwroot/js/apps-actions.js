// Apps & games: scan, clear an app's cache, uninstall, then offer its leftovers. Every destructive step is confirmed.
import { freed } from './actions.js';
import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes, plural } from './format.js';
import { batchStep, endBatch, runOp, startBatch } from './ops.js';
import { animateOut, state, update } from './store.js';
import { setStreaming } from './views/common.js';

on('apps.item', (a) => update((s) => s.apps.items.set(a.id, a)));

const total = (a) => (a.appBytes ?? 0) + a.dataBytes;
const appActionBusy = () => state.apps.queueRunning || [...state.busy].some((key) => key.startsWith('app:'));

export async function scanApps() {
  if (state.apps.queueRunning || [...state.busy].some((key) => key.startsWith('app:'))) return;
  update((s) => { s.apps.status = 'scanning'; s.apps.error = null; s.apps.items = new Map(); s.selection.apps.clear(); delete s.progress.apps; });
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
  if (appActionBusy()) return;
  const key = `app:${app.id}`;
  update((s) => s.busy.add(key));
  try {
    const caches = app.caches.filter((c) => c.deletable);
    const { confirmed } = await confirmSheet({
      title: `Clear ${app.name}'s cache?`,
      body: `${app.name} rebuilds these files when it needs them. Sign-ins, settings and saved files are kept. Close it first for the best result: files it has open are skipped.`,
      items: caches.map((c) => ({ label: c.path.split('\\').slice(-2).join('\\'), sub: c.path, size: bytes(c.bytes) })),
      confirmLabel: `Clear ${app.name}'s cache (${bytes(app.cacheBytes)})`,
    });
    if (!confirmed) return;
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
  if (appActionBusy()) return;
  const key = `app:${app.id}`;
  update((s) => s.busy.add(key));
  try {
    const isFolder = app.source === 'folder';
    const { confirmed } = await confirmSheet({
      title: isFolder ? `Remove ${app.name}?` : `Uninstall ${app.name}?`,
      body: (UNINSTALL_TEXT[app.source] ?? UNINSTALL_TEXT.win32)(app),
      items: [{ label: app.name, sub: app.installLocation ?? app.publisher ?? '', size: bytes(total(app)) }],
      warning: 'Uninstalling may remove settings, saved games and other app data.',
      confirmLabel: isFolder ? `Move ${app.name} to the Recycle Bin (${bytes(app.appBytes ?? 0)})` : `Uninstall ${app.name} (${bytes(total(app))})`,
    });
    if (!confirmed) return;

    const r = await runOp(`Uninstalling ${app.name}`, 'apps.uninstall', { id: app.id }, { key });
    if (!r.removed) {
      toast(app.source === 'epic' ? `Finish in the Epic Games launcher, then rescan.` : `${app.name} is still installed. The uninstaller was closed or did not finish.`, 'bad');
      return;
    }
    freed(app.appBytes ?? 0);
    animateOut([app.id], (s) => { s.apps.items.delete(app.id); s.selection.apps.delete(app.id); });
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
  return offerLeftoverGroups([{ app, leftovers }]);
}

async function offerLeftoverGroups(groups) {
  const folders = [...new Map(groups.flatMap(({ app, leftovers }) => leftovers.map((f) => [f.path.toLowerCase(), { ...f, app }]))).values()];
  const removable = folders.filter((f) => f.deletable);
  const locked = folders.filter((f) => !f.deletable);
  if (locked.length) toast(`${plural(locked.length, 'folder')} in protected locations (${locked.map((f) => f.path).join(', ')}) need administrator rights to remove.`, 'bad');
  if (!removable.length) return;

  const size = removable.reduce((a, f) => a + f.bytes, 0);
  const { confirmed } = await confirmSheet({
    title: `${groups.length === 1 ? groups[0].app.name : 'Uninstalled apps'} left ${bytes(size)} behind`,
    body: 'These folders still carry its name. They go to the Recycle Bin, so you can restore them.',
    items: folders.map((f) => ({ label: `${f.app.name}: ${f.path.split('\\').pop()}${f.deletable ? '' : ' (needs administrator, kept)'}`, sub: f.path, size: bytes(f.bytes) })),
    warning: 'These folders may contain settings, saved games and other personal data.',
    ack: 'I do not need the data in these folders',
    cancelLabel: 'Keep them',
    confirmLabel: `Move ${plural(removable.length, 'folder')} to the Recycle Bin (${bytes(size)})`,
  });
  if (!confirmed) return;
  try {
    let removed = 0;
    let failed = 0;
    for (const { app } of groups) {
      const targets = removable.filter((f) => f.app.id === app.id);
      if (!targets.length) continue;
      const r = await runOp(`Removing ${app.name}'s leftovers`, 'apps.removeLeftovers', { id: app.id, paths: targets.map((f) => f.path) });
      removed += r.removed;
      failed += r.failed.length;
      freed(targets.filter((f) => !r.failed.includes(f.path)).reduce((sum, f) => sum + f.bytes, 0));
    }
    toast(failed ? `${removed} removed, ${failed} could not be moved (in use?).` : 'Leftovers moved to the Recycle Bin.', failed ? 'bad' : 'good');
  } catch (err) {
    toast(err.message, 'bad');
  }
}

export async function forceRemoveApp(app) {
  if (appActionBusy()) return;
  const key = `app:${app.id}`;
  update((s) => s.busy.add(key));
  try {
    const preview = await call('apps.forcePreview', { id: app.id });
    const removable = preview.folders.filter((f) => f.deletable);
    const size = removable.reduce((sum, f) => sum + f.bytes, 0);
    const { confirmed } = await confirmSheet({
      title: `Remove ${app.name}'s entry and leftovers?`,
      body: `First, export the uninstall registry key to ${preview.backupPath}. Open that .reg file to restore the entry. Then remove the Windows app entry${preview.needsAdmin ? ' with an administrator prompt' : ''} and move the folders listed below to the Recycle Bin. Protected folders are kept. This does not run the app's uninstaller.`,
      items: [{ label: `${app.name}: Windows app entry`, sub: app.id.split('|').pop(), size: bytes(0) },
        ...preview.folders.map((f) => ({ label: f.deletable ? 'Move to the Recycle Bin' : 'Needs administrator, kept', sub: f.path, size: bytes(f.bytes) }))],
      warning: 'These folders may contain settings, saved games and other personal data. Removing the entry does not fully uninstall remaining components or services.',
      ack: 'I understand and do not need the data in these folders',
      confirmLabel: `Remove entry and leftovers (${bytes(size)})`,
    });
    if (!confirmed) return;
    const result = await runOp(`Removing ${app.name}'s entry and leftovers`, 'apps.forceRemove', { id: app.id }, { key });
    update((s) => { s.apps.items.delete(app.id); s.selection.apps.delete(app.id); });
    freed(removable.filter((f) => !result.failed.includes(f.path)).reduce((sum, f) => sum + f.bytes, 0));
    toast(`Entry removed. Backup: ${result.backupPath}${result.failed.length ? `. ${result.failed.length} folders were kept.` : ''}`, result.failed.length ? 'bad' : 'good');
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

export async function uninstallApps(apps) {
  if (appActionBusy() || !apps.length) return;
  update((s) => { s.apps.queueRunning = true; apps.forEach((a) => s.busy.add(`app:${a.id}`)); });
  try { await runUninstallQueue(apps); }
  finally { update((s) => { s.apps.queueRunning = false; apps.forEach((a) => s.busy.delete(`app:${a.id}`)); }); }
}

async function runUninstallQueue(apps) {
  const size = apps.reduce((sum, app) => sum + total(app), 0);
  const { confirmed, option: quiet } = await confirmSheet({
    title: `Uninstall ${plural(apps.length, 'app')}?`,
    body: 'Apps are uninstalled one at a time. Follow any uninstaller prompts. Afterwards, review all leftover folders together.',
    items: apps.map((a) => ({ label: a.name, sub: a.installLocation ?? a.publisher ?? '', size: bytes(total(a)) })),
    option: { label: 'Quiet when possible', checked: true },
    warning: 'Uninstalling may remove settings, saved games and other app data. Stopping waiting does not close an uninstaller.',
    confirmLabel: `Uninstall ${plural(apps.length, 'app')} (${bytes(size)})`,
  });
  if (!confirmed) return;
  const results = [];
  const groups = [];
  startBatch(`Uninstalling ${plural(apps.length, 'app')}`, apps.length, size);
  try {
    for (const app of apps) {
      try {
        // Each step gets its own cancellation scope so stopping one does not cancel the next.
        const [result] = await runOp(`Uninstalling ${app.name}`, 'apps.uninstallQueue', { paths: [app.id], quiet }, { key: `app:${app.id}` });
        results.push({ app, ...result });
        batchStep({ ok: result.removed, bytes: app.appBytes ?? 0 });
        if (result.removed) {
          freed(app.appBytes ?? 0);
          groups.push({ app, leftovers: result.leftovers });
          update((s) => { s.apps.items.delete(app.id); s.selection.apps.delete(app.id); });
        }
        if (result.needsReview && app !== apps.at(-1)) {
          const next = await confirmSheet({
            title: 'Finish the open uninstaller',
            body: `${app.name} may still be uninstalling. Finish or close its uninstaller before starting the next app.`,
            ack: 'The previous uninstaller has finished or is closed',
            confirmLabel: 'Continue queue', cancelLabel: 'Stop queue', danger: false,
          });
          if (!next.confirmed) {
            const remaining = apps.slice(apps.indexOf(app) + 1);
            remaining.forEach((pending) => results.push({ app: pending, removed: false, error: 'Not started. Queue stopped.' }));
            break;
          }
        }
      } catch (err) {
        results.push({ app, removed: false, error: err.message });
        batchStep({ ok: false });
      }
    }
  } finally {
    endBatch();
  }
  await confirmSheet({
    title: `${results.filter((r) => r.removed).length} of ${apps.length} apps uninstalled`,
    body: 'Results for every app in the queue. Finish any uninstaller still open, then rescan.',
    items: results.map((r) => ({ label: r.app.name, sub: r.removed ? 'Uninstalled' : r.error || 'Still installed', size: bytes(total(r.app)) })),
    confirmLabel: 'Review leftovers', cancelLabel: 'Review leftovers', danger: false,
  });
  await offerLeftoverGroups(groups);
}
