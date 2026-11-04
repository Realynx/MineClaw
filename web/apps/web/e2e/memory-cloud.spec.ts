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

/**
 * Screen position of point `i` at the fitted view: the unit square in 94 % of the short side, centred in the room right of the
 * legend card (290 px on canvases ≥ 720 px wide), as CloudCanvas fits it.
 */
async function pointOnScreen(page: Page, cloud: CloudXY, i: number): Promise<[number, number]> {
  const box = (await page.getByRole('application', { name: 'Memory cloud' }).boundingBox())!;
  const left = box.width >= 720 ? 290 : 0;
  const base = Math.min(box.width - left, box.height) * 0.94;
  return [
    box.x + left + (box.width - left - base) / 2 + cloud.xy[2 * i]! * base,
    box.y + (box.height - base) / 2 + cloud.xy[2 * i + 1]! * base,
  ];
}

/** The point nearest (0.6, 0.6): well inside the canvas, away from the legend and the corner hints. */
function centralPoint(cloud: CloudXY): number {
  let best = 0;
  let bd = Infinity;
  for (let i = 0; i < cloud.ids.length; i++) {
    const d = (cloud.xy[2 * i]! - 0.6) ** 2 + (cloud.xy[2 * i + 1]! - 0.6) ** 2;
    if (d < bd) {
      bd = d;
      best = i;
    }
  }
  return best;
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
  await expect(page.locator('aside').getByText('#481', { exact: true })).toBeVisible();
});

test('cloud: hover shows a tooltip, click opens detail, the hive filter changes brightness, lasso lists memories', async ({
  page,
}) => {
  await signIn(page);
  const cloud = await openCloud(page);
  if (shots) await page.screenshot({ path: `${shots}/memory-cloud.png` });

  // any point gives a tooltip (the nearest one within 9 px)
  const i = centralPoint(cloud);
  const [x, y] = await pointOnScreen(page, cloud, i);
  await page.mouse.move(x, y);
  const tip = page.getByRole('tooltip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText(/used .* ago|not used in this window/);

  // click: the shared detail panel, and the node lands in the URL
  await page.mouse.click(x, y);
  await page.waitForURL(/node=\d+/);
  const id = Number(new URL(page.url()).searchParams.get('node'));
  await expect(page.locator('aside').getByText(`#${id}`, { exact: true })).toBeVisible();

  // brightness follows the hive filter: every brain lights more than one brain alone (or a different set)
  const lit = page.getByTestId('memory-cloud');
  await expect.poll(async () => Number(await lit.getAttribute('data-lit')), { timeout: 30_000 }).toBeGreaterThan(0);
  const all = Number(await lit.getAttribute('data-lit'));
  const who = page.locator('#who');
  const brains = await who
    .locator('optgroup[label="Hive brains"] option')
    .evaluateAll((os) => os.map((o) => (o as HTMLOptionElement).value));
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
  const first = page.getByTestId('memory-cloud').locator('li button[aria-pressed]').first();
  await first.click();
  await expect(first).toHaveAttribute('aria-pressed', 'false');
  await first.click();
  await page.getByLabel('Find memories in the cloud').fill('cobble');
  await expect(page.getByText(/\d+ match/)).toBeVisible();
});

/** Screen position of point `i` of `xy` in pane `pane` of `count` side-by-side panes (the canvas's paneRects / paneFrame). */
async function pointInPane(page: Page, xy: number[], i: number, pane: number, count: number): Promise<[number, number]> {
  const box = (await page.getByRole('application', { name: 'Memory cloud' }).boundingBox())!;
  const left = box.width >= 720 ? 290 : 0;
  const roomW = box.width - left;
  // Two panes sit side by side when the room is wider than tall, else stacked.
  const across = count === 1 || roomW >= box.height;
  const pw = count === 1 || !across ? roomW : (roomW - 8) / 2;
  const ph = count === 1 || across ? box.height : (box.height - 8) / 2;
  const px = left + (across ? pane * (pw + 8) : 0);
  const py = across ? 0 : pane * (ph + 8);
  const base = Math.min(pw, ph) * 0.94;
  return [box.x + px + (pw - base) / 2 + xy[2 * i]! * base, box.y + py + (ph - base) / 2 + xy[2 * i + 1]! * base];
}

/** Opaque pixels of the overlay canvas (rings, labels) in a box around a page position. */
async function overlayInk(page: Page, x: number, y: number, r = 12): Promise<number> {
  return page.evaluate(
    ({ x, y, r }) => {
      const app = document.querySelector('[aria-roledescription="memory cloud"]')!;
      const c = app.querySelectorAll('canvas')[2] as HTMLCanvasElement;
      const b = c.getBoundingClientRect();
      const dpr = c.width / b.width;
      const d = c
        .getContext('2d')!
        .getImageData(Math.round((x - b.left - r) * dpr), Math.round((y - b.top - r) * dpr), Math.round(2 * r * dpr), Math.round(2 * r * dpr)).data;
      let n = 0;
      for (let k = 3; k < d.length; k += 4) if (d[k]! > 40) n++;
      return n;
    },
    { x, y, r },
  );
}

test('cloud: distances by default with fidelity and a scale bar; every arrangement; compare links hover across panes', async ({
  page,
}) => {
  await signIn(page);
  const cloud = await openCloud(page);
  const app = page.getByRole('application', { name: 'Memory cloud' });

  // Default: Distances (MDS), its fidelity badge, a scale bar in cosine distance, links on.
  await expect(page.getByRole('radio', { name: 'Distances' })).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('cloud-fidelity')).toHaveText(/Distances: true to embeddings \(r = 0\.\d\d, stress 0\.\d\d\)/);
  await expect(app).toHaveAttribute('data-scale0', /cosine distance\|\d/);
  await expect(page.getByRole('switch', { name: 'Links' })).toHaveAttribute('aria-checked', 'true');
  if (shots) await page.screenshot({ path: `${shots}/memory-cloud-distances.png` });

  // Hovering a memory while another is selected shows their embedding distance and the drawn one.
  const i = centralPoint(cloud);
  const j = (i + 7) % cloud.ids.length;
  const [sx, sy] = await pointOnScreen(page, cloud, i);
  await page.mouse.click(sx, sy);
  await page.waitForURL(/node=\d+/);
  const selected = Number(new URL(page.url()).searchParams.get('node'));
  const si = cloud.ids.indexOf(selected);
  const other = si === j ? (j + 1) % cloud.ids.length : j;
  const [ox, oy] = await pointOnScreen(page, cloud, other);
  await page.mouse.move(ox, oy);
  await expect(page.getByTestId('cloud-distance')).toContainText(/embedding distance \d\.\d\d · drawn \d\.\d\d/);
  await page.keyboard.press('Escape');

  // Each arrangement: the URL keeps it, the badge follows; UMAP and the tree draw no scale bar.
  for (const [name, id, scale] of [
    ['Neighbourhoods', 'umap', false],
    ['PCA', 'pca', true],
    ['Tree', 'tree', false],
    ['Distances', null, true],
  ] as const) {
    await page.getByRole('radio', { name }).click();
    if (id) await page.waitForURL(new RegExp(`arr=${id}`));
    else await expect.poll(() => new URL(page.url()).searchParams.get('arr')).toBeNull();
    await expect(page.getByRole('radio', { name })).toHaveAttribute('aria-checked', 'true');
    await expect(page.getByTestId('cloud-fidelity')).toBeVisible();
    await expect(app).toHaveAttribute('data-scale0', scale ? /cosine distance/ : '');
  }

  // Compare: two panes in one canvas; hovering a memory in the first rings it in the second too.
  await page.getByRole('button', { name: 'Compare' }).click();
  await page.waitForURL(/cmp=mds%2Cumap|cmp=mds,umap/);
  await expect(app).toHaveAttribute('data-panes', '2');
  await expect(page.getByTestId('cloud-pane-title')).toHaveCount(2);
  await expect(page.getByTestId('cloud-pane-title').nth(1)).toContainText('Neighbourhoods (UMAP)');
  const umap = await page.evaluate(async () => {
    const r = await fetch('/api/memory-activity/cloud?arrangement=umap');
    return ((await r.json()) as { points: { xy: number[] } }).points.xy;
  });
  await page.waitForTimeout(900); // the switch animation settles
  const [ax, ay] = await pointInPane(page, cloud.xy, i, 0, 2);
  const [bx, by] = await pointInPane(page, umap, i, 1, 2);
  await page.mouse.move(ax + 300, ay + 300);
  await page.mouse.move(10, 10);
  await page.waitForTimeout(200);
  const before = await overlayInk(page, bx, by);
  await page.mouse.move(ax, ay);
  await expect(page.getByRole('tooltip')).toBeVisible();
  await expect.poll(() => overlayInk(page, bx, by)).toBeGreaterThan(before + 10);
  if (shots) await page.screenshot({ path: `${shots}/memory-cloud-compare.png` });

  // Back to one pane.
  await page.getByRole('button', { name: 'Compare' }).click();
  await expect(app).toHaveAttribute('data-panes', '1');
  await expect.poll(() => new URL(page.url()).searchParams.get('cmp')).toBeNull();
});

test('cloud frame rate on software WebGL', async ({ page }) => {
  test.skip(!process.env.CLOUD_PERF, 'set CLOUD_PERF=1 to measure');
  test.setTimeout(240_000);
  await signIn(page);
  const out: Record<string, unknown> = {};
  const setLinks = async (on: boolean) => {
    const sw = page.getByRole('switch', { name: 'Links' });
    if ((await sw.getAttribute('aria-checked')) !== String(on)) await sw.click();
    await expect(sw).toHaveAttribute('aria-checked', String(on));
  };
  for (const extra of [2520, 19_520]) {
    await page.evaluate((n) => localStorage.setItem('mcx-mock-cloud-extra', String(n)), extra);
    await page.goto('/memory-activity?layout=cloud');
    await openCloud(page);
    const box = (await page.getByRole('application', { name: 'Memory cloud' }).boundingBox())!;
    const run = async (label: string, links: boolean, compare?: string) => {
      if (compare) {
        await page.goto(`/memory-activity?layout=cloud&cmp=${compare}`);
        await expect(page.getByRole('application', { name: 'Memory cloud' })).toHaveAttribute(
          'data-panes',
          String(compare.split(',').length),
        );
        await page.waitForTimeout(1500);
      }
      await setLinks(links);
      await page.evaluate(() => {
        const w = window as unknown as {
          __frames: number[];
          __run: number;
          __mineclawCloudStats: { frames: number; drawMs: number };
        };
        const run = (w.__run = (w.__run ?? 0) + 1);
        w.__frames = [];
        w.__mineclawCloudStats.frames = 0;
        w.__mineclawCloudStats.drawMs = 0;
        const tick = (t: number) => {
          if (w.__run !== run) return; // an earlier measurement's loop stops here
          w.__frames.push(t);
          if (w.__frames.length < 5000) requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      });
      const cx = box.x + box.width / 2,
        cy = box.y + box.height / 2;
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
        const w = window as unknown as {
          __frames: number[];
          __mineclawCloudStats: { frames: number; drawMs: number; points: number; renderer?: string };
        };
        const f = w.__frames;
        const gaps = f
          .slice(1)
          .map((t, i) => t - f[i]!)
          .sort((a, b) => a - b);
        const span = (f[f.length - 1]! - f[0]!) / 1000;
        const s = w.__mineclawCloudStats;
        return {
          renderer: s.renderer,
          points: s.points,
          fps: Math.round((f.length - 1) / span),
          frameP95: Math.round(gaps[Math.floor(gaps.length * 0.95)] ?? NaN),
          draws: s.frames,
          drawMsAvg: +(s.drawMs / Math.max(1, s.frames)).toFixed(2),
        };
      });
    };
    const pts = extra + 480;
    await run(`${pts} points + links (default)`, true);
    await run(`${pts} points, links off`, false);
    await run(`${pts} points + links, compare 2 panes`, true, 'mds,umap');
    if (extra < 10_000) await run(`${pts} points + links, compare 4 panes`, true, 'mds,umap,pca,tree');
    await page.goto('/memory-activity?layout=cloud');
    await setLinks(true);
  }
  await page.evaluate(() => {
    localStorage.removeItem('mcx-mock-cloud-extra');
    localStorage.removeItem('mcx-cloud-links');
  });
  console.warn(`[cloud-perf] ${JSON.stringify(out, null, 1)}`);
  // ≥ 40 fps at the live db's size with links on (one pane or compared); the 20k stress case keeps ≥ 30.
  for (const [k, v] of Object.entries(out))
    expect((v as { fps: number }).fps, k).toBeGreaterThanOrEqual(k.startsWith('3000') ? 40 : 30);
});
