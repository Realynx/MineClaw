// Screenshots every page in dark and light (mock mode) for visual review.
// Usage: node scripts/screenshots.mjs <baseUrl> <outDir> [pageFilter]
// Logs in as `fox` (TOTP 123456 in the mocks), then visits each page. Console errors are printed per page.
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { chromium } from '@playwright/test';

const base = process.argv[2] ?? 'http://localhost:3100';
const out = process.argv[3] ?? 'shots';
const filter = process.argv[4];
mkdirSync(out, { recursive: true });

export const PAGES = [
  ['login', '/login'],
  ['live', '/live'],
  ['live-tree', '/live?view=tree'],
  ['live-world', '/live?view=world'],
  ['bot', '/live/maid_chan'],
  ['bot-subhive', '/live/maid_chan_68'],
  ['memory-activity', '/memory-activity'],
  ['overview', '/overview'],
  ['memory', '/memory?kind=claims'],
  ['search', '/search?q=melons'],
  ['raptor', '/raptor'],
  ['raptor-tree', '/raptor?view=tree&node=481'],
  ['personality', '/personality'],
  ['scripts', '/scripts?name=cobble_loop'],
  ['designs', '/designs?id=12'],
  ['shards', '/shards'],
  ['admin', '/admin'],
];

const browser = await chromium.launch();
for (const theme of ['dark', 'light']) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: theme });
  await ctx.addInitScript((t) => localStorage.setItem('mcx-theme', t), theme);
  const page = await ctx.newPage();
  const errors = [];
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  // log in once per context
  await page.goto(`${base}/login`);
  await page.getByLabel('Username').waitFor({ timeout: 60000 });
  if (!filter || filter === 'login') await page.screenshot({ path: path.join(out, `login-${theme}.png`) });
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('Authenticator code').fill('123456');
  await page.getByRole('button', { name: 'Verify' }).click();
  await page.waitForURL('**/live', { timeout: 60000 });
  for (const [name, url] of PAGES) {
    if (name === 'login' || (filter && !name.includes(filter))) continue;
    errors.length = 0;
    await page.goto(`${base}${url}`);
    await page.waitForLoadState('networkidle', { timeout: 60000 }).catch(() => undefined);
    await page.waitForTimeout(1800);
    await page.screenshot({ path: path.join(out, `${name}-${theme}.png`) });
    if (errors.length) console.log(`[${theme}] ${name}: ${errors.slice(0, 5).join(' | ')}`);
  }
  await ctx.close();
}
await browser.close();
console.log(`saved to ${out}`);
