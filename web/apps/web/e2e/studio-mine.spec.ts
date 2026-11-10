import { expect, test, type Page } from '@playwright/test';

// The Design Studio's mine designer in mock mode: a new design switched to 1.12.2 and to Mine (generated terrain, not flat),
// a version preset inserted as two components, the two combined in the outliner into one custom component, a save, a reload
// (still one custom component, clean), the design opened again in a new session (the same), then a combine after that save and
// a reload without saving (still combined, "Unsaved changes" shown). MINE_PERF=1 also measures frame rates on software WebGL
// (the projects run with the GPU off). SHOTS_DIR=<dir> saves screenshots.
// Before the save, the combined component (the shared outliner's selected row) is dragged by its body with the mouse free:
// Esc puts it back, a release moves it (one undo step puts it back again), and an Alt-drag over empty space still orbits.
const shots = process.env.SHOTS_DIR;
/** The mine plot's width (DESIGNER_MINE_PLOT): a drag goes toward its middle, where the component has room. */
const MINE_PLOT = 256;
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

type Point = [number, number, number];
interface Vec {
  x: number;
  y: number;
  z: number;
  set(x: number, y: number, z: number): Vec;
  project(camera: Camera): Vec;
}
interface Camera {
  position: { clone(): Vec };
  updateMatrixWorld(): void;
}

type Hook = {
  studio: {
    id: number;
    designId: number | null;
    canvas: { dug: Set<number>; size: number; holes(): Point[] };
    terrain: { seed: string } | null;
  };
  world: { terrain: { surfaceRange(): { min: number; max: number } } | null };
  overlay: { xrayCount: number; gizmo: { visible: boolean; position: { x: number; y: number; z: number } } };
  engine: () => {
    stats(): { fps: number; columns: number; sections: number; triangles: number; frameMs: number };
    rig: {
      flyPos: { set(x: number, y: number, z: number): void };
      flyYaw: number;
      flyPitch: number;
      orbitYaw: number;
    };
    camera: Camera;
    surface: HTMLElement;
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

/** The only custom component's corner, from its outliner row ("Custom component · x, y, z. Double-click to frame."). */
async function customCorner(page: Page): Promise<Point> {
  const title = (await customComponents(page).getAttribute('title')) ?? '';
  const m = /(-?\d+), (-?\d+), (-?\d+)\. Double-click/.exec(title);
  expect(m, `a corner in "${title}"`).not.toBeNull();
  return [Number(m![1]), Number(m![2]), Number(m![3])];
}

/** Where canvas point `p` is drawn now, in client pixels (a mine's canvas y is world y). */
async function screenOf(page: Page, p: Point): Promise<{ x: number; y: number }> {
  return page.evaluate(([x, y, z]) => {
    const e = (window as unknown as { __studio: Hook }).__studio.engine()!;
    e.camera.updateMatrixWorld();
    const v = e.camera.position.clone().set(x, y, z).project(e.camera);
    const r = e.surface.getBoundingClientRect();
    return { x: r.left + ((v.x + 1) / 2) * r.width, y: r.top + ((1 - v.y) / 2) * r.height };
  }, p);
}

/**
 * A point on the component's body to press: the dug cell drawn nearest the middle of the view, with nothing over the view
 * there and well away from the selection's move handles (a press on those is the handles').
 */
async function bodyPoint(page: Page): Promise<{ x: number; y: number; cell: Point; minX: number; maxX: number }> {
  const found = await page.evaluate(() => {
    const h = (window as unknown as { __studio: Hook }).__studio;
    const e = h.engine()!;
    e.camera.updateMatrixWorld();
    const r = e.surface.getBoundingClientRect();
    const screen = (x: number, y: number, z: number) => {
      const v = e.camera.position.clone().set(x, y, z).project(e.camera);
      return { x: r.left + ((v.x + 1) / 2) * r.width, y: r.top + ((1 - v.y) / 2) * r.height, depth: v.z };
    };
    const g = h.overlay.gizmo;
    const handles = g.visible ? screen(g.position.x, g.position.y, g.position.z) : null;
    const mid = { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    let best: { x: number; y: number; cell: Point; d: number } | null = null;
    let minX = Infinity;
    let maxX = -Infinity;
    for (const [x, y, z] of h.studio.canvas.holes()) {
      minX = Math.min(minX, x);
      maxX = Math.max(maxX, x);
      const s = screen(x + 0.5, y + 0.5, z + 0.5);
      if (s.depth > 1 || Math.abs(s.x - mid.x) > r.width * 0.3 || Math.abs(s.y - mid.y) > r.height * 0.3) continue;
      if (handles && Math.hypot(s.x - handles.x, s.y - handles.y) < 160) continue;
      const d = Math.hypot(s.x - mid.x, s.y - mid.y);
      if ((best && d >= best.d) || document.elementFromPoint(s.x, s.y) !== e.surface) continue;
      best = { x: s.x, y: s.y, cell: [x, y, z], d };
    }
    return best && { x: best.x, y: best.y, cell: best.cell, minX, maxX };
  });
  expect(found, 'a dug cell of the component in the middle of the view').not.toBeNull();
  return found!;
}

/** The whole plan, as one string: the dug cells' count and coordinate sums (a move changes it, its undo restores it). */
const plan = (page: Page) =>
  hook(page, (h) => {
    let x = 0;
    let z = 0;
    for (const p of h.studio.canvas.holes()) {
      x += p[0];
      z += p[2];
    }
    return `${h.studio.canvas.dug.size}:${x}:${z}`;
  });

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

  // The shared outliner selects the new component; framed, it is dragged by its body with the mouse free (a mine never
  // locks it). Only its outline follows the mouse: Esc puts it back with nothing changed.
  const selected = page.getByRole('listbox', { name: 'Mine components' }).getByRole('option', { selected: true });
  await expect(selected).toHaveCount(1);
  await expect(selected).toHaveAccessibleName(/^Custom component \d+$/);
  const home = await customCorner(page);
  const before = await plan(page);
  await page.getByRole('button', { name: 'Frame selected component' }).click();
  await page.waitForTimeout(1500); // the camera eases onto it
  const body = await bodyPoint(page);
  // Four cells along x on the component's own level, toward the side of the plot with more room.
  const east = MINE_PLOT - 1 - body.maxX >= body.minX ? 1 : -1;
  const a = await screenOf(page, [body.cell[0] + 0.5, home[1], body.cell[2] + 0.5]);
  const b = await screenOf(page, [body.cell[0] + 0.5 + 4 * east, home[1], body.cell[2] + 0.5]);
  const to = { x: body.x + b.x - a.x, y: body.y + b.y - a.y };
  expect(Math.hypot(to.x - body.x, to.y - body.y)).toBeGreaterThan(10);
  await page.mouse.move(body.x, body.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 8 });
  expect(await page.evaluate(() => !!document.pointerLockElement)).toBe(false);
  await page.keyboard.press('Escape');
  await page.mouse.up();
  expect(await customCorner(page)).toEqual(home);
  expect(await plan(page)).toBe(before);

  // The same drag released: one move of the whole component across its level; one undo step puts it back.
  await page.mouse.move(body.x, body.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => customCorner(page)).not.toEqual(home);
  const moved = await customCorner(page);
  expect(moved[1]).toBe(home[1]);
  expect(Math.sign(moved[0] - home[0])).toBe(east);
  await expect(customComponents(page)).toHaveAttribute('aria-selected', 'true');
  expect(await plan(page)).not.toBe(before);
  if (shots) await page.screenshot({ path: `${shots}/studio-mine-dragged.png` });
  await page.keyboard.press('Control+z');
  await expect.poll(() => customCorner(page)).toEqual(home);
  await expect.poll(() => plan(page)).toBe(before);

  // Alt+left-drag over empty space still orbits the camera, and moves nothing.
  const yaw = await hook(page, (h) => h.engine()!.rig.orbitYaw);
  const sky = await page.evaluate(() => {
    const surface = (window as unknown as { __studio: Hook }).__studio.engine()!.surface;
    const r = surface.getBoundingClientRect();
    const at = { x: r.left + r.width / 2, y: r.top + r.height * 0.1 };
    return { ...at, onView: document.elementFromPoint(at.x, at.y) === surface };
  });
  expect(sky.onView, 'nothing over the top of the view').toBe(true);
  await page.mouse.move(sky.x, sky.y);
  await page.keyboard.down('Alt');
  await page.mouse.down();
  await page.mouse.move(sky.x + 150, sky.y, { steps: 6 });
  await page.mouse.up();
  await page.keyboard.up('Alt');
  await expect.poll(() => hook(page, (h) => h.engine()!.rig.orbitYaw)).not.toBe(yaw);
  expect(await customCorner(page)).toEqual(home);
  expect(await plan(page)).toBe(before);

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
