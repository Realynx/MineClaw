import { expect, test, type Page, type Request } from '@playwright/test';

// Story Mode water (docs/web/story-mode.md "Water") in mock mode: the Architect's Water tab, a channel drawn on the map is checked
// by the director (preview) and queued as a dig and a pour in one group; its cells show as water holograms.
const shots = process.env.SHOTS_DIR;

type Pt = { x: number; y: number };
type Ghost = [number, number, number, number, number, string | number, string, string?];
interface Debug {
  ready(): boolean;
  toPage(x: number, y: number, z: number): Pt | null;
  focus(x: number, z: number): void;
  setZoom(z: number): void;
  level(): number;
  holograms(): { cells: number; digs: number; boxes: number; lod: boolean } | null;
  hologramAt(x: number, y: number, z: number): boolean;
  ghosts(): Ghost[];
}

const call = <K extends keyof Debug>(page: Page, m: K, ...a: Parameters<Debug[K]>) =>
  page.evaluate(
    ([mm, aa]) => (window as unknown as { __story: Record<string, (...x: unknown[]) => unknown> }).__story[mm]!(...aa),
    [m, a] as const,
  ) as Promise<ReturnType<Debug[K]>>;

const post = (page: Page, path: RegExp, op?: string) =>
  page.waitForRequest(
    (r: Request) =>
      r.method() === 'POST' && path.test(new URL(r.url()).pathname) && (!op || (r.postDataJSON() as { op?: string }).op === op),
  );

test('a water channel: the Water tab, a drawn canal previewed and queued as dig then pour, its cells as water holograms', async ({
  page,
}) => {
  await page.addInitScript(() => sessionStorage.setItem('mcx-mock-story', 'empty'));
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
  await page.getByRole('link', { name: 'Story Mode' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: Debug }).__story?.ready());
  const count = page.getByLabel('Number of pawns');
  while (Number(await count.textContent()) !== 2)
    await page.getByRole('button', { name: Number(await count.textContent()) < 2 ? 'More pawns' : 'Fewer pawns' }).click();
  await page.getByRole('button', { name: 'Found colony' }).click();
  await expect(page.getByTestId('colonist-bar').locator('[data-kind="pawn"]')).toHaveCount(2);
  await page.waitForTimeout(1500);
  await call(page, 'focus', -6, 2);
  await call(page, 'setZoom', 36);
  await page.waitForTimeout(800);

  // The Architect's Water tab: the tools, and the palette's entries at the bucket's tier (the camp's table unlocks them).
  await page.keyboard.press('b');
  const arch = page.getByTestId('architect');
  await arch.getByRole('button', { name: 'Water', exact: true }).click();
  const pal = page.getByTestId('architect-palette');
  await expect(pal.locator('[data-entry="water_channel"]')).toHaveAttribute('data-tier', 'workbench');
  await expect(pal.locator('[data-entry="water_channel"]')).toHaveAttribute('data-unlocked', 'true');
  await expect(pal.locator('[data-entry="lava_source"]')).toHaveCount(0); // lava is off by default
  await arch.getByRole('button', { name: 'Water channel' }).click();
  await expect(page.getByTestId('water-bar')).toBeVisible();
  await expect(page.getByTestId('water-dig')).toBeChecked(); // a canal is dug into the ground
  await page.keyboard.press('b');

  // Draw a 4-long canal: the director checks it first (preview), then it is queued as the dig and the water, one group.
  const lv = await call(page, 'level');
  const at = async (x: number, z: number) => (await call(page, 'toPage', x + 0.5, lv, z + 0.5))!;
  const a = await at(-8, 2),
    b = await at(-5, 2);
  const preview = post(page, /\/builds$/, 'preview');
  const add = post(page, /\/builds$/, 'add');
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move((a.x + b.x) / 2, (a.y + b.y) / 2, { steps: 4 });
  await page.mouse.move(b.x, b.y, { steps: 4 });
  await page.waitForTimeout(300);
  await page.mouse.up();
  const pv = (await preview).postDataJSON() as { items: Array<{ kind: string }> };
  expect(pv.items.map((i) => i.kind)).toEqual(['remove', 'water']);
  // Water that would run beyond the canal waits for a confirm (the flow shows as holograms meanwhile).
  const confirm = page.getByTestId('water-confirm');
  const asked = await confirm
    .waitFor({ state: 'visible', timeout: 3000 })
    .then(() => true)
    .catch(() => false);
  if (asked) {
    await expect(page.getByTestId('water-confirm-line')).toContainText('4 sources');
    if (shots) await page.screenshot({ path: `${shots}/story-water-confirm.png` });
    await page.getByTestId('water-queue').click();
  }
  const sent = (await add).postDataJSON() as {
    items: Array<{ kind: string; blocks: Array<[number, number, number]>; group?: string }>;
  };
  expect(sent.items.map((i) => i.kind)).toEqual(['remove', 'water']);
  expect(sent.items[1]!.group).toBe(sent.items[0]!.group);
  const cells = sent.items[1]!.blocks;
  expect(cells).toHaveLength(4);
  expect(new Set(cells.map((c) => c[1]))).toEqual(new Set([lv - 1])); // one below the floor: in the trench
  await page.keyboard.press('Escape');

  // The queue lists it with its bucket; the cells are pour ghosts drawn as water holograms.
  await expect
    .poll(async () => (await call(page, 'ghosts')).filter((g) => g[7] === 'pour' && g[3] === 9).length, { timeout: 30_000 })
    .toBe(4);
  await expect.poll(async () => (await call(page, 'holograms'))?.cells ?? 0, { timeout: 30_000 }).toBeGreaterThan(0);
  // Each cell is a water hologram until a pawn pours it (the mock's pawns may get there first: then it is done).
  const first = cells[0]!;
  await expect
    .poll(
      async () =>
        (await call(page, 'hologramAt', first[0], first[1], first[2])) ||
        (await call(page, 'ghosts')).some((g) => g[0] === first[0] && g[1] === first[1] && g[2] === first[2] && g[6] === 'done'),
      { timeout: 30_000 },
    )
    .toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/story-water.png` });
  await page.keyboard.press('u');
  await expect(page.getByTestId('queue-panel')).toContainText('water channel (4 sources)');
});
