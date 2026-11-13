// The README screenshots (docs/images/*.png), taken from the app in mock mode: a demo colony staged through the mock API,
// the Studio's redstone and mine designers, the Live hive and a pawn takeover. Headless Chromium only, one page at a time.
// Usage (from web/apps/web, with `npm run dev:mock -w @mineclaw/web` running on :3000):
//   node scripts/readme-shots.mjs http://localhost:3000 <outDir> [job]
// Jobs: story, hive, redstone, mine, takeover (default: all). The PNGs come out at 1600×900; optimise them before committing.
// It signs in as a throwaway mock user ("demo", player "robinchan") so no real player name is on screen, and hides the
// player tag the mock island keeps near spawn by framing. No game textures are used: the viewer draws flat colours.
import { mkdirSync } from 'node:fs';
import { crc32, deflateSync } from 'node:zlib';
import path from 'node:path';
import { chromium } from '@playwright/test';

const base = process.argv[2] ?? 'http://localhost:3000';
const out = process.argv[3] ?? 'readme-shots';
const only = process.argv[4];
mkdirSync(out, { recursive: true });

const DEMO = { username: 'demo', password: 'demo', player: 'robinchan', roles: ['viewer', 'chat', 'commands', 'admin', 'design', 'story'], totp: null };
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'] });

/** A fresh context signed in as the demo user (the mock's first-run users live in sessionStorage). */
async function open({ seed = null, labels = true } = {}) {
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 900 }, colorScheme: 'dark' });
  await ctx.addInitScript(
    ([u, s, l]) => {
      localStorage.setItem('mcx-theme', 'dark');
      if (!localStorage.getItem('mcx-viewer-settings'))
        localStorage.setItem('mcx-viewer-settings', JSON.stringify({ v: 1, preset: 'custom', custom: { labels: l } }));
      if (!sessionStorage.getItem('mcx-mock-setup')) {
        sessionStorage.setItem('mcx-mock-setup', JSON.stringify([u]));
        sessionStorage.setItem('mcx-mock-session', JSON.stringify({ username: u.username, created: Date.now() }));
        if (s) sessionStorage.setItem('mcx-mock-story', s);
      }
      // The Next dev overlay's badge is not part of the app.
      document.addEventListener('DOMContentLoaded', () => {
        const st = document.createElement('style');
        st.textContent = 'nextjs-portal{display:none!important}';
        document.head.appendChild(st);
      });
    },
    [DEMO, seed, labels],
  );
  const page = await ctx.newPage();
  page.on('pageerror', (e) => console.log('pageerror', String(e).slice(0, 200)));
  return { ctx, page };
}

/** The mock data's sample owner account and its player: never in a screenshot (canvas labels are kept out by framing). */
const PRIVATE = /fox/i;

async function shot(page, name) {
  // The mock's bots rotate their thoughts; wait out one that names a real player.
  for (let i = 0; i < 30 && PRIVATE.test(await page.evaluate(() => document.body.innerText)); i++) await page.waitForTimeout(2000);
  if (PRIVATE.test(await page.evaluate(() => document.body.innerText))) throw new Error(`${name}: a private name is on the page`);
  const f = path.join(out, `${name}.png`);
  await page.screenshot({ path: f });
  console.log('saved', f);
}

/** POST to the mock API from the page (MSW answers it), with the CSRF token. */
function api(page, url, body) {
  return page.evaluate(
    async ([u, b]) => {
      const { csrfToken } = await (await fetch('/api/auth/csrf')).json();
      const r = await fetch(u, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken }, body: JSON.stringify(b) });
      if (!r.ok) throw new Error(`${u}: ${r.status} ${await r.text()}`);
      return r.json();
    },
    [url, body],
  );
}

// ------------------------------------------------------------------ original demo skins (drawn here: no game art)

function pngChunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'latin1');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])) >>> 0, 0);
  return Buffer.concat([head, data, crc]);
}

/** A 64×64 player skin of flat colour regions: skin, hair, shirt, trousers, boots, two eyes. */
function demoSkin({ skin, hair, shirt, trousers, boots, eyes }) {
  const px = new Uint8Array(64 * 64 * 4);
  const fill = (x0, y0, x1, y1, [r, g, b]) => {
    for (let y = y0; y < y1; y++)
      for (let x = x0; x < x1; x++) px.set([r, g, b, 255], (y * 64 + x) * 4);
  };
  fill(0, 0, 32, 16, skin); // head
  fill(8, 0, 16, 8, hair); // crown
  fill(0, 8, 32, 10, hair); // fringe all round
  fill(24, 8, 32, 14, hair); // back of the head
  fill(9, 12, 11, 13, [250, 250, 250]);
  fill(13, 12, 15, 13, [250, 250, 250]);
  fill(10, 12, 11, 13, eyes);
  fill(13, 12, 14, 13, eyes);
  fill(16, 16, 40, 32, shirt); // body
  fill(40, 16, 56, 32, shirt); // right arm
  fill(32, 48, 48, 64, shirt); // left arm
  fill(44, 28, 52, 32, skin); // right hand
  fill(36, 60, 44, 64, skin); // left hand
  fill(0, 16, 16, 32, trousers); // right leg
  fill(16, 48, 32, 64, trousers); // left leg
  fill(0, 29, 16, 32, boots);
  fill(16, 61, 32, 64, boots);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(64, 0);
  ihdr.writeUInt32BE(64, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const rows = [];
  for (let y = 0; y < 64; y++) rows.push(Buffer.from([0]), Buffer.from(px.subarray(y * 256, y * 256 + 256)));
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(Buffer.concat(rows))),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

const DEMO_SKINS = [
  ['Smith', { skin: [214, 160, 120], hair: [60, 40, 30], shirt: [200, 110, 40], trousers: [70, 60, 50], boots: [40, 30, 25], eyes: [40, 90, 160] }],
  ['Miner', { skin: [170, 115, 80], hair: [30, 30, 35], shirt: [60, 110, 180], trousers: [45, 55, 90], boots: [50, 50, 55], eyes: [60, 40, 20] }],
  ['Ranger', { skin: [230, 185, 150], hair: [190, 120, 50], shirt: [60, 140, 80], trousers: [110, 80, 50], boots: [60, 45, 30], eyes: [40, 120, 70] }],
];

// ------------------------------------------------------------------ Story Mode: the Controller over a staged colony

const story = (page, m, ...a) => page.evaluate(([mm, aa]) => window.__story[mm](...aa), [m, a]);

/** Moves the map's focus until world point `w` is drawn at page point `t` (works top-down and angled). */
async function centre(page, w, t) {
  for (let k = 0; k < 5; k++) {
    const c = await story(page, 'center');
    const p0 = await story(page, 'toPage', w[0], w[1], w[2]);
    const px = await story(page, 'toPage', w[0] + 1, w[1], w[2]);
    const pz = await story(page, 'toPage', w[0], w[1], w[2] + 1);
    if (!p0 || !px || !pz) return;
    const [a, b, cc, d] = [px.x - p0.x, pz.x - p0.x, px.y - p0.y, pz.y - p0.y];
    const det = a * d - b * cc;
    const [ex, ey] = [t[0] - p0.x, t[1] - p0.y];
    if (Math.hypot(ex, ey) < 3 || !det) return;
    // Moving the focus by (dx, dz) moves every point by -J·(dx, dz).
    const dx = -(d * ex - b * ey) / det;
    const dz = -(-cc * ex + a * ey) / det;
    await story(page, 'focus', c[0] - 0.5 + dx, c[2] - 0.5 + dz);
    await page.waitForTimeout(500);
  }
}

async function storyJob() {
  const { ctx, page } = await open({ seed: 'empty' });
  await page.goto(`${base}/story`, { timeout: 180000 });
  await page.waitForFunction(() => window.__story?.ready(), null, { timeout: 180000 });

  // Found a colony of six.
  const count = page.getByLabel('Number of pawns');
  while (Number(await count.textContent()) !== 6)
    await page.getByRole('button', { name: Number(await count.textContent()) < 6 ? 'More pawns' : 'Fewer pawns' }).click();
  await page.getByRole('button', { name: 'Found colony' }).click();
  await page.getByTestId('colonist-bar').locator('[data-kind="pawn"]').nth(5).waitFor({ timeout: 60000 });
  await page.waitForTimeout(1500);
  const list = await page.evaluate(async () => (await fetch('/api/story')).json());
  const col = `/api/story/colonies/${list.colonies[0].id}`;
  const lv = await story(page, 'level');
  const ground = async (x, z) => {
    const ys = Array.from({ length: 60 }, (_, i) => 100 - i);
    const ids = await story(page, 'blocks', ys.map((y) => [x, y, z]));
    const i = ids.findIndex((b) => b > 0 && ![18, 31, 37, 38, 175].includes(b));
    return i < 0 ? lv - 1 : ys[i];
  };

  // North of spawn (the mock's player stands south of it): a quarry on the slope, a workshop on the hill, every tree marked.
  const [QX, QZ] = [2, -24];
  const [HX, HZ] = [14, -20];
  const qg = await ground(QX + 3, QZ + 3);
  const hg = Math.min(await ground(HX, HZ), await ground(HX + 8, HZ), await ground(HX, HZ + 5), await ground(HX + 8, HZ + 5));
  await api(page, `${col}/zones`, { op: 'add', kind: 'mine', mode: 'quarry', area: [QX, qg, QZ, QX + 6, qg, QZ + 6], name: 'Quarry' });
  await api(page, `${col}/designations`, { op: 'add', kind: 'chop', area: [-40, 50, -40, 40, 90, 40] });
  await api(page, `${col}/builds`, {
    op: 'add',
    items: [
      { kind: 'wall', area: [HX, hg + 1, HZ, HX + 8, hg + 1, HZ + 5], material: 'cobblestone', height: 4, name: 'Workshop' },
      { kind: 'door', blocks: [[HX + 4, hg + 1, HZ]] },
      { kind: 'roof', area: [HX, hg + 5, HZ, HX + 8, hg + 5, HZ + 5], roof: { style: 'gable', material: 'planks' } },
    ],
  });
  // The mock leaves a quarry's own digging to the bot, so its top two layers are marked to mine as well.
  const digs = [];
  for (let x = QX; x <= QX + 6; x++)
    for (let z = QZ; z <= QZ + 6; z++) {
      const g = await ground(x, z);
      digs.push([x, g, z], [x, g - 1, z]);
    }
  await api(page, `${col}/designations`, { op: 'add', kind: 'mine', blocks: digs });
  await api(page, `${col}/priorities`, { pawns: ['maid_s1_dara', 'maid_s1_ivo'], work: { mine: 1, build: 0 } });
  await api(page, `${col}/storage`, { op: 'assign_area', area: [1, 50, -5, 7, 90, -3], priority: 'preferred' });
  await page.waitForTimeout(Number(process.env.STORY_WAIT ?? 45000));

  const mid = [(QX + 3 + HX + 4) / 2 + 0.5, (qg + hg) / 2 + 1, (QZ + 3 + HZ + 2.5) / 2 + 0.5];
  // Top-down.
  await story(page, 'setZoom', 24);
  await centre(page, mid, [900, 470]);
  await page.waitForTimeout(1200);
  await shot(page, 'controller');
  // Angled (the hero).
  await page.keyboard.press('v');
  await page.waitForTimeout(1500);
  await story(page, 'setZoom', 24);
  await centre(page, mid, [880, 500]);
  await page.waitForTimeout(2500);
  await shot(page, 'hero');
  // Free cam from there.
  await page.keyboard.press('Shift+V');
  await page.waitForTimeout(3500);
  await shot(page, 'freecam');
  await page.keyboard.press('v');
  await page.waitForTimeout(1500);

  // The pawn inspector and its Skin Browser.
  await story(page, 'setZoom', 24);
  await centre(page, mid, [900, 470]);
  await page.getByTestId('colonist-bar').locator('[data-portrait="maid_s1_dara"]').click();
  const inspector = page.getByTestId('inspector');
  await inspector.getByRole('tab', { name: 'Orders' }).waitFor();
  await page.waitForTimeout(1500);
  await shot(page, 'pawn-inspector');
  await inspector.getByRole('tab', { name: 'Skin Browser' }).click();
  const skins = inspector.getByTestId('skin-browser');
  await skins.waitFor();
  for (const [name, look] of DEMO_SKINS) {
    await skins.getByRole('button', { name: 'Upload' }).click();
    await skins.getByLabel('Skin file').setInputFiles({ name: `${name}.png`, mimeType: 'image/png', buffer: demoSkin(look) });
    await skins.getByRole('button', { name: 'Upload and apply' }).click();
    await skins.getByRole('button', { name: `Skin ${name}` }).waitFor();
  }
  await skins.getByRole('button', { name: 'Library' }).click().catch(() => undefined);
  await page.waitForTimeout(3000);
  await shot(page, 'skin-browser');
  await page.keyboard.press('Escape');

  // A storage chest: its filters next to its contents.
  await story(page, 'setZoom', 20);
  await story(page, 'focus', 4, -2);
  await page.waitForTimeout(1500);
  const chest = await story(page, 'toPage', 2.5, lv + 1, -3.5);
  await page.mouse.click(chest.x, chest.y);
  await page.getByTestId('storage-inspector').waitFor();
  await page.waitForTimeout(1500);
  await shot(page, 'storage');
  await ctx.close();
}

// ------------------------------------------------------------------ the Live hive

async function hiveJob() {
  const { ctx, page } = await open();
  await page.goto(`${base}/live`, { timeout: 180000 });
  await page.waitForTimeout(12000);
  await shot(page, 'live-hive');
  await ctx.close();
}

// ------------------------------------------------------------------ Design Studio: redstone

async function openStudio(page, kind) {
  await page.goto(`${base}/designs/studio`, { timeout: 180000 });
  await page.waitForURL('**/designs/studio?session=**', { timeout: 180000 });
  await page.getByTestId('studio-stage').locator('canvas').first().waitFor({ timeout: 120000 });
  await page.getByTestId('kind-select').selectOption(kind);
  await page.waitForTimeout(3000);
}

async function redstoneJob() {
  const { ctx, page } = await open();
  await openStudio(page, 'redstone');
  await page.getByRole('tab', { name: 'Library' }).click();
  const lib = page.getByTestId('redstone-library');
  for (const [name, at] of [
    ['Five-torch clock', [10, 0, 10]],
    ['T flip-flop', [18, 0, 10]],
    ['Full adder', [10, 0, 18]],
    ['RS latch', [22, 0, 18]],
  ]) {
    await lib.getByRole('region', { name: 'Proven gates' }).getByRole('button', { name, exact: true }).click();
    await lib.getByLabel('Place at x y z').fill(at.join(' '));
    await lib.getByRole('button', { name: 'Place at', exact: true }).click();
    await page.waitForTimeout(700);
  }
  await page.getByRole('tab', { name: 'Simulate' }).click();
  await page.getByRole('button', { name: 'Simulate', exact: true }).first().click();
  await page.waitForTimeout(1000);
  const focus = async (x, z, d) => {
    await page.evaluate(([a, b, c]) => window.__studio.engine().focus(a, 64, b, { distance: c }), [x, z, d]);
    await page.waitForTimeout(2500);
  };
  // Probes need the mouse locked (the first click), then Shift+F9 on the cell under the crosshair.
  await focus(17, 27, 24);
  const c = await page.evaluate(() => {
    const r = window.__studio.engine().surface.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  });
  await page.mouse.click(c.x, c.y);
  await page.waitForTimeout(800);
  for (const [x, z] of [[13, 11], [16, 15], [17, 27]]) {
    await focus(x, z, 24);
    await page.keyboard.press('Shift+F9');
    await page.waitForTimeout(500);
  }
  await page.evaluate(() => document.exitPointerLock());
  await page.getByRole('tab', { name: 'Probes' }).click();
  await focus(17, 19, 26);
  await page.waitForTimeout(5000);
  await shot(page, 'redstone');
  await ctx.close();
}

// ------------------------------------------------------------------ Design Studio: mine

async function mineJob() {
  const { ctx, page } = await open();
  await openStudio(page, 'mine');
  await page.getByRole('button', { name: 'Use' }).first().click(); // the b1.8.1 diamond strip mine at y 11
  await page.waitForTimeout(4000);
  await page.getByRole('button', { name: /^\s*diamond/i }).first().click(); // work at y 11: the view cuts away above it
  await page.waitForTimeout(2500);
  await page.getByRole('button', { name: 'Frame all' }).click();
  await page.waitForTimeout(3000);
  await page.getByRole('button', { name: 'Deselect' }).click();
  await page.waitForTimeout(800);
  await page.mouse.move(770, 690);
  await page.mouse.wheel(0, -120);
  await page.waitForTimeout(3000);
  await shot(page, 'mine');
  await ctx.close();
}

// ------------------------------------------------------------------ hand control (pawn takeover)

async function takeoverJob() {
  const { ctx, page } = await open({ labels: false });
  await page.goto(`${base}/live/maid_chan_70?pane=world`, { timeout: 180000 });
  const link = page.getByRole('link', { name: 'Take control of pawn 70' });
  await link.waitFor({ timeout: 120000 });
  await link.click();
  await page.waitForURL('**/control?**');
  await page.waitForTimeout(6000);
  await page.mouse.click(800, 450); // locks the mouse
  await page.waitForTimeout(800);
  for (let k = 1; k <= 20; k++) await page.mouse.move(800 + 35 * k, 450 - 4.5 * k); // turn toward the sea
  await page.waitForTimeout(3000);
  await shot(page, 'takeover');
  await ctx.close();
}

const jobs = { story: storyJob, hive: hiveJob, redstone: redstoneJob, mine: mineJob, takeover: takeoverJob };
for (const [name, job] of Object.entries(jobs)) if (!only || only === name) await job();
await browser.close();
