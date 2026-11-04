import { expect, test, type Page } from '@playwright/test';

// The Design Studio's mine designer in mock mode: a new design switched to 1.12.2 and to Mine (generated terrain, not flat),
// a spiral shaft from the template panel (its entry the anchor), a strip mine at diamond's best level, the X-ray, a save, and a
// reload that keeps the kind, the anchor, the version and the seed. MINE_PERF=1 also measures frame rates on software WebGL
// (the projects run with the GPU off). SHOTS_DIR=<dir> saves screenshots.
const shots = process.env.SHOTS_DIR;

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

type Hook = {
  studio: { canvas: { dug: Set<number>; size: number }; anchor: [number, number, number] | null; terrain: { seed: string } | null };
  world: { terrain: { surfaceRange(): { min: number; max: number }; surfaceOver(a: number, b: number, c: number, d: number): number } | null };
  overlay: { xrayCount: number };
  engine: () => {
    stats(): { fps: number; columns: number; sections: number; triangles: number; frameMs: number };
    rig: { flyPos: { set(x: number, y: number, z: number): void }; flyYaw: number; flyPitch: number };
  } | null;
};

/** Runs `f` on the studio's mock-mode hook (a string expression: evaluated by the browser's debugger, never `eval` in the page). */
const hook = <T>(page: Page, f: (h: Hook) => T): Promise<T> =>
  page.evaluate<T>(`(${f.toString()})(window.__studio)`);

async function openMine(page: Page) {
  await signIn(page);
  await page.goto('/designs/studio');
  const stage = page.getByTestId('studio-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.getByTestId('version-select').selectOption('1.12.2');
  await expect(page.getByTestId('version-select')).toHaveValue('1.12.2');
  await page.getByTestId('kind-select').selectOption('mine');
  await expect(page.getByTestId('mine-panel')).toBeVisible();
  await expect(page.getByTestId('kind-select')).toHaveValue('mine');
}

test('mine designer: generated terrain, a spiral shaft, a strip mine at an ore band, save and reload', async ({ page }) => {
  test.setTimeout(180_000);
  await openMine(page);
  // Generated terrain, not the flat grassland: rolling ground, the columns loaded and meshed.
  const range = await hook(page, (h) => h.world.terrain!.surfaceRange());
  expect(range.max - range.min).toBeGreaterThanOrEqual(4);
  await expect.poll(() => hook(page, (h) => h.engine()?.stats().columns ?? 0), { timeout: 30_000 }).toBeGreaterThanOrEqual(16);
  await expect.poll(() => hook(page, (h) => h.engine()?.stats().sections ?? 0), { timeout: 30_000 }).toBeGreaterThan(16);
  const seed = await page.getByTestId('mine-seed').inputValue();
  expect(seed.length).toBeGreaterThan(0);
  if (shots) await page.screenshot({ path: `${shots}/studio-mine-terrain.png` });

  // A 4×4 spiral shaft from the ground down to y 11 (the default), at the plot's middle: its top is the anchor.
  await page.getByTestId('insert-spiral').click();
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBeGreaterThan(150);
  const top = await hook(page, (h) => h.world.terrain!.surfaceOver(30, 30, 33, 33));
  await expect.poll(() => hook(page, (h) => h.studio.anchor)).toEqual([30, top, 30]);

  // A strip mine at diamond's best level (the "at ore band" button sets the feet y and the view's level).
  const before = await hook(page, (h) => h.studio.canvas.dug.size);
  await page.getByTestId('mine-panel').getByTitle('Diamond ore: best at y 11').nth(1).click();
  await expect(page.getByTestId('level-readout')).toContainText('Diamond ore ★');
  await page.getByTestId('insert-strip').click();
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBeGreaterThan(before + 100);
  await expect(page.getByTestId('mine-stats')).toContainText('blocks to mine');
  // The X-ray shows the ores around the level.
  await page.getByRole('button', { name: 'X-ray' }).click();
  await expect.poll(() => hook(page, (h) => h.overlay.xrayCount)).toBeGreaterThan(0);
  if (shots) {
    await page.waitForTimeout(1500);
    await page.screenshot({ path: `${shots}/studio-mine-xray.png` });
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${shots}/studio-mine-1280.png` });
    await page.setViewportSize({ width: 1440, height: 900 });
  }

  // Save: the saved bar says the y range and the absolute anchor.
  await page.getByLabel('Design name').fill('deep diamond mine');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByTestId('saved-bar')).toBeVisible();
  await expect(page.getByTestId('mine-range')).toContainText('anchor absolute');
  await expect(page.getByTestId('mine-range')).toContainText('y 11..');
  const dug = await hook(page, (h) => h.studio.canvas.dug.size);

  // A reload reopens the session: still a 1.12.2 mine on the same seed, its digs and its anchor kept.
  await page.reload();
  await expect(page.getByTestId('mine-panel')).toBeVisible();
  await expect(page.getByTestId('kind-select')).toHaveValue('mine');
  await expect(page.getByTestId('version-select')).toHaveValue('1.12.2');
  await expect(page.getByTestId('mine-seed')).toHaveValue(seed);
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBe(dug);
  expect(await hook(page, (h) => h.studio.anchor)).toEqual([30, top, 30]);
});

test('mine designer frame rate on software WebGL', async ({ page }) => {
  test.skip(!process.env.MINE_PERF, 'set MINE_PERF=1 to measure');
  test.setTimeout(240_000);
  await openMine(page);
  await page.getByTestId('insert-spiral').click();
  await expect.poll(() => hook(page, (h) => h.engine()?.stats().columns ?? 0), { timeout: 30_000 }).toBeGreaterThanOrEqual(16);
  await page.waitForTimeout(6000); // every section meshed
  // Turn the camera every frame for 6 s over the plot (the worst case: the whole terrain in view), counting frames.
  const r = await page.evaluate(
    () =>
      new Promise<{ frames: number; p95: number; stats: unknown }>((done) => {
        const h = (window as unknown as { __studio: Hook }).__studio;
        const e = h.engine()!;
        e.rig.flyPos.set(32, 95, 80);
        e.rig.flyPitch = 25;
        const times: number[] = [];
        const t0 = performance.now();
        let last = t0;
        const step = (t: number) => {
          times.push(t - last);
          last = t;
          e.rig.flyYaw = (e.rig.flyYaw + 1.5) % 360;
          if (t - t0 < 6000) requestAnimationFrame(step);
          else {
            const s = [...times].sort((a, b) => a - b);
            done({ frames: times.length, p95: s[Math.floor(s.length * 0.95)]!, stats: e.stats() });
          }
        };
        requestAnimationFrame(step);
      }),
  );
  const fps = r.frames / 6;
  console.warn(`MINE_PERF ${JSON.stringify({ fps: Math.round(fps * 10) / 10, p95FrameMs: Math.round(r.p95 * 10) / 10, stats: r.stats })}`);
  expect(fps).toBeGreaterThanOrEqual(40);
});
