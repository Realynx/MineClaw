#!/usr/bin/env node
// Opt-in performance benchmark: serves the demo with Vite, opens it in Playwright's Chromium and measures sustained fps at the
// follow camera over the dense fixture world (water, lava, glass, ~20 mobs), once with software WebGL (SwiftShader — what the
// user's hardware-acceleration-off browser runs) and once with the GPU when one is available.
//
//   npm run perf                      (both profiles)
//   npm run perf -- --profile software --seconds 20 --version 1.12.2 --mobs 20
//   npm run perf -- --profile software --camera top --zoom 144 --draw 112 --radius 14   (Story Mode's map zoomed all the way out)
//
// Prints one JSON line per run: renderer, detected quality profile, fps (median of 1 s windows), p10 fps, the controller's
// resolution scale and draw factor, draw calls, triangles, sections.

import { createServer } from 'vite';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const args = process.argv.slice(2);
const arg = (k, d) => {
  const i = args.indexOf(`--${k}`);
  return i >= 0 ? args[i + 1] : d;
};
const profiles = arg('profile', 'software,gpu').split(',');
const seconds = Number(arg('seconds', '15'));
const version = arg('version', 'b1.8.1');
const mobs = arg('mobs', '20');
/** A fixed top-down / iso view over the island centre at this zoom (view height in blocks) instead of following Bob. */
const camera = arg('camera', null);
const zoom = Number(arg('zoom', '48'));
const draw = arg('draw', '128');
const radius = arg('radius', '8');

const FLAGS = {
  software: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--disable-gpu', '--ignore-gpu-blocklist'],
  gpu: ['--enable-gpu', '--ignore-gpu-blocklist', '--use-angle=default'],
};

const server = await createServer({ configFile: resolve(root, 'vite.demo.config.ts'), root, server: { port: 0, strictPort: false }, logLevel: 'error' });
await server.listen();
const port = server.httpServer.address().port;
const results = [];
try {
  for (const p of profiles) {
    const browser = await chromium.launch({ headless: true, args: [...(FLAGS[p] ?? []), '--window-size=1280,800'] });
    try {
      const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
      page.on('pageerror', (e) => console.error(`[${p}] page error:`, e.message));
      const url = `http://localhost:${port}/?thumbs=0&dense=1&mobs=${mobs}&version=${version}&radius=${radius}&draw=${draw}`;
      await page.goto(url, { waitUntil: 'load' });
      if (camera) {
        await page.waitForFunction(() => !!window.__viewer?.current?.engine(), null, { timeout: 25_000 });
        await page.evaluate(([mode, z]) => {
          const v = window.__viewer.current;
          v.setCameraMode(mode);
          v.focus(0, 64, 0);
          // As Story Mode's map: no x-ray cone (so the cave-culling walk runs), the zoom given.
          v.engine().setXRay('off');
          v.engine().rig.isoZoom = z;
        }, [camera, zoom]);
      }
      // Let the world stream in and the meshes settle (or 25 s), then measure.
      await page.waitForFunction(() => {
        const e = window.__viewer?.current?.engine();
        if (!e) return false;
        const m = e.stats().memory;
        return m.columns > 200 && m.sectionsQueued < 50;
      }, null, { timeout: 25_000, polling: 500 }).catch(() => undefined);
      await page.waitForTimeout(3000); // the controller settles
      const r = await page.evaluate(async (secs) => {
        const windows = [];
        let n = 0, t0 = performance.now(), start = t0;
        await new Promise((done) => {
          const f = (t) => {
            n++;
            if (t - t0 >= 1000) {
              windows.push((n * 1000) / (t - t0));
              n = 0;
              t0 = t;
            }
            if (t - start < secs * 1000) requestAnimationFrame(f);
            else done();
          };
          requestAnimationFrame(f);
        });
        const s = window.__viewer.current.engine().stats();
        windows.sort((a, b) => a - b);
        return {
          fps: windows[Math.floor(windows.length / 2)],
          p10: windows[Math.floor(windows.length * 0.1)],
          renderer: s.memory.renderer,
          quality: s.memory.quality,
          reason: s.memory.qualityReason,
          scale: s.resolutionScale,
          drawFactor: s.memory.drawFactor,
          frameMs: s.memory.frameMs,
          drawCalls: s.drawCalls,
          triangles: s.triangles,
          sections: s.sections,
          culled: s.memory.culledSections,
          entities: s.memory.entities,
        };
      }, seconds);
      const line = { profile: p, version, mobs: Number(mobs), ...(camera ? { camera, zoom, draw: Number(draw), radius: Number(radius) } : {}), ...r };
      results.push(line);
      console.log(JSON.stringify(line));
    } finally {
      await browser.close();
    }
  }
} finally {
  await server.close();
}
if (results.some((r) => r.profile === 'software' && r.fps < 40)) process.exitCode = 1;
