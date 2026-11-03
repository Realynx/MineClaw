import { expect, test, type Page } from '@playwright/test';

// The sidebar must keep working while a live world view (one shared WebGL runtime) is mounted.
async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

test('sidebar links navigate away from the live hive', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  // Like a no-GPU desktop browser under load: software WebGL (launch args) plus a slower CPU.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  await signIn(page);
  await page.waitForTimeout(3000); // let the thumbnails and the world runtime start
  await page.getByRole('link', { name: 'Overview' }).click();
  await page.waitForURL('**/overview', { timeout: 10_000 });
  await page.getByRole('link', { name: 'Live hive' }).click();
  await page.waitForURL('**/live', { timeout: 10_000 });
  await page.waitForTimeout(2000);
  await page.getByRole('link', { name: 'Personality' }).click();
  await page.waitForURL('**/personality', { timeout: 10_000 });
  expect(errors).toEqual([]);
});
