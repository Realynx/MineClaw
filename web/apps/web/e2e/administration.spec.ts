import { expect, test, type Page } from '@playwright/test';

/** Administration (mock mode): fox is admin with TOTP 123456; robin is chat + viewer without TOTP. */
async function signIn(page: Page, user: string, password: string, totp?: string) {
  await page.goto('/login');
  await page.getByLabel('Username').fill(user);
  await page.getByLabel('Password').fill(password);
  if (totp) await page.getByLabel(/Authenticator code/).fill(totp);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

test('admin: change a model and the Ollama endpoint, test, review, step up, save, restart', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await signIn(page, 'fox', 'fox', '123456');

  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Administration' }).click();
  await page.waitForURL('**/administration');
  await expect(page.getByRole('heading', { name: 'Administration' })).toBeVisible();
  const models = page.getByRole('region', { name: 'Models & providers' });
  await expect(models.getByText('Set — ends in ab12')).toBeVisible();

  // A Claude tier offers only the 5.5-generation ids.
  const worker = models.getByLabel('Worker tier Model', { exact: true });
  await expect(worker.locator('option')).toHaveText([
    'claude-opus-5-5',
    'claude-sonnet-5-5',
    'claude-haiku-5-5',
    'claude-fable-5-1',
    'claude-mythos-5-1',
  ]);
  await worker.selectOption('claude-haiku-5-5');

  // A new Ollama endpoint, tested before saving: the first sensitive action asks for the code.
  await models.getByRole('textbox', { name: 'Ollama Endpoint' }).fill('http://10.1.2.60:11434');
  await models.getByRole('button', { name: 'Test the Ollama endpoint' }).click();
  const stepUp = page.getByRole('dialog', { name: /Confirm it's you/ });
  await expect(stepUp).toBeVisible();
  await stepUp.getByLabel('Authenticator code').fill('123456');
  await stepUp.getByRole('button', { name: 'Confirm' }).click();
  await expect(models.getByText(/http:\/\/10\.0\.3\.60:11434 answered: 4 models/)).toBeVisible();

  // The memory tier moves to Ollama: its model list comes from the (unsaved) endpoint's tags.
  await models.getByLabel('Memory tier Provider', { exact: true }).selectOption('ollama');
  const memModel = models.getByLabel('Memory tier Model', { exact: true });
  await expect(memModel.locator('option', { hasText: 'qwen3.5:32b' })).toHaveCount(1);
  await memModel.selectOption('qwen3.5:32b');

  // Review: the diff, a reason, save.
  const bar = page.getByRole('region', { name: 'Unsaved changes' });
  await expect(bar).toContainText('4 unsaved changes');
  await bar.getByRole('button', { name: 'Review changes' }).click();
  const review = page.getByRole('dialog', { name: 'Review changes' });
  const table = review.getByRole('table', { name: 'Changes' });
  await expect(table).toContainText('claude-sonnet-5-5');
  await expect(table).toContainText('claude-haiku-5-5');
  await expect(table).toContainText('http://10.1.2.60:11434');
  await expect(table).toContainText('qwen3.5:32b');
  await review.getByLabel('Reason').fill('try the local model for memory');
  await review.getByRole('button', { name: 'Save 4 changes' }).click();
  await expect(review).toBeHidden();

  const notice = page.getByRole('region', { name: 'Restart required' });
  await expect(notice).toBeVisible();
  await expect(notice).toContainText('Worker tier · Model');
  await expect(notice).toContainText('Ollama · Endpoint');
  await expect(page.getByRole('list', { name: 'Settings audit' })).toContainText('Memory tier · Model');

  // Restart: the safety check first, then the restart.
  await notice.getByRole('button', { name: 'Restart bot…' }).click();
  const restart = page.getByRole('dialog', { name: 'Restart the bot' });
  await expect(restart.getByText(/Safe to restart now/)).toBeVisible();
  await restart.getByLabel('Reason').fill('pick up the new models');
  await restart.getByRole('button', { name: 'Restart now' }).click();
  await expect(restart.getByRole('status')).toContainText('restarting');
  await restart.getByRole('button', { name: 'Close' }).first().click();
  await expect(page.getByRole('region', { name: 'Restart required' })).toBeHidden();
  expect(errors).toEqual([]);
});

test('admin: saving with a stale step-up asks for the code first', async ({ page }) => {
  await signIn(page, 'fox', 'fox', '123456');
  await page.goto('/administration');
  const viewer = page.getByRole('region', { name: 'Viewer access' });
  await viewer.getByLabel('Viewer Lines per minute').fill('20');
  await page.getByRole('region', { name: 'Unsaved changes' }).getByRole('button', { name: 'Review changes' }).click();
  const review = page.getByRole('dialog', { name: 'Review changes' });
  await review.getByLabel('Reason').fill('busier evenings');
  await review.getByRole('button', { name: 'Save 1 change' }).click();
  const stepUp = page.getByRole('dialog', { name: /Confirm it's you/ });
  await stepUp.getByLabel('Authenticator code').fill('000000');
  await stepUp.getByRole('button', { name: 'Confirm' }).click();
  await expect(stepUp.getByText('That code did not match.')).toBeVisible();
  await stepUp.getByLabel('Authenticator code').fill('123456');
  await stepUp.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Saved.' })).toContainText('Viewer · Lines per minute');
  await expect(page.getByRole('region', { name: 'Restart required' })).toBeHidden(); // a live setting
});

test('a non-admin does not see Administration and the API answers 403', async ({ page }) => {
  await signIn(page, 'robin', 'robin');
  const nav = page.getByRole('navigation', { name: 'Main' });
  await expect(nav.getByRole('link', { name: 'Live hive' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Administration' })).toHaveCount(0);
  const status = await page.evaluate(async () => (await fetch('/api/admin/nodes')).status);
  expect(status).toBe(403);
  await page.goto('/administration');
  await expect(page.getByText('Administration needs the admin role')).toBeVisible();
});
