import { dockerCompact, dockerPrune, scanAll } from '../actions.js';
import { button, h, icon } from '../dom.js';
import { bytes, daysSince, isUnder, plural } from '../format.js';
import { state, update } from '../store.js';
import { dockerUsed, unusedImages } from './docker.js';
import { isSafe, isStale } from './worktrees-model.js';

const CATEGORIES = [
  { key: 'worktrees', label: 'Worktrees', color: 'var(--cat-worktrees)', view: 'worktrees' },
  { key: 'docker', label: 'Docker', color: 'var(--cat-docker)', view: 'docker' },
  { key: 'build', label: 'Build output', color: 'var(--cat-build)', view: 'artifacts' },
  { key: 'caches', label: 'Caches', color: 'var(--cat-caches)', view: 'caches' },
];

function primaryDrive() {
  const drives = state.info.drives ?? [];
  const root = state.info.roots?.[0] ?? 'C:\\';
  return drives.find((d) => isUnder(root, d.name)) ?? drives[0];
}

const onDrive = (path, drive) => !!drive && path.toLowerCase().startsWith(drive.name.toLowerCase());

/** Bytes per category on the given drive, without double counting build output inside worktrees. */
export function categoryTotals(drive) {
  const worktrees = [...state.worktrees.items.values()].filter((w) => onDrive(w.path, drive));
  const wtPaths = worktrees.map((w) => w.path);
  const r = state.docker.report;
  const dockerBytes = r?.disk ? (onDrive(r.disk.path, drive) ? r.disk.bytes : 0) : dockerUsed(r);
  const build = [...state.artifacts.items.values()]
    .filter((a) => onDrive(a.path, drive) && !wtPaths.some((p) => isUnder(a.path, p)));
  const caches = [...state.caches.items.values()].filter((c) => onDrive(c.path, drive));
  return {
    worktrees: worktrees.reduce((s, w) => s + w.bytes, 0),
    docker: dockerBytes,
    build: build.reduce((s, a) => s + a.bytes, 0),
    caches: caches.reduce((s, c) => s + c.bytes, 0),
  };
}

const measuring = {
  worktrees: () => state.worktrees.status === 'scanning',
  docker: () => state.docker.status === 'scanning',
  build: () => state.artifacts.status === 'scanning',
  caches: () => state.caches.status === 'scanning',
};

export const overviewView = {
  id: 'overview',
  title: 'Overview',
  icon: 'chart-pie-slice',

  subtitle() {
    const d = primaryDrive();
    return d ? `${d.label} (${d.name.replace(/\\$/, '')}), ${bytes(d.freeBytes)} free` : 'Storage on this PC';
  },

  actions() {
    const busy = ['worktrees', 'artifacts', 'caches'].some((k) => state[k].status === 'scanning') || state.docker.status === 'scanning';
    return [button('Rescan all', { icon: 'arrow-clockwise', onClick: scanAll, disabled: busy, key: 'rescan' })];
  },

  render() {
    const drive = primaryDrive();
    return h('div', null, drive && capacity(drive), recommendations());
  },
};

function capacity(drive) {
  const used = drive.totalBytes - drive.freeBytes;
  const totals = categoryTotals(drive);
  const known = Object.values(totals).reduce((a, b) => a + b, 0);
  const other = Math.max(0, used - known);
  const pct = (n) => `${(n / drive.totalBytes) * 100}%`;

  const segs = [
    ...CATEGORIES.map((c) => ({ ...c, bytes: totals[c.key], pending: measuring[c.key]() })),
    { key: 'other', label: 'System & other', color: 'var(--cat-other)', bytes: other },
  ];

  return h('section', { class: 'section capacity', 'aria-label': 'Drive usage' },
    h('div', { class: 'capacity-line' },
      h('span', { class: 'capacity-used' }, `${bytes(used)} used`),
      h('span', { class: 'capacity-of' }, `of ${bytes(drive.totalBytes)}`)),
    h('div', { class: 'bar', role: 'img', 'aria-label': segs.map((s) => `${s.label} ${bytes(s.bytes)}`).join(', ') + `, free ${bytes(drive.freeBytes)}` },
      segs.filter((s) => s.bytes > 0).map((s) => h('div', {
        class: `bar-seg ${s.pending ? 'measuring' : ''}`,
        style: { width: pct(s.bytes), background: s.color },
        title: `${s.label}: ${bytes(s.bytes)}`,
      }))),
    h('ul', { class: 'legend' },
      segs.map((s) => h('li', null, s.view
        ? h('button', { type: 'button', onclick: () => update((st) => { st.view = s.view; }) }, swatch(s), h('span', { class: 'label' }, s.label), h('span', { class: 'val' }, s.pending && !s.bytes ? '…' : bytes(s.bytes)))
        : [swatch(s), h('span', null, s.label), h('span', { class: 'val' }, bytes(s.bytes))])),
      h('li', null, swatch({ color: 'var(--cat-free)' }), h('span', null, 'Free'), h('span', { class: 'val' }, bytes(drive.freeBytes)))));
}

const swatch = (s) => h('span', { class: 'swatch', style: { background: s.color, boxShadow: 'inset 0 0 0 1px oklch(0 0 0 / 0.08)' } });

function recommendations() {
  const recs = buildRecommendations();
  const scanning = ['worktrees', 'artifacts', 'caches'].some((k) => state[k].status === 'scanning') || state.docker.status === 'scanning';

  return h('section', { class: 'section' },
    h('div', { class: 'section-head' },
      h('h2', null, 'Recommendations'),
      h('span', { class: 'hint' }, scanning ? 'Still measuring, more may appear' : recs.length ? `Up to ${bytes(recs.reduce((a, r) => a + r.save, 0))} to reclaim` : '')),
    recs.length === 0
      ? h('div', { class: 'recs' }, scanning
        ? [1, 2, 3].map(() => h('div', { class: 'rec rec-skeleton' }, h('i', { class: 'rec-icon', style: { background: 'var(--line)' } }), h('div', null), h('div'), h('div')))
        : h('div', { class: 'rec' }, h('div', { class: 'rec-icon', style: { background: 'var(--good)' } }, icon('check', true)),
          h('div', null, h('h3', null, 'Nothing obvious to clean'), h('p', null, 'Explore your drives to see what else is taking space.')), h('div'), h('div')))
      : h('div', { class: 'recs' }, recs.sort((a, b) => b.save - a.save).map(rec)));
}

function rec(r) {
  return h('div', { class: 'rec' },
    h('div', { class: 'rec-icon', style: { background: r.color } }, icon(r.icon, true)),
    h('div', null, h('h3', null, r.title), h('p', null, r.text)),
    h('div', { class: 'rec-save' }, h('strong', null, bytes(r.save)), r.saveLabel ?? 'to reclaim'),
    button(r.action, { small: true, onClick: r.run, key: `rec-${r.id}` }));
}

const go = (view, mutate) => () => update((s) => { s.view = view; mutate?.(s); });

function buildRecommendations() {
  const recs = [];
  const wts = [...state.worktrees.items.values()];
  const safe = wts.filter(isSafe);
  if (safe.length) {
    recs.push({
      id: 'wt-safe', icon: 'git-merge', color: 'var(--cat-worktrees)', save: safe.reduce((a, w) => a + w.bytes, 0),
      title: `Remove ${plural(safe.length, 'merged worktree')}`,
      text: 'Their branches are merged and they have no uncommitted or unpushed work.',
      action: 'Review', run: go('worktrees', (s) => { s.worktrees.filter = 'safe'; }),
    });
  }
  const stale = wts.filter((w) => isStale(w) && !isSafe(w));
  if (stale.length) {
    recs.push({
      id: 'wt-stale', icon: 'clock', color: 'var(--cat-worktrees)', save: stale.reduce((a, w) => a + w.bytes, 0),
      title: `${plural(stale.length, 'worktree')} untouched for 30+ days`,
      text: 'Not merged yet, so check each one for work you still need.',
      action: 'Review', run: go('worktrees', (s) => { s.worktrees.filter = 'stale'; }),
    });
  }

  const r = state.docker.report;
  if (r?.running && r.buildCacheBytes > 256 * 2 ** 20) {
    recs.push({
      id: 'dk-cache', icon: 'stack', color: 'var(--cat-docker)', save: r.buildCacheBytes,
      title: 'Clear the Docker build cache',
      text: 'Layers left over from past image builds. Docker rebuilds them the next time you build.',
      action: 'Clear', run: () => dockerPrune('buildCache', r.buildCacheBytes),
    });
  }
  const unused = unusedImages(r);
  const unusedBytes = unused.reduce((a, i) => a + i.uniqueBytes, 0);
  if (unusedBytes > 256 * 2 ** 20) {
    recs.push({
      id: 'dk-images', icon: 'package', color: 'var(--cat-docker)', save: unusedBytes,
      title: `Remove ${plural(unused.length, 'unused Docker image')}`,
      text: 'No container uses them. They are pulled again if you need them.',
      action: 'Review', run: go('docker', (s) => { s.docker.tab = 'images'; }),
    });
  }
  if (r?.disk) {
    const slack = r.disk.bytes - (r.running ? dockerUsed(r) : 0);
    if (r.running && slack > 2 * 2 ** 30) {
      recs.push({
        id: 'dk-compact', icon: 'arrows-in-simple', color: 'var(--cat-docker)', save: slack,
        title: 'Compact Docker’s disk',
        text: 'The disk file never shrinks after a prune. Compacting returns its empty space to Windows. Needs administrator permission.',
        action: 'Compact', run: () => dockerCompact(r.disk, dockerUsed(r)),
      });
    }
  }

  const old = [...state.artifacts.items.values()].filter((a) => daysSince(a.lastWriteUtc) >= 30);
  const oldBytes = old.reduce((a, x) => a + x.bytes, 0);
  if (oldBytes > 512 * 2 ** 20) {
    const projects = new Set(old.map((a) => a.projectPath)).size;
    recs.push({
      id: 'art-old', icon: 'hammer', color: 'var(--cat-build)', save: oldBytes,
      title: 'Delete build output from inactive projects',
      text: `node_modules, bin and obj in ${plural(projects, 'project')} you have not touched in 30 days. The next build restores them.`,
      action: 'Review', run: go('artifacts', (s) => { s.artifacts.age = 30; s.artifacts.kind = 'all'; }),
    });
  }

  const bigCaches = [...state.caches.items.values()].filter((c) => c.bytes > 1024 ** 3).sort((a, b) => b.bytes - a.bytes);
  if (bigCaches.length) {
    recs.push({
      id: 'caches', icon: 'archive-box', color: 'var(--cat-caches)', save: bigCaches.reduce((a, c) => a + c.bytes, 0),
      title: bigCaches.length === 1 ? `Clear ${bigCaches[0].name}`
        : bigCaches.length === 2 ? `Clear ${bigCaches[0].name} and ${bigCaches[1].name}`
          : `Clear ${bigCaches[0].name}, ${bigCaches[1].name} and ${bigCaches.length - 2} more`,
      text: bigCaches.some((c) => c.id === 'temp')
        ? 'Temporary files and downloaded packages. Your tools recreate them when needed.'
        : 'Downloaded packages your tools fetch again when they need them.',
      action: 'Review', run: go('caches'),
    });
  }
  const win = state.windows.items ?? [];
  const winBytes = win.reduce((a, i) => a + (i.bytes ?? 0), 0);
  if (winBytes > 512 * 2 ** 20 || win.some((i) => i.id === 'previous')) {
    recs.push({
      id: 'windows', icon: 'broom', color: 'var(--cat-other)', save: winBytes,
      title: 'Clean up Windows update files',
      text: 'Update downloads and leftovers, removed by Windows Disk Cleanup. Needs administrator permission.',
      saveLabel: 'or more', action: 'Review', run: go('caches'),
    });
  }
  return recs;
}
