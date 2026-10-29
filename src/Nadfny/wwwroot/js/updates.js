// Update flow (like Dawsha): check 10 s after launch and every 6 h; installed copies download, verify and
// upgrade in place; portable copies get a link to the release page.
import { call } from './bridge.js';
import { confirmSheet, h, icon, toast } from './dom.js';
import { bytes } from './format.js';
import { runOp } from './ops.js';
import { state, update } from './store.js';

const START_DELAY = 10_000;
const INTERVAL = 6 * 60 * 60 * 1000;
const DISMISS_KEY = 'nadfny.update.dismissed';

function dismissedVersion() {
  try { return localStorage.getItem(DISMISS_KEY); } catch { return null; }
}

export async function checkForUpdates({ manual = false } = {}) {
  if (['downloading', 'ready'].includes(state.update.status)) return;
  update((s) => { s.update = { ...s.update, status: 'checking', error: null }; });
  try {
    const info = await call('update.check');
    update((s) => { s.update = { ...s.update, info, status: info.available ? 'available' : 'current' }; });
    if (manual && !info.available) toast(`You have the latest version (${info.currentVersion}).`);
    if (manual && info.available) {
      try { localStorage.removeItem(DISMISS_KEY); } catch { /* storage unavailable */ }
    }
  } catch (err) {
    update((s) => { s.update = { ...s.update, status: 'error', error: err.message }; });
    if (manual) toast(`Could not check for updates. ${err.message}`, 'bad');
  }
}

export function startUpdateChecks() {
  setTimeout(() => checkForUpdates(), START_DELAY);
  setInterval(() => checkForUpdates(), INTERVAL);
}

/** Release notes as plain text for the confirmation sheet (no images, no markdown syntax). */
function notesExcerpt(md) {
  const text = (md ?? '')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/```[\s\S]*?```/g, '')
    .replace(/^#+\s*/gm, '')
    .replace(/\*\*|__|`/g, '')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
  return text.length > 700 ? `${text.slice(0, 700).trimEnd()}…` : text;
}

async function startUpdate() {
  const info = state.update.info;
  if (info.mode !== 'installed') {
    await call('update.openPage', { url: info.releaseUrl }).catch((err) => toast(err.message, 'bad'));
    return;
  }
  const { confirmed } = await confirmSheet({
    title: `Update to Nadfny ${info.latestVersion}?`,
    body: notesExcerpt(info.notes) || 'A new version is available.',
    confirmLabel: info.downloadBytes ? `Download update (${bytes(info.downloadBytes)})` : 'Download update',
    danger: false,
  });
  if (!confirmed) return;
  update((s) => { s.update = { ...s.update, status: 'downloading' }; });
  try {
    await runOp(`Nadfny ${info.latestVersion}`, 'update.download', {}, { key: 'update' });
    update((s) => { s.update = { ...s.update, status: 'ready' }; });
  } catch (err) {
    update((s) => { s.update = { ...s.update, status: 'available' }; });
    toast(err.message, 'bad');
  }
}

async function installNow() {
  update((s) => { s.update = { ...s.update, status: 'installing' }; });
  try {
    await call('update.install');
    toast('Installing the update. Nadfny will reopen in a moment.');
  } catch (err) {
    update((s) => { s.update = { ...s.update, status: 'ready' }; });
    toast(err.message, 'bad');
  }
}

function dismiss() {
  try { localStorage.setItem(DISMISS_KEY, state.update.info?.latestVersion ?? ''); } catch { /* storage unavailable */ }
  update();
}

/** Sidebar card while an update is available, downloading or ready to install. */
export function updateCard() {
  const { status, info } = state.update;
  if (!info || !['available', 'downloading', 'ready', 'installing'].includes(status)) return null;
  if (status === 'available' && dismissedVersion() === info.latestVersion) return null;

  const portable = info.mode !== 'installed';
  const title = status === 'ready' || status === 'installing' ? 'Update ready' : `Nadfny ${info.latestVersion} is available`;
  const text = status === 'downloading' ? 'Downloading…'
    : status === 'ready' ? `Restart to finish updating to ${info.latestVersion}.`
      : status === 'installing' ? 'Closing to install…'
        : portable ? 'Get the new portable zip from GitHub.' : `You have ${info.currentVersion}.`;

  const action = status === 'available'
    ? h('button', { class: 'btn primary small', type: 'button', onclick: startUpdate, 'data-focus-key': 'update-go' }, portable ? 'Download' : 'Update')
    : status === 'ready'
      ? h('button', { class: 'btn primary small', type: 'button', onclick: installNow, 'data-focus-key': 'update-install' }, 'Restart to update')
      : null;

  return h('div', { class: 'update-card', role: 'status' },
    h('div', { class: 'update-head' },
      h('span', { class: 'update-icon' }, icon('arrow-up', true)),
      h('strong', null, title),
      status === 'available' && h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Not now', title: 'Not now', onclick: dismiss }, icon('x'))),
    h('p', null, text),
    action);
}

/** "Nadfny 0.1.0 · Check for updates" line at the bottom of the sidebar. */
export function versionLine() {
  const v = state.update.info?.currentVersion ?? state.info.version?.split('+')[0] ?? '';
  const checking = state.update.status === 'checking';
  return h('div', { class: 'version-line' },
    h('span', null, `Nadfny ${v}`),
    h('button', { type: 'button', class: 'link-btn', disabled: checking, onclick: () => checkForUpdates({ manual: true }) },
      checking ? 'Checking…' : 'Check for updates'));
}
