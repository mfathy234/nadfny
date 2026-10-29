import { call } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes } from './format.js';
import { freed } from './actions.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

export async function scanSystemSpace() {
  if (state.busy.has('systemSpace') || state.systemSpace.status === 'scanning') return;
  update((s) => { s.systemSpace.status = 'scanning'; s.systemSpace.error = null; });
  try {
    const info = await call('systemSpace.scan');
    update((s) => { s.systemSpace.info = info; s.systemSpace.status = 'done'; });
  } catch (err) {
    if (!err.cancelled) update((s) => { s.systemSpace.error = err.message; s.systemSpace.status = 'error'; });
  }
}

export async function openSpaceSettings(kind) {
  try { await call('systemSpace.settings', { kind }); }
  catch (err) { toast(err.message, 'bad'); }
}

export async function changeHibernation(action) {
  const file = state.systemSpace.info?.hibernation;
  if (!file || state.busy.has('systemSpace')) return;
  const off = action === 'off';
  const label = off ? 'Turn off hibernation' : 'Reduce hibernation file';
  const { confirmed } = await confirmSheet({
    title: `${label}?`,
    body: off
      ? 'Windows removes this file and disables Hibernate and Fast Startup. Administrator permission is required.'
      : 'Windows reduces this file to about half its full size. Fast Startup stays available; Hibernate and hybrid sleep are disabled. Administrator permission is required.',
    items: [{ label: 'hiberfil.sys', sub: file.path, size: bytes(file.bytes) }],
    warning: 'Without Hibernate and hybrid sleep, a loss of power during sleep can lose unsaved work. Save your work before continuing.',
    ack: 'I understand that Hibernate and hybrid sleep will be unavailable',
    confirmLabel: `${label} (${bytes(file.bytes)})`,
  });
  if (!confirmed || state.busy.has('systemSpace')) return;
  update((s) => s.busy.add('systemSpace'));
  try {
    const result = await runOp(label, 'systemSpace.hibernate', { action }, { key: 'systemSpace' });
    update((s) => { s.systemSpace.info = result.info; s.systemSpace.status = 'done'; });
    if (!result.cancelled) {
      freed(result.freed);
      toast(`${off ? 'Hibernation turned off' : 'Hibernation file reduced'}. ${bytes(result.freed)} freed.`);
    }
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete('systemSpace'));
    await scanSystemSpace();
  }
}
