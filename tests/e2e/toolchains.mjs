// Mock host only: no requests from this test reach the Windows application.
// Run: node tests/e2e/toolchains.mjs [path-to-playwright-or-playwright-core]
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const { chromium } = createRequire(import.meta.url)(process.argv[2] || 'playwright');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../src/Nadfny/wwwroot');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  try {
    const file = resolve(root, '.' + new URL(request.url, 'http://localhost').pathname.replace(/\/$/, '/index.html'));
    if (!file.startsWith(root + sep)) throw new Error('Invalid path');
    response.setHeader('Content-Type', types[extname(file)] || 'application/octet-stream');
    response.end(await readFile(file));
  } catch {
    response.writeHead(404).end();
  }
});
await new Promise((done) => server.listen(0, '127.0.0.1', done));
let browser;
try {
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 1000 }, reducedMotion: 'reduce' });
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => {
    const listeners = [];
    const sdk = { id: 'sdk-old', kind: 'dotnet', name: '.NET SDK 8.0.101', path: 'C:\\Program Files\\dotnet\\sdk\\8.0.101', bytes: 1024,
      note: 'A newer patch is installed.', old: true, pinned: false, canRemove: false, recycle: false, command: 'dotnet-core-uninstall remove 8.0.101 --sdk' };
    const avd = { id: 'avd', kind: 'avd', name: 'Android emulator Pixel', path: 'C:\\Users\\test\\.android\\avd\\Pixel.avd', bytes: 2048,
      note: 'Contains emulator apps, snapshots and user data.', canRemove: true, recycle: true };
    const image = { id: 'image', kind: 'android-image', name: 'Android image android-35 / google_apis / x86_64', path: 'C:\\Users\\test\\Android\\Sdk\\system-images\\android-35\\google_apis\\x86_64',
      bytes: 4096, note: 'Downloaded again in SDK Manager.', canRemove: true, recycle: false };
    const host = window.testHost = { requests: [], holdNext: false, release: null, rootAdded: false };
    const emit = (data) => listeners.forEach((listener) => listener({ data }));
    host.emit = emit;
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      addEventListener: (_event, listener) => listeners.push(listener),
      postMessage: (json) => {
        const request = JSON.parse(json);
        host.requests.push(request);
        let result;
        switch (request.method) {
          case 'app.info': result = { version: '0.3.0', roots: ['C:\\repos'], drives: [], allDrives: false }; break;
          case 'settings.addRoot': host.rootAdded = true; result = { added: true, roots: ['C:\\repos', 'C:\\pinned'] }; break;
          case 'system.info': result = {}; break;
          case 'docker.scan': result = null; break;
          case 'recycle.query': result = { items: 0, bytes: 0, drives: [] }; break;
          // Other 0.3 sections on the same pages: object-shaped answers, not the list default.
          case 'systemSpace.scan': case 'history.get': result = null; break;
          case 'update.check': result = null; break;
          case 'clipboard.set': case 'toolchains.openApps': result = true; break;
          case 'toolchains.remove': result = request.params.id === 'image' ? 4096 : 0; break;
          case 'toolchains.scan': result = { items: [host.rootAdded ? { ...sdk, old: false, pinned: true, command: null } : sdk, avd, image], warnings: ['Pins checked in scan locations.'] }; break;
          default: result = [];
        }
        const reply = () => emit({ type: 'result', id: request.id, ok: true, result });
        if (request.method === 'toolchains.scan' && host.holdNext) {
          host.holdNext = false;
          host.release = reply;
        } else queueMicrotask(reply);
      },
    };
  });
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.getByRole('button', { name: 'Caches', exact: true }).click();
  await page.getByRole('heading', { name: 'Old SDKs and tools' }).waitFor();
  await page.getByRole('button', { name: 'Copy command', exact: true }).click();
  assert.equal(await page.evaluate(() => window.testHost.requests.find((r) => r.method === 'clipboard.set').params.text), 'dotnet-core-uninstall remove 8.0.101 --sdk');
  await page.getByRole('button', { name: 'Apps & Features', exact: true }).click();
  assert.equal(await page.evaluate(() => window.testHost.requests.filter((r) => r.method === 'toolchains.openApps').length), 1);

  // Cancel must be a no-op, including for folders that contain user data.
  await page.getByRole('button', { name: 'Move to Bin', exact: true }).click();
  const dialog = page.getByRole('dialog');
  assert.match(await dialog.innerText(), /Pixel\.avd/);
  const avdConfirm = dialog.getByRole('button', { name: 'Remove Android emulator Pixel (2 KB)', exact: true });
  assert.equal(await avdConfirm.isDisabled(), true);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await dialog.waitFor({ state: 'hidden' });
  assert.equal(await page.evaluate(() => window.testHost.requests.filter((r) => r.method === 'toolchains.remove').length), 0);

  await page.getByRole('button', { name: 'Move to Bin', exact: true }).click();
  await dialog.getByRole('checkbox').check();
  await avdConfirm.click();
  await page.getByText('Android emulator Pixel moved to the Recycle Bin. Empty it to free space.', { exact: true }).waitFor();
  assert.equal(await page.evaluate(async () => (await import('/js/store.js')).state.freed), 0);

  await page.getByRole('button', { name: 'Remove', exact: true }).click();
  await dialog.getByRole('button', { name: 'Remove Android image android-35 / google_apis / x86_64 (4 KB)', exact: true }).click();
  await page.getByText('Android image android-35 / google_apis / x86_64 removed. 4 KB freed.', { exact: true }).waitFor();
  assert.equal(await page.evaluate(async () => (await import('/js/store.js')).state.freed), 4096);
  const removed = await page.evaluate(() => window.testHost.requests.filter((r) => r.method === 'toolchains.remove').map((r) => r.params.id));
  assert.deepEqual(removed, ['avd', 'image']);

  // A root change during a scan must trigger a new pin check.
  await page.evaluate(() => { window.testHost.holdNext = true; });
  await page.getByRole('button', { name: 'Rescan', exact: true }).click();
  await page.waitForFunction(() => window.testHost.release !== null);
  await page.getByRole('button', { name: 'Add location', exact: true }).click();
  await page.waitForFunction(() => window.testHost.rootAdded);
  await page.evaluate(() => window.testHost.release());
  await page.getByText('Pinned', { exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Copy command', exact: true }).count(), 0);
  assert.equal(await page.evaluate(() => window.testHost.requests.filter((r) => r.method === 'toolchains.scan').length), 3);

  for (const theme of ['light', 'dark']) {
    await page.emulateMedia({ colorScheme: theme });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `${theme} layout overflows`);
  }
  const show = page.getByRole('button', { name: 'Show folder', exact: true }).first();
  const originalShow = await show.elementHandle();
  await show.focus();
  await page.evaluate(() => window.testHost.emit({ type: 'event', name: 'scan.progress', data: { scope: 'caches', phase: 'Measuring' } }));
  await page.waitForFunction((element) => !element.isConnected, originalShow);
  await page.waitForFunction(() => document.activeElement?.dataset.focusKey?.startsWith('show-'));
  assert.deepEqual(errors, []);
  console.log('Passed: command copy, Apps & Features, cancel, acknowledged recycle, permanent delete, freed bytes, queued pin rescan, light/dark layout and keyboard focus.');
} finally {
  await browser?.close();
  server.close();
}
