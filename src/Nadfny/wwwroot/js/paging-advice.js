// Plain wording for the "Pagefile and swapfile" row: what the file is, how it compares with RAM, and one safe tip.
import { bytes } from './format.js';

const GB = 1024 ** 3;
const LARGE_PAGEFILE = 8 * GB;
const SAME_SIZE = [0.75, 1.35];

const size = (n) => n == null ? 'Size unknown' : bytes(n);

function modeText(managed) {
  if (managed == null) return 'Windows size management unknown';
  return managed ? 'Windows manages the size' : 'Custom size set, so Windows does not manage it';
}

function ramComparison({ managed, pagefileBytes, ramBytes }) {
  if (managed !== true || !pagefileBytes || !ramBytes) return '';
  const ratio = pagefileBytes / ramBytes;
  const ram = `${Math.round(ramBytes / GB)} GB of RAM`;
  if (ratio < SAME_SIZE[0]) return ` Smaller than your ${ram}.`;
  return ratio <= SAME_SIZE[1] ? ` About the size of your ${ram}.` : ` Larger than your ${ram}.`;
}

/**
 * @param {{ managed: boolean|null, pagefileBytes: number|null, swapfileBytes: number|null,
 *   ramBytes: number|null, pagefileDrives: number }} drive
 * @returns {{ note: string, tip: string|null }}
 */
export function pagingAdvice(drive) {
  const where = drive.pagefileDrives > 1 ? ` Pagefiles are on ${drive.pagefileDrives} drives.` : '';
  const note = `Can't be deleted directly. Windows uses it as overflow for RAM and to keep crash dumps. ${modeText(drive.managed)}. `
    + `pagefile.sys: ${size(drive.pagefileBytes)} · swapfile.sys: ${size(drive.swapfileBytes)}.${ramComparison(drive)}${where}`;
  const large = drive.pagefileBytes != null && drive.pagefileBytes > LARGE_PAGEFILE;
  const tip = large
    ? 'To free space, set a custom size of 4,096–16,384 MB in Performance settings → Advanced → Virtual memory. Don\'t turn it off on every drive.'
    : null;
  return { note, tip };
}
