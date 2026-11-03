import { expect, test, type Page } from '@playwright/test';

// Memory activity → Cloud (mock mode): the RAPTOR tree page is gone, the cloud draws, hovers, opens detail, lassos and follows
// the hive filter. CLOUD_PERF=1 also measures frame rates on software WebGL (the projects run with the GPU off) and prints them.
// SHOTS_DIR=<dir> saves screenshots.
const shots = process.env.SHOTS_DIR;

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

interface CloudXY {
  ids: number[];
  xy: number[];
}

/** Screen position of point `i` at the fitted view (the canvas fits the unit square into 94 % of its short side, centred). */
async function pointOnScreen(page: Page, cloud: CloudXY, i: number): Promise<[number, number]> {
  const box = (await page.getByRole('application', { name: 'Memory cloud' }).boundingBox())!;
  const base = Math.min(box.width, box.height) * 0.94;
  return [box.x + (box.width - base) / 2 + cloud.xy[2 * i]! * base, box.y + (box.height - base) / 2 + cloud.xy[2 * i + 1]! * base];
}

async function openCloud(page: Page): Promise<CloudXY> {
  await page.goto('/memory-activity?layout=cloud');
  const app = page.getByRole('application', { name: 'Memory cloud' });
  await expect(app).toBeVisible();
  await expect(app).toHaveAttribute('data-points', /^[0-9]{4,}$/);
  const cloud = await page.evaluate(async () => {
    const r = await fetch('/api/memory-activity/cloud');
    const j = (await r.json()) as { points: { id: number[]; xy: number[] } };
    return { ids: j.points.id, xy: j.points.xy };
  });
  await page.waitForTimeout(600); // first frames
  return cloud;
}

test('the RAPTOR tree page redirects into Memory activity, keeping the node', async ({ page }) => {
  await signIn(page);
  await page.goto('/raptor?view=tree&node=481');
  await page.waitForURL(/\/memory-activity\?.*node=481/);
  await expect(page.getByRole('link', { name: 'RAPTOR tree' })).toHaveCount(0);
  await expect(page.locator('aside').getByText('#481')).toBeVisible();
});

test('cloud: hover shows a tooltip, click opens detail, the hive filter changes brightness, lasso lists memories', async ({ page }) => {
  await signIn(page);
  const cloud = await openCloud(page);
  if (shots) await page.screenshot({ path: `${shots}/memory-cloud.png` });

  // hover the point that sits alone the most is overkill: any point gives a tooltip (the nearest one within 9 px)
  const i = Math.floor(cloud.ids.length / 3);
  const [x, y] = await pointOnScreen(page, cloud, i);
  await page.mouse.move(x, y);
  const tip = page.getByRole('tooltip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText(/used .* ago|not used in this window/);

  // click: the shared detail panel, and the node lands in the URL
  await page.mouse.click(x, y);
  await page.waitForURL(/node=\d+/);
  const id = Number(new URL(page.url()).searchParams.get('node'));
  await expect(page.locator('aside').getByText(`#${id}`)).toBeVisible();

  // brightness follows the hive filter: every brain lights more than one brain alone (or a different set)
  const lit = page.getByTestId('memory-cloud');
  await expect.poll(async () => Number(await lit.getAttribute('data-lit')), { timeout: 30_000 }).toBeGreaterThan(0);
  const all = Number(await lit.getAttribute('data-lit'));
  const who = page.locator('#who');
  const brains = await who.locator('optgroup[label="Hive brains"] option').evaluateAll((os) => os.map((o) => (o as HTMLOptionElement).value));
  expect(brains.length).toBeGreaterThan(1);
  await who.selectOption(brains[brains.length - 1]!);
  await page.waitForURL(/who=brain/);
  await expect.poll(async () => Number(await lit.getAttribute('data-lit'))).not.toBe(all);
  await who.selectOption('all');

  // Shift-drag lasso around the clicked point
  await page.keyboard.down('Shift');
  await page.mouse.move(x - 40, y - 40);
  await page.mouse.down();
  for (const [dx, dy] of [
    [40, -40],
    [40, 40],
    [-40, 40],
    [-40, -40],
  ] as const)
    await page.mouse.move(x + dx, y + dy, { steps: 4 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  const sel = page.getByRole('region', { name: 'Lasso selection' });
  await expect(sel).toBeVisible();
  await expect(sel).toContainText(/\d+ memories selected/);

  // legend toggle hides a cluster (fewer visible after it), search counts matches
  const first = page.locator('button[aria-pressed]').first();
  await first.click();
  await expect(first).toHaveAttribute('aria-pressed', 'false');
  await first.click();
  await page.getByLabel('Find memories in the cloud').fill('cobble');
  await expect(page.getByText(/\d+ match/)).toBeVisible();
});

test('cloud frame rate on software WebGL', async ({ page }) => {
  test.skip(!process.env.CLOUD_PERF, 'set CLOUD_PERF=1 to measure');
  test.setTimeout(240_000);
  await signIn(page);
  const out: Record<string, unknown> = {};
  for (const extra of [2520, 19_520]) {
    await page.evaluate((n) => localStorage.setItem('mcx-mock-cloud-extra', String(n)), extra);
    await page.reload();
    await openCloud(page);
    const box = (await page.getByRole('application', { name: 'Memory cloud' }).boundingBox())!;
    const run = async (label: string, links: boolean) => {
      if (links) await page.getByText('Links').click();
      await page.evaluate(() => {
        const w = window as unknown as { __frames: number[]; __mineclawCloudStats: { frames: number; drawMs: number } };
        w.__frames = [];
        w.__mineclawCloudStats.frames = 0;
        w.__mineclawCloudStats.drawMs = 0;
        const tick = (t: number) => {
          w.__frames.push(t);
          if (w.__frames.length < 5000) requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      });
      const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
      await page.mouse.move(cx, cy);
      await page.mouse.down();
      const start = Date.now();
      let k = 0;
      while (Date.now() - start < 4000) {
        k++;
        await page.mouse.move(cx + Math.sin(k / 6) * 160, cy + Math.cos(k / 8) * 90);
        if (k % 10 === 0) await page.mouse.wheel(0, k % 20 === 0 ? -120 : 120);
      }
      await page.mouse.up();
      out[label] = await page.evaluate(() => {
        const w = window as unknown as { __frames: number[]; __mineclawCloudStats: { frames: number; drawMs: number; points: number } };
        const f = w.__frames;
        const gaps = f.slice(1).map((t, i) => t - f[i]!).sort((a, b) => a - b);
        const span = (f[f.length - 1]! - f[0]!) / 1000;
        const s = w.__mineclawCloudStats;
        return {
          points: s.points,
          fps: Math.round((f.length - 1) / span),
          frameP95: Math.round(gaps[Math.floor(gaps.length * 0.95)] ?? NaN),
          draws: s.frames,
          drawMsAvg: +(s.drawMs / Math.max(1, s.frames)).toFixed(2),
        };
      });
      if (links) await page.getByText('Links').click();
    };
    await run(`${extra + 480} points`, false);
    await run(`${extra + 480} points + links`, true);
  }
  await page.evaluate(() => localStorage.removeItem('mcx-mock-cloud-extra'));
  console.warn(`[cloud-perf] ${JSON.stringify(out, null, 1)}`);
  for (const v of Object.values(out)) expect((v as { fps: number }).fps).toBeGreaterThanOrEqual(30);
});
