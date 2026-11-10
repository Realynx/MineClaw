// Isolated browser/WebGL regression. Synthetic solid skins only; no game server, user browser or downloaded assets. The player
// models come from your local 1.12.2 pack (generated/1.12.2/pack.json: npm run assets:extract && npm run assets:pack).
/* global document, createImageBitmap */
import { createServer } from 'vite';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('..', import.meta.url));
if (!existsSync(fileURLToPath(new URL('../generated/1.12.2/pack.json', import.meta.url)))) {
  console.error('generated/1.12.2/pack.json is missing: run npm run assets:extract && npm run assets:pack first, from your own 1.12.2 client jar');
  process.exit(2);
}
const server = await createServer({ root, configFile: false, server: { host: '127.0.0.1', port: 0 }, plugins: [{ name: 'skin-test',
resolveId(id) { if (id === 'virtual:skin-test') return '\0virtual:skin-test'; },
load(id) { if (id === '\0virtual:skin-test') return "export * as THREE from 'three';"; },
configureServer(s) {
  s.middlewares.use('/__skin_test', (_req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Skin regression</title>'); });
} }] });
let browser;
try {
  await server.listen();
  browser = await chromium.launch({ headless: true, args: ['--enable-unsafe-swiftshader'] });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (msg) => { if (msg.type() === 'error') errors.push(msg.text()); });
  await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/__skin_test`);
  const result = await page.evaluate(async () => {
    const { THREE } = await import('/@id/__x00__virtual:skin-test');
    const { EntityLayer } = await import('/src/render/entityLayer.ts');
    const { humanSkins, configureHumanSkins } = await import('/src/assets/humanSkins.ts');
    const pack = await fetch('/generated/1.12.2/pack.json').then((r) => r.json());
    const solid = (height, color) => {
      const canvas = document.createElement('canvas'); canvas.width = 64; canvas.height = height;
      const ctx = canvas.getContext('2d'); ctx.fillStyle = color; ctx.fillRect(0, 0, 64, height); return canvas;
    };
    const layer = new EntityLayer();
    const blue = solid(64, '#0000ff');
    const defaultSheet = await createImageBitmap(blue);
    layer.setBundle({ version: '1.12.2', pack, blocks: defaultSheet, items: defaultSheet, entity: defaultSheet, anim: null, env: {} });
    configureHumanSkins(() => Promise.resolve({ enabled: true, epoch: 'smoke', revision: 2, skins: [
      { name: 'slim', slim: true, height: 64, png: solid(64, '#ff0000').toDataURL().split(',')[1] },
      { name: 'legacy', slim: false, height: 32, png: solid(32, '#00ff00').toDataURL().split(',')[1] },
    ] }));
    for (let i = 0; i < 100 && !humanSkins.get('legacy'); i++) await new Promise((resolve) => setTimeout(resolve, 20));
    const renderer = new THREE.WebGLRenderer(); renderer.setSize(160, 160);
    const target = new THREE.WebGLRenderTarget(160, 160);
    const scene = new THREE.Scene(); scene.add(layer.group);
    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100); camera.position.set(0, 1, 5); camera.lookAt(0, 1, 0);
    const bots = ['slim', 'legacy'].map((name, i) => ({ name, dim: 0, x: i ? 0.6 : -0.6, y: 0, z: 0, yaw: 0, dto: { name, short: name, main: false, dim: 0, x: i ? 0.6 : -0.6, y: 0, z: 0, hp: 20, food: 20 } }));
    const render = () => {
      for (let i = 0; i < 2; i++) { layer.update(bots, [], 1000 + i * 16, new Set()); layer.showFor(0, camera.position, { bots: true, entities: true, labels: false }); }
      renderer.setRenderTarget(target); renderer.render(scene, camera);
      const pixels = new Uint8Array(160 * 160 * 4); renderer.readRenderTargetPixels(target, 0, 0, 160, 160, pixels);
      const counts = { red: 0, green: 0, blue: 0 };
      for (let i = 0; i < pixels.length; i += 4) {
        const [r, g, b] = pixels.subarray(i, i + 3);
        if (r > 30 && r > g * 2 && r > b * 2) counts.red++;
        if (g > 30 && g > r * 2 && g > b * 2) counts.green++;
        if (b > 30 && b > r * 2 && b > g * 2) counts.blue++;
      }
      return counts;
    };
    const restoreContext = async () => {
      const waitEvent = (event) => new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error('Timed out waiting for ' + event)), 5000);
        renderer.domElement.addEventListener(event, () => { clearTimeout(timeout); resolve(); }, { once: true });
      });
      const lost = waitEvent('webglcontextlost'); renderer.forceContextLoss(); await lost;
      // The browser finishes dispatching the loss event before it permits WEBGL_lose_context to restore it.
      await new Promise((resolve) => setTimeout(resolve, 100));
      const restored = waitEvent('webglcontextrestored'); renderer.forceContextRestore(); await restored;
    };
    const enabled = render();
    await restoreContext(); const restoredSkins = render();
    configureHumanSkins(null); const disabled = render();
    await restoreContext(); const restoredDefault = render();
    target.dispose(); renderer.dispose(); layer.dispose();
    defaultSheet.close();
    return { enabled, disabled, restoredSkins, restoredDefault };
  });
  assert.ok(result.enabled.red > 100 && result.enabled.green > 100, JSON.stringify(result));
  assert.equal(result.disabled.red + result.disabled.green, 0, JSON.stringify(result));
  assert.ok(result.disabled.blue > 200, JSON.stringify(result));
  assert.deepEqual(result.restoredSkins, result.enabled, 'Skin atlas changed after WebGL context restoration');
  assert.deepEqual(result.restoredDefault, result.disabled, 'Imported bitmap texture changed after WebGL context restoration');
  assert.deepEqual(errors, []);
  process.stdout.write('Human skin WebGL regression passed: ' + JSON.stringify(result) + '\n');
} finally { await browser?.close(); await server.close(); }
