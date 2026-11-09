// Isolated WebGL regression: generated colors only; no live game, browser profile or Minecraft resources.
/* global document, createImageBitmap */
import { createServer } from 'vite';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, configFile: false, server: { host: '127.0.0.1', port: 0 }, plugins: [{ name: 'chest-test',
  resolveId(id) { if (id === 'virtual:chest-test') return '\0virtual:chest-test'; },
  load(id) { if (id === '\0virtual:chest-test') return "export * as THREE from 'three';"; },
  configureServer(s) { s.middlewares.use('/__chest_test', (_req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Chest lid regression</title>'); }); },
}] });
let browser;
try {
  await server.listen();
  browser = await chromium.launch({ headless: true, args: ['--enable-unsafe-swiftshader'] });
  const page = await browser.newPage(), errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (msg) => { if (msg.type() === 'error') errors.push(msg.text()); });
  await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/__chest_test`);
  const result = await page.evaluate(async () => {
    const { THREE } = await import('/@id/__x00__virtual:chest-test');
    const { BlockEntityLayer } = await import('/src/render/blockEntityLayer.ts');
    const { createChunkUniforms } = await import('/src/render/chunkMaterial.ts');
    const atlas = document.createElement('canvas'); atlas.width = 256; atlas.height = 256;
    const ctx = atlas.getContext('2d'); ctx.fillStyle = '#00ff00'; ctx.fillRect(0, 0, 256, 256);
    ctx.fillStyle = '#ff0000'; ctx.fillRect(64, 128, 128, 64);
    const image = await createImageBitmap(atlas);
    const pack = { version: '1.12.2', blocks: { columns: 16, rows: 16, sheets: { 'entity/chest/normal_double': [4, 8, 8, 4] } } };
    const uniforms = createChunkUniforms(); uniforms.uFogRange.value.set(100, 200);
    const host = { blocksAt: async (_dim, pts) => {
      const out = []; for (let i = 0; i < pts.length; i += 3) out.push(...(pts[i + 1] === 0 && pts[i + 2] === 0 && (pts[i] === 0 || pts[i] === 1) ? [54, 3, 240] : [0, 0, -1])); return out;
    } };
    const layer = new BlockEntityLayer(host, uniforms);
    layer.setBundle({ version: '1.12.2', pack, blocks: image });
    const store = { revision: 1, all: () => [{ kind: 'chest', x: 0, y: 0, z: 0 }, { kind: 'chest', x: 1, y: 0, z: 0 }] };
    const scene = new THREE.Scene(); scene.add(layer.group);
    const renderer = new THREE.WebGLRenderer(); renderer.setSize(256, 256);
    const target = new THREE.WebGLRenderTarget(256, 256), camera = new THREE.PerspectiveCamera(42, 1, 0.1, 100);
    camera.position.set(3, 2.5, 4); camera.lookAt(1, 0.7, 0.5);
    const frame = (now) => layer.update(store, { dim: 0, centre: camera.position, now, ticks: now / 50, software: false });
    frame(1); await Promise.resolve(); frame(2);
    const draw = () => {
      renderer.setRenderTarget(target); renderer.render(scene, camera);
      const pixels = new Uint8Array(256 * 256 * 4); renderer.readRenderTargetPixels(target, 0, 0, 256, 256, pixels); return pixels;
    };
    const count = (pixels) => {
      let red = 0, green = 0;
      for (let i = 0; i < pixels.length; i += 4) {
        if (pixels[i] > 20 && pixels[i] > pixels[i + 1] * 2) red++;
        if (pixels[i + 1] > 20 && pixels[i + 1] > pixels[i] * 2) green++;
      }
      return { red, green };
    };
    const closed = draw();
    layer.receiveEvents([{ kind: 'chest_open', dim: 0, at: 100, x: 1, y: 0, z: 0 }], 2);
    frame(252); frame(502); const opened = draw();
    layer.receiveEvents([{ kind: 'chest_close', dim: 0, at: 1000, x: 0, y: 0, z: 0 }], 502);
    frame(752); frame(1002); const reclosed = draw();
    uniforms.uRoof.value.set(1, 0, 0.5, 10); const cut = draw();
    const difference = (a, b) => a.reduce((n, v, i) => n + (v !== b[i] ? 1 : 0), 0);
    const result = { closed: count(closed), opened: count(opened), reclosed: count(reclosed), cut: count(cut), changed: difference(closed, opened), restored: difference(closed, reclosed) };
    target.dispose(); renderer.dispose(); layer.dispose(); image.close(); return result;
  });
  assert.ok(result.closed.red > 100 && result.opened.red > 100, JSON.stringify(result));
  assert.equal(result.closed.green + result.opened.green, 0, 'UVs must stay inside the chest sheet');
  assert.ok(result.changed > 200, JSON.stringify(result));
  assert.equal(result.restored, 0, 'close returns exactly to its initial pose');
  assert.equal(result.cut.red, 0, 'top-down roof cuts must hide the lid along with its body');
  assert.deepEqual(errors, []);
  process.stdout.write('Chest lid WebGL regression passed: ' + JSON.stringify(result) + '\n');
} finally { await browser?.close(); await server.close(); }
