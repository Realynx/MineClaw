import { expect, test, type Page } from '@playwright/test';

/** Every page, after a real sign-in through the form (fox has TOTP in the mocks: 123456). */
const PAGES: Array<{ path: string; check: (p: Page) => Promise<void> }> = [
  { path: '/live', check: (p) => expect(p.getByRole('heading', { name: 'Live hive' })).toBeVisible() },
  { path: '/live?view=tree', check: (p) => expect(p.getByRole('tree', { name: 'Hive hierarchy' })).toBeVisible() },
  { path: '/live?view=world', check: (p) => expect(p.getByLabel('Follow bot')).toBeVisible() },
  { path: '/live/maid_chan', check: (p) => expect(p.getByRole('region', { name: 'Chat with maid_chan' })).toBeVisible() },
  { path: '/memory-activity', check: (p) => expect(p.getByRole('application', { name: /Memory activity map/ })).toBeVisible() },
  { path: '/overview', check: (p) => expect(p.getByRole('heading', { name: 'RAPTOR tree' })).toBeVisible() },
  { path: '/memory?kind=claims', check: (p) => expect(p.getByRole('table', { name: 'Memories' })).toBeVisible() },
  { path: '/search?q=melons', check: (p) => expect(p.getByText('kiwi likes melons').first()).toBeVisible() },
  { path: '/memory-activity?layout=cloud', check: (p) => expect(p.getByRole('application', { name: 'Memory cloud' })).toBeVisible() },
  { path: '/personality', check: (p) => expect(p.getByText('Mood')).toBeVisible() },
  { path: '/scripts?name=cobble_loop', check: (p) => expect(p.getByRole('tab', { name: 'Source' })).toBeVisible() },
  { path: '/designs?id=12', check: (p) => expect(p.getByRole('heading', { name: /cobblestone tower/ })).toBeVisible() },
  { path: '/shards', check: (p) => expect(p.getByText('Default packs')).toBeVisible() },
  { path: '/admin', check: (p) => expect(p.getByText('Web audit log')).toBeVisible() },
];

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

test('a protected page sends you to sign in', async ({ page }) => {
  await page.goto('/overview');
  await page.waitForURL(/\/login\?next=%2Foverview/);
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
});

test('a wrong code is refused with a clear message', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('000000');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByText(/wrong username, password or code/i)).toBeVisible();
});

test('signs in and visits every page without errors', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(`${page.url()}: ${String(e)}`));
  await signIn(page);
  await expect(page.getByRole('button', { name: /Connection: Live/ })).toBeVisible();
  for (const { path, check } of PAGES) {
    await test.step(path, async () => {
      await page.goto(path);
      await check(page);
    });
  }
  expect(errors).toEqual([]);
});

test('chat: a message resolves when the bot replies', async ({ page }) => {
  await signIn(page);
  await page.goto('/live/maid_chan');
  const box = page.getByLabel(/Message maid_chan as poofimafox/);
  await box.fill('how is the cobble going?');
  await box.press('Enter');
  await expect(page.getByText('maid_chan is replying…')).toBeVisible();
  await expect(page.getByText(/The generator is steady/)).toBeVisible();
  await expect(page.getByText('maid_chan is replying…')).toBeHidden();
});

test('command palette jumps to a script', async ({ page }) => {
  await signIn(page);
  await page.keyboard.press('Control+k');
  await page.getByPlaceholder(/Search bots, scripts/).fill('farm_tend');
  const option = page.getByRole('option', { name: /^farm_tend/ });
  await expect(option).toBeVisible();
  await option.click();
  await page.waitForURL(/\/scripts\?name=farm_tend/);
});
