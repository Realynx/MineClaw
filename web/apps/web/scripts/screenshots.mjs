// Screenshots every page in dark and light (mock mode) for visual review, with Chromium's GPU disabled (software rendering,
// like a browser with hardware acceleration off). On the graph pages it also pans the canvas for 2 s and reports frame rate
// and draw times.
// Usage: node scripts/screenshots.mjs <baseUrl> <outDir> [pageFilter]
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { chromium } from '@playwright/test';

const base = process.argv[2] ?? 'http://localhost:3100';
const out = process.argv[3] ?? 'shots';
const filter = process.argv[4];
const width = Number(process.env.SHOT_WIDTH ?? 1440);
const height = Number(process.env.SHOT_HEIGHT ?? 900);
mkdirSync(out, { recursive: true });

export const PAGES = [
  ['live', '/live'],
  ['live-tree', '/live?view=tree'],
  ['live-world', '/live?view=world'],
  ['bot', '/live/maid_chan'],
  ['bot-subhive', '/live/maid_chan_68'],
  ['memory-activity', '/memory-activity'],
  ['story', '/story'],
  ['overview', '/overview'],
  ['memory', '/memory?kind=claims'],
  ['memory-detail', '/memory?kind=chests&id=2'],
  ['search', '/search?q=melons'],
  ['memory-cloud', '/memory-activity?layout=cloud'],
  ['personality', '/personality'],
  ['scripts', '/scripts?name=cobble_loop'],
  ['designs', '/designs?id=12'],
  ['shards', '/shards'],
  ['admin', '/admin'],
];
const GRAPH_PAGES = new Set(['memory-activity', 'memory-cloud']);

async function measure(page) {
  const box = await page.locator('[role=application]').first().boundingBox();
  if (!box) return null;
  await page.evaluate(() => {
    window.__mcxGraphDraws = [];
    window.__frames = [];
    const tick = (t) => {
      window.__frames.push(t);
      if (window.__frames.length < 2000) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  await page.mouse.move(cx, cy);
  await page.mouse.down();
  const start = Date.now();
  let i = 0;
  while (Date.now() - start < 2000) {
    i++;
    await page.mouse.move(cx + Math.sin(i / 6) * 120, cy + Math.cos(i / 8) * 60);
  }
  await page.mouse.up();
  return page.evaluate(() => {
    const f = window.__frames;
    const d = [...window.__mcxGraphDraws].sort((a, b) => a - b);
    const gaps = f.slice(1).map((t, k) => t - f[k]);
    const span = (f[f.length - 1] - f[0]) / 1000;
    const q = (a, p) => (a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : NaN);
    return {
      fps: Math.round((f.length - 1) / span),
      frameP95: Math.round(q([...gaps].sort((a, b) => a - b), 0.95)),
      draws: d.length,
      drawMedian: +q(d, 0.5).toFixed(1),
      drawP95: +q(d, 0.95).toFixed(1),
    };
  });
}

const browser = await chromium.launch({ args: ['--disable-gpu', '--disable-gpu-compositing'] });
for (const theme of ['dark', 'light']) {
  const ctx = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, colorScheme: theme });
  await ctx.addInitScript((t) => localStorage.setItem('mcx-theme', t), theme);
  const page = await ctx.newPage();
  const errors = [];
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto(`${base}/login`);
  await page.getByLabel('Username').waitFor({ timeout: 90000 });
  if (!filter || filter === 'login') await page.screenshot({ path: path.join(out, `login-${theme}.png`) });
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live', { timeout: 90000 });
  for (const [name, url] of PAGES) {
    if (filter && !name.includes(filter)) continue;
    errors.length = 0;
    await page.goto(`${base}${url}`);
    await page.waitForLoadState('networkidle', { timeout: 60000 }).catch(() => undefined);
    await page.waitForTimeout(2500);
    await page.screenshot({ path: path.join(out, `${name}-${theme}${width === 1440 ? '' : `-${width}`}.png`) });
    if (GRAPH_PAGES.has(name) && theme === 'dark') console.log(`[perf] ${name}:`, JSON.stringify(await measure(page)));
    if (errors.length) console.log(`[${theme}] ${name}: ${errors.slice(0, 5).join(' | ')}`);
  }
  await ctx.close();
}
await browser.close();
console.log(`saved to ${out}`);
