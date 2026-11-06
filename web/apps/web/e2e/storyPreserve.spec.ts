import { expect, test, type Page, type Request } from '@playwright/test';

// Story Mode preserve zones (docs/web/story-mode.md "Preserve zones") in mock mode: a Preserve zone painted flat with its depth
// and height (an overlay: the growing zone under it keeps its cells), Preserve switched on in a growing zone's settings, its depth
// changed and a new snapshot taken, and a quarry that can't preserve its blocks.
const shots = process.env.SHOTS_DIR;

type Pt = { x: number; y: number };
interface StoryDebug {
  ready(): boolean;
  toPage(x: number, y: number, z: number): Pt | null;
  center(): [number, number, number];
  level(): number;
}

const story = <K extends keyof StoryDebug>(page: Page, m: K, ...a: Parameters<StoryDebug[K]>) =>
  page.evaluate(
    ([mm, aa]) => (window as unknown as { __story: Record<string, (...x: unknown[]) => unknown> }).__story[mm]!(...aa),
    [m, a] as const,
  ) as Promise<ReturnType<StoryDebug[K]>>;

const zonesPost = (page: Page, op: string) =>
  page.waitForResponse(
    (r) =>
      r.request().method() === 'POST' &&
      /\/zones$/.test(new URL(r.url()).pathname) &&
      (r.request().postDataJSON() as { op?: string }).op === op,
  );
const body = (r: Request) => r.postDataJSON() as Record<string, unknown>;

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
  await page.waitForFunction(() => (window as unknown as { __story?: StoryDebug }).__story?.ready());
  const count = page.getByLabel('Number of pawns');
  while (Number(await count.textContent()) !== 2)
    await page.getByRole('button', { name: Number(await count.textContent()) < 2 ? 'More pawns' : 'Fewer pawns' }).click();
  await page.getByRole('button', { name: 'Found colony' }).click();
  await expect(page.getByTestId('colonist-bar').locator('[data-kind="pawn"]')).toHaveCount(2);
  await page.waitForTimeout(1500); // the camera settles over home
}

/** A left drag on the map between two cells of the level, released. */
async function paint(page: Page, lv: number, a: [number, number], b: [number, number]) {
  const pa = (await story(page, 'toPage', a[0] + 0.5, lv, a[1] + 0.5))!;
  const pb = (await story(page, 'toPage', b[0] + 0.5, lv, b[1] + 0.5))!;
  await page.mouse.move(pa.x, pa.y);
  await page.mouse.down();
  await page.mouse.move((pa.x + pb.x) / 2, (pa.y + pb.y) / 2, { steps: 4 });
  await page.mouse.move(pb.x, pb.y, { steps: 4 });
  await page.waitForTimeout(300);
  await page.mouse.up();
}

const colonyTab = (page: Page) => page.getByTestId('tab-bar').getByRole('button', { name: /^Colony/ }).click();

test('preserve: a Preserve zone painted with its depth, Preserve on a growing zone, its depth changed, a quarry refused', async ({
  page,
}) => {
  await openColony(page);
  const [cx, , cz] = (await story(page, 'center')).map(Math.floor) as [number, number, number];
  const lv = await story(page, 'level');

  // A growing zone, then a Preserve zone over part of it (depth 2, height 4): an overlay, the growing zone keeps its cells.
  await page.keyboard.press('KeyB');
  await page.getByRole('button', { name: 'Zones', exact: true }).click();
  await page.getByRole('button', { name: 'Growing zone' }).click();
  const farmAck = zonesPost(page, 'add');
  await paint(page, lv, [cx - 4, cz - 1], [cx - 1, cz + 1]);
  const farm = (await (await farmAck).json()) as { id: string };
  await page.getByRole('button', { name: 'Preserve', exact: true }).click();
  const range = page.getByTestId('preserve-paint-range');
  await expect(range.getByLabel('Depth below')).toHaveValue('1');
  await expect(range.getByLabel('Height above')).toHaveValue('3');
  await range.getByLabel('Depth below').fill('2');
  await range.getByLabel('Depth below').press('Enter');
  await range.getByLabel('Height above').fill('4');
  await range.getByLabel('Height above').press('Tab');
  const keepAck = zonesPost(page, 'add');
  await paint(page, lv, [cx - 2, cz - 1], [cx + 1, cz]);
  const keep = await keepAck;
  expect(body(keep.request())).toMatchObject({ op: 'add', kind: 'preserve', below: 2, above: 4 });
  const kept = (await keep.json()) as { id: string; shrunk?: unknown[] };
  expect(kept.shrunk ?? []).toEqual([]);
  if (shots) await page.screenshot({ path: `${shots}/preserve-zone-painted.png` });
  await page.keyboard.press('Escape'); // the tool
  await page.keyboard.press('Escape'); // the Architect

  // The Other zones window lists the Preserve zone: always on, its y range and status.
  await colonyTab(page);
  const other = page.getByTestId('other-zones').locator(`[data-zone="${kept.id}"]`);
  await expect(other.getByTestId('zone-preserve')).toContainText('Always on');
  await expect(other.getByTestId('preserve-range')).toHaveText(`y ${lv - 2} → ${lv + 4}`);
  await expect(other.getByTestId('preserve-status')).toHaveText('intact');

  // The growing zone's settings: switch Preserve on (the defaults: 1 under, 3 over), then 5 deep, then a new snapshot.
  const grow = page.getByTestId('growing-zones').locator(`[data-zone="${farm.id}"]`);
  const toggle = grow.getByRole('switch', { name: 'Preserve blocks' });
  await expect(toggle).not.toBeChecked();
  const on = zonesPost(page, 'preserve');
  await toggle.check();
  expect(body((await on).request())).toEqual({ op: 'preserve', id: farm.id, on: true });
  await expect(grow.getByTestId('preserve-range')).toHaveText(`y ${lv - 1} → ${lv + 3}`);
  await expect(grow.getByTestId('preserve-status')).toHaveText('intact');
  await expect(grow.getByTestId('preserve-counts')).toContainText(`${12 * 5} blocks kept`);
  const deeper = zonesPost(page, 'preserve');
  await grow.getByLabel('Depth below').fill('5');
  await grow.getByLabel('Depth below').press('Enter');
  expect(body((await deeper).request())).toEqual({ op: 'preserve', id: farm.id, below: 5 });
  await expect(grow.getByTestId('preserve-range')).toHaveText(`y ${lv - 5} → ${lv + 3}`);
  const retake = zonesPost(page, 'preserve');
  await grow.getByRole('button', { name: 'Re-take snapshot' }).click();
  expect(body((await retake).request())).toEqual({ op: 'preserve', id: farm.id, retake: true });
  if (shots) await page.screenshot({ path: `${shots}/preserve-growing-zone.png` });

  // Switched off: the details go.
  const off = zonesPost(page, 'preserve');
  await toggle.uncheck();
  expect(body((await off).request())).toEqual({ op: 'preserve', id: farm.id, on: false });
  await expect(grow.getByTestId('preserve-range')).toHaveCount(0);

  // A quarry can't preserve its blocks: the switch is disabled and says why.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur()); // the keys skip a focused field
  await page.keyboard.press('KeyB');
  await page.getByRole('button', { name: 'Zones', exact: true }).click();
  await page.getByRole('button', { name: 'Quarry', exact: true }).click();
  const qAck = zonesPost(page, 'add');
  await paint(page, lv, [cx + 3, cz + 3], [cx + 5, cz + 5]);
  await qAck;
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');
  await colonyTab(page);
  const quarry = page.getByTestId('quarry-zones').getByTestId('quarry-zone');
  await expect(quarry.getByRole('switch', { name: 'Preserve blocks' })).toBeDisabled();
  await expect(quarry.getByTestId('zone-preserve')).toContainText('A quarry digs its blocks');
});
