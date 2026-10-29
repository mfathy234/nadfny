import { addRoot, loadInfo, loadSystemInfo, removeRoot, scanAll, setAllDrives } from './actions.js';
import { activityPanel } from './views/activity.js';
import { fraction, scanPanel } from './views/progress.js';
import { h, icon, toast } from './dom.js';
import { bytes } from './format.js';
import { setRenderer, state, update } from './store.js';
import { artifactSelectionBar, artifactsView } from './views/artifacts.js';
import { cachesView } from './views/caches.js';
import { dockerUsed, dockerView } from './views/docker.js';
import { exploreView } from './views/explore.js';
import { overviewView } from './views/overview.js';
import { systemView } from './views/system.js';
import { worktreeSelectionBar, worktreesView } from './views/worktrees.js';

const VIEWS = [overviewView, worktreesView, dockerView, artifactsView, cachesView, exploreView, systemView];
const SELECTION_BARS = { worktrees: worktreeSelectionBar, artifacts: artifactSelectionBar };

const $ = (id) => document.getElementById(id);

function navSize(id) {
  const sum = (m) => [...m.values()].reduce((a, x) => a + x.bytes, 0);
  switch (id) {
    case 'worktrees': return [state.worktrees.status, sum(state.worktrees.items)];
    case 'artifacts': return [state.artifacts.status, sum(state.artifacts.items)];
    case 'caches': return [state.caches.status, sum(state.caches.items)];
    case 'docker': {
      const r = state.docker.report;
      return [state.docker.status, r?.disk?.bytes ?? dockerUsed(r)];
    }
    default: return ['done', null];
  }
}

function renderNav() {
  $('nav').replaceChildren(...VIEWS.map((v) => {
    const [status, size] = navSize(v.id);
    return h('button', {
      class: 'nav-item', type: 'button', 'aria-current': state.view === v.id ? 'page' : null, 'data-focus-key': `nav-${v.id}`,
      onpointerdown: () => update((s) => { s.view = v.id; }),
      onclick: () => update((s) => { s.view = v.id; }),
    }, icon(v.icon, state.view === v.id), h('span', null, v.title),
    size == null ? h('span') : status === 'scanning' && !size ? h('span', { class: 'nav-size pending' }) : h('span', { class: 'nav-size' }, size ? bytes(size) : ''));
  }));
}

const rootLabel = (r) => (/^[A-Za-z]:\\?$/.test(r) ? `${state.info.drives?.find((d) => d.name.toLowerCase() === r.toLowerCase())?.label ?? 'Drive'} (${r.slice(0, 2)})` : r.split('\\').pop() || r);

function renderSidebarFoot() {
  const all = !!state.info.allDrives;
  const scanning = state.worktrees.status === 'scanning' || state.artifacts.status === 'scanning';
  const toggle = h('input', {
    type: 'checkbox', class: 'switch', role: 'switch', checked: all, 'aria-label': 'Scan all drives', 'data-focus-key': 'all-drives',
    onchange: (e) => setAllDrives(e.target.checked).catch((err) => toast(err.message, 'bad')),
  });

  $('sidebar-foot').replaceChildren(...[
    h('div', { class: 'roots' },
      h('h2', null, 'Scan locations'),
      h('label', { class: 'drive-toggle' },
        h('div', null, h('strong', null, 'All drives'), h('span', null, all ? (scanning ? 'Scanning every drive…' : 'Every local drive') : 'Only the folders below')),
        toggle),
      (state.info.roots ?? []).map((r) => h('div', { class: 'root', title: r },
        icon(/^[A-Za-z]:\\?$/.test(r) ? 'hard-drives' : 'folder'), h('span', null, rootLabel(r)),
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Stop scanning ${r}`, title: 'Remove location', onclick: () => removeRoot(r) }, icon('x')))),
      !all && h('button', { class: 'btn quiet small', type: 'button', style: { marginTop: '4px' }, onclick: () => addRoot().catch((e) => toast(e.message, 'bad')) }, icon('plus'), 'Add location')),
    state.freed > 0 && h('div', { class: 'freed' }, h('div', { class: 'freed-label' }, 'Freed this session'), h('div', { class: 'freed-value' }, bytes(state.freed))),
  ].filter(Boolean));
}

function renderMain(view) {
  const scanning = view.id === 'docker' ? state.docker.status === 'scanning'
    : view.id === 'explore' ? state.explore.status === 'scanning'
      : view.id === 'overview' ? ['worktrees', 'artifacts', 'caches'].some((k) => state[k].status === 'scanning') || state.docker.status === 'scanning'
        : state[view.id]?.status === 'scanning';

  $('toolbar').replaceChildren(...[
    h('div', { class: 'toolbar-title' }, h('h1', null, view.title), h('p', null, view.subtitle())),
    h('div', { class: 'toolbar-actions' }, view.actions()),
    scanning && scanLine(view.id),
  ].filter(Boolean));

  const panelScopes = PANEL_SCOPES[view.id];
  $('content').replaceChildren(...[panelScopes && scanPanel(panelScopes), view.render()].filter(Boolean));

  const bar = $('selection-bar');
  const content = SELECTION_BARS[view.id]?.();
  bar.classList.toggle('show', !!content);
  if (content) bar.replaceChildren(...content);
}

const PANEL_SCOPES = {
  overview: ['worktrees', 'docker', 'artifacts', 'caches'],
  worktrees: ['worktrees'],
  artifacts: ['artifacts'],
};

/** Thin bar under the toolbar: determinate when the backend reports a total for this view's scan. */
function scanLine(viewId) {
  const f = fraction(viewId);
  return h('div', {
    class: `scan-line ${f == null ? '' : 'determinate'}`, role: 'progressbar', 'aria-label': 'Scanning',
    style: f == null ? null : { '--p': String(f) },
  });
}

let lastView = null;
function render() {
  const focusKey = document.activeElement?.dataset?.focusKey;
  const view = VIEWS.find((v) => v.id === state.view) ?? overviewView;
  renderNav();
  renderSidebarFoot();
  renderMain(view);
  // The panel is rebuilt on every progress tick; only its first appearance animates in.
  const host = $('activity');
  const panel = activityPanel();
  if (panel && host.childElementCount === 0) panel.classList.add('enter');
  host.replaceChildren(...[panel].filter(Boolean));
  if (lastView !== view.id) {
    $('main').scrollTop = 0;
    lastView = view.id;
  }
  if (focusKey) document.querySelector(`[data-focus-key="${CSS.escape(focusKey)}"]`)?.focus({ preventScroll: true });
}

setRenderer(render);

$('main').addEventListener('scroll', (e) => e.currentTarget.classList.toggle('scrolled', e.currentTarget.scrollTop > 4), { passive: true });

document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey && e.key.toLowerCase() === 'r') || e.key === 'F5') {
    e.preventDefault();
    VIEWS.find((v) => v.id === state.view)?.actions()[0]?.click();
  }
  if (e.ctrlKey && /^[1-7]$/.test(e.key)) {
    e.preventDefault();
    update((s) => { s.view = VIEWS[Number(e.key) - 1].id; });
  }
  if (e.key === 'Escape' && !$('dialog').open) {
    update((s) => { s.selection.worktrees.clear(); s.selection.artifacts.clear(); });
  }
});

render();
loadSystemInfo(); // read-only and slow-ish: start now so This PC is ready when opened
loadInfo()
  .then(() => scanAll())
  .catch((err) => toast(err.message, 'bad'));
