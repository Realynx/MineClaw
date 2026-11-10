import { expect, test, type Page } from '@playwright/test';

/**
 * The in-world chat bar in fullscreen free cam, on the production build behind Nest (plain HTTP like the LAN, so no Keyboard
 * Lock: every way out of the bar must work without Esc). Opening the bar frees the mouse; nothing locks it again while the bar
 * is open; it closes with Enter on an empty line, the close button, or a click on the world (which takes the mouse back in
 * one click, picking nothing); sending closes it too.
 */
async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('prod-stack-e2e');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

const locked = (page: Page) => page.evaluate(() => !!document.pointerLockElement);

test('chat bar in fullscreen: frees the mouse, chips work, closes without Esc, the world takes the mouse back', async ({
  page,
}) => {
  await signIn(page);
  await page.goto('/live/maid_chan');
  await page.getByRole('radio', { name: /World/ }).click();
  const stage = page.getByTestId('world-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.getByRole('radio', { name: 'Fly' }).click();
  await page.getByLabel('Go to a place, a bot or coordinates').fill('4,62,6');
  await page.getByLabel('Go to a place, a bot or coordinates').press('Enter');
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: 'Full screen' }).click();
  await expect
    .poll(() => page.evaluate(() => document.fullscreenElement?.getAttribute('data-testid') ?? null))
    .toBe('world-stage');
  const canvas = stage.locator('canvas').first();

  // Lock, aim at the ground and set it as the target block.
  await canvas.click({ position: { x: 720, y: 450 } });
  await expect.poll(() => locked(page)).toBe(true);
  const selection = page.getByTestId('selection');
  // maid_chan walks a small circle around this spot and can pass under the crosshair between the aim and the click: a click
  // that picked it is undone (Ctrl+click clears the selected bots) and the ground is clicked again.
  await expect(async () => {
    if ((await selection.count()) && (await selection.textContent())?.includes('Selected')) {
      await page.keyboard.down('Control');
      await page.mouse.down();
      await page.mouse.up();
      await page.keyboard.up('Control');
    }
    await expect(page.getByTestId('aim-readout')).toContainText(/\d+, \d+, \d+/, { timeout: 2000 });
    await page.mouse.down();
    await page.mouse.up();
    await expect(selection).toContainText(/\d+, \d+, \d+/, { timeout: 2000 });
    await expect(selection).not.toContainText('Selected', { timeout: 500 });
  }).toPass({ timeout: 30_000 });
  const target = await selection.textContent();

  // T opens the bar and really frees the mouse; a chip is clickable and the mouse stays free.
  await page.keyboard.press('t');
  const bar = page.getByRole('textbox', { name: /chat to the selected bots/i });
  await expect(bar).toBeFocused();
  await expect.poll(() => locked(page)).toBe(false);
  await page.getByRole('button', { name: 'Come here' }).click();
  await expect(bar).toHaveValue(/^come here \(\d+, \d+, \d+\)$/);
  expect(await locked(page)).toBe(false);

  // Enter on an empty line closes it.
  await bar.fill('');
  await bar.press('Enter');
  await expect(bar).toBeHidden();

  // The close button closes it.
  await page.keyboard.press('t');
  await expect(bar).toBeFocused();
  await page.getByRole('button', { name: 'Close the chat' }).click();
  await expect(bar).toBeHidden();
  expect(await locked(page)).toBe(false);

  // A click on the world closes it and locks the mouse again in that one click, picking nothing.
  await page.keyboard.press('t');
  await expect(bar).toBeFocused();
  await canvas.click({ position: { x: 720, y: 300 } });
  await expect(bar).toBeHidden();
  await expect.poll(() => locked(page)).toBe(true);
  await expect(selection).toHaveText(target ?? '');

  // Sending closes it; the next click on the world locks the mouse.
  await page.keyboard.press('t');
  await expect(bar).toBeFocused();
  await expect.poll(() => locked(page)).toBe(false);
  await bar.fill('stay here');
  await bar.press('Enter');
  await expect(bar).toBeHidden();
  await expect(page.getByTestId('chat-overlay')).toContainText('stay here');
  await canvas.click({ position: { x: 720, y: 300 } });
  await expect.poll(() => locked(page)).toBe(true);

  await page.keyboard.press('t'); // out of the lock again, then leave fullscreen with the button
  await page.getByRole('button', { name: 'Exit full screen' }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement)).toBeNull();
});
