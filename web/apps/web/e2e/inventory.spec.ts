import { expect, test, type Page } from '@playwright/test';

// Body inventories in the Live hive (mock mode): clicking a body opens its panel with the Minecraft-style grid — the mocked
// items with their counts, the held hotbar slot highlighted, armour, durability, the 1.12.2 off hand and enchant glint.
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

test('clicking a body card opens its panel with the inventory grid', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await signIn(page);
  await page.locator('[data-body="maid_chan"]').first().click();
  await page.waitForURL('**/live/maid_chan');

  const grid = page.getByTestId('inventory-grid');
  await expect(grid).toBeVisible();
  await expect(grid).toHaveAttribute('data-version', 'b1.8.1');
  // The mocked miner: a diamond pickaxe held in hotbar 1, a stack of torches, cobblestone, armour.
  const held = grid.locator('[data-held="true"]');
  await expect(held).toHaveCount(1);
  await expect(held).toHaveAttribute('data-slot', '0');
  await expect(held).toHaveAttribute('data-item', 'diamond_pickaxe');
  await expect(grid.getByTestId('inv-slot-3')).toHaveAttribute('data-item', 'torch');
  await expect(grid.getByTestId('inv-slot-3')).toContainText('37');
  await expect(grid.getByTestId('inv-slot-9')).toContainText('64');
  await expect(grid.getByTestId('inv-slot-39')).toHaveAttribute('data-item', 'iron_helmet');
  await expect(grid.getByTestId('inv-slot-37')).not.toHaveAttribute('data-item', /./);
  await expect(grid.getByTestId('inv-slot-40')).toHaveCount(0); // b1.8.1: no off hand
  // Icons come from the bundled atlas (data URLs drawn on the page), with a durability bar on the used pickaxe.
  await expect(held.locator('img')).toHaveAttribute('src', /^data:image\/png/, { timeout: 20_000 });
  // The tooltip names it, with id:meta and durability.
  await held.hover();
  const tip = page.getByTestId('inv-tooltip');
  await expect(tip).toContainText('Diamond Pickaxe');
  await expect(tip).toContainText('278:');
  await expect(tip).toContainText(/Durability \d+ \/ 1561/);
  if (shots) await page.getByTestId('inventory-section').screenshot({ path: `${shots}/inventory-miner.png` });

  // It polls while open: the cobblestone count moves with the mock simulation, without the grid ever going away.
  const before = await grid.getByTestId('inv-slot-2').getAttribute('data-count');
  await expect
    .poll(async () => grid.getByTestId('inv-slot-2').getAttribute('data-count'), { timeout: 15_000 })
    .not.toBe(before);
  await expect(grid).toBeVisible();
  expect(errors).toEqual([]);
});

test('a 1.12.2 body shows its off hand and glinting enchanted gear', async ({ page }) => {
  await signIn(page);
  await page.goto('/live/maid_chan_67');
  const grid = page.getByTestId('inventory-grid');
  await expect(grid).toHaveAttribute('data-version', '1.12.2');
  await expect(grid.getByTestId('inv-slot-40')).toHaveAttribute('data-item', 'shield');
  await expect(grid.locator('[data-held="true"]')).toHaveAttribute('data-slot', '2');
  await expect(grid.getByTestId('inv-slot-0')).toHaveAttribute('data-glint', 'true');
  await expect(grid.getByTestId('inv-slot-9')).toContainText('48');
  if (shots) await page.getByTestId('inventory-section').screenshot({ path: `${shots}/inventory-scout.png` });
});

test('clicking a body in the world view opens its floating panel', async ({ page }) => {
  await signIn(page);
  await page.goto('/live?view=world');
  await expect(page.getByTestId('world-stage')).toBeVisible({ timeout: 30_000 });
  // What a click on the bot's model does (the WebGL pick is the viewer's own, tested in the package).
  await page.waitForFunction(() => !!(window as unknown as { __mcxWorld?: unknown }).__mcxWorld, null, {
    timeout: 30_000,
  });
  await page.evaluate(() =>
    (window as unknown as { __mcxWorld: { clickBot: (n: string) => void } }).__mcxWorld.clickBot('maid_chan_102'),
  );
  const panel = page.getByTestId('body-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('maid_chan_102');
  const grid = panel.getByTestId('inventory-grid');
  await expect(grid.locator('[data-held="true"]')).toHaveAttribute('data-item', 'iron_axe');
  await expect(grid.getByTestId('inv-slot-2')).toHaveAttribute('data-item', 'sapling');
  if (shots) await page.getByTestId('world-stage').screenshot({ path: `${shots}/inventory-world.png` });
  await panel.getByRole('button', { name: 'Close panel' }).click();
  await expect(panel).toHaveCount(0);
});
