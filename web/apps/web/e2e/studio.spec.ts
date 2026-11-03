import { expect, test, type Page } from '@playwright/test';

// The Design Studio in mock mode: a new design from the Designs page, blocks placed with the mouse locked, the fake AI's
// house and door, manual edits (select a wall, move it, rotate the whole design, brush-remove, undo), digging underground,
// leaving with unsaved changes (asked first: Stay keeps the canvas, Leave goes), a save that shows up in the Designs list
// (navigation free again). SHOTS_DIR=<dir> also saves screenshots there.
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
  studio: {
    canvas: { size: number; dug: Set<number>; get(x: number, y: number, z: number): [number, number] };
    selection: unknown;
  };
  engine: () => {
    rig: { flyPos: { set(x: number, y: number, z: number): void }; flyYaw: number; flyPitch: number };
  } | null;
};

/** Points the free camera from `from` at the centre of canvas block `at` (canvas y 0 = world y 64). */
async function lookAt(page: Page, from: [number, number, number], at: [number, number, number]) {
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

const blocks = (page: Page) => page.getByTestId('studio').getAttribute('data-blocks').then(Number);
const click = async (page: Page, button: 'left' | 'right') => {
  await page.mouse.down({ button });
  await page.waitForTimeout(60);
  await page.mouse.up({ button });
  await page.waitForTimeout(250);
};

test('design studio: build, ask the AI, edit by hand, save', async ({ page }) => {
  await signIn(page);
  await page.goto('/designs');
  await page.getByRole('link', { name: 'New design' }).click();
  await page.waitForURL('**/designs/studio**');
  const stage = page.getByTestId('studio-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await expect(page.getByLabel('Model')).toHaveValue('main');
  await page.waitForTimeout(3500); // the plot meshes
  if (shots) await page.screenshot({ path: `${shots}/studio-empty.png` });

  // Lock the mouse; right-click places the hotbar block (cobblestone) on the grass under the crosshair.
  await stage
    .locator('canvas')
    .first()
    .click({ position: { x: 300, y: 300 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await expect(page.getByTestId('crosshair')).toBeVisible();
  await lookAt(page, [10, 6, 10], [10, -1, 4]);
  await click(page, 'right');
  await expect.poll(() => blocks(page)).toBe(1);
  await lookAt(page, [10, 6, 10], [11, -1, 4]);
  await page.keyboard.press('Digit2'); // planks
  await click(page, 'right');
  await expect.poll(() => blocks(page)).toBe(2);
  // Left click breaks the block under the crosshair.
  await lookAt(page, [10, 6, 10], [10, 0, 4]);
  await click(page, 'left');
  await expect.poll(() => blocks(page)).toBe(1);

  // The fake AI builds a little house, then a door.
  await page.evaluate(() => document.exitPointerLock());
  const box = page.getByLabel('Message the designer');
  await box.fill('build me a little house');
  await box.press('Enter');
  await expect(page.getByText(/Built a little 7×7 house/)).toBeVisible();
  expect(await blocks(page)).toBeGreaterThan(100);
  await box.fill('add a door');
  await box.press('Enter');
  await expect(page.getByText('placed an oak door')).toBeVisible();
  await expect(page.getByTestId('message-cost').last()).toContainText('$');
  await expect
    .poll(() => page.evaluate(() => (window as unknown as { __studio: Hook }).__studio.studio.canvas.get(32, 1, 35)[0]))
    .toBe(64);
  await lookAt(page, [32, 10, 50], [32, 2, 32]);
  if (shots) await page.screenshot({ path: `${shots}/studio-house.png` });

  // Select the west wall between its log corners (select tool: left-drag a box) and move it one block west.
  await stage
    .locator('canvas')
    .first()
    .click({ position: { x: 300, y: 300 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await page.keyboard.press('KeyV');
  await lookAt(page, [22, 1.5, 32], [29, 1, 30]);
  await page.mouse.down({ button: 'left' });
  await lookAt(page, [22, 1.5, 32], [29, 3, 34]);
  await page.mouse.up({ button: 'left' });
  await expect(page.getByTestId('selection-hud')).toContainText('1×3×5');
  // Right-click selects the connected blocks of one type: all the planks (walls and roof).
  await click(page, 'right');
  await expect(page.getByTestId('selection-hud')).toContainText('9×4×9');
  await lookAt(page, [22, 1.5, 32], [29, 1, 30]);
  await page.mouse.down({ button: 'left' });
  await lookAt(page, [22, 1.5, 32], [29, 3, 34]);
  await page.mouse.up({ button: 'left' });
  await expect(page.getByTestId('selection-hud')).toContainText('1×3×5');
  if (shots) await page.screenshot({ path: `${shots}/studio-selection.png` });
  await page.keyboard.press('ArrowLeft');
  await expect
    .poll(() => page.evaluate(() => (window as unknown as { __studio: Hook }).__studio.studio.canvas.get(28, 1, 32)[0]))
    .toBe(5);
  await page.keyboard.press('Control+z');
  await expect
    .poll(() => page.evaluate(() => (window as unknown as { __studio: Hook }).__studio.studio.canvas.get(28, 1, 32)[0]))
    .toBe(0);

  // Rotate the whole design 90° (deselect, then the toolbar), the door turns with it.
  await page.evaluate(() => document.exitPointerLock());
  await page.getByRole('button', { name: 'Deselect' }).click();
  const beforeRotate = await blocks(page);
  const doorBefore = await page.evaluate(() =>
    (window as unknown as { __studio: Hook }).__studio.studio.canvas.get(32, 1, 35),
  );
  await page.getByRole('button', { name: /Rotate 90° clockwise/ }).click();
  const rotated = await page.evaluate(() => {
    const c = (window as unknown as { __studio: Hook }).__studio.studio.canvas as unknown as { cells(): number[][] };
    return c.cells().find((x) => x[3] === 64 && (x[4]! & 8) === 0) ?? null;
  });
  expect(rotated).not.toBeNull();
  expect(rotated![4]).not.toBe(doorBefore[1]);
  expect(await blocks(page)).toBe(beforeRotate); // nothing lost in the turn

  // Brush-remove a bite out of the roof, then undo it.
  const beforeBrush = await blocks(page);
  await stage
    .locator('canvas')
    .first()
    .click({ position: { x: 300, y: 300 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await page.keyboard.press('KeyG');
  const roof = await page.evaluate(() => {
    const c = (window as unknown as { __studio: Hook }).__studio.studio.canvas as unknown as { cells(): number[][] };
    const top = c.cells().filter((x) => x[1] === 4);
    return top[Math.floor(top.length / 2)]!;
  });
  await lookAt(page, [roof[0]! + 0.5, 12, roof[2]! + 6], [roof[0]!, 4, roof[2]!]);
  await click(page, 'left');
  await expect.poll(() => blocks(page)).toBeLessThan(beforeBrush);
  if (shots) await page.screenshot({ path: `${shots}/studio-brush.png` });
  await page.keyboard.press('Control+z');
  await expect.poll(() => blocks(page)).toBe(beforeBrush);

  // The palette (B) searches the design palette with icons.
  await page.keyboard.press('KeyB');
  await page.getByLabel('Search blocks').fill('stone br');
  await expect(page.getByRole('button', { name: 'stone bricks', exact: true })).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/studio-palette.png` });
  await page.getByRole('button', { name: 'Close the palette' }).click();

  const fps = Number(((await page.getByTestId('studio-hud').textContent()) ?? '').match(/(\d+) fps/)?.[1] ?? 0);
  console.warn(`[studio] software WebGL fps: ${fps}`);
  expect(fps).toBeGreaterThan(0);

  // Underground (K): the grass becomes diggable; a left click digs a cell out of the ground (the view cuts away above it).
  await stage
    .locator('canvas')
    .first()
    .click({ position: { x: 300, y: 300 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await page.keyboard.press('KeyP');
  await page.keyboard.press('KeyK');
  await expect(page.getByTestId('studio')).toHaveAttribute('data-under', '-1');
  await expect(page.getByLabel('Underground level')).toBeVisible();
  await page.waitForTimeout(400);
  await lookAt(page, [14, 6, 14], [14, -1, 8]);
  await click(page, 'left');
  await expect
    .poll(() => page.evaluate(() => (window as unknown as { __studio: Hook }).__studio.studio.canvas.get(14, -1, 8)))
    .toEqual([0, 0]);
  expect(await page.evaluate(() => (window as unknown as { __studio: Hook }).__studio.studio.canvas.dug.size)).toBe(1);
  if (shots) await page.screenshot({ path: `${shots}/studio-underground.png` });
  await page.keyboard.press('KeyK');
  await expect(page.getByTestId('studio')).toHaveAttribute('data-under', '');

  // Leaving with unsaved changes asks first; Stay keeps the canvas.
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '1');
  await page.evaluate(() => document.exitPointerLock());
  const kept = await blocks(page);
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Overview' }).click();
  const ask = page.getByRole('dialog', { name: 'Leave without saving?' });
  await expect(ask).toBeVisible();
  await expect(ask.getByRole('button', { name: 'Save and leave' })).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/studio-leave.png` });
  await ask.getByRole('button', { name: 'Stay' }).click();
  await expect(ask).toBeHidden();
  expect(page.url()).toContain('/designs/studio');
  expect(await blocks(page)).toBe(kept);

  // Save: a new design, then it is in the Designs list.
  await page.getByLabel('Design name').fill('e2e hut');
  await page.getByLabel('Revision message').fill('first draft from the studio');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByTestId('saved-bar')).toContainText('saved as design #100');
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '0');
  await expect(page.getByRole('link', { name: 'Build in Story Mode' })).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/studio-saved.png` });
  await page.getByRole('link', { name: 'Open in Designs' }).click();
  await page.waitForURL('**/designs?id=100'); // saved: no question
  await expect(page.getByRole('dialog', { name: 'Leave without saving?' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /e2e hut/ })).toBeVisible();
  await expect(page.getByText('“first draft from the studio”', { exact: false })).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/designs-list.png` });
});

test('design studio: Leave goes without saving; an untouched design never asks', async ({ page }) => {
  await signIn(page);
  await page.goto('/designs/studio');
  await page.waitForURL('**/designs/studio?session=**');
  const stage = page.getByTestId('studio-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.waitForTimeout(2500);
  // Untouched: navigation is free.
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '0');
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Overview' }).click();
  await page.waitForURL('**/overview');
  await page.goBack();
  await page.waitForURL('**/designs/studio**');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.waitForTimeout(2500);
  // One block placed: leaving asks; Leave goes.
  await stage
    .locator('canvas')
    .first()
    .click({ position: { x: 300, y: 300 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await lookAt(page, [10, 6, 10], [10, -1, 4]);
  await click(page, 'right');
  await expect.poll(() => blocks(page)).toBe(1);
  await expect(page.getByTestId('studio')).toHaveAttribute('data-dirty', '1');
  await page.evaluate(() => document.exitPointerLock());
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Overview' }).click();
  const ask = page.getByRole('dialog', { name: 'Leave without saving?' });
  await expect(ask).toBeVisible();
  await ask.getByRole('button', { name: 'Leave', exact: true }).click();
  await page.waitForURL('**/overview');
  await expect(ask).toHaveCount(0);
});
