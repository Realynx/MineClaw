import { expect, test, type Page } from '@playwright/test';

// The bot view's world pane: fullscreen with the one shared canvas, free cam with the crosshair, and the in-world chat bar.
// SHOTS_DIR=<dir> also saves screenshots there.
const shots = process.env.SHOTS_DIR;

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

test('live grid groups every body by hive', async ({ page }) => {
  await signIn(page);
  await expect(page.locator('[data-group="maid_chan_109"]')).toBeVisible();
  await expect(page.locator('[data-body]')).toHaveCount(11);
  await page.waitForTimeout(4000); // thumbnails draw
  if (shots) {
    // The page scrolls inside the shell, so a tall window shows every group at once.
    await page.setViewportSize({ width: 1600, height: 2300 });
    await page.waitForTimeout(3000);
    await page.screenshot({ path: `${shots}/live-grid.png` });
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.locator('[data-group="maid_chan_109"]').scrollIntoViewIfNeeded();
    await page.waitForTimeout(1500);
    await page.screenshot({ path: `${shots}/live-grid-subhive.png` });
  }
  // Folding a group unmounts its cards (and their thumbnails).
  await page.locator('[data-group="maid_chan_109"]').click();
  await expect(page.locator('[data-body]')).toHaveCount(4);
  await page.locator('[data-group="maid_chan_109"]').click();
  await expect(page.locator('[data-body]')).toHaveCount(11);
});

test('fullscreen free cam: crosshair, selection, chat bar and Esc', async ({ page }) => {
  await signIn(page);
  await page.goto('/live/maid_chan');
  await page.getByRole('radio', { name: /World/ }).click();
  const stage = page.getByTestId('world-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  const contexts = () => page.evaluate(() => document.querySelectorAll('canvas').length);
  // Free cam, flown to a bot with the go-to box: the crosshair lands on its body.
  await page.getByRole('radio', { name: 'Fly' }).click();
  await page.getByLabel('Go to a place, a bot or coordinates').fill('maid_chan_114');
  await page.getByLabel('Go to a place, a bot or coordinates').press('Enter');
  await page.waitForTimeout(1500);
  const before = await contexts();
  await page.getByRole('button', { name: 'Full screen' }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement?.getAttribute('data-testid') ?? null)).toBe('world-stage');
  expect(await contexts()).toBe(before); // the canvas moved with its container; no second WebGL context
  // The renderer follows the new size (ResizeObserver): the canvas fills the screen.
  await expect.poll(() => page.evaluate(() => (document.querySelector('[data-testid="world-stage"] canvas') as HTMLCanvasElement).clientWidth)).toBe(1440);
  await expect(page.getByRole('radio', { name: 'Free cam' })).toHaveAttribute('aria-checked', 'true');
  // First click locks the mouse; the crosshair appears.
  await stage.locator('canvas').first().click({ position: { x: 720, y: 450 } });
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(true);
  await expect(page.getByTestId('crosshair')).toBeVisible();
  await expect(page.getByTestId('aim-readout')).toContainText('maid_chan_114');
  if (shots) await page.screenshot({ path: `${shots}/fullscreen-crosshair.png` });
  // A click with the mouse locked selects the bot under the crosshair.
  await page.mouse.down();
  await page.mouse.up();
  await expect(page.getByTestId('selection')).toContainText('maid_chan_114');
  // Look down at the ground (mouse look) and click: the block becomes the target, and the chat bar offers directives for it.
  await page.mouse.move(720, 640, { steps: 4 });
  await expect(page.getByTestId('aim-readout')).not.toContainText('maid_chan_114');
  await page.mouse.down();
  await page.mouse.up();
  await expect(page.getByTestId('selection')).toContainText(/-?\d+, -?\d+, -?\d+/);
  // Keys while typing in the chat bar don't drive the world.
  await page.keyboard.press('t');
  const bar = page.getByRole('textbox', { name: /chat to the selected bots/i });
  await expect(bar).toBeFocused();
  await expect.poll(() => page.evaluate(() => !!document.pointerLockElement)).toBe(false);
  await page.getByRole('button', { name: 'Come here' }).click();
  await expect(bar).toHaveValue(/^come here \(-?\d+, -?\d+, -?\d+\)$/);
  await bar.pressSequentially(' F1 come on');
  await expect.poll(() => page.evaluate(() => document.fullscreenElement?.getAttribute('data-testid') ?? null)).toBe('world-stage');
  if (shots) await page.screenshot({ path: `${shots}/fullscreen-chat.png` });
  await bar.press('Enter');
  await expect(page.getByTestId('chat-overlay')).toContainText('come on');
  if (shots) await page.screenshot({ path: `${shots}/fullscreen-reply.png` });
  // Esc leaves fullscreen.
  await page.getByRole('button', { name: 'Exit full screen' }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement)).toBeNull();
});
