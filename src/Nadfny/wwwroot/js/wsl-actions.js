// WSL distros: scan, compact one distro's disk, and turn on automatic shrinking. Compacting is confirmed first.
import { freed } from './actions.js';
import { call } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

export async function scanWsl() {
  update((s) => { s.wsl.status = 'scanning'; s.wsl.error = null; });
  try {
    const report = await call('wsl.scan');
    update((s) => { s.wsl.report = report; s.wsl.status = 'done'; });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.wsl.status = 'error'; s.wsl.error = err.message; });
  }
}

const compactBusy = () => [...state.busy].some((key) => key.startsWith('wsl:compact:') || key === 'docker:compact');

export async function compactWsl(distro) {
  if (compactBusy()) return;
  const key = `wsl:compact:${distro.id}`;
  const { confirmed } = await confirmSheet({
    title: `Compact ${distro.name}’s disk?`,
    body: `${distro.name}’s disk file takes ${bytes(distro.bytes)} on Windows. Compacting gives back the space the distribution no longer uses.`,
    items: [{ label: distro.name, sub: distro.vhdxPath, size: bytes(distro.bytes) }],
    warning: 'Every WSL distro and Docker Desktop are shut down while this runs. Windows will ask for administrator permission. This can take several minutes.',
    confirmLabel: `Compact ${distro.name} (${bytes(distro.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(key));
  try {
    const r = await runOp(`Compacting ${distro.name}’s disk`, 'wsl.compact', { id: distro.id });
    freed(r.freed);
    toast(r.message);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
    scanWsl();
  }
}

export async function enableWslSparse(distro) {
  const key = `wsl:sparse:${distro.id}`;
  update((s) => s.busy.add(key));
  try {
    const r = await call('wsl.sparse', { id: distro.id });
    toast(r.message);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
    scanWsl();
  }
}
