// Drive filter for Apps & games: "Games (D:) 562 GB" answers "what is filling this drive?" in one click.
import { bytes } from '../format.js';

export const ALL_DRIVES = 'all';
export const NO_DRIVE = 'none';

/** Filter options with each drive's total, biggest first; empty when there is only one drive to show. */
export function driveFilterOptions(apps, sizeOf) {
  const totals = new Map();
  for (const a of apps) {
    const key = a.drive ?? NO_DRIVE;
    totals.set(key, (totals.get(key) ?? 0) + sizeOf(a));
  }
  if (totals.size < 2) return [];
  const sum = [...totals.values()].reduce((s, n) => s + n, 0);
  const drives = [...totals].sort(([ka, a], [kb, b]) => (ka === NO_DRIVE) - (kb === NO_DRIVE) || b - a);
  return [
    { value: ALL_DRIVES, label: 'All drives', count: bytes(sum) },
    ...drives.map(([key, n]) => ({ value: key, label: key === NO_DRIVE ? 'Unknown' : key, count: bytes(n) })),
  ];
}

/** Whether an app passes the drive filter. A drive that disappeared after a rescan shows everything. */
export function onDrive(app, drive, known = null) {
  if (drive === ALL_DRIVES || (known && drive !== NO_DRIVE && !known.includes(drive))) return true;
  return (app.drive ?? NO_DRIVE) === drive;
}
