import { expect, test, type Page, type Request } from '@playwright/test';

// Story Mode in mock mode: the colony director runs in the page (MSW) on the viewer's fixture island. Every gesture is
// checked by the request it sends. SHOTS_DIR=<dir> also saves screenshots there.
const shots = process.env.SHOTS_DIR;

type Pt = { x: number; y: number };
interface StoryDebug {
  ready(): boolean;
  toPage(x: number, y: number, z: number): Pt | null;
  bodies(): Array<{ name: string; x: number; y: number }>;
  center(): [number, number, number];
  focus(x: number, z: number): void;
  setZoom(z: number): void;
  mode(): string;
  level(): number;
  beds(): Array<{ pos: [number, number, number]; pawn: string | null }>;
  failed(): Array<{ id: string | number; cell: [number, number, number] | null; x: number | null; y: number | null }>;
}

async function signIn(page: Page, empty: boolean) {
  await page.addInitScript((e) => {
    if (e) sessionStorage.setItem('mcx-mock-story', 'empty');
  }, empty);
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

async function openStory(page: Page) {
  await page.getByRole('link', { name: 'Story Mode' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: StoryDebug }).__story?.ready());
}

/** Calls one of the map's test hooks (`window.__story`, mock mode only). */
function story<K extends keyof StoryDebug>(
  page: Page,
  method: K,
  ...args: Parameters<StoryDebug[K]>
): Promise<ReturnType<StoryDebug[K]>> {
  return page.evaluate(
    ([m, a]) => (window as unknown as { __story: Record<string, (...x: unknown[]) => unknown> }).__story[m]!(...a),
    [method, args] as const,
  ) as Promise<ReturnType<StoryDebug[K]>>;
}
const pageAt = async (page: Page, x: number, z: number, dy = 1) =>
  (await story(page, 'toPage', x, (await story(page, 'level')) + dy, z))!;

async function foundColony(page: Page, pawns = 4) {
  const count = page.getByLabel('Number of pawns');
  while (Number(await count.textContent()) !== pawns)
    await page
      .getByRole('button', { name: Number(await count.textContent()) < pawns ? 'More pawns' : 'Fewer pawns' })
      .click();
  await page.getByRole('button', { name: 'Found colony' }).click();
  await expect(page.getByTestId('colonist-bar').locator('[data-kind="pawn"]')).toHaveCount(pawns);
  await page.waitForTimeout(1500); // the camera settles over home, tokens interpolate in
}

const post = (page: Page, path: RegExp) =>
  page.waitForRequest((r: Request) => r.method() === 'POST' && path.test(new URL(r.url()).pathname));
const body = (r: Request) => r.postDataJSON() as Record<string, unknown>;

/** A left drag on the map between two world cells on the level's floor plane. */
async function dragCells(page: Page, a: [number, number, number], b: [number, number, number]) {
  const pa = (await story(page, 'toPage', a[0] + 0.5, a[1], a[2] + 0.5))!;
  const pb = (await story(page, 'toPage', b[0] + 0.5, b[1], b[2] + 0.5))!;
  await page.mouse.move(pa.x, pa.y);
  await page.mouse.down();
  await page.mouse.move((pa.x + pb.x) / 2, (pa.y + pb.y) / 2, { steps: 4 });
  await page.mouse.move(pb.x, pb.y, { steps: 4 });
  await page.waitForTimeout(400); // the live count catches up
  return { pa, pb };
}

test('found a colony, box-select, right-click a tree to chop, priorities, a schedule, draft, the top bar', async ({
  page,
}) => {
  await signIn(page, true);
  await openStory(page);
  // With no colony the map settles over the main bot (the fixture's Alice, renamed maid_chan, circles the island centre 10
  // blocks out), not the world origin; the form says where the colony will settle.
  await expect(page.getByTestId('settle-at')).toContainText(/Settles at -?\d+, -?\d+: the map centre/);
  const settle = (await page.getByTestId('settle-at').textContent())!.match(/Settles at (-?\d+), (-?\d+)/)!;
  expect(Math.hypot(Number(settle[1]), Number(settle[2]))).toBeGreaterThan(5);
  const founded = post(page, /\/api\/story\/colonies$/);
  await foundColony(page, 4);
  const home = body(await founded).home as { x: number; y: number; z: number };
  expect(Math.hypot(home.x, home.z)).toBeGreaterThan(5);
  // Box-select three of the four pawns (they stand in a row).
  const bodies = (await story(page, 'bodies')).filter((b) => b.name.startsWith('maid_s1_')).sort((a, b) => a.x - b.x);
  expect(bodies).toHaveLength(4);
  const three = bodies.slice(0, 3);
  const gap = (bodies[3]!.x - bodies[2]!.x) / 2;
  await page.mouse.move(three[0]!.x - gap, Math.min(...three.map((b) => b.y)) - 20);
  await page.mouse.down();
  await page.mouse.move(three[2]!.x + gap, Math.max(...three.map((b) => b.y)) + 20, { steps: 6 });
  await page.mouse.up();
  await expect(page.getByTestId('inspector')).toContainText('3 selected');
  if (shots) await page.screenshot({ path: `${shots}/story-selected.png` });

  // Right-click the tree at (6, ·, 6): the menu offers to chop it; the order goes to the three pawns.
  const tree = await pageAt(page, 6.5, 6.5);
  await page.mouse.click(tree.x, tree.y, { button: 'right' });
  const menu = page.getByTestId('order-menu');
  await expect(menu).toBeVisible();
  if (shots) await page.screenshot({ path: `${shots}/story-order-menu.png` });
  const chop = post(page, /\/api\/story\/colonies\/[^/]+\/orders$/);
  await menu.getByRole('menuitem', { name: 'Chop this tree' }).click();
  const order = body(await chop);
  expect((order.pawns as string[]).sort()).toEqual(three.map((b) => b.name).sort());
  expect(order.order).toMatchObject({ kind: 'chop', target: { x: 6, z: 6 } });

  // Work priorities: a click cycles the cell.
  await page.keyboard.press('p');
  const work = page.getByTestId('work-panel');
  await expect(work).toBeVisible();
  const first = three[0]!.name;
  const prio = post(page, /\/priorities$/);
  await work.locator(`[data-cell="${first}:haul"]`).click();
  expect(body(await prio)).toMatchObject({ pawns: [first], work: { haul: expect.any(Number) } });
  if (shots) await page.screenshot({ path: `${shots}/story-work.png` });

  // Paint a schedule: drag the sleep brush over three hours of one pawn.
  await page.keyboard.press('y');
  const sched = page.getByTestId('schedule-panel');
  await sched.getByRole('button', { name: 'Sleep' }).click();
  const from = sched.locator(`[data-hour="${first}:2"]`),
    to = sched.locator(`[data-hour="${first}:4"]`);
  const fb = (await from.boundingBox())!,
    tb = (await to.boundingBox())!;
  const paint = post(page, /\/schedule$/);
  await page.mouse.move(fb.x + 5, fb.y + 5);
  await page.mouse.down();
  await page.mouse.move(tb.x + 5, tb.y + 5, { steps: 5 });
  await page.mouse.up();
  expect(body(await paint)).toEqual({ pawns: [first], hours: { '2': 'sleep', '3': 'sleep', '4': 'sleep' } });
  if (shots) await page.screenshot({ path: `${shots}/story-schedule.png` });
  await page.keyboard.press('Escape'); // closes the tab
  await page.mouse.move(700, 500);

  // Draft and undraft with R.
  const d1 = post(page, /\/draft$/);
  await page.keyboard.press('r');
  expect(body(await d1)).toMatchObject({ drafted: true });
  await expect(page.getByTestId('colonist-bar').locator(`[data-portrait="${first}"]`)).toHaveAttribute(
    'data-drafted',
    'true',
  );
  const d2 = post(page, /\/draft$/);
  await page.keyboard.press('r');
  expect(body(await d2)).toMatchObject({ drafted: false });

  // The top bar: click selects only it, Shift adds, a double-click centres the map on the pawn.
  const bar = page.getByTestId('colonist-bar');
  await bar.locator(`[data-portrait="${bodies[3]!.name}"]`).click();
  await expect(page.getByTestId('inspector')).not.toContainText('selected');
  await bar.locator(`[data-portrait="${bodies[0]!.name}"]`).click({ modifiers: ['Shift'] });
  await expect(page.getByTestId('inspector')).toContainText('2 selected');
  await story(page, 'focus', 40, 40);
  await page.waitForTimeout(300);
  await bar.locator(`[data-portrait="${bodies[1]!.name}"]`).dblclick();
  await page.waitForTimeout(1200);
  const c = await story(page, 'center');
  expect(Math.abs(c[0] - 40)).toBeGreaterThan(20); // moved back from (40, 40) to the pawn by home
  // A single click on a pawn that is off the map's picture also finds it.
  await story(page, 'focus', 40, 40);
  await page.waitForTimeout(300);
  await bar.locator(`[data-portrait="${bodies[2]!.name}"]`).click();
  await page.waitForTimeout(1200);
  expect(Math.abs((await story(page, 'center'))[0] - 40)).toBeGreaterThan(20);
  // Typing in a field never drives the map.
  await page.keyboard.press('i');
  await page.getByLabel('Colony name').fill('rrr');
  await expect(page.getByTestId('colonist-bar').locator('[data-drafted="true"]')).toHaveCount(0);
  // Beds: click one of the camp's beds on the map, give it to a pawn; that pawn's inspector shows it. (The Colony tab is
  // closed first: it covers the middle of the map.)
  const colonyTab = page.getByTestId('tab-bar').getByRole('button', { name: /^Colony/ });
  await colonyTab.click();
  const bed = (await story(page, 'beds'))[0]!;
  await story(page, 'focus', bed.pos[0], bed.pos[2]);
  await page.waitForTimeout(600);
  const bedAt = (await story(page, 'toPage', bed.pos[0] + 0.5, bed.pos[1] + 0.5, bed.pos[2] + 0.5))!;
  await page.mouse.click(bedAt.x, bedAt.y);
  const bedPanel = page.getByTestId('bed-inspector');
  await expect(bedPanel).toBeVisible();
  const assigned = post(page, /\/api\/story\/colonies\/[^/]+\/beds$/);
  await bedPanel.getByLabel('Set owner').selectOption(bodies[0]!.name);
  expect(body(await assigned)).toEqual({
    op: 'assign',
    bed: { x: bed.pos[0], y: bed.pos[1], z: bed.pos[2] },
    pawn: bodies[0]!.name,
  });
  await expect(bedPanel.getByLabel('Set owner')).toHaveValue(bodies[0]!.name);
  if (shots) await page.screenshot({ path: `${shots}/story-bed.png` });
  await bar.locator(`[data-portrait="${bodies[0]!.name}"]`).click();
  await expect(page.getByTestId('pawn-bed')).toContainText(`Bed at ${bed.pos[0]}, ${bed.pos[2]}`);

  // Migrate home to the map centre (confirmed first): the request names the centre column, the toast and the panel say
  // where home is now.
  await colonyTab.click();
  await story(page, 'focus', 60, -20);
  const homePanel = page.getByTestId('colony-home');
  await expect(homePanel).toContainText('Here is 60, -20');
  const moved = post(page, /\/api\/story\/colonies\/[^/]+\/home$/);
  await homePanel.getByRole('button', { name: 'Move home here' }).click();
  await expect(page.getByTestId('migrate-confirm')).toContainText('Migrate home to 60, -20?');
  await page.getByTestId('migrate-confirm').getByRole('button', { name: 'Migrate' }).click();
  expect(body(await moved)).toMatchObject({ home: { x: 60, z: -20 } });
  await expect(page.getByText(/Home moved to 60, -20/)).toBeVisible();
  await expect(homePanel).toContainText('Home60, -20');
  if (shots) await page.screenshot({ path: `${shots}/story-move-home.png` });
});

test('drafted pawns line up along a right-drag; the time of day slider', async ({ page }) => {
  await signIn(page, true);
  await openStory(page);
  await foundColony(page, 3);
  const bar = page.getByTestId('colonist-bar');
  const names = await bar
    .locator('[data-kind="pawn"]')
    .evaluateAll((els) => els.map((e) => (e as HTMLElement).dataset.portrait!));
  expect(names).toHaveLength(3);
  await bar.locator(`[data-portrait="${names[0]}"]`).click();
  for (const n of names.slice(1)) await bar.locator(`[data-portrait="${n}"]`).click({ modifiers: ['Shift'] });
  await expect(page.getByTestId('inspector')).toContainText('3 selected');
  await page.mouse.move(700, 500);
  await page.keyboard.press('r');
  await expect(bar.locator('[data-kind="pawn"][data-drafted="true"]')).toHaveCount(3);

  // Right-drag a line east-west just south of the centre: one move per pawn, each to its own cell along the line.
  const c = await story(page, 'center');
  const lv = await story(page, 'level');
  const a: [number, number] = [Math.floor(c[0]) - 4, Math.floor(c[2]) + 3];
  const b: [number, number] = [Math.floor(c[0]) + 4, Math.floor(c[2]) + 3];
  const pa = (await story(page, 'toPage', a[0] + 0.5, lv + 1, a[1] + 0.5))!;
  const pb = (await story(page, 'toPage', b[0] + 0.5, lv + 1, b[1] + 0.5))!;
  const orders: Request[] = [];
  page.on('request', (r) => {
    if (r.method() === 'POST' && /\/api\/story\/colonies\/[^/]+\/orders$/.test(new URL(r.url()).pathname))
      orders.push(r);
  });
  await page.mouse.move(pa.x, pa.y);
  await page.mouse.down({ button: 'right' });
  await page.mouse.move((pa.x + pb.x) / 2, (pa.y + pb.y) / 2, { steps: 4 });
  await page.mouse.move(pb.x, pb.y, { steps: 4 });
  await page.waitForTimeout(400); // the preview reads the standing heights
  if (shots) await page.screenshot({ path: `${shots}/story-formation.png` });
  await page.mouse.up({ button: 'right' });
  await expect.poll(() => orders.length).toBe(3);
  await expect(page.getByTestId('order-menu')).toHaveCount(0);
  const bodies = orders.map(body) as Array<{
    pawns: string[];
    order: { kind: string; target: { x: number; y: number; z: number } };
  }>;
  expect(bodies.map((o) => o.pawns.length)).toEqual([1, 1, 1]);
  expect(bodies.map((o) => o.pawns[0]).sort()).toEqual([...names].sort());
  expect(bodies.every((o) => o.order.kind === 'move')).toBe(true);
  const cells = bodies.map((o) => `${o.order.target.x},${o.order.target.z}`);
  expect(new Set(cells).size).toBe(3);
  for (const o of bodies) {
    // on the line (snapping to a standable cell moves a slot at most a few cells)
    expect(Math.abs(o.order.target.z - a[1])).toBeLessThanOrEqual(3);
    expect(o.order.target.x).toBeGreaterThanOrEqual(a[0] - 3);
    expect(o.order.target.x).toBeLessThanOrEqual(b[0] + 3);
  }
  // Spread along the line, not bunched: the ends are a good part of the line apart.
  const xs = bodies.map((o) => o.order.target.x);
  expect(Math.max(...xs) - Math.min(...xs)).toBeGreaterThanOrEqual(5);
  await expect(page.getByTestId('story-toast')).toContainText('Formation: 3 pawns along the line');

  // The time of day: drag the slider, one POST on release with the tick the slider shows.
  const slider = page.getByLabel('Time of day');
  await expect(slider).toBeEnabled();
  const sb = (await slider.boundingBox())!;
  const times: Request[] = [];
  page.on('request', (r) => {
    if (r.method() === 'POST' && new URL(r.url()).pathname === '/api/story/time') times.push(r);
  });
  await page.mouse.move(sb.x + sb.width * 0.25, sb.y + sb.height / 2);
  await page.mouse.down();
  await page.mouse.move(sb.x + sb.width * 0.5, sb.y + sb.height / 2, { steps: 6 });
  await page.mouse.up();
  const shown = Number(await slider.inputValue());
  await expect.poll(() => times.length).toBe(1);
  expect(Math.abs(shown - 12000)).toBeLessThan(1500);
  expect(body(times[0]!)).toEqual({ time: shown });
  await expect(page.getByTestId('story-toast')).toContainText(/Time set to \d\d:\d\d via maid_chan/);
  await page.waitForTimeout(600);
  expect(times).toHaveLength(1); // one commit per release
  if (shots) await page.screenshot({ path: `${shots}/story-time.png` });
});

test('designate tools: chop a grove for a pawn and main, haul a drop, cancel; chat to main', async ({ page }) => {
  await signIn(page, true);
  await openStory(page);
  await foundColony(page, 3);
  await story(page, 'focus', -1, -3);
  await story(page, 'setZoom', 64);
  await page.waitForTimeout(800);
  const bar = page.getByTestId('colonist-bar');
  // Select a colony pawn and main in the top bar.
  await bar.locator('[data-kind="pawn"]').first().click();
  await bar.locator('[data-portrait="maid_chan"]').click({ modifiers: ['Shift'] });
  await expect(page.getByTestId('inspector')).toContainText('2 selected');
  // The chop tool (C), dragged over the grove: three trees; the colony designates, main gets a directive.
  await page.keyboard.press('c');
  await expect(page.getByTestId('tool-options')).toContainText('Chop wood');
  const lv = await story(page, 'level');
  await dragCells(page, [-10, lv, -14], [8, lv, 8]);
  if (shots) await page.screenshot({ path: `${shots}/story-chop-drag.png` });
  const des = post(page, /\/designations$/);
  const dir = post(page, /\/api\/story\/directives$/);
  await page.mouse.up();
  expect(body(await des)).toMatchObject({ op: 'add', kind: 'chop' });
  expect(body(await dir)).toMatchObject({ bots: ['maid_chan'], directive: { kind: 'chop' } });
  await expect(page.getByTestId('story-toast')).toContainText(
    /Chop wood: 3 trees · colony: designated · sent to maid_chan \(AI\)/,
  );
  await page.waitForTimeout(1200);
  if (shots) await page.screenshot({ path: `${shots}/story-chop-marked.png` });

  // Haul (L) over the cobblestone drop by (3, 0).
  await page.keyboard.press('l');
  await dragCells(page, [1, lv, -2], [6, lv, 3]);
  const haul = post(page, /\/designations$/);
  await page.mouse.up();
  expect(body(await haul)).toMatchObject({ op: 'add', kind: 'haul', entities: [200] });
  await expect(page.getByTestId('story-toast')).toContainText('Haul: 1 item');

  // Cancel (Z) over the grove removes them again.
  await page.keyboard.press('z');
  await dragCells(page, [-10, lv, -14], [8, lv, 8]);
  const cancel = post(page, /\/designations$/);
  await page.mouse.up();
  expect(body(await cancel)).toMatchObject({ op: 'remove' });
  await expect(page.getByTestId('story-toast')).toContainText(/Cancelled [1-9]/);

  // Chat to main (T): ordinary chat as the signed-in player.
  await page.keyboard.press('Escape');
  await bar.locator('[data-portrait="maid_chan"]').click();
  await page.keyboard.press('t');
  const box = page.getByRole('textbox', { name: /chat to the selected bots/i });
  await expect(box).toBeFocused();
  const said = post(page, /\/api\/bots\/maid_chan\/chat$/);
  await box.fill('come here');
  await box.press('Enter');
  expect(body(await said)).toMatchObject({ text: 'come here' });
  await expect(page.getByTestId('chat-overlay')).toContainText('come here');
});

test('storage, bills, the build queue and the angled view', async ({ page }) => {
  await signIn(page, true);
  await openStory(page);
  await foundColony(page, 3);
  await story(page, 'focus', 1, -3);
  await story(page, 'setZoom', 36);
  await page.waitForTimeout(800);
  const lv = await story(page, 'level');

  // Register the double chest and the single chest by dragging the storage tool over them.
  await page.keyboard.press('b');
  const arch = page.getByTestId('architect');
  await arch.getByRole('button', { name: 'Storage' }).click();
  await arch.getByRole('button', { name: 'Register chests' }).click();
  await dragCells(page, [1, lv, -5], [7, lv, -3]);
  const reg = post(page, /\/storage$/);
  await page.mouse.up();
  expect(body(await reg)).toMatchObject({ op: 'assign_area' });
  await expect(page.getByTestId('story-toast')).toContainText('2 chests registered');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(1500);
  // The double chest: Wood only, preferred.
  const chestA = await pageAt(page, 2.5, -3.5);
  await page.mouse.click(chestA.x, chestA.y);
  const inspect = page.getByTestId('storage-inspector');
  await expect(inspect).toBeVisible();
  const clear = post(page, /\/storage$/);
  await inspect.getByRole('button', { name: 'Clear' }).click();
  expect(body(await clear)).toMatchObject({ op: 'set', filter: { allow: [], deny: [] } });
  const wood = post(page, /\/storage$/);
  await inspect.getByRole('checkbox', { name: 'Wood', exact: true }).click();
  expect(body(await wood)).toMatchObject({ op: 'set', filter: { allow: ['wood'], deny: [] } });
  const pri = post(page, /\/storage$/);
  await inspect.getByLabel('Storage priority').selectOption('preferred');
  expect(body(await pri)).toMatchObject({ op: 'set', priority: 'preferred' });
  await expect(inspect).toContainText(/Preferred · Wood/);
  if (shots) await page.screenshot({ path: `${shots}/story-storage.png` });
  // The single chest: Food.
  const chestB = await pageAt(page, 6.5, -3.5);
  await page.mouse.click(chestB.x, chestB.y);
  await expect(inspect).toBeVisible();
  await inspect.getByRole('button', { name: 'Clear' }).click();
  const food = post(page, /\/storage$/);
  await inspect.getByRole('checkbox', { name: 'Food', exact: true }).click();
  expect(body(await food)).toMatchObject({ filter: { allow: ['food'] } });

  // Bills at the camp's crafting table: sticks until 64, then a second bill, reorder, pause.
  await page.keyboard.press('Escape');
  const table = await pageAt(page, -3.5, -1.5);
  await page.mouse.click(table.x, table.y);
  const bills = page.getByTestId('bills-inspector');
  await expect(bills).toBeVisible();
  await bills.getByRole('button', { name: 'Add bill' }).click();
  await page.getByLabel('Search recipes').fill('stick');
  await page.getByRole('button', { name: 'Recipe Sticks' }).click();
  const dialog = page.getByTestId('bill-dialog');
  await dialog.getByLabel('Repeat mode').selectOption('until');
  await dialog.getByLabel('Ingredient search radius').fill('48');
  await dialog.getByLabel('Worker').selectOption({ index: 1 });
  if (shots) await page.screenshot({ path: `${shots}/story-bill-dialog.png` });
  const add = post(page, /\/bills$/);
  await page.getByRole('button', { name: 'Add bill' }).last().click();
  const added = body(await add);
  expect(added).toMatchObject({ op: 'add', bill: { recipe: 'sticks', mode: 'until', target: 64, radius: 48 } });
  expect((added.bill as { pawn: string }).pawn).toMatch(/^maid_s1_/);
  await bills.getByRole('button', { name: 'Add bill' }).click();
  await page.getByRole('button', { name: 'Recipe Torches' }).click();
  const add2 = post(page, /\/bills$/);
  await page.getByRole('button', { name: 'Add bill' }).last().click();
  await add2;
  await expect(bills.locator('[data-bill]')).toHaveCount(2);
  const reorder = post(page, /\/bills$/);
  await bills.locator('[data-bill]').nth(1).dragTo(bills.locator('[data-bill]').nth(0));
  expect(body(await reorder)).toMatchObject({ op: 'reorder' });
  const pause = post(page, /\/bills$/);
  await bills
    .getByRole('button', { name: /^Pause / })
    .first()
    .click();
  expect(body(await pause)).toMatchObject({ op: 'pause' });
  if (shots) await page.screenshot({ path: `${shots}/story-bills.png` });

  // The build queue: a 5×5 hut (walls 3 high, a door, a flat roof).
  await page.keyboard.press('Escape');
  await page.keyboard.press('b');
  await arch.getByRole('button', { name: 'Structure' }).click();
  await arch.getByRole('button', { name: 'Wall' }).click();
  await expect(page.getByTestId('wall-height')).toHaveText('3');
  const wall = post(page, /\/builds$/);
  await dragCells(page, [-8, lv, 2], [-4, lv, 6]);
  await page.mouse.up();
  const w = body(await wall) as { items: Array<{ kind: string; height: number }> };
  expect(w.items[0]).toMatchObject({ kind: 'wall', height: 3 });
  await arch.getByRole('button', { name: 'Door' }).click();
  const doorAt = (await story(page, 'toPage', -5.5, lv, 2.5))!;
  const door = post(page, /\/builds$/);
  await page.mouse.click(doorAt.x, doorAt.y);
  expect((body(await door) as { items: Array<{ kind: string }> }).items[0]?.kind).toBe('door');
  await arch.getByRole('button', { name: 'Roof' }).click();
  const roof = post(page, /\/builds$/);
  await dragCells(page, [-8, lv, 2], [-4, lv, 6]);
  await page.mouse.up();
  expect((body(await roof) as { items: Array<{ kind: string; roof: unknown }> }).items[0]).toMatchObject({
    kind: 'roof',
    roof: { style: 'flat' },
  });
  await page.waitForTimeout(1200);
  if (shots) await page.screenshot({ path: `${shots}/story-ghosts.png` });
  // Reorder two builds in the queue panel.
  await page.keyboard.press('Escape');
  await page.keyboard.press('u');
  const queue = page.getByTestId('queue-panel');
  await expect(queue.locator('[data-build]')).toHaveCount(3);
  const re = post(page, /\/builds$/);
  await queue
    .getByRole('button', { name: /^Build .* first$/ })
    .last()
    .click();
  expect(body(await re)).toMatchObject({ op: 'reorder' });
  if (shots) await page.screenshot({ path: `${shots}/story-queue.png` });

  // The angled view: place a single block against a face, then back to top-down.
  await page.keyboard.press('Escape');
  await page.keyboard.press('v');
  await expect.poll(() => story(page, 'mode')).toBe('iso');
  await page.waitForTimeout(1000);
  await page.keyboard.press('b');
  await arch.getByRole('button', { name: 'Structure' }).click();
  await arch.getByRole('button', { name: 'Single block' }).click();
  const at = await pageAt(page, -6.5, 4.5);
  await page.mouse.move(at.x, at.y);
  await page.waitForTimeout(400);
  const one = post(page, /\/builds$/);
  await page.mouse.click(at.x, at.y);
  expect((body(await one) as { items: Array<{ kind: string; blocks: number[][] }> }).items[0]).toMatchObject({
    kind: 'block',
  });
  if (shots) await page.screenshot({ path: `${shots}/story-angled.png` });
  await page.keyboard.press('v');
  await expect.poll(() => story(page, 'mode')).toBe('top');
});

test('a design from the design pages is placed on the map and queued (/story?design=N)', async ({ page }) => {
  await signIn(page, false);
  await page.goto('/story?design=12');
  await page.waitForFunction(() => (window as unknown as { __story?: StoryDebug }).__story?.ready());
  await expect(page.getByTestId('tool-options')).toContainText('Design:');
  const at = await pageAt(page, 6.5, 2.5);
  await page.mouse.move(at.x, at.y);
  await page.keyboard.press('e'); // turned a quarter
  await page.waitForTimeout(400);
  if (shots) await page.screenshot({ path: `${shots}/story-design.png` });
  const queued = post(page, /\/builds$/);
  await page.mouse.click(at.x, at.y);
  const b = body(await queued) as {
    op: string;
    items: Array<{ kind: string; blocks: number[][]; material: number[] }>;
  };
  expect(b.op).toBe('add');
  expect(b.items.every((i) => i.kind === 'block' && i.blocks.length > 0)).toBe(true);
  // The mock design: a 5×5 hut, walls 3 high with a 2-high doorway, a plank roof.
  expect(b.items.reduce((n, i) => n + i.blocks.length, 0)).toBe(46 + 25);
  await expect(page.getByTestId('story-toast')).toContainText(/Queued .*: 71 blocks/);
});

test('offline pawns say why and come back; a failed cut says why; full screen keeps the whole HUD', async ({
  page,
}) => {
  // The seeded colony (mocks/data/story.ts): one pawn kicked "not white-listed", one kept out by the player cap
  // (1 seat free, 2 kept for regulars), and a cut over the tall grass that kept failing on a hive claim.
  await signIn(page, false);
  await openStory(page);
  const bar = page.getByTestId('colonist-bar');
  const pawnBadges = bar.locator('[data-kind="pawn"]').getByTestId('offline-badge');
  await expect(pawnBadges).toHaveCount(2);
  const kicked = bar.locator('[data-kind="pawn"][data-offline="true"]').last();
  await kicked.hover();
  await expect(bar.getByTestId('offline-reason')).toHaveText('Offline: not whitelisted — re-adding');
  // Its inspector says why and can bring it online.
  await kicked.click();
  await expect(page.getByTestId('pawn-offline-reason')).toHaveText('not whitelisted — re-adding');
  if (shots) await page.screenshot({ path: `${shots}/story-offline.png` });

  // "Bring 2 online" on the bar: every offline pawn. The kicked one is re-added; the other is refused by the cap, and the
  // Colony tab says why with the explicit "Use regulars' seats".
  const all = post(page, /\/api\/story\/colonies\/[^/]+\/online$/);
  await bar.getByTestId('bar-bring-online').click();
  expect(body(await all)).toEqual({ pawns: null });
  const box = page.getByTestId('colony-bring-online');
  await expect(box).toBeVisible();
  await expect(box.getByTestId('online-blocked')).toContainText(
    'server full (player cap: 1 free, 2 kept for regulars)',
  );
  await expect(box).toContainText('A regular may then find the server full until a pawn leaves');
  const over = post(page, /\/api\/story\/colonies\/[^/]+\/online$/);
  await box.getByTestId('use-regulars-seats').click();
  expect(body(await over)).toEqual({ pawns: null, overCap: true });
  await expect(pawnBadges).toHaveCount(0, { timeout: 15_000 });
  await page.keyboard.press('Escape'); // the Colony tab
  await page.keyboard.press('Escape'); // the selection

  // A failed cut: the red ring and one calm label for the cluster; clicking it opens why, in full, with Retry now.
  const failed = await story(page, 'failed');
  expect(failed.length).toBeGreaterThan(0);
  const cell = failed[0]!.cell!;
  await story(page, 'focus', cell[0], cell[2]);
  await story(page, 'setZoom', 24);
  const label = page.getByTestId('designation-label').first();
  await expect(label).toBeVisible();
  await expect(label).toHaveText(/busy — claimed|Busy: claimed by another bot/);
  if (shots) await page.screenshot({ path: `${shots}/story-failed-label.png` });
  await label.click();
  const inspector = page.getByTestId('designation-inspector');
  await expect(inspector.getByTestId('designation-reason')).toContainText('another hive bot is working on that part');
  await expect(inspector.getByTestId('designation-fails')).toHaveText('3 failed');
  const retry = post(page, /\/designations$/);
  await inspector.getByRole('button', { name: 'Retry now' }).click();
  expect(body(await retry)).toMatchObject({ op: 'retry', ids: [failed[0]!.id] });
  await page.keyboard.press('Escape');

  // Full screen: the button (Shift+F works too) puts the map AND the HUD in it; a toast shows inside; it leaves cleanly.
  const fsEl = () => page.evaluate(() => document.fullscreenElement?.getAttribute('data-testid') ?? null);
  await page.getByTestId('story-fullscreen').click();
  await expect.poll(fsEl).toBe('story-mode');
  await expect(bar).toBeVisible();
  await expect(page.getByTestId('tab-bar')).toBeVisible();
  // The map follows the new size (its overlay canvas fills the screen).
  await expect
    .poll(() =>
      page.evaluate(() => {
        const c = document.querySelector(
          '[data-testid="story-map"] canvas[aria-label^="Colony map"]',
        ) as HTMLCanvasElement;
        return c.width === window.innerWidth && c.clientWidth === window.innerWidth;
      }),
    )
    .toBe(true);
  await page.keyboard.press('Space'); // pause: its toast
  const toast = page.getByTestId('story-toast');
  await expect(toast).toBeVisible();
  expect(
    await page.evaluate(
      () => !!document.fullscreenElement?.contains(document.querySelector('[data-testid="story-toast"]')),
    ),
  ).toBe(true);
  if (shots) await page.screenshot({ path: `${shots}/story-fullscreen.png` });
  await page.keyboard.press('Space'); // unpause
  // A drag box-select still works in full screen: over home, a box around the first pawn on screen.
  await page.keyboard.press('Home');
  await page.waitForTimeout(1200);
  const vw = await page.evaluate(() => [window.innerWidth, window.innerHeight]);
  const pawnOnScreen = (await story(page, 'bodies')).find(
    (b) => b.name.startsWith('maid_s1_') && b.x > 80 && b.y > 160 && b.x < vw[0]! - 80 && b.y < vw[1]! - 160,
  );
  expect(pawnOnScreen).toBeTruthy();
  await page.mouse.move(pawnOnScreen!.x - 40, pawnOnScreen!.y - 40);
  await page.mouse.down();
  await page.mouse.move(pawnOnScreen!.x + 40, pawnOnScreen!.y + 40, { steps: 6 });
  await page.mouse.up();
  await expect(bar.locator('[aria-pressed="true"]').first()).toBeVisible();
  await expect.poll(fsEl).toBe('story-mode'); // still full screen
  await page.keyboard.press('Shift+F');
  await expect.poll(fsEl).toBe(null);
  await expect(page.getByTestId('story-fullscreen')).toHaveAttribute('aria-pressed', 'false');
  await expect(bar).toBeVisible();
});
