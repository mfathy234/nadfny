// Tiny DOM helpers: element builder, icons, toasts and the confirmation sheet.

export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props ?? {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style' && typeof v === 'object') {
      for (const [prop, val] of Object.entries(v)) {
        if (prop.startsWith('--')) el.style.setProperty(prop, val);
        else el.style[prop] = val;
      }
    }
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k in el && typeof v !== 'string') el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children) {
    if (c == null || c === false) continue;
    if (Array.isArray(c)) append(el, c);
    else el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

export const icon = (name, fill = false) => h('i', { class: `${fill ? 'ph-fill' : 'ph'} ph-${name}`, 'aria-hidden': 'true' });

export function button(label, { icon: ic, variant = '', onClick, title, disabled, small, key } = {}) {
  return h('button', {
    class: `btn ${variant} ${small ? 'small' : ''}`.trim(),
    type: 'button', title, disabled, onclick: onClick, 'data-focus-key': key,
  }, ic && icon(ic), label);
}

/** Puts a button into a spinning busy state until the promise settles. */
export async function withBusy(btn, promise) {
  const original = btn.innerHTML;
  btn.classList.add('busy');
  btn.innerHTML = '';
  btn.append(icon('arrow-clockwise'), btn.dataset.busyLabel || 'Working…');
  try { return await promise; } finally { btn.classList.remove('busy'); btn.innerHTML = original; }
}

export function toast(message, kind = 'good') {
  const host = document.getElementById('toasts');
  const el = h('div', { class: `toast ${kind}` }, icon(kind === 'good' ? 'check-circle' : 'warning-circle', true), message);
  host.append(el);
  setTimeout(() => {
    el.classList.add('leaving');
    el.addEventListener('animationend', () => el.remove(), { once: true });
  }, kind === 'good' ? 3800 : 7000);
}

/**
 * Confirmation sheet for every destructive action. The confirm button always names what is removed.
 * Resolves { confirmed, option }.
 */
export function confirmSheet({ title, body, items = [], warning, option, ack, confirmLabel, cancelLabel = 'Cancel', danger = true }) {
  const dialog = document.getElementById('dialog');
  return new Promise((resolve) => {
    let optionBox, ackBox;
    const confirm = h('button', { class: `btn ${danger ? 'danger' : 'primary'}`, type: 'button', 'data-focus-key': 'sheet-confirm' }, confirmLabel);
    const cancel = h('button', { class: 'btn', type: 'button' }, cancelLabel);

    const syncAck = () => { confirm.disabled = !!ackBox && !ackBox.checked; };

    dialog.replaceChildren(
      h('div', { class: 'sheet-body' },
        h('h2', { id: 'sheet-title' }, title),
        body && h('p', null, body),
        items.length > 0 && h('ul', { class: 'sheet-items' }, items.map((it) =>
          h('li', null,
            h('span', { class: 'p' }, it.label, it.sub && h('small', null, it.sub)),
            h('span', { class: 's' }, it.size ?? '')))),
        warning && h('div', { class: 'sheet-warn' }, icon('warning', true), h('div', null, warning)),
        ack && h('label', { class: 'sheet-option' },
          (ackBox = h('input', { type: 'checkbox', class: 'check-input', onchange: syncAck })), ack),
        option && h('label', { class: 'sheet-option' },
          (optionBox = h('input', { type: 'checkbox', class: 'check-input', checked: !!option.checked })), option.label)),
      h('div', { class: 'sheet-actions' }, cancel, confirm));
    dialog.setAttribute('aria-labelledby', 'sheet-title');
    syncAck();

    const close = (confirmed) => {
      dialog.classList.add('closing');
      setTimeout(() => {
        dialog.classList.remove('closing');
        dialog.close();
        resolve({ confirmed, option: !!optionBox?.checked });
      }, matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 150);
    };
    confirm.onclick = () => close(true);
    cancel.onclick = () => close(false);
    dialog.oncancel = (e) => { e.preventDefault(); close(false); };
    dialog.showModal();
    (ackBox ? cancel : confirm).focus();
  });
}

export function emptyState({ ic, title, text, action, error }) {
  return h('div', { class: `empty ${error ? 'error' : ''}` }, icon(ic), h('h3', null, title), text && h('p', null, text), action);
}

export function skeletonList(rows = 5) {
  return h('div', { class: 'list', 'aria-busy': 'true', 'aria-label': 'Loading' },
    Array.from({ length: rows }, () => h('div', { class: 'skeleton-row' }, h('i'), h('i'), h('i'))));
}

export function sizeCell(bytesText, ratio, pending = false) {
  return h('div', { class: `size ${pending ? 'pending' : ''}` },
    h('div', { class: 'size-num' }, pending ? '' : bytesText),
    !pending && h('div', { class: 'size-bar', style: { width: `${Math.max(2, Math.round(ratio * 100))}%` } }));
}
