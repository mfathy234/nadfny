import { cleanWindows, clearCache, emptyRecycleBin, queryRecycleBin, reveal, scanCaches, scanWindows } from '../actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state } from '../store.js';
import { rowWrap, withOpProgress } from './common.js';
import { growthSize } from './history.js';

export const cachesView = {
  id: 'caches',
  title: 'Caches',
  icon: 'archive-box',

  subtitle() {
    const total = [...state.caches.items.values()].reduce((a, c) => a + c.bytes, 0);
    if (state.caches.status === 'scanning') return `Measuring package caches… ${bytes(total)} so far`;
    return total ? `${bytes(total)} of downloads your tools fetch again when needed` : 'Package manager and tool caches';
  },

  actions() {
    return [button('Rescan', {
      icon: 'arrow-clockwise', key: 'rescan', disabled: state.caches.status === 'scanning',
      onClick: () => Promise.allSettled([scanCaches(), scanWindows(), queryRecycleBin()]),
    })];
  },

  render() {
    const st = state.caches;
    if (st.status === 'error') return emptyState({ ic: 'warning-circle', error: true, title: 'Scan failed', text: st.error, action: button('Try again', { onClick: scanCaches }) });
    const items = [...st.items.values()].sort((a, b) => b.bytes - a.bytes);
    if (!items.length && st.status === 'scanning') return skeletonList(5);
    const max = Math.max(1, ...items.map((c) => c.bytes));

    return h('div', null,
      recycleSection(),
      h('section', { class: 'section' },
        h('div', { class: 'section-head' }, h('h2', null, 'Developer caches'), h('span', { class: 'hint' }, 'Your tools download these again when needed')),
        items.length
          ? h('div', { class: 'list' }, items.map((c) => cacheRow(c, max)))
          : emptyState({ ic: 'archive-box', title: 'No caches found', text: 'None of the known package caches exist on this machine.' })),
      windowsSection());
  },
};

function cacheRow(c, max) {
  const busy = state.busy.has(`cache:${c.id}`);
  const clear = button(busy ? 'Clearing…' : 'Clear', { small: true, onClick: () => clearCache(c), disabled: busy || c.bytes === 0, key: `clear-${c.id}` });
  if (busy) clear.classList.add('busy');
  return rowWrap(c.id, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, c.name)),
      h('div', { class: 'row-sub' }, c.note)),
    h('div', { class: 'row-meta' },
      h('div', { class: 'row-actions' },
        h('button', { class: 'icon-btn', type: 'button', title: c.path, 'aria-label': `Show ${c.name} in Explorer`, onclick: () => reveal(c.path) }, icon('arrow-square-out')))),
    withOpProgress(`cache:${c.id}`, growthSize('caches', c.id, sizeCell(bytes(c.bytes), c.bytes / max), c.bytes)),
    clear));
}

function recycleSection() {
  const info = state.recycle.info;
  if (!info) return null;
  const busy = state.busy.has('recycle');
  const empty = button(busy ? 'Emptying…' : 'Empty', { small: true, onClick: emptyRecycleBin, disabled: busy || !info.items, key: 'empty-recycle' });
  if (busy) empty.classList.add('busy');
  const drives = info.drives.map((d) => `${d.drive.slice(0, 2)} ${bytes(d.bytes)}`).join(' · ');
  return h('section', { class: 'section' },
    h('div', { class: 'list' }, rowWrap('recycle-bin', h('div', { class: 'row no-check' },
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' }, icon('trash'), h('span', { class: 'name' }, 'Recycle Bin')),
        h('div', { class: 'row-sub' }, info.items
          ? `${plural(info.items, 'item')}${info.drives.length > 1 ? ` · ${drives}` : ''}. Deleted files only free space once the Bin is emptied.`
          : 'Empty')),
      h('div', { class: 'row-meta' }),
      h('div', { class: 'size' }, h('div', { class: 'size-num' }, bytes(info.bytes))),
      empty))));
}

function windowsSection() {
  const w = state.windows;
  if (w.status === 'error') return null;
  const items = w.items ?? [];
  if (!items.length && w.status !== 'scanning') return null;
  const busy = state.busy.has('windows');
  const action = button(busy ? 'Cleaning…' : 'Clean up', { icon: 'broom', onClick: () => cleanWindows(items), disabled: busy || !items.length, key: 'clean-windows' });
  if (busy) action.classList.add('busy');
  const max = Math.max(1, ...items.map((i) => i.bytes ?? 0));

  return h('section', { class: 'section' },
    h('div', { class: 'section-head' },
      h('h2', null, 'Windows update leftovers'),
      action),
    h('p', { class: 'row-sub', style: { margin: '-2px 0 10px' } },
      'Cleaned by Windows Disk Cleanup with its own safety rules. Needs administrator permission once.'),
    items.length ? h('div', { class: 'list' }, items.map((i) => rowWrap(`win-${i.id}`, h('div', { class: 'row no-check' },
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' }, h('span', { class: 'name' }, i.name), i.warning && h('span', { class: 'badge warn', title: i.warning }, icon('warning'), 'Read before cleaning')),
        h('div', { class: 'row-sub' }, i.note)),
      h('div', { class: 'row-meta' }),
      i.bytes != null ? sizeCell(bytes(i.bytes), i.bytes / max) : h('div', { class: 'size' }, h('div', { class: 'row-sub', style: { justifyContent: 'flex-end', whiteSpace: 'nowrap' } }, 'Size unknown')),
      h('div'))))) : skeletonList(3));
}
