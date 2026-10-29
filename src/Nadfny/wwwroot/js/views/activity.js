// Floating progress panel (bottom right) for everything Nadfny is deleting or cleaning right now.
import { h, icon } from '../dom.js';
import { bytes, plural } from '../format.js';
import { elapsed, opFraction } from '../ops.js';
import { state } from '../store.js';

const MAX_ROWS = 4;

export function activityPanel() {
  const ops = [...state.ops.values()].sort((a, b) => a.startedAt - b.startedAt);
  const batch = state.batch;
  if (!ops.length && !batch) return null;

  const head = batch
    ? h('div', { class: 'act-head' },
      h('div', null,
        h('strong', null, batch.label),
        h('span', null, `${batch.done} of ${batch.count} done${batch.freedBytes ? `, ${bytes(batch.freedBytes)} freed` : ''}${batch.failed ? `, ${batch.failed} kept` : ''}`)),
      h('span', { class: 'act-time' }, elapsed(batch.startedAt)))
    : null;

  const batchBar = batch && h('div', { class: 'meter act-meter', role: 'progressbar', 'aria-label': batch.label, 'aria-valuenow': String(Math.round((batch.done / batch.count) * 100)), 'aria-valuemin': '0', 'aria-valuemax': '100' },
    h('span', { style: { transform: `scaleX(${batchFraction(batch, ops)})` } }));

  const rows = ops.slice(0, MAX_ROWS).map((op) => {
    const f = opFraction(op);
    const amount = op.total > 0 ? `${bytes(op.done)} of ${bytes(op.total)}` : elapsed(op.startedAt);
    return h('div', { class: 'act-row' },
      h('div', { class: 'act-line' },
        h('span', { class: 'act-label', title: op.label }, op.label),
        h('span', { class: 'act-amount' }, f != null ? `${Math.round(f * 100)}%` : '')),
      h('div', { class: 'act-phase' }, `${op.phase || 'Starting'}, ${amount}`),
      h('div', { class: `meter ${f == null ? 'indeterminate' : ''}` }, h('span', { style: f == null ? null : { transform: `scaleX(${f})` } })));
  });

  return h('section', { class: 'activity', 'aria-label': 'Work in progress', 'aria-live': 'polite' },
    !batch && h('div', { class: 'act-title' }, icon('broom', true), ops.length === 1 ? 'Working' : `${ops.length} tasks running`),
    head, batchBar, rows,
    ops.length > MAX_ROWS && h('div', { class: 'act-more' }, `and ${plural(ops.length - MAX_ROWS, 'more task')}`));
}

/** Batch bar counts finished items plus the partial progress of the ones in flight, so it moves smoothly. */
function batchFraction(batch, ops) {
  const partial = ops.reduce((a, o) => a + (opFraction(o) ?? 0), 0);
  return Math.min(1, (batch.done + partial) / Math.max(1, batch.count));
}
