import { reveal } from '../actions.js';
import { clearAppCache, forceRemoveApp, scanApps, stopWaiting, uninstallApp, uninstallApps } from '../apps-actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { opFor } from '../ops.js';
import { state, update } from '../store.js';
import { checkbox, rowWrap, segmented, withOpProgress } from './common.js';

const total = (a) => (a.appBytes ?? 0) + a.dataBytes;
const MB = 1024 * 1024;
const MAX_CACHE_LINES = 5;

const FILTERS = [
  ['all', 'All', () => true],
  ['apps', 'Apps', (a) => !a.isGame],
  ['games', 'Games', (a) => a.isGame],
  ['cache', 'Has cache', (a) => a.cacheBytes >= MB],
];

const SOURCE_BADGE = { store: ['storefront', 'Store'], steam: ['game-controller', 'Steam'], epic: ['game-controller', 'Epic'], folder: ['folder', 'Folder'] };

function visibleApps() {
  const pass = FILTERS.find(([k]) => k === state.apps.filter)?.[2] ?? (() => true);
  const q = state.apps.query.trim().toLowerCase();
  return [...state.apps.items.values()]
    .filter((a) => pass(a) && (!q || `${a.name} ${a.publisher ?? ''} ${a.launcher ?? ''}`.toLowerCase().includes(q)))
    .sort((a, b) => total(b) - total(a));
}

export const appsView = {
  id: 'apps',
  title: 'Apps & games',
  icon: 'squares-four',

  subtitle() {
    const items = [...state.apps.items.values()];
    const sum = items.reduce((a, x) => a + total(x), 0);
    if (state.apps.status === 'scanning') return `Measuring installed apps and games… ${bytes(sum)} so far`;
    if (!items.length) return 'Installed apps and games, their data and caches';
    const games = items.filter((a) => a.isGame).length;
    const cache = items.reduce((a, x) => a + x.cacheBytes, 0);
    return `${plural(items.length - games, 'app')} and ${plural(games, 'game')} using ${bytes(sum)}, ${bytes(cache)} of it cache`;
  },

  actions() {
    return [button('Rescan', { icon: 'arrow-clockwise', onClick: scanApps, disabled: state.apps.status === 'scanning' || state.apps.queueRunning || [...state.busy].some((key) => key.startsWith('app:')), key: 'rescan' })];
  },

  render() {
    const st = state.apps;
    // Measuring every app reads a lot of folders, so it starts the first time this view is opened, not at launch.
    if (st.status === 'idle') queueMicrotask(scanApps);
    if (st.status === 'error') return emptyState({ ic: 'warning-circle', error: true, title: 'Scan failed', text: st.error, action: button('Try again', { onClick: scanApps }) });
    if (st.items.size === 0) return st.status === 'done'
      ? emptyState({ ic: 'squares-four', title: 'No apps found', text: 'Windows did not list any installed apps.' })
      : skeletonList(8);

    const all = [...st.items.values()];
    const items = visibleApps();
    const max = Math.max(1, ...items.map(total));
    return h('div', null,
      h('div', { class: 'filters' },
        segmented(FILTERS.map(([value, label, pass]) => ({ value, label, count: all.filter(pass).length })), st.filter, (v) => update((s) => { s.apps.filter = v; }), 'Show'),
        h('span', { class: 'spacer' }),
        h('label', { class: 'search' }, icon('magnifying-glass'),
          h('input', {
            type: 'search', placeholder: 'Search apps', value: st.query, 'aria-label': 'Search apps', 'data-focus-key': 'apps-search',
            oninput: (e) => update((s) => { s.apps.query = e.target.value; }),
          }))),
      items.length === 0
        ? emptyState({ ic: 'magnifying-glass', title: 'Nothing matches', text: 'Try another filter or search.' })
        : h('div', { class: 'list' }, items.map((a) => appRow(a, max))));
  },
};

function appRow(a, max) {
  const key = `app:${a.id}`;
  const busy = state.busy.has(key);
  const unavailable = state.apps.queueRunning || [...state.busy].some((key) => key.startsWith('app:'));
  const expanded = state.apps.expanded.has(a.id);
  const op = opFor(key);
  const waiting = busy && op && /Waiting/.test(op.phase ?? '');
  const toggleOpen = () => update((s) => (expanded ? s.apps.expanded.delete(a.id) : s.apps.expanded.add(a.id)));
  const [badgeIcon, badgeText] = SOURCE_BADGE[a.source] ?? [];
  const sub = [a.publisher, a.version && `v${a.version}`, a.launcher, a.installedOn && `installed ${a.installedOn}`].filter(Boolean).join(' · ');

  const actions = waiting
    ? button('Stop waiting', { small: true, variant: 'quiet', onClick: (e) => { e.stopPropagation(); stopWaiting(); }, key: `stop-${a.id}` })
    : h('div', { class: 'app-actions' },
      a.cacheBytes >= MB && button(`Clear ${bytes(a.cacheBytes)}`, { small: true, disabled: unavailable || state.apps.status === 'scanning', title: 'Clear cache', onClick: (e) => { e.stopPropagation(); clearAppCache(a); }, key: `cache-${a.id}` }),
      a.canUninstall && button(a.broken ? 'Remove entry and leftovers' : a.source === 'folder' ? 'Remove' : 'Uninstall', { small: true, variant: 'quiet', disabled: unavailable || state.apps.status === 'scanning', onClick: (e) => { e.stopPropagation(); a.broken ? forceRemoveApp(a) : uninstallApp(a); }, key: `un-${a.id}` }));

  return [
    rowWrap(a.id, h('div', { class: `row clickable app-row ${state.selection.apps.has(a.id) ? 'is-selected' : ''} ${busy && !waiting ? 'is-busy' : ''}`, onclick: toggleOpen },
      a.canUninstall && !a.broken && !unavailable && state.apps.status !== 'scanning'
        ? checkbox({ checked: state.selection.apps.has(a.id), label: `Select ${a.name}`, key: `select-app-${a.id}`, onChange: (checked) => update((s) => checked ? s.selection.apps.add(a.id) : s.selection.apps.delete(a.id)) })
        : h('span'),
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' },
          h('button', { class: 'icon-btn', type: 'button', 'aria-expanded': String(expanded), 'aria-label': expanded ? `Hide ${a.name} details` : `Show ${a.name} details`, style: { width: '20px', height: '20px', marginLeft: '-4px' }, onclick: (e) => { e.stopPropagation(); toggleOpen(); } },
            icon(expanded ? 'caret-down' : 'caret-right')),
          h('span', { class: 'name', title: a.name }, a.name),
          a.broken && h('span', { class: 'badge' }, 'Uninstaller missing'),
          a.isGame && h('span', { class: 'badge' }, icon('game-controller'), 'Game'),
          badgeText && !(a.isGame && badgeIcon === 'game-controller') && h('span', { class: 'badge' }, icon(badgeIcon), badgeText)),
        h('div', { class: 'row-sub' }, sub || ' '),
        a.note && a.source !== 'folder' && h('div', { class: 'row-sub' }, icon('info'), a.note)),
      h('div', { class: 'row-meta' }, a.appBytes == null ? 'App size unknown' : ''),
      withOpProgress(key, sizeCell(bytes(total(a)), total(a) / max)),
      actions)),
    expanded && rowWrap(`${a.id}:detail`, detail(a)),
  ];
}

function detail(a) {
  const line = (label, path, size, extra) => h('div', { class: 'app-detail-line' },
    h('span', { class: 'label' }, label),
    path ? h('span', { class: 'mono', title: path }, path) : h('span', { class: 'row-sub' }, extra ?? ''),
    h('span', { class: 'size-num' }, size == null ? '' : bytes(size)),
    path ? h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Show ${path} in Explorer`, title: 'Show in Explorer', onclick: () => reveal(path) }, icon('arrow-square-out')) : h('span'));

  // Browsers keep a dozen small caches; the biggest few say enough, the rest are summed.
  const caches = a.caches.filter((c) => c.bytes > 0).sort((x, y) => y.bytes - x.bytes);
  const shown = caches.slice(0, MAX_CACHE_LINES);
  const rest = caches.slice(MAX_CACHE_LINES);
  return h('div', { class: 'app-detail' },
    a.source === 'folder' && a.note && h('div', { class: 'row-sub' }, icon('info'), a.note),
    line('Program', a.installLocation, a.appBytes, 'Windows does not say where it is installed'),
    a.data.map((d) => line('Data', d.path, d.bytes)),
    shown.map((c) => line(c.deletable ? 'Cache' : 'Cache (protected)', c.path, c.bytes)),
    rest.length > 0 && line(`+${plural(rest.length, 'more cache')}`, null, rest.reduce((s, c) => s + c.bytes, 0), 'Smaller cache folders'),
    a.data.length === 0 && caches.length === 0 && h('div', { class: 'row-sub' }, 'No data folders found under AppData or ProgramData.'));
}

export function appsSelectionBar() {
  const apps = [...state.selection.apps].map((id) => state.apps.items.get(id)).filter((a) => a?.canUninstall && !a.broken);
  if (!apps.length) return null;
  const size = apps.reduce((sum, app) => sum + total(app), 0);
  const unavailable = state.apps.queueRunning || [...state.busy].some((key) => key.startsWith('app:'));
  return [
    h('span', { class: 'sel-count' }, h('strong', null, plural(apps.length, 'app')), ` selected, ${bytes(size)}`),
    button('Deselect', { variant: 'quiet', small: true, disabled: state.apps.queueRunning, onClick: () => update((s) => s.selection.apps.clear()) }),
    button(`Uninstall ${plural(apps.length, 'app')}`, { variant: 'danger', disabled: unavailable || state.apps.status === 'scanning', onClick: () => uninstallApps(apps), key: 'sel-uninstall-apps' }),
  ];
}
