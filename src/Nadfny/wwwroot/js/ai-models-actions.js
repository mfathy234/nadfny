// AI model scans and removals. Every removal goes through confirmSheet first.
import { call, on } from './bridge.js';
import { freed, reveal } from './actions.js';
import { confirmSheet, toast } from './dom.js';
import { bytes } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

on('aiModels.item', (item) => update((s) => s.aiModels.items.set(item.id, item)));

export async function scanAiModels() {
  update((s) => { s.aiModels.status = 'scanning'; s.aiModels.error = null; s.aiModels.items = new Map(); delete s.progress.aiModels; });
  try {
    const list = await call('aiModels.scan');
    update((s) => { s.aiModels.items = new Map(list.map((x) => [x.id, x])); s.aiModels.status = 'done'; s.aiModels.finishedAt = Date.now(); });
  } catch (err) {
    update((s) => { s.aiModels.items = new Map(); s.aiModels.status = err.cancelled ? 'idle' : 'error'; s.aiModels.error = err.message; });
  }
}

export async function removeAiModel(item) {
  const key = `aimodel:${item.id}`;
  if (!item.canRemove || state.aiModels.status === 'scanning' || state.busy.has(key)) return;
  const { confirmed } = await confirmSheet({
    title: `Remove ${item.name}?`,
    body: item.note,
    items: [{ label: item.name, sub: item.source === 'Ollama' ? `ollama rm ${item.name}` : item.path, size: bytes(item.bytes) }],
    warning: item.recycle
      ? 'The model folder is moved to the Recycle Bin. Emptying the Bin deletes it permanently.'
      : item.source === 'Ollama'
        ? 'Ollama deletes this model. Download it again with ollama pull if you need it.'
        : 'This model is deleted permanently and downloaded again on next use.',
    confirmLabel: `Remove ${item.name} (${bytes(item.bytes)})`,
  });
  if (!confirmed) return;
  update((s) => s.busy.add(key));
  try {
    const n = await runOp(`Removing ${item.name}`, 'aiModels.remove', { id: item.id }, { key });
    update((s) => s.aiModels.items.delete(item.id));
    freed(n);
    toast(item.recycle ? `${item.name} moved to the Recycle Bin. Empty it to free space.` : `${item.name} removed.${n ? ` ${bytes(n)} freed.` : ''}`);
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => s.busy.delete(key));
  }
}

export const showAiModelFolder = (item) => reveal(item.path);
