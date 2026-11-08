#!/usr/bin/env node
// Opt-in browser regression: real ZIP/PNG/OffscreenCanvas/IndexedDB, isolated headless Chromium, no app/server/user session.
import assert from 'node:assert/strict';
import { deflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { createServer } from 'vite';
import { chromium } from 'playwright';
import { PNG } from 'pngjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const localResources = process.argv.includes('--local');
function png(width, height, pixel) {
  const image = new PNG({ width, height });
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) image.data.set(pixel(x, y), (y * width + x) * 4);
  return PNG.sync.write(image);
}
function crc(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) {
    value ^= byte;
    for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ ((value & 1) ? 0xedb88320 : 0);
  }
  return (value ^ 0xffffffff) >>> 0;
}
function jar(entries) {
  const local = [], directory = [];
  let offset = 0;
  for (const [name, raw] of Object.entries(entries)) {
    const path = Buffer.from(name), data = typeof raw === 'string' ? Buffer.from(raw) : raw, compressed = deflateRawSync(data);
    const header = Buffer.alloc(30 + path.length), central = Buffer.alloc(46 + path.length);
    header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4); header.writeUInt16LE(8, 8);
    header.writeUInt32LE(crc(data), 14); header.writeUInt32LE(compressed.length, 18); header.writeUInt32LE(data.length, 22);
    header.writeUInt16LE(path.length, 26); path.copy(header, 30);
    central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(8, 10);
    central.writeUInt32LE(crc(data), 16); central.writeUInt32LE(compressed.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(path.length, 28); central.writeUInt32LE(offset, 42); path.copy(central, 46);
    local.push(header, compressed); directory.push(central); offset += header.length + compressed.length;
  }
  const size = directory.reduce((sum, b) => sum + b.length, 0), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50); end.writeUInt16LE(directory.length, 8); end.writeUInt16LE(directory.length, 10);
  end.writeUInt32LE(size, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, ...directory, end]).toString('base64');
}
const tex = 'assets/minecraft/textures/';
const json = JSON.stringify;
const stone = png(16, 16, () => [80, 90, 100, 255]);
const modern = {
  [tex + 'blocks/stone.png']: stone,
  [tex + 'blocks/water_flow.png']: png(32, 64, (x, y) => y < 32 ? [x, y, 220, 255] : [20, 200, y - 32, 255]),
  [tex + 'blocks/water_flow.png.mcmeta']: json({ animation: { frames: [{ index: 1, time: 3 }, 0], interpolate: true } }),
  [tex + 'blocks/lava_flow.png']: png(32, 96, (_x, y) => [[250, 60, 0, 255], [220, 30, 0, 255], [190, 0, 0, 255]][Math.floor(y / 32)]),
  [tex + 'blocks/lava_flow.png.mcmeta']: json({ animation: { frametime: 2 } }),
  [tex + 'items/clock.png']: png(16, 32, (_x, y) => y < 16 ? [255, 240, 10, 255] : [10, 240, 255, 255]),
  [tex + 'items/clock.png.mcmeta']: json({ animation: { frames: [1, 0], frametime: 4 } }),
  [tex + 'entity/chest/normal.png']: png(64, 64, () => [100, 50, 25, 255]),
  [tex + 'entity/banner_base.png']: png(64, 64, () => [20, 40, 200, 255]),
  [tex + 'entity/banner/base.png']: png(64, 64, (x) => [255, 255, 255, x < 16 ? 0 : 128]),
  [tex + 'font/ascii.png']: png(128, 128, () => [255, 255, 255, 255]),
  'assets/minecraft/blockstates/stone.json': json({ variants: { normal: { model: 'stone' } } }),
  'assets/minecraft/models/block/stone.json': json({ parent: 'block/cube_all', textures: { all: 'blocks/stone' } }),
  'assets/minecraft/models/item/clock.json': json({ parent: 'item/generated', textures: { layer0: 'items/clock' } }),
  'assets/minecraft/sounds.json': json({ 'block.chest.open': { sounds: ['block/chest/open'] } }),
  'assets/minecraft/sounds/block/chest/open.ogg': Buffer.from('OggS synthetic storage fixture; not played'),
  'net/minecraft/client/Main.class': Buffer.from('not executable'),
};
const beta = {
  'terrain.png': png(256, 256, () => [30, 120, 200, 255]),
  'gui/items.png': png(256, 256, () => [220, 200, 30, 255]),
  'font/default.png': png(128, 128, () => [255, 255, 255, 255]),
  'font.txt': '# synthetic font table\n ABCDEFGHIJKLMNOPQRSTUVWXYZ',
};
const soundFiles = [], soundObjects = {};
for (const [name, data] of Object.entries({
  'minecraft/sounds.json': Buffer.from(json({ 'entity.test': { sounds: ['entity/test'] } })),
  'minecraft/sounds/entity/test.ogg': Buffer.from('OggS synthetic browser-storage test, never played'),
})) {
  const hash = createHash('sha1').update(data).digest('hex');
  soundObjects[name] = { hash, size: data.length };
  soundFiles.push({ path: `assets/objects/${hash.slice(0, 2)}/${hash}`, data: data.toString('base64') });
}
soundFiles.push({ path: 'assets/indexes/1.12.json', data: Buffer.from(json({ objects: soundObjects })).toString('base64') });

async function verifyLocalResources(page, version) {
  const directory = resolve(root, 'assets', version), generated = resolve(root, 'generated', version);
  if (!existsSync(directory) || !existsSync(resolve(generated, 'pack.json'))) throw new Error(`Missing local extracted resources/reference pack for ${version}`);
  const entries = {};
  function walk(dir, prefix) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = `${prefix}${entry.name}`;
      if (entry.isDirectory()) walk(resolve(dir, entry.name), path + '/');
      else entries[(version === '1.12.2' ? 'assets/minecraft/' : '') + path] = readFileSync(resolve(dir, entry.name));
    }
  }
  walk(directory, '');
  const reference = JSON.parse(readFileSync(resolve(generated, 'pack.json'), 'utf8'));
  const samples = version === '1.12.2' ? [
    { atlas: 'blocks', kind: 'tiles', name: 'blocks/stone' },
    { atlas: 'blocks', kind: 'tiles', name: 'blocks/log_oak' },
    { atlas: 'blocks', kind: 'tiles', name: 'blocks/leaves_oak' },
    { atlas: 'items', kind: 'tiles', name: 'items/diamond_pickaxe' },
    { atlas: 'blocks', kind: 'sheets', name: 'blocks/water_flow@32' },
    { atlas: 'blocks', kind: 'sheets', name: 'blocks/lava_flow@32' },
    { atlas: 'blocks', kind: 'sheets', name: 'mineclaw/banner_white' },
    { atlas: 'blocks', kind: 'sheets', name: 'entity/chest/normal' },
    { atlas: 'blocks', kind: 'sheets', name: 'entity/bed/red' },
    { atlas: 'blocks', kind: 'sheets', name: 'entity/sign' },
  ] : [{ atlas: 'blocks', coord: [0, 0] }, { atlas: 'blocks', coord: [1, 0] }, { atlas: 'items', coord: [1, 0] }];
  const positions = [[0, 0], [2, 7], [7, 12], [10, 6], [15, 15]];
  const actual = await page.evaluate(async ({ encoded, version, samples, positions }) => {
    const { importGameJar } = await import('/src/assets/importPack.ts');
    const data = Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0));
    const start = performance.now(), pack = await importGameJar(new File([data], `local-${version}.jar`));
    const images = {};
    for (const atlas of ['blocks', 'items']) {
      const bitmap = await createImageBitmap(pack.images[atlas]);
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), context = canvas.getContext('2d');
      context.drawImage(bitmap, 0, 0); bitmap.close(); images[atlas] = context;
    }
    const pixels = samples.map((sample) => {
      const cell = sample.coord ?? pack.pack[sample.atlas][sample.kind][sample.name];
      if (!cell) throw new Error(`Missing imported texture ${sample.name}`);
      return positions.map(([x, y]) => [...images[sample.atlas].getImageData(cell[0] * 16 + x, cell[1] * 16 + y, 1, 1).data]);
    });
    return { pack: pack.pack, pixels, ms: Math.round(performance.now() - start) };
  }, { encoded: jar(entries), version, samples, positions });
  assert.equal(actual.pack.version, version);
  assert.deepEqual(actual.pack.font, reference.font, `${version} complete font advances/table`);
  if (version === '1.12.2') {
    assert.deepEqual(actual.pack.models, reference.models, 'complete block models');
    assert.deepEqual(actual.pack.itemModels, reference.itemModels, 'complete item models');
    assert.deepEqual(actual.pack.blockstates, reference.blockstates, 'complete blockstates');
    assert.deepEqual(actual.pack.biomes, reference.biomes, 'all biome metadata');
    assert.deepEqual(Object.keys(actual.pack.blocks.tiles).sort(), Object.keys(reference.blocks.tiles).sort(), 'all block tiles');
    assert.deepEqual(Object.keys(actual.pack.items.tiles).sort(), Object.keys(reference.items.tiles).sort(), 'all item tiles');
    for (const a of reference.anims) {
      const imported = actual.pack.anims.find((b) => b.name === a.name);
      assert.ok(imported, `missing animation ${a.name}`);
      assert.deepEqual(imported.frames, a.frames, `animation frames ${a.name}`);
      assert.equal(imported.count, a.count, `animation count ${a.name}`);
      assert.equal(imported.interpolate, a.interpolate, `animation interpolation ${a.name}`);
    }
  }
  const referenceImages = Object.fromEntries(['blocks', 'items'].map((atlas) => [atlas, PNG.sync.read(readFileSync(resolve(generated, version === 'b1.8.1' && atlas === 'blocks' ? 'terrain.png' : atlas + '.png')))]));
  for (const [i, sample] of samples.entries()) {
    const cell = sample.coord ?? reference[sample.atlas][sample.kind][sample.name], image = referenceImages[sample.atlas];
    for (const [j, [x, y]] of positions.entries()) {
      const offset = ((cell[1] * 16 + y) * image.width + cell[0] * 16 + x) * 4;
      const expected = [...image.data.subarray(offset, offset + 4)], got = actual.pixels[i][j];
      assert.equal(got[3], expected[3], `alpha ${sample.name ?? i} at ${x},${y}`);
      if (expected[3] > 0) for (let k = 0; k < 3; k++) assert.ok(Math.abs(got[k] - expected[k]) <= 2, `pixel ${sample.name ?? i} at ${x},${y}: ${got} vs ${expected}`);
    }
  }
  console.log(`PASS: ${version} imported ${Object.keys(entries).length} local game resources in ${actual.ms}ms; full font/model/animation metadata and representative atlas pixels match the old generated pack.`);
}

const server = await createServer({
  configFile: false, root, logLevel: 'error', server: { host: '127.0.0.1', port: 0 },
  plugins: [{ name: 'import-test-page', configureServer(s) { s.middlewares.use((req, res, next) => {
    if (req.url !== '/__import_test') return next();
    res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Isolated import test</title>');
  }); } }],
});
let browser;
try {
  await server.listen();
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/__import_test`);
  const result = await page.evaluate(async ({ modern, beta, replacement, unsupported, corrupt, soundFiles }) => {
    const importer = await import('/src/assets/importPack.ts');
    const storage = await import('/src/assets/localStore.ts');
    const file = (encoded, name) => new File([Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0))], name);
    let changed = 0;
    window.addEventListener(storage.ASSETS_CHANGED, () => changed++);
    const progress = [];
    const first = await importer.importGameJar(file(modern, 'fixture-1.12.2.jar'), (p) => progress.push(p));
    const readPixels = async (blob, positions) => {
      const bitmap = await createImageBitmap(blob);
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), context = canvas.getContext('2d');
      context.drawImage(bitmap, 0, 0); bitmap.close();
      return positions.map(([x, y]) => [...context.getImageData(x, y, 1, 1).data]);
    };
    const water = first.pack.blocks.sheets['blocks/water_flow@32'];
    const banner = first.pack.blocks.sheets['mineclaw/banner_white'];
    const animations = first.pack.anims;
    const frames = [];
    for (const animation of animations) {
      const size = animation.size ?? 16;
      frames.push({ name: animation.name, pixels: await readPixels(first.images.anim, Array.from({ length: animation.count }, (_, i) => [i * size + 2, animation.row * 16 + 2])) });
    }
    const summary = {
      version: first.version, info: first.pack.blocks.info['blocks/stone'],
      water, waterPixel: await readPixels(first.images.blocks, [[water[0] * 16 + 27, water[1] * 16 + 9]]),
      banner, bannerPixels: await readPixels(first.images.blocks, [[banner[0] * 16 + 2, banner[1] * 16 + 2], [banner[0] * 16 + 20, banner[1] * 16 + 2]]),
      animations, frames, models: first.pack.models, itemModels: first.pack.itemModels,
      blockstates: first.pack.blockstates, font: first.pack.font, imageNames: Object.keys(first.images),
      stored: (await storage.readLocalPack('1.12.2')).name, progress,
    };
    const legacy = await importer.importGameJar(file(beta, 'fixture-b1.8.1.jar'));
    const next = await importer.importGameJar(file(replacement, 'replacement.jar'));
    const folder = soundFiles.map(({ path, data }) => {
      const entry = file(data, path.split('/').at(-1));
      Object.defineProperty(entry, 'webkitRelativePath', { value: path });
      return entry;
    });
    const soundCount = await importer.importSoundFolder(folder, '1.12.2');
    const withSounds = await storage.readLocalPack('1.12.2');
    const failures = [];
    for (const bytes of [unsupported, corrupt]) {
      try { await importer.importGameJar(file(bytes, 'invalid.jar')); failures.push(null); }
      catch (error) { failures.push(String(error)); }
    }
    return { summary, legacy: { version: legacy.version, info: legacy.pack.terrain.info[0], font: legacy.pack.font },
      replacement: { name: next.name, models: next.pack.models, images: Object.keys(next.images), sounds: Object.keys(next.sounds), events: next.soundEvents },
      soundImport: { count: soundCount, name: withSounds.name, sounds: Object.keys(withSounds.sounds), events: withSounds.soundEvents },
      persisted: (await storage.readLocalPack('1.12.2')).name, legacyPersisted: (await storage.readLocalPack('b1.8.1')).name, changed, failures };
  }, {
    modern: jar(modern), beta: jar(beta), replacement: jar({ [tex + 'blocks/stone.png']: stone }),
    unsupported: jar({ 'assets/minecraft/textures/block/stone.png': stone }),
    corrupt: jar({ [tex + 'blocks/stone.png']: Buffer.from('not a PNG') }),
    soundFiles,
  });
  const { summary: s } = result;
  assert.equal(s.version, '1.12.2'); assert.deepEqual(s.info, [1, 0, 0x505a64]);
  assert.deepEqual(s.water.slice(2), [2, 2]); assert.deepEqual(s.waterPixel, [[27, 9, 220, 255]]);
  assert.deepEqual(s.banner.slice(2), [4, 4]); assert.deepEqual(s.bannerPixels[0], [20, 40, 200, 255]);
  for (const [i, expected] of [138, 148, 228, 255].entries()) assert.ok(Math.abs(s.bannerPixels[1][i] - expected) <= 1);
  assert.deepEqual(s.animations.find((a) => a.name === 'blocks/water_flow@32').frames, [{ index: 1, time: 3 }, { index: 0, time: 1 }]);
  assert.equal(s.animations.find((a) => a.name === 'blocks/water_flow@32').interpolate, true);
  assert.deepEqual(s.frames.find((a) => a.name === 'blocks/water_flow@32').pixels, [[2, 2, 220, 255], [20, 200, 2, 255]]);
  assert.deepEqual(s.frames.find((a) => a.name === 'blocks/lava_flow@32').pixels, [[250, 60, 0, 255], [220, 30, 0, 255], [190, 0, 0, 255]]);
  assert.deepEqual(s.frames.find((a) => a.name === 'items/clock').pixels, [[255, 240, 10, 255], [10, 240, 255, 255]]);
  assert.equal(s.models['block/stone'].textures.all, 'blocks/stone'); assert.equal(s.itemModels['item/clock'].parent, 'item/generated');
  assert.equal(s.blockstates.stone.variants.normal.model, 'stone'); assert.equal(s.font.widths.length, 256);
  assert.equal(s.stored, 'fixture-1.12.2.jar'); assert.equal(s.progress.at(-1), 'Saving on this device…');
  assert.equal(result.legacy.version, 'b1.8.1'); assert.deepEqual(result.legacy.info, [1, 0, 0x1e78c8]);
  assert.equal(result.legacy.font.chars.slice(32), ' ABCDEFGHIJKLMNOPQRSTUVWXYZ');
  assert.deepEqual(result.replacement.models, {}); assert.equal(result.replacement.images.includes('font'), false);
  assert.deepEqual(result.replacement.sounds, ['block/chest/open']); assert.ok(result.replacement.events['block.chest.open']);
  assert.equal(result.persisted, 'replacement.jar'); assert.equal(result.legacyPersisted, 'fixture-b1.8.1.jar');
  assert.equal(result.soundImport.count, 1); assert.equal(result.soundImport.name, 'replacement.jar');
  assert.deepEqual(result.soundImport.sounds, ['block/chest/open', 'entity/test']); assert.ok(result.soundImport.events['entity.test']);
  assert.equal(result.changed, 4); assert.match(result.failures[0], /Other texture formats/); assert.match(result.failures[1], /Invalid PNG/);
  console.log('PASS: real browser ZIP/PNG import, native32 fluid/animation pixels, banner composition, models/font, both formats, atomic replacement, sound-folder objects, per-version storage and invalid-import preservation.');
  if (localResources) for (const version of ['1.12.2', 'b1.8.1']) await verifyLocalResources(page, version);
} finally {
  await browser?.close();
  await server.close();
}
