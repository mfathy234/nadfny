import { copyText, loadSystemInfo } from '../actions.js';
import { button, emptyState, h, icon } from '../dom.js';
import { state } from '../store.js';
import { buildSections, buildSummary, sectionText, summaryText } from './system-format.js';

export const systemView = {
  id: 'system',
  title: 'This PC',
  icon: 'desktop',

  subtitle() {
    const info = state.system.info;
    if (!info) return state.system.status === 'error' ? 'Could not read the hardware details' : 'Reading hardware details…';
    const os = info.os ?? {};
    return [os.computerName, os.name?.replace('Microsoft ', ''), os.displayVersion].filter(Boolean).join(', ');
  },

  actions() {
    const info = state.system.info;
    return [
      info && button('Copy all', { icon: 'copy', key: 'copy-all', onClick: () => copyText(fullText(info), 'All specs copied') }),
      button('Refresh', { icon: 'arrow-clockwise', key: 'rescan', disabled: state.system.status === 'scanning', onClick: () => loadSystemInfo(true) }),
    ].filter(Boolean);
  },

  render() {
    const { info, status, error } = state.system;
    if (status === 'error' && !info) return emptyState({ ic: 'warning-circle', error: true, title: 'Could not read this PC', text: error, action: button('Try again', { onClick: () => loadSystemInfo(true) }) });
    if (!info) return skeleton();

    const summary = buildSummary(info);
    const sections = buildSections(info);
    return h('div', null,
      h('section', { class: 'section spec-hero' },
        h('div', { class: 'spec-hero-head' },
          h('h2', null, machineName(info)),
          button('Copy summary', { small: true, icon: 'copy', key: 'copy-summary', onClick: () => copyText(summaryText(summary), 'Summary copied') })),
        h('dl', { class: 'spec-summary' }, summary.map((r) => [h('dt', null, r.label), h('dd', null, r.value)]))),
      h('div', { class: 'spec-grid' }, sections.map(section)));
  },
};

function machineName(info) {
  const s = info.system ?? {};
  const generic = !s.manufacturer || /to be filled|default string|system manufacturer/i.test(s.manufacturer);
  if (generic) return info.os?.computerName ?? 'This PC';
  const brand = s.manufacturer.replace(/ Technology Co\., Ltd\.|,? Inc\.?|Co\., Ltd\./g, '').trim();
  return s.model && !s.model.includes(brand) ? `${brand} ${s.model}` : s.model || brand;
}

function section(s) {
  return h('section', { class: 'spec-section', 'aria-labelledby': `spec-${s.id}` },
    h('header', { class: 'spec-head' },
      h('span', { class: 'spec-icon' }, icon(s.icon)),
      h('h3', { id: `spec-${s.id}` }, s.title),
      h('button', {
        class: 'icon-btn', type: 'button', title: `Copy ${s.title}`, 'aria-label': `Copy ${s.title}`, 'data-focus-key': `copy-${s.id}`,
        onclick: () => copyText(sectionText(s), `${s.title} copied`),
      }, icon('copy'))),
    s.hint && h('p', { class: 'spec-hint' }, icon('lightning', true), s.hint),
    h('dl', { class: 'spec-rows' }, s.rows.map((r) => h('div', { class: 'spec-row' },
      h('dt', null, r.label),
      h('dd', { class: r.mono ? 'mono' : null }, r.value),
      h('button', {
        class: 'icon-btn spec-copy', type: 'button', title: 'Copy', 'aria-label': `Copy ${r.label}`,
        onclick: () => copyText(r.value, `${r.label} copied`),
      }, icon('copy'))))));
}

function fullText(info) {
  return [machineName(info), '', summaryText(buildSummary(info)), '', ...buildSections(info).map(sectionText)].join('\n');
}

function skeleton() {
  const block = (rows) => h('div', { class: 'spec-section', 'aria-hidden': 'true' },
    h('div', { class: 'spec-head' }, h('i', { class: 'skel', style: { width: '40%' } })),
    Array.from({ length: rows }, () => h('div', { class: 'spec-row' }, h('i', { class: 'skel', style: { width: '30%' } }), h('i', { class: 'skel', style: { width: '45%' } }))));
  return h('div', { 'aria-busy': 'true', 'aria-label': 'Reading hardware details' },
    h('section', { class: 'section spec-hero' }, h('i', { class: 'skel', style: { width: '35%', height: '22px' } }),
      h('div', { class: 'spec-summary' }, Array.from({ length: 5 }, () => [h('i', { class: 'skel' }), h('i', { class: 'skel', style: { width: '70%' } })]))),
    h('div', { class: 'spec-grid' }, [6, 8, 5, 6].map(block)));
}
