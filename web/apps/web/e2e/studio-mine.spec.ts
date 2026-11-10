import { expect, test, type Page } from '@playwright/test';

// The Design Studio's mine designer in mock mode: a new design switched to 1.12.2 and to Mine (generated terrain, not flat),
// a version preset inserted as two components, the two combined in the outliner into one custom component, a save, a reload
// (still one custom component, clean), the design opened again in a new session (the same), then a combine after that save and
// a reload without saving (still combined, "Unsaved changes" shown). MINE_PERF=1 also measures frame rates on software WebGL
// (the projects run with the GPU off). SHOTS_DIR=<dir> saves screenshots.
const shots = process.env.SHOTS_DIR;
const PRESET = 'preset-1.12.2-diamond-y11';
const SECOND_PRESET = 'preset-1.12.2-lapis-iron-y16';

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

type Hook = {
  studio: {
    id: number;
    designId: number | null;
    canvas: { dug: Set<number>; size: number };
    terrain: { seed: string } | null;
  };
  world: { terrain: { surfaceRange(): { min: number; max: number } } | null };
  overlay: { xrayCount: number };
  engine: () => {
    stats(): { fps: number; columns: number; sections: number; triangles: number; frameMs: number };
    rig: { flyPos: { set(x: number, y: number, z: number): void }; flyYaw: number; flyPitch: number };
  } | null;
};

/** Runs `f` on the studio's mock-mode hook (a string expression: evaluated by the browser's debugger, never `eval` in the page). */
const hook = <T>(page: Page, f: (h: Hook) => T): Promise<T> => page.evaluate<T>(`(${f.toString()})(window.__studio)`);

const components = (page: Page) => page.getByRole('listbox', { name: 'Mine components' }).getByRole('option');
const customComponents = (page: Page) =>
  page.getByRole('listbox', { name: 'Mine components' }).getByRole('option', { name: /^Custom component \d+$/ });

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

/** Inserts a version preset from the side panel's (collapsed) preset list. */
async function usePreset(page: Page, testId: string) {
  const button = page.getByTestId(testId);
  if (!(await button.isVisible())) await page.getByText('Version presets · 1.12.2').click();
  await button.click();
}

/** Combines the outliner rows whose position (1-based) is in `rows`, and only those. */
async function combine(page: Page, rows: number[]) {
  await page.getByRole('button', { name: 'Combine components' }).click();
  const dialog = page.getByRole('region', { name: 'Combine mine components' });
  const boxes = dialog.getByRole('checkbox');
  const n = await boxes.count();
  for (let i = 0; i < n; i++) {
    if (rows.includes(i + 1)) await boxes.nth(i).check();
    else await boxes.nth(i).uncheck();
  }
  await dialog.getByRole('button', { name: `Combine ${rows.length} selected` }).click();
}

/** Waits until the bot holds every edit (the header's sync chip). */
async function synced(page: Page) {
  await expect(page.getByTestId('sync-chip')).toHaveAttribute('data-sync', 'idle');
  await expect(page.getByTestId('sync-chip')).toHaveText('Synced');
}

test('mine designer: a combined component survives save, reload, a new session and an unsaved reload', async ({
  page,
}) => {
  test.setTimeout(240_000);
  await openMine(page);
  // Generated terrain, not the flat grassland: rolling ground, the columns loaded and meshed.
  const range = await hook(page, (h) => h.world.terrain!.surfaceRange());
  expect(range.max - range.min).toBeGreaterThanOrEqual(4);
  await expect
    .poll(() => hook(page, (h) => h.engine()?.stats().columns ?? 0), { timeout: 30_000 })
    .toBeGreaterThanOrEqual(16);
  const seed = await page.getByTestId('mine-seed').inputValue();
  expect(seed.length).toBeGreaterThan(0);

  // A preset: two editable components (its shaft and its branches), dug into the terrain.
  await usePreset(page, PRESET);
  await expect(components(page)).toHaveCount(2);
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBeGreaterThan(100);
  await expect(page.getByTestId('mine-stats')).toContainText('blocks to mine');
  await expect(page.getByTestId('unsaved-chip')).toBeVisible();

  // Combined in the outliner: one custom component, the excavation unchanged.
  const dug = await hook(page, (h) => h.studio.canvas.dug.size);
  await combine(page, [1, 2]);
  await expect(components(page)).toHaveCount(1);
  await expect(customComponents(page)).toHaveCount(1);
  expect(await hook(page, (h) => h.studio.canvas.dug.size)).toBe(dug);
  if (shots) await page.screenshot({ path: `${shots}/studio-mine-combined.png` });

  // Save (Ctrl+S from the name field): the saved bar, the range, a clean page and a synced canvas.
  await page.getByLabel('Design name').fill('deep diamond mine');
  await page.getByLabel('Design name').press('Control+s');
  await expect(page.getByTestId('saved-bar')).toBeVisible();
  await expect(page.getByTestId('mine-range')).toContainText('anchor absolute');
  await expect(page.getByTestId('unsaved-chip')).toHaveCount(0);
  await expect(page.getByTestId('saved-bar')).not.toContainText('edited since');
  await expect(page.getByTestId('studio-alert')).toHaveCount(0);
  await synced(page);
  const designId = await hook(page, (h) => h.studio.designId);
  expect(designId).not.toBeNull();
  const firstSession = await hook(page, (h) => h.studio.id);

  // A reload reopens the session: still a 1.12.2 mine on the same seed, its digs kept, one custom component, nothing unsaved.
  await page.reload();
  await expect(page.getByTestId('mine-panel')).toBeVisible();
  await expect(page.getByTestId('kind-select')).toHaveValue('mine');
  await expect(page.getByTestId('version-select')).toHaveValue('1.12.2');
  await expect(page.getByTestId('mine-seed')).toHaveValue(seed);
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBe(dug);
  await expect(components(page)).toHaveCount(1);
  await expect(customComponents(page)).toHaveCount(1);
  await expect(page.getByTestId('unsaved-chip')).toHaveCount(0);

  // "Open in studio" for the design: a new session from its newest revision, with the same component.
  await page.goto(`/designs/studio?id=${designId}`);
  await expect(page.getByTestId('mine-panel')).toBeVisible();
  // The hook runs in the page: it can name nothing from here (a new document has no hook until the studio mounts).
  await expect.poll(() => hook(page, (h) => h?.studio?.id ?? 0)).toBeGreaterThan(firstSession);
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBe(dug);
  await expect(components(page)).toHaveCount(1);
  await expect(customComponents(page)).toHaveCount(1);
  await expect(page.getByTestId('unsaved-chip')).toHaveCount(0);

  // After that save: a second preset, its two pieces combined, and a reload without saving.
  await usePreset(page, SECOND_PRESET);
  await expect(components(page)).toHaveCount(3);
  await combine(page, [2, 3]);
  await expect(components(page)).toHaveCount(2);
  await expect(customComponents(page)).toHaveCount(2);
  await expect(page.getByTestId('unsaved-chip')).toBeVisible();
  await synced(page);
  const dugNow = await hook(page, (h) => h.studio.canvas.dug.size);
  // The page asks before a reload that would leave unsaved changes: leave.
  page.on('dialog', (d) => void d.accept());
  await page.reload();
  await expect(page.getByTestId('mine-panel')).toBeVisible();
  await expect.poll(() => hook(page, (h) => h.studio.canvas.dug.size)).toBe(dugNow);
  await expect(customComponents(page)).toHaveCount(2);
  await expect(page.getByTestId('unsaved-chip')).toBeVisible();
  await expect(page.getByTestId('studio-alert')).toContainText('Recovered unsaved component changes from this browser');
  await expect(page.getByTestId('saved-bar')).toContainText('edited since');
  if (shots) await page.screenshot({ path: `${shots}/studio-mine-recovered.png` });
});

test('mine designer frame rate on software WebGL', async ({ page }) => {
  test.skip(!process.env.MINE_PERF, 'set MINE_PERF=1 to measure');
  test.setTimeout(240_000);
  await openMine(page);
  await usePreset(page, PRESET);
  await expect(components(page)).toHaveCount(2);
  await expect
    .poll(() => hook(page, (h) => h.engine()?.stats().columns ?? 0), { timeout: 30_000 })
    .toBeGreaterThanOrEqual(16);
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
  console.warn(
    `MINE_PERF ${JSON.stringify({ fps: Math.round(fps * 10) / 10, p95FrameMs: Math.round(r.p95 * 10) / 10, stats: r.stats })}`,
  );
  expect(fps).toBeGreaterThanOrEqual(40);
});
