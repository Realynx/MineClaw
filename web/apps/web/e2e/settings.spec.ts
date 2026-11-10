import { expect, test, type Page } from '@playwright/test';

// Settings → Viewer quality in mock mode: the panel saves per device (localStorage), survives a reload, and every viewer
// applies it live (read back through `window.__mcxViewer`, mock mode only: the applied quality and the live viewer status).

interface ViewerDebug {
  quality(): { drawDistance: number; entityDistance: 'ground' | number; perfOverlay: boolean };
  status(): { entityRadius: number; fps: number; software: boolean } | null;
  settings(): { preset: string };
}

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

const viewer = <K extends keyof ViewerDebug>(page: Page, k: K) =>
  page.evaluate((key) => (window as unknown as { __mcxViewer: Record<string, () => unknown> }).__mcxViewer[key]!(), k) as Promise<ReturnType<ViewerDebug[K]>>;

const radio = (page: Page, group: string, name: string) =>
  page.getByRole('radiogroup', { name: group }).first().getByRole('radio', { name, exact: true });

test('viewer settings persist across a reload and reach the viewers live', async ({ page }) => {
  await signIn(page);
  await page.getByRole('link', { name: 'Settings' }).first().click();
  await page.waitForURL('**/settings');
  await expect(page.getByRole('heading', { name: 'Viewer quality' })).toBeVisible();
  await expect(radio(page, 'Quality preset', 'Auto')).toBeChecked();
  expect((await viewer(page, 'quality')).entityDistance).toBe('ground');

  // A preset applies at once.
  await radio(page, 'Quality preset', 'Low').click();
  await expect.poll(async () => (await viewer(page, 'quality')).drawDistance).toBe(64);
  // Editing a field switches to Custom.
  await radio(page, 'Entity distance', 'Limit').click();
  await expect(radio(page, 'Quality preset', 'Custom')).toBeChecked();
  await expect.poll(async () => (await viewer(page, 'quality')).entityDistance).toBe(48);
  // The preview viewer (the shared runtime) draws with it.
  await expect.poll(async () => (await viewer(page, 'status'))?.entityRadius, { timeout: 60_000 }).toBe(48);

  // Saved on this device: a reload keeps it, and the viewers get it again before they draw.
  const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('mcx-viewer-settings') ?? 'null') as { v: number; preset: string });
  expect(stored).toMatchObject({ v: 1, preset: 'custom', custom: { entityDistance: 48, drawDistance: 64 } });
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Viewer quality' })).toBeVisible();
  await expect(radio(page, 'Quality preset', 'Custom')).toBeChecked();
  await expect(radio(page, 'Entity distance', 'Limit')).toBeChecked();
  await expect.poll(async () => (await viewer(page, 'quality')).entityDistance).toBe(48);

  // Story Mode zoomed all the way out: the quick popover puts mobs and players back as far as the ground (the whole map).
  await page.getByRole('link', { name: 'Controller' }).click();
  await page.waitForURL('**/story**');
  await page.waitForFunction(() => (window as unknown as { __story?: { ready(): boolean } }).__story?.ready());
  await expect(page.getByTestId('story-controls')).toBeVisible();
  // The map settles over home (and sets its own zoom) first; zoom out once that has happened, until it holds.
  await expect
    .poll(
      () =>
        page.evaluate(() => {
          const s = (window as unknown as { __story: { setZoom(z: number): void; zoom(): number } }).__story;
          if (s.zoom() !== 112) s.setZoom(112);
          return s.zoom();
        }),
      { timeout: 30_000, intervals: [1000] },
    )
    .toBe(112);
  await page.waitForTimeout(1500);
  expect(await page.evaluate(() => (window as unknown as { __story: { zoom(): number } }).__story.zoom())).toBe(112);
  await expect.poll(async () => (await viewer(page, 'status'))?.entityRadius, { timeout: 60_000 }).toBe(48);
  await page.getByTestId('story-controls').getByRole('button', { name: 'Viewer quality' }).click();
  const pop = page.getByRole('dialog', { name: 'Viewer quality' });
  await pop.getByRole('radio', { name: 'Same as ground' }).click();
  await expect.poll(async () => (await viewer(page, 'status'))?.entityRadius ?? 0, { timeout: 30_000 }).toBeGreaterThan(100);
  // The fps overlay shows over the map.
  await pop.getByRole('switch').click();
  await expect(page.getByTestId('viewer-perf')).toBeVisible();
  await expect(page.getByTestId('viewer-perf')).toContainText('fps');
  expect((await viewer(page, 'quality')).perfOverlay).toBe(true);

  // Reset to defaults from the full panel.
  await pop.getByRole('link', { name: /All viewer settings/ }).click();
  await page.waitForURL('**/settings');
  await page.getByRole('button', { name: 'Reset to defaults' }).click();
  await expect(radio(page, 'Quality preset', 'Auto')).toBeChecked();
  expect(await viewer(page, 'quality')).toMatchObject({ drawDistance: 128, entityDistance: 'ground', perfOverlay: false });
});

test('the command palette opens Settings', async ({ page }) => {
  await signIn(page);
  await page.keyboard.press('Control+k');
  await page.getByPlaceholder(/Search bots/).fill('viewer quality');
  await page.getByRole('option', { name: /Settings/ }).first().click();
  await page.waitForURL('**/settings');
  await expect(page.getByRole('heading', { name: 'Viewer quality' })).toBeVisible();
});
