import { expect, test, type Page } from '@playwright/test';

/**
 * The Controller's first frame on the production build behind Nest (scripts/prod-stack.mjs, mock bridge). The map must
 * open on the colony, not at the world origin: while the colony's state is on its way, the view is held and asks the
 * world for nothing, so the first world index it asks for is around home. Before the fix the first index was x=0&z=0.
 */
const HOME = { x: 400, y: 63, z: 400 };
/** How long the colony's state reply is held back (the position is unknown until it arrives). */
const STATE_DELAY_MS = 1500;

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('prod-stack-e2e');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

/** Founds a colony at `home` the way the page would (its own cookie, Origin and CSRF token). Returns its id. */
async function foundColony(page: Page, home: typeof HOME): Promise<string> {
  return page.evaluate(async (h) => {
    const csrf = ((await (await fetch('/api/auth/csrf')).json()) as { csrfToken: string }).csrfToken;
    const res = await fetch('/api/story/colonies', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf },
      body: JSON.stringify({ name: `First frame ${Date.now() % 100000}`, pawns: 1, home: h }),
    });
    if (!res.ok) throw new Error(`found colony: ${res.status}`);
    return ((await res.json()) as { id: string }).id;
  }, home);
}

async function releaseColony(page: Page, id: string) {
  await page.evaluate(async (colony) => {
    const csrf = ((await (await fetch('/api/auth/csrf')).json()) as { csrfToken: string }).csrfToken;
    await fetch(`/api/story/colonies/${colony}/release`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-csrf-token': csrf },
      body: '{}',
    });
  }, id);
}

test('the Controller opens on the colony: no world index near the origin, the first one at home', async ({ page }) => {
  await signIn(page);
  const id = await foundColony(page, HOME);

  // The state reply (and its catch-up deltas) is held back, so the page knows the colony but not where it is.
  await page.route('**/api/story/colonies/*', async (route) => {
    if (route.request().method() === 'GET') await new Promise((r) => setTimeout(r, STATE_DELAY_MS));
    await route.continue();
  });
  // Only the Controller's own requests count (the live page's thumbnails look at the bots, which stand near the origin).
  await page.goto('about:blank');
  const index: Array<{ x: number; z: number }> = [];
  page.on('request', (req) => {
    const url = new URL(req.url());
    if (url.pathname !== '/api/world/index') return;
    index.push({ x: Number(url.searchParams.get('x')), z: Number(url.searchParams.get('z')) });
  });

  // A fresh page load: nothing of the colony is cached, and this browser remembers no view of it.
  await page.goto(`/story?colony=${encodeURIComponent(id)}`);
  await expect(page.getByTestId('story-map')).toBeVisible();
  await expect.poll(() => index.length, { timeout: 30_000 }).toBeGreaterThan(0);
  await page.waitForTimeout(STATE_DELAY_MS + 1000); // the requests of the first seconds, the held stretch included

  const nearOrigin = index.filter((q) => Math.abs(q.x) <= 16 && Math.abs(q.z) <= 16);
  expect(nearOrigin, 'world index requests near the origin').toEqual([]);
  const first = index[0]!;
  expect(Math.abs(first.x - HOME.x), `first index at ${first.x}, ${first.z}`).toBeLessThanOrEqual(1);
  expect(Math.abs(first.z - HOME.z), `first index at ${first.x}, ${first.z}`).toBeLessThanOrEqual(1);

  await page.unroute('**/api/story/colonies/*');
  await releaseColony(page, id);
});
