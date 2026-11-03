import { expect, test, type Page } from '@playwright/test';

/**
 * First-run setup in mock mode: `sessionStorage['mcx-mock-setup']` starts the mock with no users (an empty users file).
 * The mock accepts the TOTP code 123456 for any enrolment.
 */
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    if (sessionStorage.getItem('mcx-mock-setup') === null) sessionStorage.setItem('mcx-mock-setup', '[]');
  });
});

const PASSWORD = 'lantern orbit pickle seventy';

async function fillAccount(page: Page, username: string) {
  await expect(page.getByText('Whoever completes this page first becomes the administrator.')).toBeVisible();
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
  await expect(page.getByText('Strong', { exact: true })).toBeVisible();
  await page.getByLabel('Confirm password').fill('not the same');
  await expect(page.getByText('The passwords do not match.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create account and sign in' })).toBeDisabled();
  await page.getByLabel('Confirm password').fill(PASSWORD);
  await page.getByLabel(/Minecraft player/).fill('poofimafox');
  // The preset: every role.
  for (const role of ['Admin', 'Chat', 'Commands', 'Viewer']) await expect(page.getByRole('checkbox', { name: role })).toBeChecked();
  await page.getByRole('button', { name: 'Create account and sign in' }).click();
}

test('every page goes to /setup while there are no users, and sign-in hands over to it', async ({ page }) => {
  await page.goto('/overview');
  await page.waitForURL('**/setup');
  await page.goto('/login');
  await page.waitForURL('**/setup');
  await expect(page.getByRole('heading', { name: 'Welcome to MineClaw Explorer' })).toBeVisible();
});

test('setup with an authenticator flows into the app; afterwards /setup is closed and the account signs in', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto('/live');
  await page.waitForURL('**/setup');
  await fillAccount(page, 'Owner');

  await expect(page.getByRole('heading', { name: 'Secure it with an authenticator' })).toBeVisible();
  await page.getByRole('button', { name: 'Set up authenticator' }).click();
  const qr = page.getByRole('img', { name: 'Authenticator QR code' });
  await expect(qr).toBeVisible();
  expect(((await qr.locator('path').getAttribute('d')) ?? '').length).toBeGreaterThan(500);
  await expect(page.getByTestId('totp-secret')).toHaveText('JBSWY3DPEHPK3PXP');
  await page.getByLabel('6-digit code').fill('000000');
  await page.getByRole('button', { name: 'Verify and turn on' }).click();
  await expect(page.getByText('That code did not match.', { exact: false })).toBeVisible();
  await page.getByLabel('6-digit code').fill('123456');
  await page.getByRole('button', { name: 'Verify and turn on' }).click();

  await expect(page.getByText('two-factor sign-in is on')).toBeVisible();
  await page.getByRole('button', { name: 'Open the explorer' }).click();
  await page.waitForURL('**/live');
  await expect(page.getByRole('heading', { name: 'Live hive' })).toBeVisible();

  // Setup is over for good.
  await page.goto('/setup');
  await page.waitForURL('**/login');
  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
  expect(errors).toEqual([]);
});

test('the authenticator step is optional', async ({ page }) => {
  await page.goto('/setup');
  await fillAccount(page, 'robin');
  await page.getByRole('button', { name: 'Skip for now' }).click();
  await expect(page.getByText('You can add an authenticator later')).toBeVisible();
  await page.getByRole('button', { name: 'Open the explorer' }).click();
  await page.waitForURL('**/live');
  await expect(page.getByRole('heading', { name: 'Live hive' })).toBeVisible();
});
