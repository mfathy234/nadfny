import { exploreDelete, exploreList, reveal } from '../actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { ago, bytes, plural } from '../format.js';
import { state, update } from '../store.js';
import { rowWrap, withOpProgress } from './common.js';

function crumbs(path) {
  const parts = path.replace(/[\\/]+$/, '').split('\\');
  const list = [];
  let acc = '';
  parts.forEach((p, i) => {
    acc = i === 0 ? `${p}\\` : `${acc.replace(/\\$/, '')}\\${p}`;
    list.push({ label: p, path: acc });
  });
  return list;
}

export const exploreView = {
  id: 'explore',
  title: 'Explore',
  icon: 'folder-open',

  subtitle() {
    const ex = state.explore;
    if (!ex.path) return 'Find what else is taking space, folder by folder';
    const total = [...ex.items.values()].reduce((a, e) => a + (e.bytes ?? 0), 0);
    return ex.status === 'scanning' ? `Measuring ${ex.path}… ${bytes(total)} so far` : `${ex.path} holds ${bytes(total)}`;
  },

  actions() {
    const ex = state.explore;
    if (!ex.path) return [];
    const parts = crumbs(ex.path);
    return [
      parts.length > 1 && button('Up', { icon: 'caret-left', onClick: () => exploreList(parts[parts.length - 2].path), key: 'up' }),
      button('Rescan', { icon: 'arrow-clockwise', onClick: () => exploreList(ex.path), disabled: ex.status === 'scanning', key: 'rescan' }),
    ].filter(Boolean);
  },

  render() {
    const ex = state.explore;
    if (!ex.path) return startPoints();
    const parts = crumbs(ex.path);
    const nav = h('nav', { class: 'crumbs', 'aria-label': 'Location' },
      h('button', { type: 'button', onclick: () => update((st) => { st.explore = { path: null, status: 'idle', error: null, items: new Map() }; }) }, 'Drives'),
      parts.map((c, i) => [icon('caret-right'), h('button', { type: 'button', 'aria-current': i === parts.length - 1 ? 'location' : null, onclick: () => exploreList(c.path) }, c.label)]));

    if (ex.status === 'error') return h('div', null, nav, emptyState({ ic: 'warning-circle', error: true, title: 'Could not open this folder', text: ex.error }));
    const items = [...ex.items.values()].sort((a, b) => (b.bytes ?? 0) - (a.bytes ?? 0));
    if (!items.length && ex.status === 'scanning') return h('div', null, nav, skeletonList(8));
    if (!items.length) return h('div', null, nav, emptyState({ ic: 'folder', title: 'Empty folder' }));
    const max = Math.max(1, ...items.map((e) => e.bytes ?? 0));
    const shown = items.slice(0, 400);

    return h('div', null, nav,
      h('div', { class: 'list' }, shown.map((e) => rowWrap(e.path, row(e, max)))),
      items.length > shown.length && h('p', { class: 'row-sub', style: { padding: '10px 4px' } }, `${items.length - shown.length} smaller items not shown.`));
  },
};

function startPoints() {
  const drives = state.info.drives ?? [];
  const roots = state.info.roots ?? [];
  const home = roots[0]?.split('\\').slice(0, 3).join('\\');
  const starts = [
    ...drives.map((d) => ({ path: d.name, label: `${d.label} (${d.name.replace(/\\$/, '')})`, sub: `${bytes(d.totalBytes - d.freeBytes)} used of ${bytes(d.totalBytes)}`, ic: 'hard-drives' })),
    ...roots.map((r) => ({ path: r, label: r.split('\\').pop(), sub: r, ic: 'folder' })),
    home && { path: home, label: 'Your user folder', sub: home, ic: 'folder' },
  ].filter(Boolean);
  return h('div', { class: 'list' }, starts.map((s) => h('div', { class: 'row no-check clickable', role: 'button', tabindex: '0', onclick: () => exploreList(s.path), onkeydown: (e) => { if (e.key === 'Enter') exploreList(s.path); } },
    h('div', { style: { minWidth: 0, display: 'flex', gap: '12px', alignItems: 'center' } },
      h('span', { style: { fontSize: '20px', color: 'var(--ink-2)', display: 'flex' } }, icon(s.ic)),
      h('div', { style: { minWidth: 0 } }, h('div', { class: 'row-title' }, h('span', { class: 'name' }, s.label)), h('div', { class: 'row-sub' }, s.sub))),
    h('div', { class: 'row-meta' }), h('div'),
    h('span', { class: 'icon-btn', 'aria-hidden': 'true' }, icon('caret-right')))));
}

function row(e, max) {
  const open = () => e.isDirectory && exploreList(e.path);
  return h('div', {
    class: `row no-check ${e.isDirectory ? 'clickable' : ''} ${state.busy.has(e.path) ? 'is-busy' : ''}`,
    role: e.isDirectory ? 'button' : null, tabindex: e.isDirectory ? '0' : null,
    onclick: open, onkeydown: (ev) => { if (ev.key === 'Enter') open(); },
  },
    h('div', { style: { minWidth: 0, display: 'flex', gap: '12px', alignItems: 'center' } },
      h('span', { style: { fontSize: '18px', color: e.isDirectory ? 'var(--cat-worktrees)' : 'var(--ink-3)', display: 'flex' } }, icon(e.isDirectory ? 'folder' : 'file', e.isDirectory)),
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' }, h('span', { class: 'name', title: e.path }, e.name)),
        h('div', { class: 'row-sub' }, e.isDirectory ? plural(e.files ?? 0, 'file') : '', e.lastWriteUtc ? `${e.isDirectory ? ', changed ' : 'Changed '}${ago(e.lastWriteUtc)}` : ''))),
    h('div', { class: 'row-meta' }),
    withOpProgress(e.path, sizeCell(bytes(e.bytes ?? 0), (e.bytes ?? 0) / max, e.bytes == null)),
    h('div', { class: 'row-actions' },
      h('button', { class: 'icon-btn', type: 'button', title: 'Show in Explorer', 'aria-label': `Show ${e.name} in Explorer`, onclick: (ev) => { ev.stopPropagation(); reveal(e.path); } }, icon('arrow-square-out')),
      h('button', { class: 'icon-btn', type: 'button', title: 'Delete', 'aria-label': `Delete ${e.name}`, onclick: (ev) => { ev.stopPropagation(); exploreDelete(e); } }, icon('trash'))));
}
