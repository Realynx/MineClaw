#!/usr/bin/env node
// Headless screenshots of the demo (Playwright's own Chromium, never the user's browser): serves the demo with Vite, opens
// the given query, waits for the world to load, optionally runs a few viewer calls, and saves a PNG.
//
//   node scripts/shot.mjs --query "version=1.12.2&thumbs=0" --out shot.png [--wait 8000] [--eval "h.setCameraMode('iso')"]
//   node scripts/shot.mjs --page models.html --query "set=looks" --eval "g.set(0.5, 0.4, 12)"   (the model gallery: g = __gallery)
//   node scripts/shot.mjs --plan plan.json --out dir   (several shots in one session; see the plan branch below)
//   (--gpu uses the machine's GPU; default SwiftShader)

import { createServer } from 'vite';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { readFileSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const args = process.argv.slice(2);
const arg = (k, d) => {
  const i = args.indexOf(`--${k}`);
  return i >= 0 ? args[i + 1] : d;
};
const out = resolve(arg('out', 'shot.png'));
const query = arg('query', 'thumbs=0');
const wait = Number(arg('wait', '8000'));
const script = arg('eval', '');
const gpu = args.includes('--gpu');
const pageName = arg('page', '');
const plan = arg('plan', '');

const server = await createServer({ configFile: resolve(root, 'vite.demo.config.ts'), root, server: { port: 0 }, logLevel: 'error' });
await server.listen();
const port = server.httpServer.address().port;
const browser = await chromium.launch({ headless: true, args: gpu ? ['--enable-gpu', '--ignore-gpu-blocklist'] : ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
try {
  const page = await browser.newPage({ viewport: { width: Number(arg('width', '1280')), height: Number(arg('height', '800')) } });
  page.on('pageerror', (e) => console.error('page error:', e.message));
  page.on('console', (m) => m.type() === 'error' && console.error('console:', m.text()));
  await page.goto(`http://localhost:${port}/${pageName}?${query}`, { waitUntil: 'load' });
  await page.waitForTimeout(wait);
  if (plan) {
    // A plan: shared setup code ("setup"), then [{ name, eval, after }] shots saved as <out dir>/<name>.png.
    const steps = JSON.parse(readFileSync(resolve(plan), 'utf8'));
    const pre = steps.setup ?? '';
    for (const s of steps.shots) {
      await page.evaluate(`(async () => { const h = window.__viewer?.current, src = window.__source; ${pre}; ${s.eval} })()`);
      await page.waitForTimeout(s.after ?? 2500);
      const file = resolve(out, `${s.name}.png`);
      await page.screenshot({ path: file });
      console.log(file);
    }
  } else if (script) {
    await page.evaluate(`(async () => { const h = window.__viewer?.current, g = window.__gallery; ${script} })()`);
    await page.waitForTimeout(Number(arg('after', '2500')));
  }
  if (!plan) {
    await page.screenshot({ path: out });
    console.log(out);
  }
} finally {
  await browser.close();
  await server.close();
}
