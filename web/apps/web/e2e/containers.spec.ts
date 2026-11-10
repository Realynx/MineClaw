import { expect, test, type Page } from '@playwright/test';

// Chest contents in every viewer (mock mode): a container clicked in the Live world view opens a floating panel with the
// Minecraft-style grid and how fresh the hive's knowledge is; a chest never opened says so; in Story Mode the chest inspector
// shows the same grid next to its storage filters. The fixture island's chests: a double chest at (2..3, 74, -4) seen 3 min ago
// by maid_chan_102, a single one at (6, 74, -4) kept by Story's ledger; the gallery's single chest (20, 70, -32) was never
// opened. SHOTS_DIR=<dir> also saves screenshots there.
const shots = process.env.SHOTS_DIR;

type Pt = { x: number; y: number };

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

/** What a click on that block in the world view does (the WebGL pick is the viewer's own, tested in the package). */
async function clickBlock(page: Page, x: number, y: number, z: number, id = 54) {
  await page.evaluate(
    ([a, b, c, d]) =>
      (window as unknown as { __mcxWorld: { clickBlock: (x: number, y: number, z: number, id: number) => void } }).__mcxWorld.clickBlock(
        a!,
        b!,
        c!,
        d!,
      ),
    [x, y, z, id],
  );
}

test('clicking a chest in the Live world view opens its contents; a chest nobody opened says so', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await signIn(page);
  // The world view is a bot's World pane (the hive page no longer has one).
  await page.goto('/live/maid_chan?pane=world');
  await expect(page.getByTestId('world-stage')).toBeVisible({ timeout: 30_000 });
  await page.waitForFunction(() => !!(window as unknown as { __mcxWorld?: unknown }).__mcxWorld, null, { timeout: 30_000 });

  // The east half of the island's double chest: the panel shows all 54 slots, keyed by the west half.
  await clickBlock(page, 3, 74, -4);
  const panel = page.getByTestId('container-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('data-kind', 'double_chest');
  await expect(panel).toContainText('Large chest');
  await expect(panel).toContainText('2, 74, -4');
  await expect(panel.getByTestId('container-freshness')).toHaveText('Seen 3 min ago by maid_chan_102');
  const grid = panel.getByTestId('container-grid');
  await expect(grid.locator('[data-slot]')).toHaveCount(54);
  await expect(grid.getByTestId('ctr-slot-0')).toHaveAttribute('data-item', 'log');
  await expect(grid.getByTestId('ctr-slot-0')).toContainText('64');
  await expect(grid.getByTestId('ctr-slot-53')).toHaveAttribute('data-item', 'diamond');
  await expect(grid.getByTestId('ctr-slot-52')).not.toHaveAttribute('data-item', /./);
  // Icons from the bundled atlas; the tooltip names the item.
  await expect(grid.getByTestId('ctr-slot-13').locator('img')).toHaveAttribute('src', /^data:image\/png/, { timeout: 20_000 });
  await grid.getByTestId('ctr-slot-13').hover();
  await expect(page.getByTestId('inv-tooltip')).toContainText('Stone Axe');
  await expect(page.getByTestId('inv-tooltip')).toContainText('Durability 100 / 131');
  if (shots) await page.getByTestId('world-stage').screenshot({ path: `${shots}/container-live.png` });

  // A chest a bot has open right now: live, and it refreshes while the panel is open.
  await clickBlock(page, 17, 70, -32);
  await expect(panel.getByTestId('container-freshness')).toHaveText('Open right now by maid_chan_70');
  const before = await panel.getByTestId('ctr-slot-0').getAttribute('data-count');
  await expect.poll(async () => panel.getByTestId('ctr-slot-0').getAttribute('data-count'), { timeout: 20_000 }).not.toBe(before);

  // The gallery's single chest: never opened by any bot.
  await clickBlock(page, 20, 70, -32);
  await expect(panel.getByTestId('container-unknown')).toBeVisible();
  await expect(panel.getByTestId('container-freshness')).toHaveText('Not opened yet by any bot');
  await expect(panel.getByTestId('container-grid')).toHaveCount(0);

  // A furnace: input, fuel, output.
  await clickBlock(page, 0, 74, -6, 61);
  await expect(panel).toHaveAttribute('data-kind', 'furnace');
  await expect(panel.getByRole('group', { name: 'Furnace' })).toBeVisible();
  await expect(panel.getByTestId('ctr-slot-2')).toHaveAttribute('data-item', 'iron_ingot');

  // Not a container (stone): nothing opens; Esc and the close button close it.
  await clickBlock(page, 5, 70, 5, 1);
  await expect(panel).toHaveAttribute('data-kind', 'furnace');
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await clickBlock(page, 2, 74, -4);
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'Close container' }).click();
  await expect(panel).toHaveCount(0);
  expect(errors).toEqual([]);
});

// ------------------------------------------------------------------ Story Mode

interface StoryDebug {
  ready(): boolean;
  toPage(x: number, y: number, z: number): Pt | null;
  focus(x: number, z: number): void;
  setZoom(z: number): void;
  level(): number;
}

function story<K extends keyof StoryDebug>(page: Page, method: K, ...args: Parameters<StoryDebug[K]>) {
  return page.evaluate(
    ([m, a]) => (window as unknown as { __story: Record<string, (...x: unknown[]) => unknown> }).__story[m]!(...a),
    [method, args] as const,
  ) as Promise<ReturnType<StoryDebug[K]>>;
}

test('in Story Mode the chest inspector shows the chest contents next to its storage filters', async ({ page }) => {
  await signIn(page);
  // The seeded colony uses the island's chests as storage.
  await page.getByRole('link', { name: 'Controller' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: StoryDebug }).__story?.ready());
  await story(page, 'focus', 1, -3);
  await story(page, 'setZoom', 36);
  await page.waitForTimeout(1500);
  const lv = await story(page, 'level');
  const at = async (x: number, z: number) => (await story(page, 'toPage', x, lv + 1, z))!;

  const chestA = await at(2.5, -3.5);
  await page.mouse.click(chestA.x, chestA.y);
  const inspect = page.getByTestId('storage-inspector');
  await expect(inspect).toBeVisible();
  // The storage settings stay (the filter tree, the priority) …
  await expect(inspect.getByTestId('filter-tree')).toBeVisible();
  await expect(inspect.getByLabel('Storage priority')).toBeVisible();
  // … with the contents grid and its freshness.
  const contents = inspect.getByTestId('container-contents');
  await expect(contents.getByTestId('container-freshness')).toHaveText('Seen 3 min ago by maid_chan_102');
  await expect(contents.getByTestId('container-grid')).toHaveAttribute('data-size', '54');
  await expect(contents.getByTestId('ctr-slot-26')).toHaveAttribute('data-item', 'cobblestone');
  await expect(contents).toContainText('12 / 54 slots');
  if (shots) await page.screenshot({ path: `${shots}/container-story.png` });

  // The single chest: Story's own ledger, laid out from its counts.
  const chestB = await at(6.5, -3.5);
  await page.mouse.click(chestB.x, chestB.y);
  await expect(contents.getByTestId('container-freshness')).toContainText('Story storage ledger');
  await expect(contents.getByTestId('container-freshness')).toContainText('counts only');
  await expect(contents.getByTestId('ctr-slot-0')).toHaveAttribute('data-item', 'bread');
});
