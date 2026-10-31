import { cleanWindows, clearCache, emptyRecycleBin, queryRecycleBin, reveal, scanCaches, scanWindows } from '../actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state } from '../store.js';
import { rowWrap, withOpProgress } from './common.js';
import { scanSystemSpace } from '../system-space-actions.js';
import { systemSpaceSection } from './system-space.js';
import { growthSize } from './history.js';
import { scanToolchains } from '../toolchains-actions.js';
import { toolchainsSection } from './toolchains.js';
import { scanAiModels } from '../ai-models-actions.js';
import { aiModelsSection } from './ai-models.js';
import { tempGrowthNote, tempBreakdownRow } from './temp-breakdown.js';
import { toggleTempBreakdown } from '../temp-actions.js';

const CRASH_CACHE_IDS = new Set(['crash-dumps', 'wer-archive', 'wer-queue']);
const CRASH_WINDOWS_IDS = new Set(['memory-dump', 'minidumps', 'wer-system']);

export const cachesView = {
  id: 'caches',
  title: 'Caches',
  icon: 'archive-box',

  subtitle() {
    const total = developerCaches().reduce((a, c) => a + c.bytes, 0);
    if (state.caches.status === 'scanning') return `Measuring package caches… ${bytes(total)} so far`;
    return total ? `${bytes(total)} of downloads your tools fetch again when needed` : 'Package manager and tool caches';
  },

  actions() {
    return [button('Rescan', {
      icon: 'arrow-clockwise', key: 'rescan', disabled: state.caches.status === 'scanning' || state.toolchains.status === 'scanning' || state.aiModels.status === 'scanning',
      onClick: () => Promise.allSettled([scanCaches(), scanToolchains(), scanAiModels(), scanWindows(), queryRecycleBin(), scanSystemSpace()]),
    })];
  },

  render() {
    const st = state.caches;
    const items = developerCaches();
    const max = Math.max(1, ...items.map((c) => c.bytes));

    return h('div', null,
      recycleSection(),
      systemSpaceSection(),
      h('section', { class: 'section' },
        h('div', { class: 'section-head' }, h('h2', null, 'Developer caches'), h('span', { class: 'hint' }, 'Your tools download these again when needed')),
        st.status === 'error' ? emptyState({ ic: 'warning-circle', error: true, title: 'Scan failed', text: st.error, action: button('Try again', { onClick: scanCaches }) })
          : !items.length && st.status === 'scanning' ? skeletonList(5) : items.length
          ? h('div', { class: 'list' }, items.flatMap((c) => [cacheRow(c, max), c.id === 'temp' ? tempBreakdownRow() : null]))
          : st.status === 'scanning' ? skeletonList(5) : emptyState({ ic: 'archive-box', title: 'No caches found', text: 'None of the known package caches exist on this machine.' })),
      aiModelsSection(),
      toolchainsSection(),
      crashSection(),
      windowsSection());
  },
};

function developerCaches() {
  return [...state.caches.items.values()].filter((c) => !CRASH_CACHE_IDS.has(c.id)).sort((a, b) => b.bytes - a.bytes);
}

function cacheRow(c, max) {
  const busy = state.busy.has(`cache:${c.id}`);
  const clear = button(busy ? 'Clearing…' : 'Clear', { small: true, onClick: () => clearCache(c), disabled: busy || state.caches.status === 'scanning' || c.bytes === 0, key: `clear-${c.id}` });
  if (busy) clear.classList.add('busy');
  const cell = growthSize('caches', c.id, sizeCell(bytes(c.bytes), c.bytes / max), c.bytes);
  const note = c.id === 'temp' && cell.querySelector('.growth-hint') ? tempGrowthNote() : null;
  if (note) cell.append(note);
  const open = state.tempBreakdown.open;
  const toggle = c.id === 'temp' && h('button', {
    class: 'icon-btn', type: 'button', 'aria-expanded': String(open), 'aria-label': open ? 'Hide what is in Temp' : 'Show what is in Temp',
    style: { width: '20px', height: '20px', marginRight: '4px' }, onclick: toggleTempBreakdown,
  }, icon(open ? 'caret-down' : 'caret-right'));
  return rowWrap(c.id, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, toggle, h('span', { class: 'name' }, c.name)),
      h('div', { class: 'row-sub' }, c.note)),
    h('div', { class: 'row-meta' },
      h('div', { class: 'row-actions' },
        h('button', { class: 'icon-btn', type: 'button', title: c.path, 'aria-label': `Show ${c.name} in Explorer`, onclick: () => reveal(c.path) }, icon('arrow-square-out')))),
    withOpProgress(`cache:${c.id}`, cell),
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

function crashSection() {
  const userCaches = [...state.caches.items.values()].filter((c) => CRASH_CACHE_IDS.has(c.id)).sort((a, b) => b.bytes - a.bytes);
  const systemItems = (state.windows.items ?? []).filter((i) => CRASH_WINDOWS_IDS.has(i.id));
  if (!userCaches.length && !systemItems.length) return state.caches.status === 'scanning' ? skeletonList(2) : null;

  const busy = state.busy.has('windows');
  const action = systemItems.length
    ? button(busy ? 'Cleaning…' : 'Clean up system dumps', { icon: 'broom', onClick: () => cleanWindows(systemItems), disabled: busy, key: 'clean-system-dumps' })
    : null;
  if (action && busy) action.classList.add('busy');
  const max = Math.max(1, ...userCaches.map((c) => c.bytes), ...systemItems.map((i) => i.bytes ?? 0));

  return h('section', { class: 'section' },
    h('div', { class: 'section-head' }, h('h2', null, 'Crash dumps and error reports'), action),
    h('p', { class: 'row-sub', style: { margin: '-2px 0 10px' } },
      'Developers sometimes need these to find out why a program or the PC crashed. Recent files are kept. System dumps need administrator permission and are cleaned by Windows Disk Cleanup.'),
    h('div', { class: 'list' },
      userCaches.map((c) => cacheRow(c, max)),
      systemItems.map((i) => windowsRow(i, max))));
}

function windowsSection() {
  const w = state.windows;
  if (w.status === 'error') return null;
  const items = (w.items ?? []).filter((i) => !CRASH_WINDOWS_IDS.has(i.id));
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
    items.length ? h('div', { class: 'list' }, items.map((i) => windowsRow(i, max))) : skeletonList(3));
}

function windowsRow(i, max) {
  return rowWrap(`win-${i.id}`, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, i.name), i.warning && h('span', { class: 'badge warn', title: i.warning }, icon('warning'), 'Read before cleaning')),
      h('div', { class: 'row-sub' }, i.note)),
    h('div', { class: 'row-meta' }),
    i.bytes != null ? sizeCell(bytes(i.bytes), i.bytes / max) : h('div', { class: 'size' }, h('div', { class: 'row-sub', style: { justifyContent: 'flex-end', whiteSpace: 'nowrap' } }, 'Size unknown')),
    h('div')));
}
