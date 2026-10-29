import { copyText, reveal } from '../actions.js';
import { button, emptyState, h, sizeCell, skeletonList } from '../dom.js';
import { bytes } from '../format.js';
import { state } from '../store.js';
import { openAppsFeatures, removeToolchain, scanToolchains } from '../toolchains-actions.js';
import { rowWrap, withOpProgress } from './common.js';

export function toolchainsSection() {
  const st = state.toolchains;
  const items = [...st.items.values()].sort((a, b) => Number(b.old) - Number(a.old) || b.bytes - a.bytes);
  const max = Math.max(1, ...items.map((item) => item.bytes));
  return h('section', { class: 'section' },
    h('div', { class: 'section-head' }, h('h2', null, 'Old SDKs and tools'),
      h('span', { class: 'hint' }, st.status === 'scanning' ? 'Checking SDK pins and measuring tools…' : 'Review versions and per-user folders')),
    h('p', { class: 'row-sub' }, '.NET SDKs are kept here. Copy the command for an old patch, or use Apps & Features to uninstall it. Install the .NET Uninstall Tool first if needed.'),
    st.warnings.map((warning) => h('p', { class: 'row-sub' }, warning)),
    st.status === 'error'
      ? emptyState({ ic: 'warning-circle', error: true, title: 'Tool scan failed', text: st.error, action: button('Try again', { onClick: scanToolchains }) })
      : items.length ? h('div', { class: 'list' }, items.map((item) => toolchainRow(item, max)))
        : st.status === 'scanning' ? skeletonList(3)
          : emptyState({ ic: 'archive-box', title: 'No SDKs or tools found', text: 'No .NET SDKs, Android images or older JetBrains folders were found.' }));
}

function toolchainRow(item, max) {
  const key = `toolchain:${item.id}`;
  const busy = state.busy.has(key);
  const actions = item.kind === 'dotnet'
    ? h('div', { class: 'row-actions' },
      item.command && button('Copy command', { small: true, key: `copy-${item.id}`, onClick: () => copyText(item.command, 'Uninstall command copied') }),
      button('Apps & Features', { small: true, key: `apps-${item.id}`, onClick: openAppsFeatures }))
    : button(busy ? 'Removing…' : item.recycle ? 'Move to Bin' : 'Remove', {
      small: true, key: `remove-${item.id}`, disabled: busy || !item.canRemove || state.toolchains.status === 'scanning',
      onClick: () => removeToolchain(item),
    });
  return rowWrap(key, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, item.name),
        item.pinned && h('span', { class: 'badge' }, 'Pinned'),
        item.old && h('span', { class: 'badge' }, 'Older version')),
      h('div', { class: 'row-sub' }, item.note),
      h('div', { class: 'row-sub', title: item.path }, item.path),
      item.command && h('div', { class: 'row-sub' }, h('code', null, item.command))),
    h('div', { class: 'row-meta' }, button('Show folder', { small: true, key: `show-${item.id}`, title: item.path, onClick: () => reveal(item.path) })),
    withOpProgress(key, sizeCell(bytes(item.bytes), item.bytes / max)), actions));
}
