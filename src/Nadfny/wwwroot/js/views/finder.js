import { reveal } from '../actions.js';
import { button, emptyState, h, sizeCell, skeletonList } from '../dom.js';
import { cancelFinder, changeFinder, recycleFinder, scanFinder, toggleFinder } from '../finder-actions.js';
import { ago, bytes, plural } from '../format.js';
import { state, update } from '../store.js';
import { checkbox, segmented, withOpProgress } from './common.js';
import { fraction } from './progress.js';

const TABS = [['large', 'Large files'], ['downloads', 'Old downloads'], ['duplicates', 'Duplicates']];

export const finderView = {
  id: 'finder', title: 'Large files', icon: 'file',
  subtitle() {
    const st = state.finder;
    if (st.status === 'scanning') return 'Checking files. You can cancel at any time.';
    if (st.tab === 'duplicates') return `${plural(st.groups.length, 'group')}, ${bytes(st.groups.reduce((n, g) => n + g.wastedBytes, 0))} in extra copies. Keep at least one copy of every group.`;
    return st.tab === 'downloads' ? 'Old items directly in your Downloads folder. Review them before removing.' : 'Large files in your scan locations. Selected items go to the Recycle Bin.';
  },
  actions() {
    const st = state.finder;
    return [button(st.status === 'idle' ? 'Scan' : 'Rescan', { icon: 'arrow-clockwise', key: 'finder-scan', onClick: scanFinder, disabled: st.status === 'scanning' || state.busy.has('finder') }),
      st.status === 'scanning' && button('Cancel', { key: 'finder-cancel', onClick: cancelFinder })].filter(Boolean);
  },
  render() {
    const st = state.finder;
    const locked = st.status === 'scanning' || state.busy.has('finder');
    const options = st.tab === 'large' ? [[100, '100 MB'], [500, '500 MB'], [1024, '1 GB']] : [[30, '30 days'], [90, '90 days'], [180, '180 days']];
    const select = (list) => {
      const all = list.every((f) => st.selected.has(f.path));
      list.forEach((f) => toggleFinder(f.path, !all));
    };
    const all = [...st.items.values()];
    return h('div', null,
      h('fieldset', { class: 'filters', disabled: locked, style: { border: '0', padding: '0', margin: '0 0 20px', minWidth: '0' } },
        segmented(TABS.map(([value, label]) => ({ value, label })), st.tab, (v) => changeFinder('tab', v), 'Finder tab'),
        st.tab !== 'duplicates' && segmented(options.map(([value, label]) => ({ value, label })), st.tab === 'large' ? st.threshold : st.age,
          (v) => changeFinder(st.tab === 'large' ? 'threshold' : 'age', v), st.tab === 'large' ? 'Larger than' : 'Not modified for')),
      st.status === 'scanning' && progressPanel(),
      st.status === 'error' ? emptyState({ ic: 'warning-circle', error: true, title: 'Scan failed', text: st.error })
        : st.status === 'idle' || st.status === 'cancelled' ? emptyState({ ic: 'file', title: st.status === 'cancelled' ? 'Scan cancelled' : 'Ready to look through files', text: st.tab === 'downloads' ? 'Scan Downloads for items older than the selected age.' : 'Scan the locations listed in the sidebar.', action: button('Scan files', { onClick: scanFinder, disabled: locked }) })
          : !all.length ? st.status === 'scanning' ? skeletonList(6) : emptyState({ ic: 'check-circle', title: 'No matching files', text: 'Try another filter or scan location.' })
            : h('div', null,
              st.tab !== 'duplicates' && button(st.selected.size === all.length ? 'Deselect all' : 'Select all', { variant: 'quiet', small: true, key: 'finder-select-all', onClick: () => select(all), disabled: locked }),
              st.tab === 'duplicates' ? st.groups.map((g) => section(`${plural(g.files.length, 'copy', 'copies')}, ${bytes(g.wastedBytes)} in extra copies`, g.files, locked))
                : st.tab === 'downloads' ? ['Installers', 'Archives', 'Videos', 'Images', 'Documents', 'Other'].map((type) => {
                  const files = all.filter((f) => f.type === type);
                  return files.length && section(`${type}, ${plural(files.length, 'item')}, ${bytes(files.reduce((n, f) => n + f.bytes, 0))}`, files, locked);
                }) : h('div', { class: 'list' }, all.sort((a, b) => b.bytes - a.bytes).map((f) => fileRow(f, locked)))));
  },
};

function section(title, files, locked) {
  return h('section', { class: 'section' }, h('div', { class: 'section-head' }, h('h2', null, title)), h('div', { class: 'list' }, files.map((f) => fileRow(f, locked))));
}

function fileRow(file, locked) {
  const st = state.finder;
  const group = st.groups.find((g) => g.files.some((f) => f.path === file.path));
  const selected = st.selected.has(file.path);
  const keeper = !selected && group && group.files.filter((f) => !st.selected.has(f.path)).length <= 1;
  return h('div', { class: `row ${selected ? 'is-selected' : ''}` },
    h('fieldset', { disabled: locked || keeper, style: { border: '0', padding: '0', margin: '0', minWidth: '0' } },
      checkbox({ checked: selected, label: keeper ? `Keep ${file.path}, last unselected copy` : `Select ${file.path}`, key: `finder-${file.path}`, onChange: (on) => toggleFinder(file.path, on) })),
    h('div', { style: { minWidth: '0' } },
      h('div', { class: 'row-title' }, h('span', { class: 'name', title: file.name }, file.name), keeper && h('span', { class: 'badge' }, 'Keep')),
      h('div', { class: 'row-sub', title: file.folder }, file.folder)),
    h('div', { class: 'row-meta' },
      h('div', null, file.isDirectory ? 'Folder' : file.type, h('div', { title: new Date(file.lastWriteUtc).toLocaleString() }, `Modified ${ago(file.lastWriteUtc)}`)),
      h('div', { class: 'row-actions' }, button('Show', { variant: 'quiet', small: true, key: `finder-show-${file.path}`, title: `Show ${file.path} in Explorer`, onClick: () => reveal(file.path) }))),
    withOpProgress('finder', sizeCell(bytes(file.bytes))));
}

function progressPanel() {
  const p = state.progress.finder;
  const f = fraction('finder');
  return h('section', { class: 'section scan-panel', 'aria-live': 'polite' },
    h('div', { class: 'section-head' }, h('h2', null, p?.phase || 'Starting scan'), h('span', { class: 'hint' }, p?.total ? `${p.done} of ${p.total}` : 'Results appear as they are found.')),
    h('div', { class: `meter ${f == null ? 'indeterminate' : ''}`, role: 'progressbar', 'aria-label': 'Finder scan', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': f == null ? null : String(Math.round(f * 100)) }, h('span', { style: f == null ? null : { transform: `scaleX(${f})` } })),
    h('div', { class: 'scan-path', title: p?.current }, p?.current ?? ''));
}

export function finderSelectionBar() {
  const st = state.finder;
  const files = [...st.selected].map((p) => st.items.get(p)).filter(Boolean);
  if (!files.length) return null;
  const total = files.reduce((n, f) => n + f.bytes, 0);
  const busy = state.busy.has('finder');
  return [h('span', { class: 'sel-count' }, `${plural(files.length, 'item')} selected, ${bytes(total)}`),
    button('Deselect', { variant: 'quiet', small: true, disabled: busy, onClick: () => update((s) => s.finder.selected.clear()) }),
    button(`Move to Recycle Bin (${bytes(total)})`, { variant: 'danger', icon: 'trash', key: 'finder-remove', disabled: busy || st.status !== 'done', onClick: recycleFinder })];
}
