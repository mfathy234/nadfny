import { call, on } from './bridge.js';
import { confirmSheet, toast } from './dom.js';
import { bytes } from './format.js';
import { state, update } from './store.js';

let request = 0;
export async function loadHistory() {
  const current = ++request;
  try {
    const [report, drives] = await Promise.all([call('history.get'), call('drives.list')]);
    if (current !== request) return;
    update((s) => { s.history.report = report; s.history.error = null; s.info.drives = drives; });
  } catch (err) {
    if (current === request) update((s) => { s.history.error = err.message; });
  }
}

export async function clearHistory() {
  const report = state.history.report;
  if (!report || state.history.clearing) return;
  const { confirmed } = await confirmSheet({
    title: 'Clear history?',
    body: 'Remove Nadfny’s saved storage measurements. New scans will start a new history.',
    items: [{ label: 'Nadfny scan history', sub: report.path, size: bytes(report.storedBytes) }],
    warning: 'Past measurements and growth comparisons will be lost.',
    confirmLabel: `Clear history (${bytes(report.storedBytes)})`,
  });
  if (!confirmed) return;
  update((s) => { s.history.clearing = true; });
  try {
    await call('history.clear');
    await loadHistory();
    toast('History cleared.');
  } catch (err) {
    toast(err.message, 'bad');
  } finally {
    update((s) => { s.history.clearing = false; });
  }
}

on('history.changed', loadHistory);
loadHistory();
