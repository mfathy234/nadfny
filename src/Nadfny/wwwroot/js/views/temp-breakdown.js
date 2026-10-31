// The expandable "what is in Temp" panel under the Temporary files row, and the "what grew" note.
import { button, h, icon } from '../dom.js';
import { bytes, plural } from '../format.js';
import { keepTempPattern, loadTempBreakdown, unkeepTempPattern, clearTempGroup } from '../temp-actions.js';
import { state } from '../store.js';
import { rowWrap, withOpProgress } from './common.js';

export function tempBreakdownRow() {
  const t = state.tempBreakdown;
  if (!t.open) return null;
  return rowWrap('temp:breakdown', h('div', { class: 'temp-breakdown' }, body(t)));
}

function body(t) {
  if (t.status === 'error') return [h('p', { class: 'hint' }, t.error), button('Try again', { small: true, onClick: loadTempBreakdown })];
  if (!t.data) return h('p', { class: 'hint' }, 'Looking inside Temp…');
  const { groups, other, kept, keepList } = t.data;
  const max = Math.max(1, ...groups.map((g) => g.bytes));
  const mine = kept.find((k) => k.kind === 'user');
  return [
    h('p', { class: 'hint' }, 'Grouped by name. Files changed in the last 24 hours are always kept.'),
    h('div', { class: 'temp-groups' },
      groups.map((g) => groupLine(g, max)),
      other && h('div', { class: 'temp-line' },
        h('div', { class: 'temp-name' }, h('span', { class: 'name' }, 'Everything else'),
          h('span', { class: 'row-sub' }, `${plural(other.groups, 'name pattern')} · ${plural(other.count, 'item')}`)),
        h('div', { class: 'temp-size' }, bytes(other.bytes)),
        h('div'))),
    keptSection(kept.filter((k) => k.kind === 'ai'), mine, keepList),
  ];
}

function groupLine(g, max) {
  const key = `tempgroup:${g.id}`;
  const busy = state.busy.has(key);
  const sub = [plural(g.count, 'item'), g.recentCount ? `${bytes(g.recentBytes)} recent, kept` : null].filter(Boolean).join(' · ');
  const clear = button(busy ? 'Clearing…' : 'Clear', {
    small: true, key: `clear-${key}`, onClick: () => clearTempGroup(g),
    disabled: busy || state.caches.status === 'scanning' || g.bytes - g.recentBytes <= 0 && g.kind !== 'links',
    title: `Clear ${g.pattern} (${bytes(g.bytes)})`,
  });
  if (busy) clear.classList.add('busy');
  const actions = h('div', { class: 'temp-actions' },
    g.kind === 'normal' && h('button', {
      class: 'btn small quiet', type: 'button', title: `Never clear anything named ${g.pattern}`, onclick: () => keepTempPattern(g.pattern),
    }, 'Always keep'),
    clear);
  return h('div', { class: 'temp-line' },
    h('div', { class: 'temp-name' }, h('span', { class: 'name mono', title: g.pattern }, g.pattern), h('span', { class: 'row-sub' }, sub)),
    withOpProgress(key, h('div', { class: 'temp-size' }, bytes(g.bytes), h('div', { class: 'meter' }, h('span', { style: { transform: `scaleX(${g.bytes / max})` } })))),
    actions);
}

function keptSection(ai, mine, keepList) {
  const aiGroup = ai[0];
  if (!aiGroup && !keepList.length) return null;
  return h('div', { class: 'temp-kept' },
    aiGroup && h('div', { class: 'temp-line' },
      h('div', { class: 'temp-name' }, h('span', { class: 'name' }, 'Kept: AI tools'),
        h('span', { class: 'row-sub' }, `${plural(aiGroup.count, 'item')} · never cleared`)),
      h('div', { class: 'temp-size' }, bytes(aiGroup.bytes)), h('div')),
    keepList.length > 0 && h('div', { class: 'temp-mine' },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, 'Kept by you'),
        mine && h('span', { class: 'row-sub' }, `${plural(mine.count, 'item')} · ${bytes(mine.bytes)}`)),
      h('div', { class: 'temp-chips' }, keepList.map((p) => h('span', { class: 'chip' }, p,
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Stop keeping ${p}`, title: `Stop keeping ${p}`, onclick: () => unkeepTempPattern(p) }, icon('x')))))));
}

/** "mostly dawsha-e2e-* (+35 GB, 17,000 new)" for the growth hint on the Temp row; nothing without an earlier breakdown. */
export function tempGrowthNote() {
  const groups = state.history.report?.tempGroups ?? [];
  if (!groups.length) return null;
  const part = (g) => `${g.pattern} (+${bytes(g.bytes)}${g.newItems > 0 ? `, ${g.newItems.toLocaleString('en')} new` : ''})`;
  return h('div', { class: 'growth-hint' }, `mostly ${groups.slice(0, 2).map(part).join(', ')}`);
}
