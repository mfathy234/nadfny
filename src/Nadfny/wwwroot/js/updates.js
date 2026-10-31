// Automatic updates: check 10 s after launch, every hour, and when the window is shown again or the PC wakes
// (at most once an hour). Installed copies download the new version in the background and then offer
// "Restart to update"; they never restart on their own. Portable copies get a link to the release page.
import { call, on } from './bridge.js';
import { h, icon, toast } from './dom.js';
import { bytes } from './format.js';
import { opFor, runOp } from './ops.js';
import { state, update } from './store.js';

const START_DELAY = 10_000;
const INTERVAL = 60 * 60 * 1000;
const STALE_AFTER = INTERVAL;
const LEGACY_DISMISS_KEY = 'nadfny.update.dismissed';

let checking = false;
let lastCheckAt = 0;
let dismissedFor = null;

/** Test hook: forget throttle, in-flight and "Not now" state. */
export function resetUpdateChecks() {
  checking = false;
  lastCheckAt = 0;
  dismissedFor = null;
}

const busy = () => ['downloading', 'ready', 'installing'].includes(state.update.status);

export async function checkForUpdates({ manual = false, now = Date.now() } = {}) {
  if (checking || busy()) return;
  checking = true;
  lastCheckAt = now;
  if (manual || state.update.status !== 'available') update((s) => { s.update = { ...s.update, status: 'checking', error: null }; });
  let info = null;
  try {
    info = await call('update.check');
    update((s) => { s.update = { ...s.update, info, status: info.available ? 'available' : 'current' }; });
    if (manual && !info.available) toast(`You have the latest version (${info.currentVersion}).`);
    if (manual && info.available) dismissedFor = null;
  } catch (err) {
    update((s) => { s.update = { ...s.update, status: 'error', error: err.message }; });
    if (manual) toast(`Could not check for updates. ${err.message}`, 'bad');
  } finally {
    checking = false;
  }
  if (info?.available && info.mode === 'installed') await downloadUpdate({ silent: !manual });
}

/** Checks again only when the last check is over an hour old (window shown again, PC woke up). */
export async function checkIfStale(now = Date.now()) {
  if (now - lastCheckAt >= STALE_AFTER) await checkForUpdates({ now });
}

export function startUpdateChecks() {
  try { localStorage.removeItem(LEGACY_DISMISS_KEY); } catch { /* storage unavailable */ }
  setTimeout(() => checkForUpdates(), START_DELAY);
  setInterval(() => checkForUpdates(), INTERVAL);
  const whenShown = () => { if (document.visibilityState !== 'hidden') checkIfStale(); };
  document.addEventListener('visibilitychange', whenShown);
  window.addEventListener('focus', whenShown);
  on('system.resumed', () => checkIfStale());
}

/** Downloads in the background. A failed automatic try is quiet and retried by the next check. */
async function downloadUpdate({ silent = false } = {}) {
  const info = state.update.info;
  if (!info || busy()) return;
  update((s) => { s.update = { ...s.update, status: 'downloading' }; });
  try {
    await runOp(`Nadfny ${info.latestVersion}`, 'update.download', {}, { key: 'update' });
    update((s) => { s.update = { ...s.update, status: 'ready' }; });
  } catch (err) {
    update((s) => { s.update = { ...s.update, status: 'available' }; });
    if (!silent) toast(err.message, 'bad');
  }
}

async function startUpdate() {
  const info = state.update.info;
  if (info.mode !== 'installed') {
    await call('update.openPage', { url: info.releaseUrl }).catch((err) => toast(err.message, 'bad'));
    return;
  }
  await downloadUpdate();
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

/** "Not now": hides the card for this version until the next launch. */
export function dismissUpdate() {
  dismissedFor = state.update.info?.latestVersion ?? null;
  update();
}

// Bytes and time when this download was first seen, so the speed ignores bytes resumed from an earlier try.
let downloadStart = null;

/** "Downloading 12%, about 40 min left. You can keep using Nadfny." */
function downloadText() {
  const op = opFor('update');
  if (!op || !op.total || !/^Downloading|resuming/.test(op.phase ?? '')) {
    downloadStart = null;
    return op?.phase ? `${op.phase}…` : 'Downloading…';
  }
  const now = Date.now();
  if (!downloadStart) downloadStart = { done: op.done, at: now };
  const percent = Math.floor((op.done / op.total) * 100);
  const seconds = (now - downloadStart.at) / 1000;
  const rate = seconds >= 5 ? (op.done - downloadStart.done) / seconds : 0;
  const left = rate > 0 ? Math.ceil((op.total - op.done) / rate / 60) : null;
  const eta = left == null ? '' : left <= 1 ? ', about a minute left' : `, about ${left} min left`;
  return `${/resuming/.test(op.phase) ? 'Reconnecting' : 'Downloading'} ${percent}% (${bytes(op.done)} of ${bytes(op.total)})${eta}. You can keep using Nadfny.`;
}

/** Sidebar card while an update is available, downloading or ready to install. */
export function updateCard() {
  const { status, info } = state.update;
  if (!info || !['available', 'downloading', 'ready', 'installing'].includes(status)) return null;
  if (status === 'available' && dismissedFor === info.latestVersion) return null;

  const portable = info.mode !== 'installed';
  const title = status === 'ready' || status === 'installing' ? 'Update ready' : `Nadfny ${info.latestVersion} is available`;
  const text = status === 'downloading' ? downloadText()
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
      status === 'available' && h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Not now', title: 'Not now', onclick: dismissUpdate }, icon('x'))),
    h('p', null, text),
    action);
}

/** "Nadfny 0.1.0 · Check for updates" line at the bottom of the sidebar. */
export function versionLine() {
  const v = state.update.info?.currentVersion ?? state.info.version?.split('+')[0] ?? '';
  const isChecking = state.update.status === 'checking';
  return h('div', { class: 'version-line' },
    h('span', null, `Nadfny ${v}`),
    h('button', { type: 'button', class: 'link-btn', disabled: isChecking, onclick: () => checkForUpdates({ manual: true }) },
      isChecking ? 'Checking…' : 'Check for updates'));
}
