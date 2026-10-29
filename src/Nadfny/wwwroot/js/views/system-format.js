// Pure formatting of the raw system.info payload into labelled spec rows and a shareable text summary.
import { ago, bytes } from '../format.js';

const MEMORY_TYPES = { 20: 'DDR', 21: 'DDR2', 24: 'DDR3', 26: 'DDR4', 34: 'DDR5', 35: 'LPDDR5' };
const FORM_FACTORS = { 8: 'DIMM', 12: 'SO-DIMM' };
const MONITOR_MAKERS = {
  SAM: 'Samsung', AOC: 'AOC', DEL: 'Dell', GSM: 'LG', ACR: 'Acer', ACI: 'ASUS', AUS: 'ASUS', BNQ: 'BenQ', HWP: 'HP',
  LEN: 'Lenovo', MSI: 'MSI', PHL: 'Philips', VSC: 'ViewSonic', GBT: 'Gigabyte', HPN: 'HP', SNY: 'Sony', AUO: 'AU Optronics', BOE: 'BOE',
};
const BRAND_CLEAN = /\((R|TM)\)|CPU|Processor|@.*$/gi;

/** Disks are sold in decimal units ("1 TB"), RAM and VRAM in binary ("16 GB"). */
export function decimalSize(n) {
  if (!n) return '';
  const tb = n / 1e12;
  if (tb >= 1) return `${Number.isInteger(Math.round(tb * 10) / 10) ? Math.round(tb) : tb.toFixed(1)} TB`;
  return `${Math.round(n / 1e9)} GB`;
}

export const cleanName = (s) => (s ?? '').replace(BRAND_CLEAN, '').replace(/\s+/g, ' ').trim();
/** RAM/VRAM are whole binary sizes: show "16 GB", not "16.0 GB". */
export const wholeSize = (n) => bytes(n).replace(/\.0 /, ' ');

// Drives that report only a part code get their brand in front.
const DRIVE_BRANDS = [
  [/^CT\d/, 'Crucial'], [/^ST\d/, 'Seagate'], [/^WDS|^WD\d|^WDC /, 'Western Digital'], [/^MZ-|^MZV/, 'Samsung'],
  [/^SSDPE|^SSDSC/, 'Intel'], [/^HFM|^HFS/, 'SK hynix'], [/^KXG|^KBG/, 'Kioxia'], [/^SA400|^SKC|^SNV/, 'Kingston'],
];
export function driveName(n) {
  const hit = DRIVE_BRANDS.find(([re]) => re.test(n ?? ''));
  return hit && !n.startsWith(hit[1]) ? `${hit[1]} ${n}` : n;
}

const ghz = (mhz) => (mhz ? `${(mhz / 1000).toFixed(1)} GHz` : '');
const yesNo = (v) => (v == null ? '' : v ? 'Enabled' : 'Disabled');

export function memoryType(m) { return MEMORY_TYPES[m.type] ?? ''; }

/** Rated speed hint from the part number, e.g. "UD5-6000" -> 6000. Only used to suggest XMP/EXPO. */
export function ratedSpeed(m) {
  const match = /(?:^|[^\d])(\d{4})(?:[^\d]|$)/.exec(m.partNumber ?? '');
  const n = match ? Number(match[1]) : 0;
  return n >= 1600 && n <= 9600 ? n : 0;
}

export function diskKind(d) {
  if (d.busType === 'NVMe') return 'M.2 NVMe SSD';
  if (d.mediaType === 'SSD') return `${d.busType === 'USB' ? 'USB' : 'SATA'} SSD`;
  if (d.mediaType === 'HDD') return d.busType === 'USB' ? 'External hard drive' : 'Hard drive (HDD)';
  return d.busType ? `${d.busType} drive` : 'Drive';
}

export function displayInches(m) {
  if (!m.widthCm || !m.heightCm) return '';
  return `${Math.round(Math.hypot(m.widthCm, m.heightCm) / 2.54)}"`;
}

/** Sections of { id, title, icon, rows: [{ label, value, mono? }] } in display order. */
export function buildSections(info) {
  const sections = [];
  const gpu = info.gpus ?? [];
  const cpus = info.cpus ?? [];
  const mem = info.memory ?? [];
  const disks = info.disks ?? [];
  const os = info.os ?? {};
  const board = info.board ?? {};

  cpus.forEach((c, i) => sections.push({
    id: `cpu-${i}`, title: 'Processor', icon: 'cpu',
    rows: [
      { label: 'Name', value: cleanName(c.name) },
      { label: 'Cores', value: `${c.cores} cores, ${c.threads} threads` },
      { label: 'Base clock', value: ghz(c.maxClockMHz) },
      { label: 'L2 cache', value: c.l2KB ? bytes(c.l2KB * 1024) : '' },
      { label: 'L3 cache', value: c.l3KB ? bytes(c.l3KB * 1024) : '' },
      { label: 'Virtualization', value: info.system?.hypervisorPresent ? 'On (Hyper-V / WSL running)' : yesNo(c.virtualization) },
    ],
  }));

  gpu.forEach((g, i) => {
    const nv = g.nvidia;
    sections.push({
      id: `gpu-${i}`, title: /UHD|Iris|Radeon\(TM\) Graphics|Radeon Graphics|Vega/i.test(g.name) ? 'Integrated graphics' : 'Graphics', icon: 'graphics-card',
      rows: [
        { label: 'Name', value: g.name },
        { label: 'Video memory', value: g.vramBytes ? wholeSize(g.vramBytes) : '' },
        { label: 'Driver', value: g.driverVersion, mono: true },
        { label: 'Driver date', value: g.driverDate ? new Date(g.driverDate).toLocaleDateString('en-GB', { year: 'numeric', month: 'short', day: 'numeric' }) : '' },
        { label: 'Max boost clock', value: nv?.coreClockMHz ? `${nv.coreClockMHz} MHz` : '' },
        { label: 'Memory clock', value: nv?.memoryClockMHz ? `${nv.memoryClockMHz} MHz` : '' },
        { label: 'Power limit', value: nv?.powerLimitW ? `${Math.round(Number(nv.powerLimitW))} W` : '' },
        { label: 'PCIe link', value: nv?.pcieGen ? `PCIe ${nv.pcieGen}.0 x${nv.pcieWidth}` : '' },
        { label: 'Temperature now', value: nv?.temperatureC ? `${Math.round(Number(nv.temperatureC))} °C` : '' },
        { label: 'Output', value: g.resolution ? `${g.resolution} at ${g.refreshHz} Hz` : '' },
      ],
    });
  });

  if (mem.length) {
    const total = mem.reduce((a, m) => a + m.capacity, 0);
    const type = memoryType(mem[0]);
    const rated = Math.max(...mem.map(ratedSpeed));
    const running = Math.min(...mem.map((m) => m.configuredMTs || m.speedMTs || 0));
    sections.push({
      id: 'memory', title: 'Memory', icon: 'memory',
      hint: rated > running && running > 0
        ? `Rated for ${rated} MT/s but running at ${running} MT/s. Turning on XMP or EXPO in the BIOS usually unlocks the rated speed.`
        : null,
      rows: [
        { label: 'Installed', value: `${wholeSize(total)} ${type}`.trim() },
        { label: 'Running speed', value: running ? `${running} MT/s` : '' },
        ...mem.map((m) => ({
          label: m.slot || m.bank || 'Slot',
          value: [wholeSize(m.capacity), memoryType(m), m.manufacturer, m.partNumber, FORM_FACTORS[m.formFactor]].filter(Boolean).join(', '),
        })),
      ],
    });
  }

  disks.forEach((d) => sections.push({
    id: `disk-${d.number}`, title: d.letters?.length ? `Drive ${d.letters.join(' and ')}` : 'Drive', icon: 'hard-drive',
    rows: [
      { label: 'Name', value: driveName(d.name) },
      { label: 'Type', value: diskKind(d) + (d.spindleRpm > 0 ? `, ${d.spindleRpm} RPM` : '') },
      { label: 'Capacity', value: d.size ? `${decimalSize(d.size)} (${bytes(d.size)} usable)` : '' },
      { label: 'Health', value: d.health },
      { label: 'Firmware', value: d.firmware, mono: true },
      ...(d.letters ?? []).map((l) => {
        const v = info.volumes?.[l];
        return { label: `Volume ${l}`, value: v ? `${bytes(v.size - v.free)} used of ${bytes(v.size)}${v.label ? `, "${v.label}"` : ''}` : '' };
      }),
    ],
  }));

  (info.displays ?? []).forEach((m, i) => sections.push({
    id: `display-${i}`, title: (info.displays.length > 1 ? `Display ${i + 1}` : 'Display'), icon: 'monitor',
    rows: [
      { label: 'Model', value: [MONITOR_MAKERS[m.maker] ?? m.maker, m.name].filter(Boolean).join(' ') },
      { label: 'Size', value: displayInches(m) },
      { label: 'Made in', value: m.year ? String(m.year) : '' },
    ],
  }));

  sections.push({
    id: 'board', title: 'Motherboard', icon: 'circuitry',
    rows: [
      { label: 'Model', value: [board.manufacturer?.replace(/ Technology Co\., Ltd\.|,? Inc\.?|Co\., Ltd\./g, ''), board.product].filter(Boolean).join(' ') },
      { label: 'BIOS', value: [board.biosVersion, board.biosDate ? `(${new Date(board.biosDate).getFullYear()})` : ''].filter(Boolean).join(' '), mono: true },
      { label: 'BIOS vendor', value: board.biosVendor },
    ],
  });

  sections.push({
    id: 'os', title: 'Windows', icon: 'desktop',
    rows: [
      { label: 'Edition', value: os.name?.replace('Microsoft ', '') },
      { label: 'Version', value: os.displayVersion },
      { label: 'Build', value: os.build, mono: true },
      { label: 'Architecture', value: os.architecture },
      { label: 'Device name', value: os.computerName },
      { label: 'Installed', value: os.installed ? new Date(os.installed).toLocaleDateString('en-GB', { year: 'numeric', month: 'short', day: 'numeric' }) : '' },
      { label: 'Up since', value: os.lastBoot ? ago(os.lastBoot) : '' },
    ],
  });

  return sections.map((s) => ({ ...s, rows: s.rows.filter((r) => r.value) }));
}

/** The one-glance lines for the top of the page and the "Copy summary" text. */
export function buildSummary(info) {
  const mem = info.memory ?? [];
  const total = mem.reduce((a, m) => a + m.capacity, 0);
  const sticks = mem.length ? `${mem.length} x ${wholeSize(mem[0].capacity)}` : '';
  const speed = mem.length ? Math.min(...mem.map((m) => m.configuredMTs || m.speedMTs || 0)) : 0;
  const board = info.board ?? {};
  const mainGpu = [...(info.gpus ?? [])].sort((a, b) => (b.vramBytes ?? 0) - (a.vramBytes ?? 0))[0];
  return [
    { label: 'Processor', value: (info.cpus ?? []).map((c) => `${cleanName(c.name)} (${c.cores} cores, ${c.threads} threads)`).join(', ') },
    { label: 'Graphics', value: mainGpu ? `${mainGpu.name}${mainGpu.vramBytes ? `, ${wholeSize(mainGpu.vramBytes)}` : ''}` : '' },
    { label: 'Memory', value: total ? `${wholeSize(total)} ${memoryType(mem[0])} (${sticks}) at ${speed} MT/s` : '' },
    { label: 'Storage', value: (info.disks ?? []).map((d) => `${driveName(d.name)} (${decimalSize(d.size)} ${diskKind(d)})`).join(', ') },
    { label: 'Motherboard', value: [board.manufacturer?.split(' ')[0], board.product].filter(Boolean).join(' ') },
    { label: 'Windows', value: [info.os?.name?.replace('Microsoft ', ''), info.os?.displayVersion, info.os?.build && `build ${info.os.build}`].filter(Boolean).join(' ') },
  ].filter((r) => r.value);
}

export const sectionText = (s) => [s.title, ...s.rows.map((r) => `  ${r.label}: ${r.value}`)].join('\n');
export const summaryText = (rows) => rows.map((r) => `${r.label}: ${r.value}`).join('\n');
