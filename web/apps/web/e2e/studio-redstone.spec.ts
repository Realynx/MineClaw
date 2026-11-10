import { expect, test, type Page } from '@playwright/test';

// The Design Studio's redstone designer in mock mode, its components moved with the mouse free (never locked): a new
// design switched to Redstone (the chat starts collapsed); a library NOT gate placed by its corner, dragged by its body
// (its cells move, one undo step puts them back), turned and mirrored with R, Shift+R and M; a second gate placed with the
// mouse (its ghost follows it, a click places it); a drag onto it refused (red, with the reason) and put back with Esc; a
// drag beside it that snaps its input onto the other's output (the port shown joined); the two combined (Ctrl-click,
// Ctrl+G) and ungrouped (Ctrl+Shift+G); a save and a reload (both gates back); then one gate edited by hand and the page
// opened without this browser's copy of the components: that gate opens as plain blocks, with a note. SHOTS_DIR=<dir>
// saves screenshots.
const shots = process.env.SHOTS_DIR;
/** This browser's copy of a session's components (RedstoneComponentController's storage key prefix). */
const COPY_PREFIX = 'mineclaw.redstone.components.v1:';
const STONE = 1;
const DUST = 55;
const TORCH = 76;
const REPEATER = 93;

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
  studio: { canvas: { get(x: number, y: number, z: number): [number, number] } };
  /** Amber (open) and green (joined) port squares and arrows drawn now. */
  overlay: { portCounts: [number, number]; portArrowCounts: [number, number] };
  engine: () => {
    rig: { flyPos: { set(x: number, y: number, z: number): void }; flyYaw: number; flyPitch: number };
    camera: Camera;
    surface: HTMLElement;
  } | null;
};

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

/** Runs `f` on the studio's mock-mode hook (a string expression: evaluated by the browser's debugger, never `eval` in the page). */
const hook = <T>(page: Page, f: (h: Hook) => T): Promise<T> => page.evaluate<T>(`(${f.toString()})(window.__studio)`);

/** The block id at canvas cell `p`; null while the page has no studio yet. */
const blockAt = (page: Page, p: Point) =>
  page.evaluate(
    ([x, y, z]) => (window as unknown as { __studio?: Hook }).__studio?.studio.canvas.get(x, y, z)[0] ?? null,
    p,
  );

const locked = (page: Page) => page.evaluate(() => !!document.pointerLockElement);

/** Points the free camera from `from` at the centre of canvas block `at` (canvas y 0 = world y 64). */
async function lookAt(page: Page, from: Point, at: Point) {
  await page.evaluate(
    ({ fx, fy, fz, tx, ty, tz }) => {
      const e = (window as unknown as { __studio: Hook }).__studio.engine()!;
      const [px, py, pz] = [fx, fy + 64, fz];
      const [dx, dy, dz] = [tx + 0.5 - px, ty + 64.5 - py, tz + 0.5 - pz];
      const len = Math.hypot(dx, dy, dz);
      e.rig.flyPos.set(px, py, pz);
      e.rig.flyYaw = ((Math.atan2(-dx, dz) * 180) / Math.PI + 360) % 360;
      e.rig.flyPitch = (-Math.asin(dy / len) * 180) / Math.PI;
    },
    { fx: from[0], fy: from[1], fz: from[2], tx: at[0], ty: at[1], tz: at[2] },
  );
  await page.waitForTimeout(400);
}

/** Where canvas point `p` is drawn now, in client pixels, and whether the view itself is there (nothing over it). */
async function screenOf(page: Page, p: Point): Promise<{ x: number; y: number; onView: boolean }> {
  return page.evaluate(([x, y, z]) => {
    const e = (window as unknown as { __studio: Hook }).__studio.engine()!;
    e.camera.updateMatrixWorld();
    const v = e.camera.position
      .clone()
      .set(x, y + 64, z)
      .project(e.camera);
    const r = e.surface.getBoundingClientRect();
    const at = { x: r.left + ((v.x + 1) / 2) * r.width, y: r.top + ((1 - v.y) / 2) * r.height };
    return { ...at, onView: document.elementFromPoint(at.x, at.y) === e.surface };
  }, p);
}

/**
 * Presses the left button over canvas point `p`, the mouse free. A point on canvas level 0 is where a component standing
 * there is dragged across: a drag from one such point to another moves it by the cells between them.
 */
async function pressAt(page: Page, p: Point) {
  const s = await screenOf(page, p);
  expect(s.onView, `the view is under ${p.join(' ')}`).toBe(true);
  await page.mouse.move(s.x, s.y);
  await page.mouse.down();
}

async function dragTo(page: Page, p: Point) {
  const s = await screenOf(page, p);
  await page.mouse.move(s.x, s.y, { steps: 8 });
}

/** A click with the mouse locked (the crosshair's block). */
async function click(page: Page, button: 'left' | 'right') {
  await page.mouse.down({ button });
  await page.waitForTimeout(60);
  await page.mouse.up({ button });
  await page.waitForTimeout(250);
}

const outliner = (page: Page) => page.getByRole('listbox', { name: 'Redstone components' });
const rows = (page: Page) => outliner(page).getByRole('option');
const row = (page: Page, name: string) => outliner(page).getByRole('option', { name, exact: true });

/** Waits until the bot holds every edit (the header's sync chip). */
async function synced(page: Page) {
  await expect(page.getByTestId('sync-chip')).toHaveAttribute('data-sync', 'idle');
  await expect(page.getByTestId('sync-chip')).toHaveText('Synced');
}

async function openRedstone(page: Page) {
  await signIn(page);
  await page.goto('/designs/studio');
  await page.waitForURL('**/designs/studio?session=**');
  const stage = page.getByTestId('studio-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.getByTestId('kind-select').selectOption('redstone');
  await expect(page.getByTestId('redstone-panel')).toBeVisible();
  await expect(page.getByTestId('kind-select')).toHaveValue('redstone');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.waitForTimeout(3000); // the plot meshes
}

/** Places the library's NOT gate with its box's corner at `at` (typed: the Library tab's "Place at"). */
async function placeNot(page: Page, at: Point) {
  await page.getByRole('tab', { name: 'Library' }).click();
  const library = page.getByTestId('redstone-library');
  await library.getByRole('region', { name: 'Proven gates' }).getByRole('button', { name: 'NOT', exact: true }).click();
  await library.getByLabel('Place at x y z').fill(at.join(' '));
  await library.getByRole('button', { name: 'Place at', exact: true }).click();
}

test('redstone designer: gates are parts the mouse moves, snaps, combines, and the design keeps', async ({ page }) => {
  test.setTimeout(240_000);
  await openRedstone(page);

  // The chat starts collapsed, as in a mine: the circuit has the room.
  await expect(page.getByRole('button', { name: 'Expand designer chat' })).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByLabel('Message the designer')).toBeHidden();

  // A NOT gate by its corner: its box is 3×1×3, a repeater on the west, a block, a torch on the east.
  await placeNot(page, [20, 0, 10]);
  await expect(rows(page)).toHaveCount(1);
  await expect(row(page, 'NOT 1')).toBeVisible();
  await expect.poll(() => blockAt(page, [20, 0, 11])).toBe(REPEATER);
  expect(await blockAt(page, [21, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [22, 0, 11])).toBe(TORCH);

  // Dragged by its body with the mouse free: the press selects it without locking the view, the outline and its two open
  // ports follow the mouse, and the release moves its cells.
  await lookAt(page, [16, 24, 24], [16, 0, 14]);
  await pressAt(page, [20.5, 0, 11.5]);
  await dragTo(page, [20.5, 0, 19.5]);
  await expect.poll(() => hook(page, (h) => h.overlay.portCounts)).toEqual([2, 0]);
  expect(await locked(page)).toBe(false);
  await expect(page.getByTestId('redstone-drag-tip')).toHaveCount(0);
  if (shots) await page.screenshot({ path: `${shots}/studio-redstone-drag.png` });
  await page.mouse.up();
  await expect.poll(() => blockAt(page, [21, 0, 19])).toBe(STONE);
  expect(await blockAt(page, [20, 0, 19])).toBe(REPEATER);
  expect(await blockAt(page, [21, 0, 11])).toBe(0);
  await expect(row(page, 'NOT 1')).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel('Corner of NOT 1')).toHaveValue('20 0 18');
  // One undo step puts every cell back.
  await page.keyboard.press('Control+z');
  await expect.poll(() => blockAt(page, [21, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [20, 0, 11])).toBe(REPEATER);
  expect(await blockAt(page, [21, 0, 19])).toBe(0);

  // A click on it (the mouse still free) selects it and gives the view the keys: R turns it a quarter (the repeater
  // leaves the west side), Shift+R turns it back, M mirrors it east-west and M again undoes the mirror.
  await pressAt(page, [20.5, 0, 11.5]);
  await page.mouse.up();
  expect(await locked(page)).toBe(false);
  await expect(row(page, 'NOT 1')).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('r');
  await expect.poll(() => blockAt(page, [20, 0, 11])).toBe(0);
  expect(await blockAt(page, [21, 0, 11])).toBe(STONE);
  await page.keyboard.press('Shift+R');
  await expect.poll(() => blockAt(page, [20, 0, 11])).toBe(REPEATER);
  await page.keyboard.press('m');
  await expect.poll(() => blockAt(page, [22, 0, 11])).toBe(REPEATER);
  expect(await blockAt(page, [20, 0, 11])).toBe(TORCH);
  await page.keyboard.press('m');
  await expect.poll(() => blockAt(page, [20, 0, 11])).toBe(REPEATER);
  expect(await blockAt(page, [22, 0, 11])).toBe(TORCH);

  // A second NOT to the west, with room between them, placed with the mouse free: after "Place NOT" its ghost follows the
  // mouse on the selected gate's level, and a click puts its corner on the cell under the mouse without locking the view.
  await page.getByRole('tab', { name: 'Library' }).click();
  await page.getByTestId('redstone-library').getByRole('button', { name: 'Place NOT', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Place a component' }).getByRole('status')).toContainText(
    'right click or Esc cancels',
  );
  await pressAt(page, [10.5, 0, 10.5]);
  await page.mouse.up();
  expect(await locked(page)).toBe(false);
  await expect(rows(page)).toHaveCount(2);
  await expect.poll(() => blockAt(page, [11, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [10, 0, 11])).toBe(REPEATER);
  await expect(row(page, 'NOT 2')).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('region', { name: 'Place a component' }).getByRole('status')).toHaveCount(0);

  // Dragged onto it: the outline is refused (red, the reason as a tip) and Esc puts it back where it was.
  await pressAt(page, [20.5, 0, 11.5]);
  await dragTo(page, [10.5, 0, 11.5]);
  await expect(page.getByTestId('redstone-drag-tip')).toContainText('keep one free cell between components');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('redstone-drag-tip')).toHaveCount(0);
  await page.mouse.up();
  expect(await blockAt(page, [21, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [11, 0, 11])).toBe(STONE);
  await expect(page.getByLabel('Corner of NOT 1')).toHaveValue('20 0 10');

  // Dragged near the other's output: it snaps its input A onto that output's cell, which turns green while the outline
  // is still in hand; the release lays one dust there.
  await pressAt(page, [20.5, 0, 11.5]);
  await dragTo(page, [15.5, 0, 11.5]);
  await expect.poll(() => hook(page, (h) => h.overlay.portCounts)).toEqual([1, 1]);
  if (shots) await page.screenshot({ path: `${shots}/studio-redstone-snap.png` });
  await page.mouse.up();
  await expect.poll(() => blockAt(page, [13, 0, 11])).toBe(DUST);
  expect(await blockAt(page, [14, 0, 11])).toBe(REPEATER);
  expect(await blockAt(page, [15, 0, 11])).toBe(STONE);
  await expect(page.getByLabel('Corner of NOT 1')).toHaveValue('14 0 10');
  await expect(
    page
      .getByTestId('redstone-selected-component')
      .getByTestId('component-port')
      .filter({ hasText: 'joined to NOT 2 Y' }),
  ).toHaveAttribute('data-joined', 'true');
  await expect(row(page, 'NOT 1')).toContainText('1/2 ports joined');
  await expect(row(page, 'NOT 2')).toContainText('1/2 ports joined');
  await expect.poll(() => hook(page, (h) => h.overlay.portCounts)).toEqual([1, 1]);
  await expect.poll(() => hook(page, (h) => h.overlay.portArrowCounts)).toEqual([1, 1]);

  // Combined: a Ctrl-click ticks the other row (the selected one is ticked with it) and Ctrl+G makes one component of the
  // two and the dust between them; Ctrl+Shift+G gives the two gates back. The cells never change.
  await row(page, 'NOT 2').click({ modifiers: ['Control'] });
  await expect(page.getByRole('region', { name: 'Combine redstone components' })).toBeVisible();
  await page.keyboard.press('Control+g');
  await expect(rows(page)).toHaveCount(1);
  await expect(row(page, 'combined 1')).toHaveAttribute('aria-selected', 'true');
  expect(await blockAt(page, [13, 0, 11])).toBe(DUST);
  expect(await blockAt(page, [11, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [15, 0, 11])).toBe(STONE);
  await page.keyboard.press('Control+Shift+G');
  await expect(rows(page)).toHaveCount(2);
  await expect(row(page, 'NOT 1')).toContainText('1/2 ports joined');
  await expect(row(page, 'NOT 2')).toContainText('1/2 ports joined');
  expect(await blockAt(page, [13, 0, 11])).toBe(DUST);
  expect(await blockAt(page, [11, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [15, 0, 11])).toBe(STONE);

  // Saved, then reloaded: both gates come back as components, joined as they were, and nothing is unsaved.
  await page.getByLabel('Design name').fill('e2e gates');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByTestId('saved-bar')).toBeVisible();
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '0');
  await synced(page);
  await page.reload();
  await expect(page.getByTestId('redstone-panel')).toBeVisible();
  await expect(page.getByTestId('kind-select')).toHaveValue('redstone');
  await expect.poll(() => blockAt(page, [15, 0, 11])).toBe(STONE);
  await expect(rows(page)).toHaveCount(2);
  await expect(row(page, 'NOT 1')).toContainText('1/2 ports joined');
  await expect(row(page, 'NOT 2')).toContainText('1/2 ports joined');
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '0');
  await expect(page.getByTestId('studio-alert')).toHaveCount(0);

  // A press on empty grass still goes to the view: it locks the mouse. Then a left click breaks the western gate's block
  // by hand, and that gate becomes plain blocks.
  await page.waitForTimeout(2500); // the plot meshes
  await lookAt(page, [16, 24, 24], [16, 0, 14]);
  const grass = await screenOf(page, [16.5, 0, 20.5]);
  expect(grass.onView).toBe(true);
  await page.mouse.click(grass.x, grass.y);
  await expect.poll(() => locked(page)).toBe(true);
  await lookAt(page, [11.5, 6, 15], [11, 0, 11]);
  await click(page, 'left');
  await expect.poll(() => blockAt(page, [11, 0, 11])).toBe(0);
  await expect(rows(page)).toHaveCount(1);
  expect(await blockAt(page, [10, 0, 11])).toBe(REPEATER);
  await page.evaluate(() => document.exitPointerLock());
  await synced(page);

  // Opened again without this browser's copy (as another browser would): the saved gate whose cells no longer match
  // opens as plain blocks and a note says so; the other one is still a component.
  await page.evaluate((prefix) => {
    for (const key of Object.keys(localStorage)) if (key.startsWith(prefix)) localStorage.removeItem(key);
  }, COPY_PREFIX);
  // The page asks before a reload that would leave unsaved changes: leave.
  page.on('dialog', (d) => void d.accept());
  await page.reload();
  await expect(page.getByTestId('redstone-panel')).toBeVisible();
  await expect.poll(() => blockAt(page, [15, 0, 11])).toBe(STONE);
  expect(await blockAt(page, [11, 0, 11])).toBe(0);
  await expect(rows(page)).toHaveCount(1);
  await expect(row(page, 'NOT 1')).toBeVisible();
  await expect(page.getByTestId('studio-alert')).toContainText(
    '1 of 2 saved components no longer matches the circuit and was opened as plain blocks.',
  );
  if (shots) await page.screenshot({ path: `${shots}/studio-redstone-restore-note.png` });
});
