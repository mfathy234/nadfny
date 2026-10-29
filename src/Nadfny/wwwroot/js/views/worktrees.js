import { removeWorktrees, reveal, scanWorktrees } from '../actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { ago, bytes, plural } from '../format.js';
import { state, update } from '../store.js';
import { checkbox, rowWrap, segmented, withOpProgress } from './common.js';
import { FILTERS, isRisky, isSafe, isStale } from './worktrees-model.js';

const FILTER_LABELS = [
  ['all', 'All'],
  ['safe', 'Merged & clean'],
  ['stale', 'Inactive 30+ days'],
  ['risky', 'Unsaved work'],
];

export function filtered() {
  const f = FILTERS[state.worktrees.filter] ?? FILTERS.all;
  return [...state.worktrees.items.values()].filter(f);
}

export const worktreesView = {
  id: 'worktrees',
  title: 'Worktrees',
  icon: 'git-branch',

  subtitle() {
    const items = [...state.worktrees.items.values()];
    if (state.worktrees.status === 'scanning') return `Scanning repositories… ${plural(items.length, 'worktree')} found`;
    if (!items.length) return 'Linked git worktrees in your scan locations';
    const total = items.reduce((a, w) => a + w.bytes, 0);
    return `${plural(items.length, 'linked worktree')} using ${bytes(total)}`;
  },

  actions() {
    return [button('Rescan', { icon: 'arrow-clockwise', onClick: scanWorktrees, disabled: state.worktrees.status === 'scanning', key: 'rescan' })];
  },

  render() {
    const ws = state.worktrees;
    const all = [...ws.items.values()];
    if (ws.status === 'error') {
      return emptyState({ ic: 'warning-circle', error: true, title: 'Could not list worktrees', text: ws.error, action: button('Try again', { onClick: scanWorktrees }) });
    }
    if (all.length === 0 && ws.status === 'scanning') return skeletonList(6);
    if (all.length === 0) {
      return emptyState({ ic: 'git-branch', title: 'No linked worktrees', text: 'None of the repositories in your scan locations have extra worktrees. Add another location from the sidebar if your repos live elsewhere.' });
    }

    const counts = Object.fromEntries(FILTER_LABELS.map(([k]) => [k, all.filter(FILTERS[k]).length]));
    const items = filtered();
    const max = Math.max(1, ...all.map((w) => w.bytes));

    const groups = new Map();
    for (const w of items) {
      if (!groups.has(w.repoPath)) groups.set(w.repoPath, []);
      groups.get(w.repoPath).push(w);
    }
    const ordered = [...groups.entries()]
      .map(([repo, list]) => ({ repo, list: list.sort((a, b) => b.bytes - a.bytes), total: list.reduce((a, w) => a + w.bytes, 0) }))
      .sort((a, b) => b.total - a.total);

    return h('div', null,
      h('div', { class: 'filters' },
        segmented(FILTER_LABELS.map(([value, label]) => ({ value, label, count: counts[value] })), ws.filter,
          (v) => update((s) => { s.worktrees.filter = v; }), 'Filter worktrees')),
      items.length === 0
        ? emptyState({ ic: 'check-circle', title: 'Nothing here', text: 'No worktrees match this filter.' })
        : ordered.map((g) => group(g, max)));
  },
};

function group({ repo, list, total }, max) {
  const sel = state.selection.worktrees;
  const selectedCount = list.filter((w) => sel.has(w.path)).length;
  return h('section', { class: 'group' },
    h('div', { class: 'group-head' },
      checkbox({
        checked: selectedCount === list.length,
        indeterminate: selectedCount > 0 && selectedCount < list.length,
        label: `Select all worktrees of ${repo.split('\\').pop()}`,
        key: `grp-${repo}`,
        onChange: (on) => update((s) => list.forEach((w) => (on ? s.selection.worktrees.add(w.path) : s.selection.worktrees.delete(w.path)))),
      }),
      h('h2', null, repo.split('\\').pop()),
      h('span', { class: 'meta' }, `${plural(list.length, 'worktree')}, ${bytes(total)}`)),
    h('div', { class: 'list' }, list.map((w) => rowWrap(w.path, row(w, max)))));
}

function row(w, max) {
  const selected = state.selection.worktrees.has(w.path);
  const toggle = (on) => update((s) => (on ? s.selection.worktrees.add(w.path) : s.selection.worktrees.delete(w.path)));
  const when = w.lastCommitUtc ? `Committed ${ago(w.lastCommitUtc)}` : w.lastWriteUtc ? `Edited ${ago(w.lastWriteUtc)}` : '';

  return h('div', {
    class: `row clickable ${selected ? 'is-selected' : ''} ${state.busy.has(w.path) ? 'is-busy' : ''}`,
    onclick: () => toggle(!selected),
  },
    checkbox({ checked: selected, label: `Select ${w.name}`, key: `wt-${w.path}`, onChange: toggle }),
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name', title: w.path }, w.name), ...badges(w)),
      h('div', { class: 'row-sub' }, icon('git-branch'), h('span', { class: 'mono', title: w.branch ?? '' }, w.branch ?? `detached at ${w.head?.slice(0, 8) ?? '?'}`))),
    h('div', { class: 'row-meta' },
      when,
      h('div', { class: 'row-actions' },
        !w.missing && h('button', { class: 'icon-btn', type: 'button', title: 'Show in Explorer', 'aria-label': `Show ${w.name} in Explorer`, onclick: (e) => { e.stopPropagation(); reveal(w.path); } }, icon('arrow-square-out')),
        h('button', { class: 'icon-btn', type: 'button', title: 'Remove', 'aria-label': `Remove ${w.name}`, onclick: (e) => { e.stopPropagation(); removeWorktrees([w.path]); } }, icon('trash')))),
    withOpProgress(w.path, sizeCell(w.missing ? '0 B' : bytes(w.bytes), w.bytes / max)));
}

function badges(w) {
  const list = [];
  if (w.driveOffline) list.push(h('span', { class: 'badge' }, 'Drive offline'));
  else if (w.missing) list.push(h('span', { class: 'badge bad' }, 'Folder missing'));
  if (!w.missing && !w.statusKnown) list.push(h('span', { class: 'badge warn' }, icon('warning'), 'Status unknown'));
  if (w.locked) list.push(h('span', { class: 'badge' }, icon('lock-simple'), 'Locked'));
  if (w.merged) list.push(h('span', { class: 'badge good' }, icon('git-merge'), 'Merged'));
  if (w.changedFiles > 0) list.push(h('span', { class: 'badge warn' }, icon('pencil-simple'), plural(w.changedFiles, 'change')));
  if (w.ahead > 0) list.push(h('span', { class: 'badge warn' }, icon('arrow-up'), `${w.ahead} unpushed`));
  else if (!w.missing && !w.hasUpstream && !w.merged) list.push(h('span', { class: 'badge warn' }, icon('cloud-arrow-up'), 'Never pushed'));
  return list;
}

export function worktreeSelectionBar() {
  const sel = [...state.selection.worktrees].map((p) => state.worktrees.items.get(p)).filter(Boolean);
  if (!sel.length) return null;
  const total = sel.reduce((a, w) => a + w.bytes, 0);
  const risky = sel.filter(isRisky).length;
  return [
    h('span', { class: 'sel-count' }, h('strong', null, plural(sel.length, 'worktree')), ` selected, ${bytes(total)}`,
      risky ? `, ${risky} with unsaved work` : ''),
    button('Deselect', { variant: 'quiet', small: true, onClick: () => update((s) => s.selection.worktrees.clear()) }),
    button(`Remove ${plural(sel.length, 'worktree')}`, { variant: 'danger', icon: 'trash', onClick: () => removeWorktrees(sel.map((w) => w.path)), key: 'sel-remove' }),
  ];
}

export { isSafe, isStale };
