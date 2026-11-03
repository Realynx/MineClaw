import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * Production build behind the Nest gateway (scripts/prod-stack.mjs, mock bridge at production stream rates; the mock bot
 * answers chat after 20 s). The sidebar must work from the live hive and from a bot view, including while a chat reply is
 * pending, and while other explorer tabs are open in the background (HTTP/1.1: six connections per host).
 */
const USER = 'fox';
const PASSWORD = 'prod-stack-e2e';

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill(USER);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

/** Clicks a sidebar link and expects the page to change within `ms`; the link itself must be what is under the pointer. */
async function navigate(page: Page, name: string, path: string, ms = 8000) {
  const link = page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name, exact: true });
  const box = await link.boundingBox();
  expect(box).not.toBeNull();
  const hit = await page.evaluate(
    ([x, y]) => document.elementFromPoint(x!, y!)?.closest('a')?.getAttribute('href') ?? null,
    [box!.x + box!.width / 2, box!.y + box!.height / 2],
  );
  expect(hit, `nothing covers the ${name} link`).toBe(path);
  await link.click();
  await page.waitForURL(`**${path}`, { timeout: ms });
}

test.describe('sidebar navigation (production build behind Nest)', () => {
  test('works from the live hive grid, the world tab and a bot view', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(String(e)));
    await signIn(page);
    await expect(page.locator('[data-body]').first()).toBeVisible();
    await page.waitForTimeout(3000); // thumbnails, the world runtime and the stream at full rate
    await navigate(page, 'Overview', '/overview');
    await navigate(page, 'Live hive', '/live');
    await page.waitForTimeout(2000);
    await navigate(page, 'Scripts', '/scripts');
    await page.goto('/live?view=world');
    await page.waitForTimeout(3000);
    await navigate(page, 'Personality', '/personality');
    await page.goto('/live/maid_chan');
    await page.waitForTimeout(3000);
    await navigate(page, 'Memory browser', '/memory');
    expect(errors).toEqual([]);
  });

  test('works while a chat reply is pending in the bot view', async ({ page }) => {
    const warnings: string[] = [];
    page.on('console', (m) => {
      if (/Maximum update depth/i.test(m.text())) warnings.push(m.text());
    });
    await signIn(page);
    await page.goto('/live/maid_chan');
    const box = page.getByPlaceholder(/^Message maid_chan as/);
    await box.fill('where is the sand?');
    await box.press('Enter');
    await expect(page.getByText('where is the sand?').first()).toBeVisible();
    await page.waitForTimeout(1500); // our line is echoed by the feed; the reply is 20 s away
    await navigate(page, 'Overview', '/overview', 5000);
    expect(warnings).toEqual([]);
  });

  test('background tabs do not starve the visible one', async ({ browser, page }) => {
    test.setTimeout(180_000);
    await signIn(page);
    const others = await openHiddenTabs(browser, page, 5);
    await page.bringToFront();
    // Hidden tabs give their stream back after 15 s and stop polling the world at once.
    await page.waitForTimeout(17_000);
    await navigate(page, 'Overview', '/overview', 10_000);
    await navigate(page, 'Live hive', '/live', 10_000);
    for (const p of others) await p.close();
  });

  test('the grid shows every body, grouped by hive', async ({ page }) => {
    await signIn(page);
    const status = await page.evaluate(
      async () => (await (await fetch('/api/status')).json()) as { bots: Array<{ name: string }> },
    );
    await expect(page.locator('[data-body]')).toHaveCount(status.bots.length);
    expect(status.bots.length).toBeGreaterThanOrEqual(8);
    await expect(page.locator('[data-group]')).toHaveCount(2); // main, and the hive with its crew
  });
});

/** Opens `n` more explorer tabs on the live hive in the same browser profile and marks them hidden (background tabs). */
async function openHiddenTabs(browser: Browser, page: Page, n: number): Promise<Page[]> {
  void browser;
  const ctx = page.context();
  const tabs: Page[] = [];
  for (let i = 0; i < n; i++) {
    const p = await ctx.newPage();
    await p.goto('/live', { waitUntil: 'domcontentloaded' });
    await expect(p.locator('[data-body]').first()).toBeVisible({ timeout: 30_000 });
    await p.evaluate(() => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    tabs.push(p);
  }
  return tabs;
}
