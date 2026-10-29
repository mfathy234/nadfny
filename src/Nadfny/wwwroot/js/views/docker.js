import { dockerCompact, dockerPrune, dockerRemove, scanDocker } from '../actions.js';
import { button, emptyState, h, icon, sizeCell, skeletonList } from '../dom.js';
import { bytes, plural } from '../format.js';
import { state, update } from '../store.js';
import { rowWrap, segmented } from './common.js';

/** Bytes Docker itself accounts for inside its disk. */
export function dockerUsed(r) {
  return r ? r.imagesBytes + r.containersBytes + r.volumesBytes + r.buildCacheBytes : 0;
}

export function unusedImages(r) {
  return (r?.images ?? []).filter((i) => i.containers === 0);
}

export const dockerView = {
  id: 'docker',
  title: 'Docker',
  icon: 'cube',

  subtitle() {
    const { status, report } = state.docker;
    if (status === 'scanning' && !report) return 'Asking Docker for disk usage…';
    if (!report) return 'Images, containers, volumes and build cache';
    if (!report.running) return report.message;
    return `Using ${bytes(dockerUsed(report))}${report.disk ? ` in a ${bytes(report.disk.bytes)} disk file` : ''}`;
  },

  actions() {
    return [button('Rescan', { icon: 'arrow-clockwise', onClick: scanDocker, disabled: state.docker.status === 'scanning', key: 'rescan' })];
  },

  render() {
    const { status, report, error } = state.docker;
    if (status === 'error') return emptyState({ ic: 'warning-circle', error: true, title: 'Docker did not answer', text: error, action: button('Try again', { onClick: scanDocker }) });
    if (!report) return skeletonList(4);

    return h('div', null,
      report.running ? summary(report) : emptyState({
        ic: 'cube', title: report.installed ? 'Docker is not running' : 'Docker is not installed', text: report.message,
        action: report.installed && button('Check again', { onClick: scanDocker }),
      }),
      report.disk && diskCallout(report),
      report.running && lists(report));
  },
};

function summary(r) {
  const unused = unusedImages(r);
  const unusedBytes = unused.reduce((a, i) => a + i.uniqueBytes, 0);
  const stopped = r.containers.filter((c) => c.state !== 'running');
  const looseVolumes = r.volumes.filter((v) => v.links === 0);

  const tile = (ic, title, big, sub, action) => h('div', { class: 'summary-item' },
    h('h3', null, icon(ic), title), h('div', { class: 'big' }, big), h('div', { class: 'sub' }, sub), action);
  const prune = (kind, label, estimate, disabled, warning) => {
    const b = button(label, { small: true, onClick: () => dockerPrune(kind, estimate, warning), disabled: disabled || state.busy.has(`docker:${kind}`), key: `prune-${kind}` });
    if (state.busy.has(`docker:${kind}`)) b.classList.add('busy');
    return b;
  };

  return h('section', { class: 'section' },
    h('div', { class: 'summary' },
      tile('stack', 'Build cache', bytes(r.buildCacheBytes), `${bytes(r.buildCacheReclaimable)} reclaimable`,
        prune('buildCache', 'Clear build cache', r.buildCacheBytes, r.buildCacheBytes === 0)),
      tile('package', 'Images', bytes(r.imagesBytes), unused.length ? `${plural(unused.length, 'unused image')}, ${bytes(unusedBytes)}` : 'All in use',
        prune('unusedImages', 'Remove unused', r.imagesReclaimable, unused.length === 0)),
      tile('shipping-container', 'Containers', bytes(r.containersBytes), stopped.length ? `${plural(stopped.length, 'stopped container')}` : 'All running',
        prune('stoppedContainers', 'Remove stopped', r.containersReclaimable, stopped.length === 0)),
      tile('database', 'Volumes', bytes(r.volumesBytes), looseVolumes.length ? `${plural(looseVolumes.length, 'unused volume')}, ${bytes(r.volumesReclaimable)}` : 'All attached',
        prune('unusedVolumes', 'Remove unused', r.volumesReclaimable, looseVolumes.length === 0,
          'Unused volumes can still hold data from containers you removed, such as local databases.'))));
}

function diskCallout(r) {
  const used = dockerUsed(r);
  const slack = Math.max(0, r.disk.bytes - used);
  const busy = state.busy.has('docker:compact');
  const action = button(busy ? 'Compacting…' : 'Compact disk', { icon: 'arrows-in-simple', onClick: () => dockerCompact(r.disk, used), disabled: busy, key: 'compact' });
  if (busy) action.classList.add('busy');
  return h('section', { class: 'section' },
    h('div', { class: 'callout' },
      h('div', { class: 'rec-icon', style: { background: 'var(--cat-docker)' } }, icon('hard-drives', true)),
      h('div', null,
        h('h3', null, `Docker’s disk file takes ${bytes(r.disk.bytes)} on Windows`),
        h('p', null, r.running
          ? `Docker uses about ${bytes(used)} inside it, so roughly ${bytes(slack)} is empty space. Pruning frees space inside the file; compacting returns it to Windows.`
          : 'The file never shrinks on its own after a prune. Compacting returns its empty space to Windows.')),
      action));
}

function lists(r) {
  const tab = state.docker.tab;
  const tabs = [
    { value: 'images', label: 'Images', count: r.images.length },
    { value: 'volumes', label: 'Volumes', count: r.volumes.length },
    { value: 'containers', label: 'Containers', count: r.containers.length },
  ];
  const body = tab === 'volumes' ? volumeRows(r) : tab === 'containers' ? containerRows(r) : imageRows(r);
  return h('section', { class: 'section' },
    h('div', { class: 'filters' }, segmented(tabs, tab, (v) => update((s) => { s.docker.tab = v; }), 'Docker objects')),
    body.length ? h('div', { class: 'list' }, body) : emptyState({ ic: 'check-circle', title: 'Nothing here' }));
}

const removeBtn = (kind, id, label, size, disabledReason) => h('button', {
  class: 'icon-btn', type: 'button', title: disabledReason ?? 'Remove', 'aria-label': `Remove ${label}`,
  disabled: !!disabledReason || state.busy.has(`docker:${kind}:${id}`),
  onclick: () => dockerRemove(kind, id, label, size),
}, icon('trash'));

function imageRows(r) {
  const max = Math.max(1, ...r.images.map((i) => i.uniqueBytes));
  return r.images.map((i) => {
    const name = i.repository === '<none>' ? i.id.replace('sha256:', '').slice(0, 12) : `${i.repository}:${i.tag}`;
    return rowWrap(`img-${i.id}`, h('div', { class: 'row no-check' },
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' }, h('span', { class: 'name', title: name }, name),
          i.repository === '<none>' && h('span', { class: 'badge' }, 'Dangling'),
          i.containers > 0 ? h('span', { class: 'badge good' }, 'In use') : h('span', { class: 'badge' }, 'Unused')),
        h('div', { class: 'row-sub' }, `Created ${i.createdSince}`, i.bytes - i.uniqueBytes > 2 ** 20 && `, ${bytes(i.bytes - i.uniqueBytes)} shared with other images`)),
      h('div', { class: 'row-meta' }, i.containers > 0 ? plural(i.containers, 'container') : ''),
      sizeCell(bytes(i.uniqueBytes), i.uniqueBytes / max),
      h('div', { class: 'row-actions' }, removeBtn('image', i.id, name, i.uniqueBytes, i.containers > 0 ? 'Used by a container' : null))));
  });
}

function volumeRows(r) {
  const max = Math.max(1, ...r.volumes.map((v) => v.bytes));
  return r.volumes.map((v) => {
    const name = v.anonymous ? v.name.slice(0, 12) : v.name;
    return rowWrap(`vol-${v.name}`, h('div', { class: 'row no-check' },
      h('div', { style: { minWidth: 0 } },
        h('div', { class: 'row-title' }, h('span', { class: 'name', title: v.name }, name),
          v.anonymous && h('span', { class: 'badge' }, 'Anonymous'),
          v.links === 0 ? h('span', { class: 'badge warn' }, 'Unused') : h('span', { class: 'badge good' }, 'Attached'))),
      h('div', { class: 'row-meta' }, v.links ? plural(v.links, 'container') : ''),
      sizeCell(bytes(v.bytes), v.bytes / max),
      h('div', { class: 'row-actions' }, removeBtn('volume', v.name, name, v.bytes, v.links > 0 ? 'Attached to a container' : null))));
  });
}

function containerRows(r) {
  const max = Math.max(1, ...r.containers.map((c) => c.bytes));
  return r.containers.map((c) => rowWrap(`ctr-${c.id}`, h('div', { class: 'row no-check' },
    h('div', { style: { minWidth: 0 } },
      h('div', { class: 'row-title' }, h('span', { class: 'name' }, c.name),
        c.state === 'running' ? h('span', { class: 'badge good' }, 'Running') : h('span', { class: 'badge' }, c.state)),
      h('div', { class: 'row-sub' }, h('span', { class: 'mono' }, c.image))),
    h('div', { class: 'row-meta' }, c.status),
    sizeCell(bytes(c.bytes), c.bytes / max),
    h('div', { class: 'row-actions' }, removeBtn('container', c.id, c.name, c.bytes, c.state === 'running' ? 'Stop it first' : null)))));
}
