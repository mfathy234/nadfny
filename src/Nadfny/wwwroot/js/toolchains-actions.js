import { call, on } from './bridge.js';
import { freed } from './actions.js';
import { confirmSheet, toast } from './dom.js';
import { bytes } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

on('toolchains.item', (item) => update((s) => s.toolchains.items.set(item.id, item)));

let activeScan = null;
let rescanRequested = false;

export function scanToolchains() {
  rescanRequested = true;
  if (!activeScan) activeScan = scanPending().finally(() => { activeScan = null; });
  return activeScan;
}

async function scanPending() {
  do {
    rescanRequested = false;
    await runScan();
  } while (rescanRequested);
}

async function runScan() {
  update((s) => {
    s.toolchains.status = 'scanning';
    s.toolchains.error = null;
    s.toolchains.items = new Map();
    s.toolchains.warnings = [];
    delete s.progress.toolchains;
  });
  try {
    const report = await call('toolchains.scan');
    update((s) => {
      s.toolchains.items = new Map(report.items.map((item) => [item.id, item]));
      s.toolchains.warnings = report.warnings;
      s.toolchains.status = 'done';
      s.toolchains.finishedAt = Date.now();
    });
  } catch (err) {
    update((s) => {
      s.toolchains.items = new Map();
      s.toolchains.status = err.cancelled ? 'idle' : 'error';
      s.toolchains.error = err.message;
    });
  }
}

export async function removeToolchain(item) {
  if (!item.canRemove || state.toolchains.status === 'scanning' || state.busy.has(`toolchain:${item.id}`)) return;
  const { confirmed } = await confirmSheet({
    title: `Remove ${item.name}?`,
    body: item.note,
    items: [{ label: item.name, sub: item.path, size: bytes(item.bytes) }],
    warning: item.recycle
      ? 'Saved emulator data, settings, plugins or Local History in this folder will be moved to the Recycle Bin. Emptying the Bin loses them permanently.'
      : 'This image is deleted permanently. Devices using it cannot start until you download it again.',
    ack: item.recycle ? 'I understand this folder may contain data I still need' : null,
    confirmLabel: `Remove ${item.name} (${bytes(item.bytes)})`,
  });
  if (!confirmed) return;
  const key = `toolchain:${item.id}`;
  update((s) => s.busy.add(key));
  try {
    const n = await runOp(`Removing ${item.name}`, 'toolchains.remove', { id: item.id }, { key });
    update((s) => s.toolchains.items.delete(item.id));
    freed(n);
    toast(item.recycle ? `${item.name} moved to the Recycle Bin. Empty it to free space.` : `${item.name} removed. ${bytes(n)} freed.`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

export const openAppsFeatures = () => call('toolchains.openApps').catch((err) => toast(err.message, 'bad'));
