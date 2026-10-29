import { button, h } from '../dom.js';
import { bytes } from '../format.js';
import { clearHistory } from '../history-actions.js';
import { state } from '../store.js';

const names = { worktrees: 'Worktrees', artifacts: 'Build output', caches: 'Caches', docker: 'Docker', apps: 'Apps & games' };
const date = (value) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });

export function growthSize(category, id, cell, currentBytes) {
  const change = state.history.report?.hints.find((g) => g.category === category && g.id.toLowerCase() === id.toLowerCase());
  if (change && !state.history.error && state[category]?.status === 'done' && currentBytes === change.currentBytes) {
    cell.append(h('div', { class: 'growth-hint' }, `+${bytes(change.bytes)} since ${date(change.since)}`));
  }
  return cell;
}

export function historyCard() {
  const report = state.history.report;
  return h('section', { class: 'section history', 'aria-label': 'Storage history' },
    h('div', { class: 'section-head' },
      h('h2', null, report?.since ? `Since ${date(report.since)}` : 'Storage history'),
      button('Clear history', { small: true, variant: 'quiet', key: 'clear-history', onClick: clearHistory,
        disabled: !report?.storedBytes || state.history.clearing })),
    state.history.error ? h('p', { class: 'hint' }, 'Storage history is unavailable. Try another scan.')
      : !report ? h('p', { class: 'hint' }, 'Loading storage history…')
        : [
          h('p', { class: 'hint' }, 'Compared with each category’s previous completed scan. Newly seen items may have been outside the previous top 200.'),
          h('div', { class: 'history-drives' }, report.drives.map((d) => h('p', null,
            h('strong', null, d.name), ` ${bytes(Math.abs(d.freeBytes))} ${d.freeBytes >= 0 ? 'more' : 'less'} free`,
            h('span', { class: 'hint' }, ` since ${date(d.since)}`)))),
          h('h3', null, 'What grew'),
          report.growth.length ? h('div', { class: 'list' }, report.growth.map((g) => historyRow(g.name,
            `${names[g.category] ?? g.category} · ${g.isNew ? 'Newly seen' : `Since ${date(g.since)}`}`, `+${bytes(g.bytes)}`)))
            : h('p', { class: 'hint' }, 'No growth measured yet. Scan a category again to compare it.'),
          h('h3', null, 'Category growth'),
          report.categories.some((c) => c.bytes > 0)
            ? h('div', { class: 'list' }, report.categories.filter((c) => c.bytes > 0).map((c) => historyRow(
              names[c.category] ?? c.category, `Since ${date(c.since)}`, `+${bytes(c.bytes)}`)))
            : h('p', { class: 'hint' }, 'No category totals increased.'),
          h('h3', null, 'Used space over the last 90 days'),
          charts(report.series),
        ]);
}

function historyRow(name, detail, size) {
  return h('div', { class: 'row no-check history-row' },
    h('div', { style: { minWidth: 0 } }, h('div', { class: 'row-title', title: name }, name), h('div', { class: 'row-sub' }, detail)),
    h('span', { class: 'size-num' }, size));
}

function svgElement(tag, props) {
  const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, value] of Object.entries(props)) el.setAttribute(key, String(value));
  return el;
}

function charts(series) {
  const drives = new Map();
  for (const snapshot of series) {
    for (const drive of snapshot.drives) {
      if (!drives.has(drive.name)) drives.set(drive.name, []);
      drives.get(drive.name).push({ time: new Date(snapshot.timestamp).getTime(), used: drive.usedBytes });
    }
  }
  return [...drives.entries()].map(([name, points]) => {
    const first = points[0], last = points.at(-1);
    const min = Math.min(...points.map((p) => p.used));
    const max = Math.max(...points.map((p) => p.used));
    const x = (p) => 6 + (p.time - first.time) / Math.max(1, last.time - first.time) * 468;
    const y = (p) => 74 - (p.used - min) / Math.max(1, max - min) * 64;
    const summary = `${name}: ${points.length} daily measurements. ${bytes(first.used)} used on ${date(first.time)}; ${bytes(last.used)} used on ${date(last.time)}. Range ${bytes(min)} to ${bytes(max)}.`;
    const svg = svgElement('svg', { viewBox: '0 0 480 84', role: 'img', 'aria-label': summary, class: 'history-chart' });
    svg.append(svgElement('path', { d: points.map((p, i) => `${i ? 'L' : 'M'}${x(p)},${y(p)}`).join(' '), fill: 'none', stroke: 'currentColor', 'stroke-width': 2 }));
    svg.append(svgElement('circle', { cx: x(last), cy: y(last), r: 3, fill: 'currentColor' }));
    return h('figure', { class: 'history-figure' }, h('figcaption', null,
      h('strong', null, name), ` ${bytes(last.used)} used`, h('span', { class: 'hint' }, ` · ${bytes(min)} to ${bytes(max)}`)),
    svg, h('p', { class: 'hint' }, `${date(first.time)} to ${date(last.time)}`));
  });
}
