import { defineConfig, devices } from '@playwright/test';

/**
 * Smoke tests run the app in mock mode (MSW + the synthetic SSE stream; no Nest or bot needed) on :3200, with the GPU off
 * (software rendering, like a browser with hardware acceleration disabled). Set PLAYWRIGHT_BASE_URL to test a running server.
 */
const port = 3200;
const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? `http://localhost:${port}`;

export default defineConfig({
  testDir: 'e2e',
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    launchOptions: { args: ['--disable-gpu', '--disable-gpu-compositing'] },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
  webServer: process.env.PLAYWRIGHT_BASE_URL
    ? undefined
    : {
        command: `node scripts/with-mock.mjs next dev --turbopack -p ${port}`,
        url: `${baseURL}/login`,
        timeout: 180_000,
        reuseExistingServer: true,
        env: { NEXT_DIST_DIR: '.next-e2e' },
      },
});
