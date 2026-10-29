// Live scan progress: one row per scan with phase, a determinate bar when the total is known, and the folder being read.
import { h, icon } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state } from '../store.js';

const SCANS = {
  worktrees: { label: 'Worktrees', icon: 'git-branch', color: 'var(--cat-worktrees)', status: () => state.worktrees.status, found: () => summary(state.worktrees.items, 'worktree') },
  docker: { label: 'Docker', icon: 'cube', color: 'var(--cat-docker)', status: () => state.docker.status, found: () => (state.docker.report ? 'Done' : '') },
  artifacts: { label: 'Build output', icon: 'hammer', color: 'var(--cat-build)', status: () => state.artifacts.status, found: () => summary(state.artifacts.items, 'folder') },
  caches: { label: 'Caches', icon: 'archive-box', color: 'var(--cat-caches)', status: () => state.caches.status, found: () => summary(state.caches.items, 'cache') },
};

function summary(map, noun) {
  const total = [...map.values()].reduce((a, x) => a + x.bytes, 0);
  return map.size ? `${plural(map.size, noun)}, ${bytes(total)}` : '';
}

/** 0..1 when the backend reported a total, otherwise null (indeterminate). */
export function fraction(scope) {
  const p = state.progress[scope];
  if (!p || !p.total) return null;
  return Math.min(1, p.done / p.total);
}

export const anyScanning = (scopes = Object.keys(SCANS)) => scopes.some((s) => SCANS[s].status() === 'scanning');

/** Middle-ellipsis keeps both the drive and the leaf folder readable. */
function shortenPath(p, max = 72) {
  if (!p || p.length <= max) return p ?? '';
  const keep = max - 1;
  return `${p.slice(0, Math.ceil(keep * 0.4))}…${p.slice(-Math.floor(keep * 0.6))}`;
}

export function scanPanel(scopes = Object.keys(SCANS)) {
  if (!anyScanning(scopes)) return null;
  const rows = scopes.map((scope) => {
    const meta = SCANS[scope];
    const p = state.progress[scope];
    const running = meta.status() === 'scanning';
    const f = running ? fraction(scope) : 1;
    const found = meta.found();
    const detail = running
      ? [p?.phase || 'Starting', p?.folders ? `${p.folders.toLocaleString('en')} scanned` : null].filter(Boolean).join(', ')
      : found || 'Done';

    return h('div', { class: `scan-row ${running ? '' : 'is-done'}` },
      h('span', { class: 'scan-icon', style: { color: meta.color } }, icon(running ? meta.icon : 'check-circle', true)),
      h('div', { class: 'scan-main' },
        h('div', { class: 'scan-head' },
          h('span', { class: 'scan-label' }, meta.label),
          h('span', { class: 'scan-pct' }, running ? (f == null ? '' : `${Math.round(f * 100)}%`) : '')),
        h('div', { class: 'scan-detail' }, detail),
        running && found && h('div', { class: 'scan-found' }, `Found ${found}`),
        h('div', {
          class: `meter ${f == null ? 'indeterminate' : ''}`,
          role: 'progressbar', 'aria-label': `${meta.label} scan`,
          'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': f == null ? null : String(Math.round(f * 100)),
        }, h('span', { style: f == null ? null : { transform: `scaleX(${f})` } })),
        running && p?.current && h('div', { class: 'scan-path', title: p.current }, shortenPath(p.current))));
  });

  const drives = state.info.allDrives;
  return h('section', { class: 'section scan-panel', 'aria-live': 'polite' },
    h('div', { class: 'section-head' },
      h('h2', null, drives ? 'Scanning all drives' : 'Scanning'),
      h('span', { class: 'hint' }, drives ? 'The first whole-drive scan can take a minute. Results appear as they are found.' : 'Results appear as they are found.')),
    h('div', { class: 'scan-rows' }, rows));
}
