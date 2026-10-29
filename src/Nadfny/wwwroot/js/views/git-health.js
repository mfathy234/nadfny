// "Repository health" section under the worktree list: one block per main repository with its findings.
import { deleteGoneBranches, healthBusyKey, pruneLfs, runGc, scanGitHealth } from '../git-health-actions.js';
import { button, emptyState, h, icon } from '../dom.js';
import { ago, bytes, plural } from '../format.js';
import { opFor, elapsed } from '../ops.js';
import { state, update } from '../store.js';
import { rowWrap } from './common.js';

export function repoHealthSection() {
  const gh = state.gitHealth;
  return h('section', { class: 'group', 'aria-label': 'Repository health' },
    h('div', { class: 'group-head' },
      h('h2', null, 'Repository health'),
      h('span', { class: 'meta' }, subtitle()),
      h('span', { class: 'spacer' }),
      gh.status !== 'idle' && button(gh.status === 'scanning' ? 'Checking…' : 'Check again', {
        icon: 'arrow-clockwise', small: true, disabled: gh.status === 'scanning', onClick: scanGitHealth, key: 'health-scan',
      })),
    body());
}

function subtitle() {
  const gh = state.gitHealth;
  if (gh.status === 'scanning') {
    const p = state.progress.gitHealth;
    return p?.total ? `Checking repositories… ${p.done} of ${p.total}` : 'Checking repositories…';
  }
  if (gh.status === 'done') return `${plural(gh.items.size, 'repository', 'repositories')} checked`;
  return 'Git housekeeping for each repository';
}

function body() {
  const gh = state.gitHealth;
  if (gh.status === 'idle') {
    return emptyState({
      ic: 'git-branch', title: 'Not checked yet',
      text: 'Looks at each repository for loose objects git gc can pack, the Git LFS cache, branches gone from the remote and stashes. Nothing changes until you confirm an action.',
      action: button('Check repositories', { icon: 'arrow-clockwise', onClick: scanGitHealth, key: 'health-scan' }),
    });
  }
  if (gh.status === 'error') {
    return emptyState({ ic: 'warning-circle', error: true, title: 'Could not check repositories', text: gh.error, action: button('Try again', { onClick: scanGitHealth }) });
  }
  const repos = [...gh.items.values()];
  if (repos.length === 0) {
    if (gh.status === 'scanning') return h('div', { class: 'list', 'aria-busy': 'true' }, h('div', { class: 'skeleton-row' }, h('i'), h('i'), h('i')));
    return emptyState({ ic: 'git-branch', title: 'No repositories', text: 'None of the scan locations contain a git repository.' });
  }
  // With 100+ repositories, lead with the ones worth acting on, biggest first; the rest fold into one line.
  const attention = repos.filter(needsAttention).sort((a, b) => reclaimable(b) - reclaimable(a) || a.name.localeCompare(b.name));
  const fine = repos.filter((r) => !needsAttention(r)).sort((a, b) => a.name.localeCompare(b.name));
  const showAll = !!gh.showAll;
  return h('div', null,
    attention.length === 0 && gh.status === 'done' && emptyState({ ic: 'check-circle', title: 'Nothing to tidy', text: 'No repository has much for git gc or the LFS cache, gone branches or stashes.' }),
    attention.map(repoBlock),
    fine.length > 0 && h('div', { class: 'filters' },
      h('span', { class: 'hint' }, `${plural(fine.length, 'repository', 'repositories')} ${fine.length === 1 ? 'looks' : 'look'} fine`),
      button(showAll ? 'Hide' : 'Show', { variant: 'quiet', small: true, key: 'health-show-all', onClick: () => update((s) => { s.gitHealth.showAll = !showAll; }) })),
    showAll && fine.map(repoBlock));
}

const ATTENTION_BYTES = 10 * 1024 * 1024;
const reclaimable = (r) => (r.gc ? r.gc.looseBytes + r.gc.garbageBytes : 0) + (r.lfs?.bytes ?? 0);
const needsAttention = (r) => (r.gc && r.gc.looseBytes + r.gc.garbageBytes >= ATTENTION_BYTES)
  || (r.lfs?.bytes ?? 0) >= ATTENTION_BYTES || r.goneBranches.length > 0 || (r.stashes?.count ?? 0) > 0;

function repoBlock(repo) {
  const key = repo.path;
  const rows = [
    rowWrap(key, repoHead(repo)),
    repo.gc && rowWrap(`${key}|gc`, gcRow(repo)),
    repo.lfs && rowWrap(`${key}|lfs`, lfsRow(repo)),
    repo.goneBranches.length > 0 && rowWrap(`${key}|gone`, goneRow(repo)),
    repo.stashes && rowWrap(`${key}|stash`, stashRow(repo)),
  ].filter(Boolean);
  return h('div', { class: 'group' }, h('div', { class: 'list' }, rows));
}

function repoHead(repo) {
  const op = opFor(repo.path);
  return h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name', title: repo.path }, repo.name)),
      h('div', { class: 'row-sub' }, icon('folder'), h('span', { class: 'mono', title: repo.path }, repo.path)),
      op && h('div', { class: 'row-sub' }, `${op.phase || 'Working'} · ${elapsed(op.startedAt)}`)),
    h('div', { class: 'row-meta' }, `.git ${bytes(repo.gitBytes)}`),
    h('span'),
    h('span'));
}

function finding({ ic, title, sub, value, action }) {
  return h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, icon(ic), h('span', { class: 'name' }, title)),
      h('div', { class: 'row-sub' }, sub)),
    h('div', { class: 'row-meta' }, value ?? ''),
    h('span'),
    action ?? h('span'));
}

function gcRow(repo) {
  const gc = repo.gc;
  const busy = state.busy.has(healthBusyKey(repo));
  const parts = [`${plural(gc.looseCount, 'loose object')} (${bytes(gc.looseBytes)})`];
  if (gc.garbageCount > 0) parts.push(`${plural(gc.garbageCount, 'garbage file')} (${bytes(gc.garbageBytes)})`);
  parts.push(plural(gc.packs, 'pack'));
  return finding({
    ic: 'broom',
    title: 'Reclaimable by git gc',
    sub: parts.join(', '),
    value: bytes(gc.looseBytes + gc.garbageBytes),
    action: button('Run git gc', { icon: 'broom', small: true, disabled: busy, onClick: () => runGc(repo) }),
  });
}

function lfsRow(repo) {
  const lfs = repo.lfs;
  const busy = state.busy.has(healthBusyKey(repo));
  return finding({
    ic: 'package',
    title: 'Git LFS cache',
    sub: lfs.toolInstalled ? 'Large files downloaded under .git\\lfs' : 'git lfs is not installed, so this cache cannot be pruned',
    value: bytes(lfs.bytes),
    action: button('Prune LFS cache', { small: true, disabled: busy || !lfs.toolInstalled, onClick: () => pruneLfs(repo) }),
  });
}

function goneRow(repo) {
  const names = repo.goneBranches;
  const busy = state.busy.has(healthBusyKey(repo));
  return finding({
    ic: 'git-branch',
    title: 'Branches gone from the remote',
    sub: h('span', { class: 'row-names' }, names.map((n) => h('span', { class: 'mono', title: n }, n))),
    value: plural(names.length, 'branch'),
    action: button(`Delete ${plural(names.length, 'branch')}`, { icon: 'trash', small: true, variant: 'danger', disabled: busy, onClick: () => deleteGoneBranches(repo) }),
  });
}

function stashRow(repo) {
  const s = repo.stashes;
  const oldest = s.oldestUtc ? `, oldest ${ago(s.oldestUtc)}` : '';
  return finding({
    ic: 'stack',
    title: 'Stashes',
    sub: `Information only${oldest}`,
    value: plural(s.count, 'stash', 'stashes'),
  });
}
