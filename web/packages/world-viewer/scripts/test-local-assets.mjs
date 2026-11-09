// Real browser persistence/cross-tab regression, using synthetic resources only. No app, game, or user browser session.
/* global window, document, OffscreenCanvas */
import assert from 'node:assert/strict';
import { deflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import { chromium } from 'playwright';
import { PNG } from 'pngjs';

const root = fileURLToPath(new URL('..', import.meta.url));
function crc(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) { value ^= byte; for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ ((value & 1) ? 0xedb88320 : 0); }
  return (value ^ 0xffffffff) >>> 0;
}
function jar(entries) {
  const local = [], directory = []; let offset = 0;
  for (const [name, raw] of Object.entries(entries)) {
    const path = Buffer.from(name), data = typeof raw === 'string' ? Buffer.from(raw) : raw, compressed = deflateRawSync(data);
    const header = Buffer.alloc(30 + path.length), central = Buffer.alloc(46 + path.length);
    header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4); header.writeUInt16LE(8, 8);
    header.writeUInt32LE(crc(data), 14); header.writeUInt32LE(compressed.length, 18); header.writeUInt32LE(data.length, 22); header.writeUInt16LE(path.length, 26); path.copy(header, 30);
    central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(8, 10);
    central.writeUInt32LE(crc(data), 16); central.writeUInt32LE(compressed.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(path.length, 28); central.writeUInt32LE(offset, 42); path.copy(central, 46);
    local.push(header, compressed); directory.push(central); offset += header.length + compressed.length;
  }
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50); end.writeUInt16LE(directory.length, 8); end.writeUInt16LE(directory.length, 10);
  end.writeUInt32LE(directory.reduce((sum, b) => sum + b.length, 0), 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, ...directory, end]).toString('base64');
}
function stone(color) { const png = new PNG({ width: 16, height: 16 }); for (let i = 0; i < png.data.length; i += 4) png.data.set(color, i); return PNG.sync.write(png); }
const soundBytes = 'OggS persistent synthetic sound fixture; not played';
const soundEvents = { 'fixture.sound': { sounds: ['fixture/retained'] } };
const soundObjects = {}, soundFiles = [];
for (const [name, value] of Object.entries({ 'minecraft/sounds.json': JSON.stringify(soundEvents), 'minecraft/sounds/fixture/retained.ogg': soundBytes })) {
  const bytes = Buffer.from(value), hash = createHash('sha1').update(bytes).digest('hex');
  soundObjects[name] = { hash, size: bytes.length }; soundFiles.push({ path: `assets/objects/${hash.slice(0, 2)}/${hash}`, bytes: bytes.toString('base64') });
}
soundFiles.push({ path: 'assets/indexes/1.12.json', bytes: Buffer.from(JSON.stringify({ objects: soundObjects })).toString('base64') });
const texture = 'assets/minecraft/textures/blocks/stone.png';
const firstJar = jar({ [texture]: stone([255, 0, 0, 255]) });
const replacementJar = jar({ [texture]: stone([0, 0, 255, 255]) });
const catalogJar = jar({ [texture]: stone([0, 255, 0, 255]), 'assets/minecraft/sounds.json': JSON.stringify({
  'fixture.sound': { sounds: ['fixture/not-installed'] }, 'fixture.extra': { sounds: ['fixture/extra'] },
}) });

const server = await createServer({ root, configFile: false, logLevel: 'error', server: { host: '127.0.0.1', port: 0 }, plugins: [{ name: 'local-assets-test', configureServer(s) {
  s.middlewares.use('/__local_assets_test', (_req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Local resource regression</title>'); });
} }] });
let browser;
async function initialize(page) {
  await page.evaluate(async () => {
    const storage = await import('/src/assets/localStore.ts');
    const { loadBundle } = await import('/src/assets/bundle.ts');
    const snapshot = async () => {
      const local = await storage.readLocalPack('1.12.2'), bundle = await loadBundle('1.12.2');
      const canvas = new OffscreenCanvas(bundle.blocks.width, bundle.blocks.height), ctx = canvas.getContext('2d');
      ctx.drawImage(bundle.blocks, 0, 0);
      const cell = bundle.pack.blocks.tiles['blocks/stone'] ?? [0, 0];
      return { name: local?.name ?? null, fallback: !!bundle.fallback, pixel: [...ctx.getImageData(cell[0] * 16, cell[1] * 16, 1, 1).data],
        sound: await local?.sounds['fixture/retained']?.text() ?? null, soundEvents: local?.soundEvents ?? {} };
    };
    window.assetsTest = { events: 0, snapshot, last: await snapshot() };
    window.addEventListener(storage.ASSETS_CHANGED, () => { window.assetsTest.events++; void snapshot().then((next) => { window.assetsTest.last = next; }); });
  });
}
async function importJar(page, bytes, name) {
  return page.evaluate(async ({ bytes, name }) => {
    const { importGameJar } = await import('/src/assets/importPack.ts');
    await importGameJar(new File([Uint8Array.from(atob(bytes), (c) => c.charCodeAt(0))], name));
    return window.assetsTest.snapshot();
  }, { bytes, name });
}
async function updated(page, name, pixel) {
  await page.waitForFunction(({ name, pixel }) => window.assetsTest.last.name === name && JSON.stringify(window.assetsTest.last.pixel) === JSON.stringify(pixel), { name, pixel }, { timeout: 5000 });
  return page.evaluate(() => window.assetsTest.last);
}
try {
  await server.listen(); browser = await chromium.launch({ headless: true });
  const context = await browser.newContext(), first = await context.newPage(), second = await context.newPage();
  const url = `http://127.0.0.1:${server.httpServer.address().port}/__local_assets_test`;
  for (const page of [first, second]) { await page.goto(url); await initialize(page); }
  assert.equal((await second.evaluate(() => window.assetsTest.last)).fallback, true, 'Prime another tab with the missing-pack fallback');
  await importJar(first, firstJar, 'first.jar');
  await updated(second, 'first.jar', [255, 0, 0, 255]);
  await first.evaluate(async (files) => {
    const { importSoundFolder } = await import('/src/assets/importPack.ts');
    const folder = files.map(({ path, bytes }) => { const file = new File([Uint8Array.from(atob(bytes), (c) => c.charCodeAt(0))], path.split('/').at(-1)); Object.defineProperty(file, 'webkitRelativePath', { value: path }); return file; });
    await importSoundFolder(folder, '1.12.2');
  }, soundFiles);
  await second.waitForFunction(() => window.assetsTest.last.sound !== null);
  const replacement = await importJar(first, replacementJar, 'replacement.jar');
  assert.equal(replacement.sound, soundBytes); assert.deepEqual(replacement.soundEvents, soundEvents);
  const other = await updated(second, 'replacement.jar', [0, 0, 255, 255]);
  assert.equal(other.sound, soundBytes); assert.deepEqual(other.soundEvents, soundEvents);
  await first.reload(); await initialize(first);
  const reload = await first.evaluate(() => window.assetsTest.last);
  assert.equal(reload.sound, soundBytes); assert.deepEqual(reload.soundEvents, soundEvents); assert.deepEqual(reload.pixel, [0, 0, 255, 255]); assert.equal(reload.fallback, false);
  const catalog = await importJar(first, catalogJar, 'catalog.jar');
  assert.equal(catalog.sound, soundBytes); assert.deepEqual(catalog.soundEvents['fixture.sound'], soundEvents['fixture.sound']); assert.ok(catalog.soundEvents['fixture.extra']);
  await updated(second, 'catalog.jar', [0, 255, 0, 255]);
  await first.reload(); await initialize(first);
  const afterCatalogReload = await first.evaluate(() => window.assetsTest.last);
  assert.equal(afterCatalogReload.sound, soundBytes); assert.deepEqual(afterCatalogReload.soundEvents['fixture.sound'], soundEvents['fixture.sound']);
  const recoveryPage = await context.newPage();
  await recoveryPage.goto(url); await recoveryPage.bringToFront();
  const recovery = await recoveryPage.evaluate(async () => {
    const { WorldViewerEngine } = await import('/src/render/engine.ts');
    const { createFixtureSource } = await import('/src/fixtures/world.ts');
    const open = window.indexedDB.open;
    let blockedReads = 0, successfulReads = 0;
    window.indexedDB.open = () => { blockedReads++; throw new DOMException('Synthetic temporary storage interruption', 'InvalidStateError'); };
    const host = document.createElement('div'); host.style.cssText = 'width:320px;height:240px'; document.body.append(host);
    const errors = [];
    const engine = new WorldViewerEngine(host, { source: createFixtureSource({ version: '1.12.2', radius: 0, mobs: 0 }), inlineMesher: true, drawDistance: 32,
      onError: (route, error) => errors.push({ route, message: String(error) }) });
    const runtime = engine.runtime;
    const until = async (condition, what) => {
      for (let attempt = 0; attempt < 200; attempt++) { if (condition()) return; await new Promise((resolve) => setTimeout(resolve, 20)); }
      throw new Error('Timed out waiting for ' + what + ': ' + JSON.stringify({ errors, textured: runtime.textured, version: runtime.version }));
    };
    try {
      await until(() => errors.length > 0 && runtime.version === '1.12.2', 'real runtime asset failure');
      const before = runtime.textured;
      window.indexedDB.open = function (...args) { successfulReads++; return open.apply(this, args); };
      window.dispatchEvent(new Event('focus'));
      await until(() => runtime.textured && runtime.blockAtlas?.bundle.version === '1.12.2' && !runtime.blockAtlas.bundle.fallback && runtime.bundle === runtime.blockAtlas.bundle, 'focus recovery from persisted pack');
      await new Promise((resolve) => setTimeout(resolve, 100));
      const atlas = runtime.blockAtlas, bundle = runtime.bundle, reads = successfulReads;
      window.dispatchEvent(new Event('focus'));
      Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
      document.dispatchEvent(new Event('visibilitychange'));
      delete document.visibilityState;
      document.dispatchEvent(new Event('visibilitychange'));
      await new Promise((resolve) => setTimeout(resolve, 100));
      return { before, after: runtime.textured, blockedReads, recoveredReads: reads, healthyExtraReads: successfulReads - reads,
        sameAtlas: atlas === runtime.blockAtlas, sameBundle: bundle === runtime.bundle, fallback: !!runtime.blockAtlas.bundle.fallback };
    } finally { window.indexedDB.open = open; delete document.visibilityState; engine.dispose(); host.remove(); }
  });
  assert.equal(recovery.before, false); assert.equal(recovery.after, true); assert.equal(recovery.fallback, false);
  assert.ok(recovery.blockedReads > 0 && recovery.recoveredReads > 0);
  assert.equal(recovery.healthyExtraReads, 0); assert.equal(recovery.sameAtlas, true); assert.equal(recovery.sameBundle, true);
  await recoveryPage.close();
  await first.evaluate(async () => { const { removeLocalPack } = await import('/src/assets/localStore.ts'); await removeLocalPack('1.12.2'); });
  await second.waitForFunction(() => window.assetsTest.last.name === null && window.assetsTest.last.fallback === true);
  await first.reload(); await initialize(first);
  assert.equal((await first.evaluate(() => window.assetsTest.last)).name, null);
  process.stdout.write('PASS: texture/sound import, texture re-import preserving sounds/catalog, reload persistence, cross-tab fallback recovery/import/remove, real runtime IndexedDB-error recovery on focus with stable healthy textures across focus/visibility.\n');
} finally { await browser?.close(); await server.close(); }
