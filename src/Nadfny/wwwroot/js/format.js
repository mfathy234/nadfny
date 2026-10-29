const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

/** Binary units like Explorer: "12.4 GB", "880 MB". */
export function bytes(n) {
  let v = Math.max(0, Number(n) || 0);
  let i = 0;
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
  const digits = i >= 2 && v < 100 ? 1 : 0;
  return `${v.toFixed(digits)} ${UNITS[i]}`;
}

const rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
const STEPS = [
  ['year', 365 * 864e5], ['month', 30 * 864e5], ['week', 7 * 864e5],
  ['day', 864e5], ['hour', 36e5], ['minute', 6e4],
];

export function ago(iso) {
  if (!iso) return '';
  const diff = new Date(iso).getTime() - Date.now();
  for (const [unit, ms] of STEPS) {
    if (Math.abs(diff) >= ms) return rtf.format(Math.round(diff / ms), unit);
  }
  return 'just now';
}

export function daysSince(iso) {
  if (!iso) return Infinity;
  return (Date.now() - new Date(iso).getTime()) / 864e5;
}

export const plural = (n, one, many = `${one}s`) => `${n.toLocaleString('en')} ${n === 1 ? one : many}`;

/** Docker prints SI sizes: "Total reclaimed space: 4.899GB". */
export function parseDockerSize(text) {
  const m = /([\d.]+)\s*(TB|GB|MB|kB|KB|B)\b/.exec(text ?? '');
  if (!m) return 0;
  const f = { TB: 1e12, GB: 1e9, MB: 1e6, kB: 1e3, KB: 1e3, B: 1 }[m[2]];
  return Math.round(parseFloat(m[1]) * f);
}

export function isUnder(path, root) {
  const p = path.toLowerCase().replace(/[\\/]+$/, '');
  const r = root.toLowerCase().replace(/[\\/]+$/, '');
  return p === r || p.startsWith(r + '\\');
}
