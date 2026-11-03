import { expect, test, type Page } from '@playwright/test';

// Chat bubbles over heads in the 3D world (mock mode): a line in the feed shows as a speech bubble over the speaker's body in
// the world pane (every camera), and as a small one on the hive cards. SHOTS_DIR=<dir> also saves screenshots there.
const shots = process.env.SHOTS_DIR;

interface Shown {
  speaker: string;
  key: string;
  x: number;
  y: number;
  w: number;
  h: number;
  bottom: number;
  top: number;
  lines: string[];
  stacked: number;
}

async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Username').fill('fox');
  await page.getByLabel('Password').fill('fox');
  await page.getByLabel(/Authenticator code/).fill('123456');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL('**/live');
}

const bubbles = (page: Page) =>
  page.evaluate(() => (window as unknown as { __mcxWorld?: { bubbles: () => Shown[] } }).__mcxWorld?.bubbles() ?? []);

const bubbleOf = async (page: Page, speaker: string) =>
  (await bubbles(page)).find((b) => b.speaker === speaker) ?? null;

/** Share of near-white pixels (the bubble's fill) in a rectangle of the page. */
async function whiteShare(page: Page, r: { x: number; y: number; width: number; height: number }): Promise<number> {
  const png = await page.screenshot({ clip: r });
  return page.evaluate(
    async (bytes) => {
      const img = await createImageBitmap(new Blob([new Uint8Array(bytes)], { type: 'image/png' }));
      const c = new OffscreenCanvas(img.width, img.height);
      const ctx = c.getContext('2d')!;
      ctx.drawImage(img, 0, 0);
      const d = ctx.getImageData(0, 0, img.width, img.height).data;
      let n = 0;
      for (let i = 0; i < d.length; i += 4) if (d[i]! > 225 && d[i + 1]! > 225 && d[i + 2]! > 225) n++;
      return d.length ? n / (d.length / 4) : -1;
    },
    [...png],
  );
}

async function say(page: Page, text: string) {
  const box = page.getByLabel('Message maid_chan as poofimafox');
  await box.fill(text);
  await box.press('Enter');
}

test('a chat line shows a bubble over the right bot, stacks, and fades', async ({ page }) => {
  await signIn(page);
  await page.goto('/live/maid_chan');
  await page.getByRole('radio', { name: /World/ }).click();
  const stage = page.getByTestId('world-stage');
  await expect(stage.locator('canvas').first()).toBeVisible();
  await page.waitForTimeout(3000); // the world meshes in
  // The viewer line bubbles over the linked player (poofimafox stands on the island); the bot's reply over maid_chan.
  await say(page, 'hello there');
  await expect
    .poll(async () => (await bubbleOf(page, 'maid_chan'))?.lines.join(' ') ?? '', { timeout: 15_000 })
    .toContain('Hi poofimafox!');
  // The next question now, so its reply arrives while this bubble still shows (they stack).
  await say(page, 'how is it going?');
  await page.waitForTimeout(400); // past the fade-in
  const b = (await bubbleOf(page, 'maid_chan'))!;
  expect(b.key).toBe('maid_chan');
  // In the follow camera the bot is in the middle of the pane: its bubble is there, above its head and name label.
  const canvas = (await stage.locator('canvas').first().boundingBox())!;
  expect(Math.abs(b.x - canvas.width / 2)).toBeLessThan(canvas.width * 0.2);
  expect(b.bottom).toBeLessThan(b.y);
  // The pixels are really there: the bubble's white fill above the bot. The follow camera eases in and out around the trees
  // between the report and the screenshot, so the box is the whole column over the bot's head.
  const top = Math.max(0, b.top - 120);
  const rect = { x: canvas.x + b.x - b.w / 2 - 60, y: canvas.y + top, width: b.w + 120, height: b.y + 40 - top };
  if (shots) await page.screenshot({ path: `${shots}/pane-follow.png` });
  expect(await whiteShare(page, rect)).toBeGreaterThan(0.04);
  // The reply stacks under the first line (the newest at the bottom, nearest the head), wrapped.
  await expect.poll(async () => (await bubbleOf(page, 'maid_chan'))?.stacked ?? 0, { timeout: 8_000 }).toBe(2);
  await page.waitForTimeout(300);
  const s2 = (await bubbleOf(page, 'maid_chan'))!;
  expect(s2.lines.join(' ')).toContain('Going well!');
  expect(s2.lines.length).toBeGreaterThan(1);
  expect(s2.top).toBeLessThan(s2.bottom - s2.h);
  if (shots) await page.screenshot({ path: `${shots}/pane-stack.png` });
  // The other cameras draw it too (the reply holds 7 s).
  for (const m of ['iso', 'top'] as const) {
    await page.evaluate(
      (mode) => (window as unknown as { __mcxWorld: { camera: (m: string) => void } }).__mcxWorld.camera(mode),
      m,
    );
    await page.waitForTimeout(900); // the camera glides over 0.6 s
    expect((await bubbleOf(page, 'maid_chan'))?.lines.join(' ')).toContain('Going well!');
    if (shots) await page.screenshot({ path: `${shots}/pane-${m}.png` });
  }
  // Fullscreen free cam, flown to the bot, with a fresh line.
  await page.getByRole('radio', { name: 'Fly' }).click();
  await page.getByLabel('Go to a place, a bot or coordinates').fill('maid_chan');
  await page.getByLabel('Go to a place, a bot or coordinates').press('Enter');
  await say(page, 'hey');
  await page.getByRole('button', { name: 'Full screen' }).click();
  await expect
    .poll(async () => (await bubbleOf(page, 'maid_chan'))?.lines.join(' ') ?? '', { timeout: 15_000 })
    .toContain('Hi poofimafox!');
  await page.waitForTimeout(400);
  if (shots) await page.screenshot({ path: `${shots}/fullscreen-free-cam.png` });
  await page.getByRole('button', { name: 'Exit full screen' }).click();
  // Gone after their time (6 s + 0.5 s a line, then a 0.5 s fade).
  await expect.poll(async () => bubbleOf(page, 'maid_chan'), { timeout: 15_000 }).toBeNull();
  // The text chat is unchanged: both replies are there.
  await expect(page.getByRole('region', { name: 'Chat with maid_chan' })).toContainText('Going well!');
});

test('hive cards show a small bubble over the speaking bot', async ({ page }) => {
  await signIn(page);
  await page.goto('/live/maid_chan');
  await say(page, 'hello');
  // Back to the grid (client navigation keeps the stream and the world): the reply arrives there.
  await page.getByRole('link', { name: 'Back to the hive' }).click();
  await page.waitForURL('**/live');
  await page.waitForTimeout(5000);
  if (shots) await page.screenshot({ path: `${shots}/live-grid.png` });
  const card = page.locator('[data-body="maid_chan"]').first();
  if (shots) await card.screenshot({ path: `${shots}/card-maid_chan.png` });
  // The card's thumbnail has the white bubble in its upper half.
  const box = (await card.locator('canvas').first().boundingBox())!;
  expect(
    await whiteShare(page, {
      x: box.x + box.width * 0.25,
      y: box.y + 2,
      width: box.width * 0.5,
      height: box.height * 0.5,
    }),
  ).toBeGreaterThan(0.02);
});
