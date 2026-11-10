import { expect, test, type Page, type Request } from '@playwright/test';

// Story Mode work roles (docs/web/story-mode.md "Roles") in mock mode: two pawns made Farmers from the Work grid (the priorities
// POST carries the role), their rows show the Farmer's priorities, and editing the Farmer role's Farm cell updates both rows.
const shots = process.env.SHOTS_DIR;

const post = (page: Page, path: RegExp) =>
  page.waitForRequest((r: Request) => r.method() === 'POST' && path.test(new URL(r.url()).pathname));
const body = (r: Request) => r.postDataJSON() as Record<string, unknown>;

test('roles: assign Farmer to two pawns, the rows follow the role, editing the role updates both', async ({ page }) => {
  await page.addInitScript(() => sessionStorage.setItem('mcx-mock-story', 'empty'));
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
  await page.getByRole('link', { name: 'Controller' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: { ready(): boolean } }).__story?.ready());
  const count = page.getByLabel('Number of pawns');
  while (Number(await count.textContent()) !== 3)
    await page.getByRole('button', { name: Number(await count.textContent()) < 3 ? 'More pawns' : 'Fewer pawns' }).click();
  await page.getByRole('button', { name: 'Found colony' }).click();
  await expect(page.getByTestId('colonist-bar').locator('[data-kind="pawn"]')).toHaveCount(3);

  await page.keyboard.press('p');
  const work = page.getByTestId('work-panel');
  await expect(work).toBeVisible();
  const names = await work.locator('tbody tr[data-row]').evaluateAll((rows) => rows.map((r) => r.getAttribute('data-row')!));
  expect(names).toHaveLength(3);
  const [a, b, c] = names as [string, string, string];

  // Tick two rows, set their role in one go.
  await work.locator(`tr[data-row="${c}"] input[type="checkbox"]`).uncheck();
  await work.locator(`tr[data-row="${a}"] input[type="checkbox"]`).check();
  await work.locator(`tr[data-row="${b}"] input[type="checkbox"]`).check();
  const assigned = post(page, /\/priorities$/);
  await page.getByTestId('role-bulk-select').selectOption('farmer');
  expect(body(await assigned)).toEqual({ pawns: [a, b], role: 'farmer' });
  for (const n of [a, b]) {
    await expect(work.locator(`tr[data-row="${n}"]`)).toHaveAttribute('data-role', 'farmer');
    await expect(work.locator(`[data-cell="${n}:farm"]`)).toHaveText(/^1/);
    await expect(work.locator(`[data-cell="${n}:farm"]`)).toHaveAttribute('data-from-role', 'farmer');
  }
  await expect(work.locator(`tr[data-row="${c}"]`)).toHaveAttribute('data-role', 'custom');

  // Edit the Farmer role: Farm 1 → 2, both Farmers' rows follow.
  await page.getByTestId('roles-toggle').click();
  const editor = page.getByTestId('roles-editor');
  await expect(editor.locator('[data-role-row="farmer"]')).toBeVisible();
  const edited = post(page, /\/roles$/);
  await editor.locator('[data-role-cell="farmer:farm"]').click();
  expect(body(await edited)).toEqual({ op: 'update', role: { id: 'farmer', priorities: { farm: 2 } } });
  for (const n of [a, b]) await expect(work.locator(`[data-cell="${n}:farm"]`)).toHaveText(/^2/);
  await expect(editor.locator('[data-role-row="farmer"]')).toContainText('2 pawns');
  if (shots) await page.screenshot({ path: `${shots}/story-roles.png` });

  // A cell edit on a Farmer row detaches that pawn (Custom, the values kept) with an undo.
  const detach = post(page, /\/priorities$/);
  await work.locator(`[data-cell="${a}:mine"]`).click();
  expect(body(await detach)).toEqual({ pawns: [a], work: { mine: 1 } });
  await expect(work.locator(`tr[data-row="${a}"]`)).toHaveAttribute('data-role', 'custom');
  await expect(work.locator(`[data-cell="${a}:farm"]`)).toHaveText(/^2/);
  await expect(page.getByTestId('role-detached')).toContainText('is now Custom (was Farmer)');
  const undo = post(page, /\/priorities$/);
  await page.getByTestId('role-detached').getByRole('button', { name: /undo/i }).click();
  expect(body(await undo)).toEqual({ pawns: [a], role: 'farmer' });
  await expect(work.locator(`tr[data-row="${a}"]`)).toHaveAttribute('data-role', 'farmer');
});
