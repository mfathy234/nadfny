import { button, emptyState, h, sizeCell, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state } from '../store.js';
import { removeAiModel, scanAiModels, showAiModelFolder } from '../ai-models-actions.js';
import { rowWrap, withOpProgress } from './common.js';

export function aiModelsSection() {
  const st = state.aiModels;
  const items = [...st.items.values()].sort((a, b) => b.bytes - a.bytes);
  const max = Math.max(1, ...items.map((item) => item.bytes));
  const total = items.reduce((sum, item) => sum + item.bytes, 0);
  const hint = st.status === 'scanning' ? 'Measuring local models…'
    : items.length ? `${plural(items.length, 'item')} · ${bytes(total)}` : 'Ollama, Hugging Face and LM Studio';
  return h('section', { class: 'section' },
    h('div', { class: 'section-head' }, h('h2', null, 'AI models'), h('span', { class: 'hint' }, hint)),
    h('p', { class: 'row-sub' }, 'Local models are often several gigabytes each. Removing one here cannot be undone, except where the tool downloads it again.'),
    st.status === 'error' ? emptyState({ ic: 'warning-circle', error: true, title: 'AI model scan failed', text: st.error, action: button('Try again', { onClick: scanAiModels }) })
      : items.length ? h('div', { class: 'list' }, items.map((item) => aiModelRow(item, max)))
        : st.status === 'scanning' ? skeletonList(3)
          : emptyState({ ic: 'cube', title: 'No AI models found', text: 'No Ollama, Hugging Face or LM Studio model folders were found on this machine.' }));
}

function aiModelRow(item, max) {
  const key = `aimodel:${item.id}`;
  const busy = state.busy.has(key);
  const remove = item.canRemove
    ? button(busy ? 'Removing…' : item.recycle ? 'Move to Bin' : 'Remove', {
      small: true, key: `remove-${item.id}`, disabled: busy || state.aiModels.status === 'scanning',
      onClick: () => removeAiModel(item),
    })
    : h('div');
  return rowWrap(key, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, item.name),
        h('span', { class: 'badge' }, item.source), item.kind !== 'folder' && h('span', { class: 'badge' }, item.kind)),
      h('div', { class: 'row-sub' }, item.note),
      item.modified && h('div', { class: 'row-sub' }, `Last modified ${item.modified}`)),
    h('div', { class: 'row-meta' }, button('Show folder', { small: true, key: `show-${item.id}`, title: item.path, onClick: () => showAiModelFolder(item) })),
    withOpProgress(key, sizeCell(bytes(item.bytes), item.bytes / max)),
    remove));
}
