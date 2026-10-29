import { compactWsl, enableWslSparse, scanWsl } from '../wsl-actions.js';
import { button, emptyState, h, sizeCell, skeletonList } from '../dom.js';
import { bytes } from '../format.js';
import { state } from '../store.js';

/** WSL distros on this PC. Shown under the Docker content, whether or not Docker is running. */
export function wslSection() {
  const { status, report, error } = state.wsl;
  const heading = h('div', { class: 'section-head' }, h('h2', null, 'WSL distros'),
    h('span', { class: 'hint' }, status === 'scanning' ? 'Reading WSL…' : 'Linux distributions on this PC'));
  if (status === 'error') return h('section', { class: 'section' }, heading,
    h('div', { class: 'callout' }, error, button('Try again', { onClick: scanWsl })));
  if (!report) return h('section', { class: 'section' }, heading, skeletonList(2));
  if (report.distros.length === 0) return h('section', { class: 'section' }, heading,
    emptyState({ ic: 'hard-drive', title: 'No WSL distributions', text: 'WSL is not installed, or no Linux distribution has been added yet.' }));

  const max = Math.max(1, ...report.distros.map((d) => d.bytes));
  return h('section', { class: 'section' }, heading,
    !report.sparseSupported && sparseNote(report.version),
    h('div', { class: 'list' }, report.distros.map((d) => distroRow(d, max, report.sparseSupported))));
}

function sparseNote(version) {
  return h('div', { class: 'callout' },
    h('div', null, h('p', null, version
      ? `Automatic shrinking needs WSL 2.0 or newer. This PC has WSL ${version}. Run wsl --update to get it.`
      : 'Automatic shrinking is hidden because the WSL version could not be read.')));
}

function distroRow(d, max, sparseOk) {
  const compacting = state.busy.has(`wsl:compact:${d.id}`);
  const shrinking = state.busy.has(`wsl:sparse:${d.id}`);
  const busy = compacting || shrinking;

  const compact = button(compacting ? 'Compacting…' : 'Compact disk', {
    small: true, icon: 'arrows-in-simple', key: `wsl-compact-${d.id}`,
    disabled: busy || !d.compactable, title: d.compactable ? undefined : 'WSL 1 keeps files in a folder, so there is no disk file to compact',
    onClick: () => compactWsl(d),
  });
  if (compacting) compact.classList.add('busy');

  const actions = [compact];
  if (d.version === 2 && sparseOk && !d.isSparse) {
    actions.push(button(shrinking ? 'Turning on…' : 'Turn on automatic shrinking', {
      small: true, key: `wsl-sparse-${d.id}`, disabled: busy, onClick: () => enableWslSparse(d),
    }));
  }

  return h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name', title: d.name }, d.name),
        h('span', { class: 'badge' }, `WSL ${d.version}`),
        d.version === 2 && (d.isSparse ? h('span', { class: 'badge good' }, 'Shrinks automatically') : h('span', { class: 'badge' }, 'Fixed size'))),
      h('div', { class: 'row-sub' }, d.vhdxPath
        ? h('span', { class: 'mono', title: d.vhdxPath }, d.vhdxPath)
        : 'Files are kept in a normal folder')),
    d.compactable ? sizeCell(bytes(d.bytes), d.bytes / max) : h('div', { class: 'size' }),
    h('div', { class: 'space-actions' }, ...actions));
}
