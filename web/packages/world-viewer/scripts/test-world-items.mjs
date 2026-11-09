// Isolated WebGL item regression; synthetic atlas colors only, no private textures or game server.
/* global document, createImageBitmap */
import { createServer } from 'vite';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, configFile: false, server: { host: '127.0.0.1', port: 0 }, plugins: [{ name: 'item-test',
  resolveId(id) { if (id === 'virtual:item-test') return '\0virtual:item-test'; },
  load(id) { if (id === '\0virtual:item-test') return "export * as THREE from 'three';"; },
  configureServer(s) { s.middlewares.use('/__item_test', (_req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Item regression</title>'); }); },
}] });
let browser;
try {
  await server.listen();
  browser = await chromium.launch({ headless: true, args: ['--enable-unsafe-swiftshader'] });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (msg) => { if (msg.type() === 'error') errors.push(msg.text()); });
  await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/__item_test`);
  const result = await page.evaluate(async () => {
    const { THREE } = await import('/@id/__x00__virtual:item-test');
    const { EntityLayer } = await import('/src/render/entityLayer.ts');
    const pack = { version: '1.12.2', tileSize: 16,
      items: { columns: 32, rows: 11, tiles: { 'items/fish_cod_raw': [9, 6], 'items/compass_00': [17, 3] } },
      blocks: { columns: 32, rows: 11, tiles: {}, info: {}, sheets: {} },
      entity: { width: 512, height: 176, rects: { 'entity/steve': [0, 0, 64, 64] } }, anims: [], env: [], models: {}, blockstates: {}, biomes: {},
      itemModels: { 'item/cod': { parent: 'item/generated', textures: { layer0: 'items/fish_cod_raw' } } },
    };
    const canvas = document.createElement('canvas'); canvas.width = 512; canvas.height = 176;
    const ctx = canvas.getContext('2d'); ctx.fillStyle = '#00ff00'; ctx.fillRect(0, 0, 512, 176);
    const [x, y] = pack.items.tiles['items/fish_cod_raw'];
    ctx.fillStyle = '#ff0000'; ctx.fillRect(x * 16, y * 16, 16, 16);
    const itemSheet = await createImageBitmap(canvas);
    ctx.clearRect(0, 0, 512, 176);
    const empty = await createImageBitmap(canvas);
    const layer = new EntityLayer();
    layer.setBundle({ version: '1.12.2', pack, items: itemSheet, blocks: empty, entity: empty, anim: null, env: {} });
    const renderer = new THREE.WebGLRenderer(); renderer.setSize(256, 256);
    const target = new THREE.WebGLRenderTarget(256, 256);
    const scene = new THREE.Scene(); scene.add(layer.group);
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100); camera.position.set(0, 0.7, 3); camera.lookAt(0, 0.7, 0);
    const bot = { name: 'fish-holder', dim: 0, x: -0.5, y: 0, z: 0, yaw: 0, dto: { name: 'fish-holder', main: false, dim: 0, x: -0.5, y: 0, z: 0, hp: 20, food: 20, held: 'fish', equipment: { main: [349, 0] } } };
    const drop = { key: '0:1', id: 1, dim: 0, kind: 'item', name: null, type: 2, item: 349, count: 1, x: 0.5, y: 0.3, z: 0, yaw: 0, ageTicks: 0, flags: 0, dto: { id: 1, dim: 0, kind: 'item', type: 2, item: 349, count: 1, x: 0.5, y: 0.3, z: 0, damage: 0 } };
    const render = (bots, entities) => {
      layer.update(bots, entities, 1000, new Set()); layer.showFor(0, camera.position, { bots: true, entities: true, labels: false });
      renderer.setRenderTarget(target); renderer.render(scene, camera);
      const pixels = new Uint8Array(256 * 256 * 4); renderer.readRenderTargetPixels(target, 0, 0, 256, 256, pixels);
      const counts = { red: 0, green: 0 };
      for (let i = 0; i < pixels.length; i += 4) { if (pixels[i] > 30 && pixels[i] > pixels[i + 1] * 2) counts.red++; if (pixels[i + 1] > 30 && pixels[i + 1] > pixels[i] * 2) counts.green++; }
      return counts;
    };
    const held = render([bot], []), heldAndDropped = render([bot], [drop]);
    // A reimport can reorder the atlas while the same pawn keeps holding the same item.
    const nextPack = structuredClone(pack);
    nextPack.items.tiles['items/fish_cod_raw'] = [17, 3];
    ctx.fillStyle = '#00ff00'; ctx.fillRect(0, 0, 512, 176);
    ctx.fillStyle = '#ff0000'; ctx.fillRect(17 * 16, 3 * 16, 16, 16);
    const nextSheet = await createImageBitmap(canvas);
    layer.setBundle({ version: '1.12.2', pack: nextPack, items: nextSheet, blocks: empty, entity: empty, anim: null, env: {} });
    const repacked = render([bot], [drop]), repackedHeld = render([bot], []), dropped = render([], [drop]);
    target.dispose(); renderer.dispose(); layer.dispose(); itemSheet.close(); nextSheet.close(); empty.close();
    return { held, heldAndDropped, repacked, repackedHeld, dropped };
  });
  for (const counts of Object.values(result)) { assert.ok(counts.red > 20, JSON.stringify(result)); assert.equal(counts.green, 0); }
  assert.deepEqual(errors, []);
  process.stdout.write('World item WebGL regression passed: ' + JSON.stringify(result) + '\n');
} finally { await browser?.close(); await server.close(); }
