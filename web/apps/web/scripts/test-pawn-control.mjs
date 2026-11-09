// Isolated Chromium smoke; uses the local mock app and never touches a player's browser or live colony.
import { chromium } from '@playwright/test';
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const base = process.argv[2] ?? 'http://localhost:3104';
const output = path.resolve(process.argv[3] ?? '../../../artifacts/pawn-browser');
mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true, args: ['--enable-unsafe-swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await context.addInitScript(() => {
  sessionStorage.setItem('mcx-mock-session', JSON.stringify({ username: 'fox', created: Date.now() }));
});
const page = await context.newPage();
const errors = [], requests = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('request', (r) => {
  if (/\/control\/(input|action|release)$/.test(new URL(r.url()).pathname)) {
    const data = r.postDataJSON();
    // Never persist even mock ownership credentials.
    delete data.session;
    requests.push({ path: new URL(r.url()).pathname, data });
  }
});
try {
  await page.goto(`${base}/live/maid_chan_70`, { timeout: 120000 });
  await page.getByRole('link', { name: 'Take control of pawn 70' }).click({ timeout: 120000 });
  await page.getByText('You are in control · AI paused').waitFor({ timeout: 120000 });
  await page.getByRole('button', { name: 'Click to play', exact: true }).waitFor();
  assert.equal(await page.getByRole('slider', { name: 'Controller sound volume' }).count(), 0);
  await page.getByRole('button', { name: 'Sound settings', exact: true }).click();
  await page.getByRole('slider', { name: 'Controller sound volume' }).waitFor();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Click to play', exact: true }).click();
  await page.waitForFunction(() => !!document.fullscreenElement && !!document.pointerLockElement);
  const moving = page.waitForRequest((r) => r.url().endsWith('/control/input') &&
    r.postDataJSON()?.forward === 1 && r.postDataJSON()?.sprint === true, { timeout: 5000 });
  await page.keyboard.down('Control'); await page.keyboard.down('w');
  await moving;
  assert.equal(page.isClosed(), false, 'Ctrl+W must not close this isolated Chrome tab during protected play');
  await page.keyboard.up('w'); await page.keyboard.up('Control');
  const selected = page.waitForRequest((r) => r.url().endsWith('/control/input') && r.postDataJSON()?.hotbar === 2);
  await page.keyboard.press('3');
  await selected;
  const inventoryRequest = page.waitForRequest((r) => r.url().endsWith('/control/action') && r.postDataJSON()?.kind === 'inventory');
  await page.keyboard.press('e');
  await inventoryRequest;
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor();
  assert(requests.some((r) => r.data.forward === 1 && r.data.sprint === true), 'W + Control reaches movement input');
  assert(requests.some((r) => r.data.hotbar === 2), 'hotbar choice reaches the client');
  assert(requests.some((r) => r.data.kind === 'inventory'), 'inventory opens through the control API');
  await page.screenshot({ path: path.join(output, 'inventory.png') });
  await page.getByRole('button', { name: 'Close · E' }).click();
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor({ state: 'hidden' });
  await page.screenshot({ path: path.join(output, 'pawn-control.png') });
  await page.getByRole('button', { name: 'Release & return' }).click();
  await page.waitForURL('**/live/maid_chan_70');
  assert(requests.some((r) => r.path.endsWith('/release')), 'leaving releases the pawn');
  await page.goto(`${base}/story`);
  await page.getByRole('button', { name: 'Switch to free camera', exact: true }).click({ timeout: 45000 });
  await page.waitForFunction(() => !!document.fullscreenElement && !!document.pointerLockElement);
  await page.keyboard.down('Control'); await page.keyboard.down('w');
  await page.waitForTimeout(300);
  assert.equal(page.isClosed(), false, 'Controller freecam also protects Ctrl+W');
  await page.keyboard.up('w'); await page.keyboard.up('Control');
  await page.keyboard.press('Escape');
  assert.deepEqual(errors, []);
  writeFileSync(path.join(output, 'result.json'), JSON.stringify({ ok: true, controls: requests.length, errors }, null, 2));
  process.stdout.write('Pawn browser smoke passed: Live Hive entry, fullscreen + Ctrl+W in takeover and Controller, movement, hotbar, inventory, release, compact sound.\n');
} catch (error) {
  if (!page.isClosed()) await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {});
  writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), errors, requests: requests.slice(-8) }, null, 2));
  throw error;
} finally { await browser.close(); }
