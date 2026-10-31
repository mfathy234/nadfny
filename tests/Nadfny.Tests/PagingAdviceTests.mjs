import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pagingAdvice } from '../../src/Nadfny/wwwroot/js/paging-advice.js';

const GB = 1024 ** 3;
const base = { managed: true, pagefileBytes: 4 * GB, swapfileBytes: 256 * 1024 ** 2, ramBytes: 32 * GB, pagefileDrives: 1 };

test('always says what the file is and that it cannot be deleted directly', () => {
  const { note } = pagingAdvice(base);
  assert.match(note, /can't be deleted/i);
  assert.match(note, /overflow for RAM/);
  assert.match(note, /crash dumps/);
});

test('managed size is compared with installed RAM', () => {
  assert.match(pagingAdvice({ ...base, pagefileBytes: 31 * GB }).note, /About the size of your 32 GB of RAM\./);
  assert.match(pagingAdvice({ ...base, pagefileBytes: 4 * GB }).note, /Smaller than your 32 GB of RAM\./);
  assert.match(pagingAdvice({ ...base, pagefileBytes: 64 * GB }).note, /Larger than your 32 GB of RAM\./);
});

test('no comparison when RAM is unknown or the size is custom', () => {
  assert.doesNotMatch(pagingAdvice({ ...base, ramBytes: null }).note, /of your/);
  assert.doesNotMatch(pagingAdvice({ ...base, managed: false, pagefileBytes: 31 * GB }).note, /of your/);
  assert.match(pagingAdvice({ ...base, managed: false }).note, /custom size/i);
  assert.match(pagingAdvice({ ...base, managed: null }).note, /Windows size management unknown/);
});

test('shows the sizes, with unknown handled', () => {
  assert.match(pagingAdvice(base).note, /pagefile\.sys: 4\.0 GB · swapfile\.sys: 256 MB/);
  assert.match(pagingAdvice({ ...base, pagefileBytes: null }).note, /pagefile\.sys: Size unknown/);
});

test('tip appears only for a large pagefile', () => {
  assert.equal(pagingAdvice({ ...base, pagefileBytes: 8 * GB }).tip, null);
  assert.equal(pagingAdvice({ ...base, pagefileBytes: null }).tip, null);
  const tip = pagingAdvice({ ...base, pagefileBytes: 31 * GB }).tip;
  assert.match(tip, /4,096–16,384 MB/);
  assert.match(tip, /Performance settings → Advanced → Virtual memory/);
  assert.match(tip, /Don't turn it off on every drive/);
});

test('mentions other drives when several hold a pagefile', () => {
  assert.match(pagingAdvice({ ...base, pagefileDrives: 2 }).note, /2 drives/);
  assert.doesNotMatch(pagingAdvice(base).note, /drives/);
});
