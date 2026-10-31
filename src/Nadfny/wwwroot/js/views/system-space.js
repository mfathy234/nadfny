import { button, h, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state } from '../store.js';
import { changeHibernation, openSpaceSettings, scanSystemSpace } from '../system-space-actions.js';
import { pagingAdvice } from '../paging-advice.js';
import { withOpProgress } from './common.js';

const size = (n) => n == null ? 'Size unknown' : bytes(n);
const settings = (kind, label) => button(label, { small: true, key: `space-${kind}`, onClick: () => openSpaceSettings(kind) });

function row(title, note, amount, actions = [], progress = false) {
  const cell = h('div', { class: 'size' }, h('div', { class: 'size-num' }, amount));
  return h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } }, h('div', { class: 'row-title' }, h('span', { class: 'name' }, title)),
      h('div', { class: 'row-sub' }, note)),
    h('div', { class: 'row-meta' }),
    progress ? withOpProgress('systemSpace', cell) : cell,
    // Not .row-actions: those only show on hover, and these are the section's main actions.
    h('div', { class: 'space-actions' }, actions));
}

export function systemSpaceSection() {
  const s = state.systemSpace;
  const info = s.info;
  const heading = h('div', { class: 'section-head' }, h('h2', null, 'Windows space'),
    h('span', { class: 'hint' }, s.status === 'scanning' ? 'Reading Windows space...' : 'Managed by Windows'));
  if (!info) return h('section', { class: 'section' }, heading,
    s.status === 'error' ? h('div', { class: 'callout' }, s.error, button('Try again', { onClick: scanSystemSpace })) : skeletonList(3));
  const file = info.hibernation;
  const busy = state.busy.has('systemSpace') || s.status === 'scanning';
  const available = file.enabled === true && file.bytes > 0;
  const mode = file.enabled == null ? 'State unknown' : !file.enabled ? 'Off' : file.fileType === 'reduced' ? 'Reduced, Fast Startup only' : file.fileType === 'full' ? 'On, full file' : 'On, file type unknown';
  const actions = [button('Reduce', {
    small: true, key: 'hibernate-reduce', disabled: busy || !available || file.fileType === 'reduced', onClick: () => changeHibernation('reduce'),
  }), button('Turn off hibernation', {
    small: true, key: 'hibernate-off', disabled: busy || !available, onClick: () => changeHibernation('off'),
  })];
  const pagefileDrives = info.paging.filter((d) => d.pagefileBytes > 0).length;
  const pagingRow = (d) => {
    const { note, tip } = pagingAdvice({
      managed: info.automaticManaged, pagefileBytes: d.pagefileBytes, swapfileBytes: d.swapfileBytes,
      ramBytes: info.installedMemoryBytes, pagefileDrives,
    });
    return row(`Pagefile and swapfile (${d.drive.slice(0, 2)})`, [note, tip && h('div', null, tip)],
      d.pagefileBytes == null || d.swapfileBytes == null ? 'Size unknown' : bytes(d.pagefileBytes + d.swapfileBytes),
      [settings('pagefile', 'Performance settings')]);
  };
  return h('section', { class: 'section' }, heading,
    s.status === 'error' && h('div', { class: 'callout' }, s.error),
    h('div', { class: 'list' },
      row('Hibernation file', `${file.path} · ${mode}. Turning it off frees the file and disables Hibernate and Fast Startup. Reduce keeps Fast Startup, at about half the full size.`, size(file.bytes), actions, true),
      info.paging.map(pagingRow),
      row('System restore points', info.restorePoints == null ? 'Needs administrator to read' : `${plural(info.restorePoints, 'restore point')} across this PC. Restore points help recover Windows after a system change.`,
        '', [settings('protection', 'System Protection')]),
      !info.shadowReadable ? row('Shadow storage', 'Needs administrator to read', 'Size unknown', [settings('protection', 'System Protection')])
        : info.storage.length ? info.storage.map((d) => row(`Shadow storage (${d.drive})`,
          `Stored on ${d.storageDrive}. Includes restore points and other shadow copies. Maximum: ${d.maximumUnlimited ? 'No limit' : size(d.maximum)}.`,
          `${size(d.used)} used`, [settings('protection', 'System Protection')]))
          : row('Shadow storage', 'No shadow storage configured.', bytes(0), [settings('protection', 'System Protection')])));
}
