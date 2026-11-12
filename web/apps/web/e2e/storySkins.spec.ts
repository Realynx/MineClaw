import { crc32, deflateSync } from 'node:zlib';
import { expect, test, type Page, type Request } from '@playwright/test';

// The pawn inspector's Skin Browser (docs/web/human-skins.md "Pawn skins") in mock mode: click a pawn in the colonist bar,
// open "Skin Browser", upload a synthetic 64×64 PNG (refused at the wrong size first), apply it, check the world views' skin
// feed carries it for that pawn, reload (kept), reuse it from the library for a second pawn and give a third Alex.
const shots = process.env.SHOTS_DIR;

function chunk(type: string, data: Buffer): Buffer {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'latin1');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])) >>> 0, 0);
  return Buffer.concat([head, data, crc]);
}

/** A synthetic one-colour RGBA skin (no real player skin is committed). */
function skinPng(width: number, height: number, rgba: [number, number, number, number]): Buffer {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const row = Buffer.alloc(1 + width * 4);
  for (let x = 0; x < width; x++) row.set(rgba, 1 + x * 4);
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(Buffer.concat(Array.from({ length: height }, () => row)))),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const post = (page: Page, path: RegExp) =>
  page.waitForRequest((r: Request) => r.method() === 'POST' && path.test(new URL(r.url()).pathname));

async function openController(page: Page) {
  await page.getByRole('link', { name: 'Controller' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: { ready(): boolean } }).__story?.ready());
}

/** Clicks the pawn's portrait in the colonist bar and opens its Skin Browser tab. */
async function skinTab(page: Page, pawn: string) {
  await page.getByTestId('colonist-bar').locator(`[data-portrait="${pawn}"]`).click();
  const inspector = page.getByTestId('inspector');
  await expect(inspector.getByRole('tab', { name: 'Orders' })).toBeVisible();
  await inspector.getByRole('tab', { name: 'Skin Browser' }).click();
  const browser = inspector.getByTestId('skin-browser');
  await expect(browser).toBeVisible();
  return browser;
}

/** What the world views are told to draw for `pawn` (the same feed the Controller's 3D view polls). */
async function feedFor(page: Page, pawn: string) {
  return page.evaluate(async (name) => {
    const feed = (await (await fetch('/api/human-skins', { cache: 'no-store' })).json()) as {
      enabled: boolean;
      skins: Array<{ name: string; slim: boolean; height: number; png: string; base?: string }>;
    };
    return { enabled: feed.enabled, skin: feed.skins.find((s) => s.name === name.toLowerCase()) ?? null };
  }, pawn);
}

test('skin browser: upload, apply, persist, reuse from the library and pick a default skin', async ({ page }) => {
  // The seeded mock colony (re-seeded with the same pawns after a reload; the skins persist in sessionStorage).
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
  await openController(page);
  const portraits = page.getByTestId('colonist-bar').locator('[data-portrait][data-kind="pawn"]');
  await expect.poll(() => portraits.count()).toBeGreaterThanOrEqual(3);
  const [a, b, c] = (await portraits.evaluateAll((els) => els.map((e) => e.getAttribute('data-portrait')!))) as [string, string, string];

  let browser = await skinTab(page, a);
  await expect(browser).toContainText('offline mode');
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Default appearance');
  await expect(browser).toContainText('No skins yet');

  // Upload: a 128×128 image is refused in the browser, before any request.
  await browser.getByRole('button', { name: 'Upload' }).click();
  const file = browser.getByLabel('Skin file');
  await file.setInputFiles({ name: 'huge.png', mimeType: 'image/png', buffer: skinPng(128, 128, [0, 0, 0, 255]) });
  await expect(browser.getByTestId('skin-upload-error')).toContainText('this image is 128×128');
  await file.setInputFiles({ name: 'Ember Knight.png', mimeType: 'image/png', buffer: skinPng(64, 64, [220, 90, 30, 255]) });
  await expect(browser.getByRole('img', { name: 'Uploaded skin preview' })).toBeVisible();
  await expect(browser.getByLabel('Skin name')).toHaveValue('Ember Knight');
  const uploaded = post(page, /\/api\/skins$/);
  const applied = post(page, /\/api\/skins\/assign$/);
  await browser.getByRole('button', { name: 'Upload and apply' }).click();
  expect((await uploaded).postDataJSON()).toMatchObject({ label: 'Ember Knight', slim: false });
  expect((await applied).postDataJSON()).toMatchObject({ pawns: [a], skin: { kind: 'library' } });
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Ember Knight');
  await expect(browser.getByRole('button', { name: 'Skin Ember Knight' })).toBeVisible();
  expect(await feedFor(page, a)).toMatchObject({ enabled: true, skin: { slim: false, height: 64 } });
  if (shots) await page.screenshot({ path: `${shots}/story-skin-browser.png` });

  // Kept across a reload.
  await page.reload();
  await page.waitForFunction(() => (window as unknown as { __story?: { ready(): boolean } }).__story?.ready());
  browser = await skinTab(page, a);
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Ember Knight');

  // The library is shared: the second pawn picks the same skin, previews it, applies it.
  browser = await skinTab(page, b);
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Default appearance');
  await browser.getByRole('button', { name: 'Skin Ember Knight' }).click();
  await expect(browser.getByTestId('skin-preview')).toContainText('Preview, not applied yet');
  await browser.getByRole('button', { name: /^Apply to / }).click();
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Ember Knight');
  await expect(browser.getByRole('button', { name: 'Skin Ember Knight' })).toContainText('2 pawns');
  expect((await feedFor(page, b)).skin?.png).toBe((await feedFor(page, a)).skin?.png);

  // A default skin for the third pawn: drawn by the viewer from the user's own imported game assets.
  browser = await skinTab(page, c);
  await browser.getByRole('button', { name: 'Defaults' }).click();
  await browser.getByRole('button', { name: 'Skin Alex' }).click();
  await browser.getByRole('button', { name: /^Apply to / }).click();
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Alex');
  expect((await feedFor(page, c)).skin).toEqual({ name: c.toLowerCase(), slim: true, height: 64, png: '', base: 'alex' });

  // Back to the default appearance: gone from the feed.
  await browser.getByRole('button', { name: 'Use default appearance' }).click();
  await expect(browser.getByTestId('skin-wearing')).toHaveText('Default appearance');
  expect((await feedFor(page, c)).skin).toBeNull();
});
