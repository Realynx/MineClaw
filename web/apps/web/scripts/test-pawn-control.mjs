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
  await page.goto(`${base}/live/maid_chan_70?pane=world`, { timeout: 120000 });
  await page.getByRole('link', { name: 'Take control of pawn 70' }).waitFor({ timeout: 120000 });
  // Install after MSW has replaced WebSocket. Its virtual connections do not appear in Chromium's network events.
  await page.evaluate(() => {
    window.__captureTrace = [];
    for (const name of ['requestFullscreen', 'requestPointerLock']) {
      const original = Element.prototype[name];
      Element.prototype[name] = function (...args) {
        window.__captureTrace.push({ name, tag: this.tagName, at: performance.now(), active: navigator.userActivation.isActive });
        const promise = original.apply(this, args);
        void Promise.resolve(promise).then(() => window.__captureTrace.push({ name: `${name}:ok`, tag: this.tagName, at: performance.now() }),
          (error) => window.__captureTrace.push({ name: `${name}:failed`, error: String(error), at: performance.now() }));
        return promise;
      };
    }
    for (const name of ['pointerlockchange', 'fullscreenchange', 'blur']) (name === 'blur' ? window : document).addEventListener(name, () =>
      window.__captureTrace.push({ name, at: performance.now(), locked: document.pointerLockElement?.tagName, fullscreen: document.fullscreenElement?.tagName }));
    const exit = document.exitPointerLock.bind(document);
    document.exitPointerLock = () => {
      window.__captureTrace.push({ name: 'exitPointerLock', at: performance.now(), stack: new Error().stack }); exit();
    };
    window.__pawnControlFrames = [];
    window.__pawnControlStates = [];
    window.WebSocket = new Proxy(window.WebSocket, {
      construct(target, args) {
        const socket = Reflect.construct(target, args);
        if (new URL(String(args[0]), location.href).pathname.endsWith('/control/socket')) {
          const send = socket.send.bind(socket);
          socket.send = (payload) => {
            const frame = JSON.parse(String(payload));
            delete frame.session;
            window.__pawnControlFrames.push(frame);
            send(payload);
          };
          socket.addEventListener('message', (event) => {
            const frame = JSON.parse(String(event.data));
            if (frame.type === 'state') window.__pawnControlStates.push({ at: performance.now(), sampled: frame.sampled_at_ms, state: frame.data.state });
          });
        }
        return socket;
      },
    });
  });
  const waitForFrame = (expected) => page.waitForFunction((criteria) => window.__pawnControlFrames.some((frame) =>
    Object.entries(criteria).every(([key, value]) => frame[key] === value)), expected, { timeout: 5000 });
  await page.getByRole('link', { name: 'Take control of pawn 70' }).click({ timeout: 120000 });
  await page.getByText('You are in control · AI paused').waitFor({ timeout: 120000 });
  // No second click: the original gesture survives route loading and lease acquisition on the document root.
  await page.waitForFunction(() => document.fullscreenElement === document.documentElement
    && document.pointerLockElement?.tagName === 'CANVAS', undefined, { timeout: 15000 });
  assert.equal(await page.getByRole('button', { name: 'Click to play', exact: true }).count(), 0);
  assert(await page.getByTestId('pawn-control').evaluate((element) => {
    const rect = element.getBoundingClientRect();
    return rect.x === 0 && rect.y === 0 && rect.width === innerWidth && rect.height === innerHeight;
  }), 'takeover covers the entire fullscreen viewport');
  const moving = waitForFrame({ type: 'input', forward: 1, sprint: true });
  await page.keyboard.down('Control'); await page.keyboard.down('w');
  await moving;
  assert.equal(page.isClosed(), false, 'Ctrl+W must not close this isolated Chrome tab during protected play');
  await page.keyboard.up('w'); await page.keyboard.up('Control');
  const selected = waitForFrame({ type: 'input', hotbar: 2 });
  await page.keyboard.press('3');
  await selected;
  const inventoryRequest = waitForFrame({ type: 'action', kind: 'inventory' });
  await page.keyboard.press('e');
  await inventoryRequest;
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor();
  const frames = await page.evaluate(() => window.__pawnControlFrames);
  assert(frames.some((r) => r.forward === 1 && r.sprint === true), 'W + Control reaches movement input');
  assert(frames.some((r) => r.hotbar === 2), 'hotbar choice reaches the client');
  assert(frames.some((r) => r.kind === 'inventory'), 'inventory opens through the control socket');
  assert(!requests.some((r) => /\/(input|action)$/.test(r.path)), 'movement and actions do not fall back to HTTP round trips');
  await page.screenshot({ path: path.join(output, 'inventory.png') });
  await page.getByRole('button', { name: 'Close · E' }).click();
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor({ state: 'hidden' });
  assert.equal(await page.getByRole('slider', { name: 'Controller sound volume' }).count(), 0);
  await page.getByRole('button', { name: 'Sound settings', exact: true }).click();
  await page.getByRole('slider', { name: 'Controller sound volume' }).waitFor();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Click to play', exact: true }).click();
  await page.waitForFunction(() => !!document.pointerLockElement);
  await page.mouse.click(720, 450, { button: 'right' });
  await waitForFrame({ type: 'action', kind: 'release' });
  // Headless CDP Escape does not consistently invoke Chromium's native pointer-lock exit. E uses the application's
  // explicit unlock path, so the following buttons can receive real pointer clicks.
  await page.keyboard.press('e');
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Close · E' }).click();
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor({ state: 'hidden' });
  const actions = await page.evaluate(() => window.__pawnControlFrames.filter((f) => f.type === 'action').map((f) => f.kind));
  const use = actions.lastIndexOf('use');
  assert(use >= 0 && actions[use + 1] === 'release', 'brief right click reaches the wire before its release');
  const states = await page.evaluate(() => window.__pawnControlStates);
  assert(states.length >= 4 && states.every((s) => Number.isFinite(s.sampled)), 'server pushes timestamped positions independently');
  await page.screenshot({ path: path.join(output, 'pawn-control.png') });
  await page.getByRole('button', { name: 'Release & return' }).click();
  await page.waitForURL('**/live/maid_chan_70?pane=world');
  assert.equal(await page.evaluate(() => !!document.pointerLockElement || !!document.fullscreenElement), false, 'release clears owned fullscreen and mouse capture');
  assert(requests.some((r) => r.path.endsWith('/release')), 'leaving releases the pawn');
  await page.goto(`${base}/story`);
  const portrait = page.locator('[data-portrait][data-kind="pawn"]:not([data-offline])').first();
  await portrait.click({ timeout: 45000 });
  await page.getByTestId('story-fullscreen').click();
  await page.waitForFunction(() => document.fullscreenElement?.getAttribute('data-testid') === 'story-mode');
  await page.getByRole('link', { name: /^Take control of pawn / }).click();
  await page.getByText('You are in control · AI paused').waitFor({ timeout: 45000 });
  await page.waitForFunction(() => document.fullscreenElement === document.documentElement && document.pointerLockElement?.tagName === 'CANVAS');
  assert.equal(await page.getByRole('button', { name: 'Click to play', exact: true }).count(), 0, 'already-fullscreen Controller needs no second capture click');
  await page.keyboard.press('e');
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Close · E' }).click();
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor({ state: 'hidden' });
  await page.getByRole('button', { name: 'Release & return' }).click();
  await page.waitForURL('**/story');

  // Preserve the first click even when acquisition takes longer than Chromium's transient user activation.
  await page.evaluate(() => {
    const original = window.fetch;
    window.__delayedAcquires = 0;
    window.__delayedCapture = [];
    window.fetch = async (...args) => {
      if (String(args[0]).endsWith('/control/acquire')) {
        window.__delayedAcquires++;
        await new Promise((resolve) => setTimeout(resolve, 6000));
      }
      return original(...args);
    };
    const lock = Element.prototype.requestPointerLock;
    Element.prototype.requestPointerLock = function (...args) {
      window.__delayedCapture.push({ tag: this.tagName, active: navigator.userActivation.isActive, at: performance.now() });
      return lock.apply(this, args);
    };
  });
  await page.locator('[data-portrait][data-kind="pawn"]:not([data-offline])').first().click();
  await page.getByRole('link', { name: /^Take control of pawn / }).click();
  // Playwright's DOM polling grants synthetic user activation; wait outside the page while the real capture completes.
  await new Promise((resolve) => setTimeout(resolve, 8000));
  await page.getByText('You are in control · AI paused').waitFor({ timeout: 20000 });
  await page.waitForFunction(() => document.pointerLockElement?.tagName === 'CANVAS');
  const delayed = await page.evaluate(() => ({ requests: window.__delayedAcquires, captures: window.__delayedCapture }));
  assert(delayed.requests >= 1, 'the acquisition is actually delayed');
  assert(delayed.captures.some((capture) => capture.tag === 'CANVAS' && !capture.active), 'delayed handoff succeeds after transient activation expires');
  await page.keyboard.press('e');
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Close · E' }).click();
  await page.getByRole('dialog', { name: 'Inventory', exact: true }).waitFor({ state: 'hidden' });
  await page.getByRole('button', { name: 'Release & return' }).click();
  await page.waitForURL('**/story');

  // A real Escape during slow acquisition must cancel capture permanently, even when the session arrives later.
  await page.locator('[data-portrait][data-kind="pawn"]:not([data-offline])').first().click();
  await page.getByRole('link', { name: /^Take control of pawn / }).click();
  await page.waitForURL('**/control?*');
  await page.waitForFunction(() => document.pointerLockElement === document.documentElement);
  await page.keyboard.press('Escape');
  await page.getByText('You are in control · AI paused').waitFor({ timeout: 20000 });
  await page.getByRole('button', { name: 'Click to play', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => !!document.pointerLockElement), false, 'late acquisition must not relock after Escape');
  await page.getByRole('button', { name: 'Release & return' }).click();
  await page.waitForURL('**/story');
  await page.getByRole('button', { name: 'Switch to free camera', exact: true }).click({ timeout: 45000 });
  await page.waitForFunction(() => !!document.fullscreenElement && !!document.pointerLockElement);
  await page.keyboard.down('Control'); await page.keyboard.down('w');
  await page.waitForTimeout(300);
  assert.equal(page.isClosed(), false, 'Controller freecam also protects Ctrl+W');
  await page.keyboard.up('w'); await page.keyboard.up('Control');
  await page.keyboard.press('Escape');
  assert.deepEqual(errors, []);
  writeFileSync(path.join(output, 'result.json'), JSON.stringify({ ok: true, oneClick: ['Live Hive', 'fullscreen Controller', 'after activation expires'], escapeDuringAcquire: true, delayed, controlFrames: frames.length, stateFrames: states.length, actions, errors }, null, 2));
  process.stdout.write('Pawn browser smoke passed: one-click Live Hive/fullscreen Controller, Escape during acquisition, socket movement, click ordering, Ctrl+W, inventory, release.\n');
} catch (error) {
  if (!page.isClosed()) await page.screenshot({ path: path.join(output, 'failure.png') }).catch(() => {});
  const frames = page.isClosed() ? [] : await page.evaluate(() => window.__pawnControlFrames?.slice(-12) ?? []).catch(() => []);
  const capture = page.isClosed() ? [] : await page.evaluate(() => window.__captureTrace ?? []).catch(() => []);
  const delayed = page.isClosed() ? [] : await page.evaluate(() => window.__delayedCapture ?? []).catch(() => []);
  writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: String(error), errors, requests: requests.slice(-8), frames, capture, delayed }, null, 2));
  throw error;
} finally { await browser.close(); }
