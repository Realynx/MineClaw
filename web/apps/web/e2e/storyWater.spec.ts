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
  holograms(): {
    cells: number;
    digs: number;
    boxes: number;
    lod: boolean;
    preview: { ok: number; blocked: number; dig: number };
  } | null;
  hologramAt(x: number, y: number, z: number, kind?: 'block' | 'dig'): boolean;
  hologramKinds(): Record<string, { queued: number; shown: number }>;
  scan(min: [number, number, number], max: [number, number, number]): Promise<number[]>;
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

async function openColony(page: Page) {
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
}

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
  // The level is the floor the map stands on (the drag picks the layer above it): the trench is the floor itself, dug first.
  expect(new Set(cells.map((c) => c[1]))).toEqual(new Set([lv]));
  await page.keyboard.press('Escape');

  // The queue lists it with its bucket; the cells are pour ghosts drawn as water holograms.
  await expect
    .poll(async () => (await call(page, 'ghosts')).filter((g) => g[7] === 'pour' && g[3] === 9).length, { timeout: 30_000 })
    .toBe(4);
  // The trench's dig outlines first (the water in a solid cell waits for its dig), the water once a cell is dug.
  await expect
    .poll(async () => ((h) => (h?.cells ?? 0) + (h?.digs ?? 0))(await call(page, 'holograms')), { timeout: 30_000 })
    .toBeGreaterThan(0);
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

test('a trench through solid ground: red dig outlines in the drag preview, the confirm bar and the queue; water once dug', async ({
  page,
}) => {
  await openColony(page);
  await page.keyboard.press('Space'); // pause the colony: nothing is dug while the outlines are counted
  // Three cells in a row (the fixture island is terraced) whose floor block at the working level is solid ground with open air above: the surface the
  // trench is dug into.
  const lv = await call(page, 'level');
  const hits = await call(page, 'scan', [-30, lv, -30], [30, lv + 1, 30]);
  const solid = new Set<string>(), above = new Set<string>();
  for (let i = 0; i < hits.length; i += 6) {
    const [x, y, z, id] = hits.slice(i, i + 4) as [number, number, number, number];
    if (y === lv && [1, 2, 3, 4, 12, 13].includes(id)) solid.add(`${x},${z}`);
    if (y === lv + 1) above.add(`${x},${z}`);
  }
  const open = (x: number, z: number) => solid.has(`${x},${z}`) && !above.has(`${x},${z}`);
  let row: [number, number] | null = null;
  for (let z = -30; z <= 30 && !row; z++)
    for (let x = -30; x <= 28 && !row; x++) if ([0, 1, 2].every((k) => open(x + k, z))) row = [x, z];
  expect(row, 'a flat stretch of ground at the working level').not.toBeNull();
  const [x0, z0] = row!;
  await call(page, 'focus', x0 + 1, z0);
  await call(page, 'setZoom', 20);
  await page.waitForTimeout(800);
  await page.keyboard.press('b');
  const arch = page.getByTestId('architect');
  await arch.getByRole('button', { name: 'Water', exact: true }).click();
  await arch.getByRole('button', { name: 'Water channel' }).click();
  await expect(page.getByTestId('water-dig')).toBeChecked();
  await page.keyboard.press('b');
  const at = async (x: number, z: number) => (await call(page, 'toPage', x + 0.5, lv, z + 0.5))!;
  // A 3-long channel through that ground.
  const a = await at(x0, z0),
    b = await at(x0 + 2, z0);
  const add = post(page, /\/builds$/, 'add');
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move((a.x + b.x) / 2, (a.y + b.y) / 2, { steps: 4 });
  await page.mouse.move(b.x, b.y, { steps: 4 });
  // While dragging: the 3 cells to dig show as dig outlines, the water in them is not "blocked" by the ground they dig out.
  await expect.poll(async () => (await call(page, 'holograms'))?.preview).toMatchObject({ dig: 3, blocked: 0 });
  if (shots) await page.screenshot({ path: `${shots}/story-trench-preview.png` });
  await page.mouse.up();
  // The confirm bar (when the water would run on) shows the trench too.
  const confirm = page.getByTestId('water-confirm');
  if (await confirm.waitFor({ state: 'visible', timeout: 3000 }).then(() => true, () => false)) {
    await expect.poll(async () => (await call(page, 'holograms'))?.preview.dig).toBe(3);
    if (shots) await page.screenshot({ path: `${shots}/story-trench-confirm.png` });
    await page.getByTestId('water-queue').click();
  }
  const sent = (await add).postDataJSON() as { items: Array<{ kind: string; blocks: Array<[number, number, number]> }> };
  expect(sent.items.map((i) => i.kind)).toEqual(['remove', 'water']);
  const cells = sent.items[0]!.blocks;
  expect(cells.map((c) => `${c[0]},${c[1]},${c[2]}`)).toEqual([0, 1, 2].map((k) => `${x0 + k},${lv},${z0}`));
  await page.keyboard.press('Escape');
  // Queued: every trench cell is a dig outline (the remove build), its water waits underground until the cell is dug.
  await expect
    .poll(async () => (await call(page, 'hologramKinds')).remove, { timeout: 30_000 })
    .toEqual({ queued: 3, shown: 3 });
  for (const c of cells) {
    expect(await call(page, 'hologramAt', c[0], c[1], c[2], 'dig')).toBe(true);
    expect(await call(page, 'hologramAt', c[0], c[1], c[2], 'block')).toBe(false);
  }
  expect((await call(page, 'holograms'))?.digs).toBe(3);
  if (shots) await page.screenshot({ path: `${shots}/story-trench-queued.png` });
  // Unpaused, the pawns dig: a dug cell drops its outline and shows its water (or is already poured).
  await page.keyboard.press('Space');
  const c0 = cells[0]!;
  await expect
    .poll(
      async () => {
        const g = (await call(page, 'ghosts')).filter((x) => x[0] === c0[0] && x[1] === c0[1] && x[2] === c0[2]);
        const dug = g.some((x) => x[7] === 'dig' && x[6] === 'done');
        if (!dug) return 'not dug yet';
        if (await call(page, 'hologramAt', c0[0], c0[1], c0[2], 'dig')) return 'outline left';
        const poured = g.some((x) => x[7] === 'pour' && x[6] === 'done');
        return poured || (await call(page, 'hologramAt', c0[0], c0[1], c0[2], 'block')) ? 'ok' : 'no water hologram';
      },
      { timeout: 90_000, intervals: [1000] },
    )
    .toBe('ok');
});
