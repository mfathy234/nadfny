import { h } from '../dom.js';
import { opFor, opFraction } from '../ops.js';
import { state } from '../store.js';

const seen = new Set();
let scanning = false;

/** Marks rows that appear while a scan streams in so they fade in once; never on plain re-renders. */
export function setStreaming(on) { scanning = on; }

export function rowWrap(key, row) {
  const fresh = scanning && !seen.has(key);
  seen.add(key);
  const cls = ['row-wrap', fresh && 'entering', state.leaving.has(key) && 'leaving'].filter(Boolean).join(' ');
  return h('div', { class: cls, 'data-key': key }, row);
}

export function checkbox({ checked, indeterminate, label, onChange, key }) {
  const box = h('input', {
    type: 'checkbox', class: 'check-input', checked: !!checked, 'aria-label': label, 'data-focus-key': key,
    onchange: (e) => onChange(e.target.checked),
    onclick: (e) => e.stopPropagation(),
  });
  box.indeterminate = !!indeterminate;
  return box;
}

export function segmented(options, value, onChange, label) {
  return h('div', { class: 'segmented', role: 'group', 'aria-label': label },
    options.map((o) => h('button', {
      type: 'button', 'aria-pressed': String(o.value === value), 'data-focus-key': `seg-${label}-${o.value}`,
      onclick: () => onChange(o.value),
    }, o.label, o.count != null && h('span', { class: 'count' }, o.count))));
}

/** While a row's item is being deleted, its size cell turns into a live percentage + bar. */
export function withOpProgress(key, normalCell) {
  const op = opFor(key);
  if (!op) return normalCell;
  const f = opFraction(op);
  return h('div', { class: 'size row-op', title: op.phase },
    h('div', { class: 'size-num' }, f == null ? (op.phase || 'Working') : `${Math.round(f * 100)}%`),
    h('div', { class: `meter ${f == null ? 'indeterminate' : ''}` }, h('span', { style: f == null ? null : { transform: `scaleX(${f})` } })));
}

export function errorText(err) {
  return err?.message ?? String(err ?? 'Something went wrong.');
}
