import assert from 'node:assert/strict';
import { test } from 'node:test';

const { driveFilterOptions, onDrive, ALL_DRIVES, NO_DRIVE } = await import('../../src/Nadfny/wwwroot/js/views/apps-drives.js');

const GB = 1024 ** 3;
const app = (drive, gb) => ({ drive, appBytes: gb * GB, dataBytes: 0 });
const size = (a) => a.appBytes + a.dataBytes;

test('one option per drive, biggest first, after "All drives"', () => {
  const apps = [app('Local Disk (C:)', 50), app('Games (D:)', 400), app('Games (D:)', 162), app(null, 3)];
  const options = driveFilterOptions(apps, size);

  assert.deepEqual(options.map((o) => o.value), [ALL_DRIVES, 'Games (D:)', 'Local Disk (C:)', NO_DRIVE]);
  assert.deepEqual(options.map((o) => o.label), ['All drives', 'Games (D:)', 'Local Disk (C:)', 'Unknown']);
  assert.equal(options[0].count, '615 GB');
  assert.equal(options[1].count, '562 GB');
});

test('no filter when every app is on one drive', () => {
  assert.deepEqual(driveFilterOptions([app('Games (D:)', 1), app('Games (D:)', 2)], size), []);
  assert.deepEqual(driveFilterOptions([], size), []);
});

test('onDrive matches the chosen drive, apps without a known folder, or everything', () => {
  assert.equal(onDrive(app('Games (D:)', 1), ALL_DRIVES), true);
  assert.equal(onDrive(app('Games (D:)', 1), 'Games (D:)'), true);
  assert.equal(onDrive(app('Local Disk (C:)', 1), 'Games (D:)'), false);
  assert.equal(onDrive(app(null, 1), NO_DRIVE), true);
  assert.equal(onDrive(app('Games (D:)', 1), NO_DRIVE), false);
});

test('a drive that is no longer listed falls back to all drives', () => {
  assert.equal(onDrive(app('Local Disk (C:)', 1), 'Gone (Z:)', ['Local Disk (C:)']), true);
});
