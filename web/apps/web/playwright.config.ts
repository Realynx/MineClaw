import { defineConfig, devices } from '@playwright/test';

/**
 * Two projects, both with the GPU off (software rendering, like a browser with hardware acceleration disabled):
 * - `mock`: the app in mock mode (MSW + the synthetic SSE stream; no Nest or bot needed) on :3200.
 * - `prod`: the production standalone build behind the Nest gateway with its mock bridge (scripts/prod-stack.mjs) on :3280:
 *   real HTTP/1.1 (six connections per host), the real CSP, the real stream rates. It rebuilds the app first.
 * Set PLAYWRIGHT_BASE_URL to run the mock specs against a running server (the prod project is skipped then).
 */
const port = 3200;
const prodPort = 3280;
const external = process.env.PLAYWRIGHT_BASE_URL;
const baseURL = external ?? `http://localhost:${port}`;
/** E2E_PROJECT=mock|prod runs one project and starts only its server. */
const only = process.env.E2E_PROJECT;
const wantProd = !external && only !== 'mock';
const wantMock = only !== 'prod';
const desktop = { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } };

export default defineConfig({
  testDir: 'e2e',
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    trace: 'retain-on-failure',
    launchOptions: { args: ['--disable-gpu', '--disable-gpu-compositing'] },
  },
  projects: [
    ...(wantMock ? [{ name: 'mock', testIgnore: /prod\//, use: { ...desktop, baseURL } }] : []),
    ...(!wantProd
      ? []
      : [
          {
            name: 'prod',
            testMatch: /prod\/.*\.spec\.ts/,
            use: { ...desktop, baseURL: `http://127.0.0.1:${prodPort}` },
          },
        ]),
  ],
  webServer: external
    ? undefined
    : [
        ...(wantProd
          ? [
              {
                // Built first, while nothing else compiles; then the mock dev server below.
                command: `node scripts/prod-stack.mjs --build --port ${prodPort}`,
                url: `http://127.0.0.1:${prodPort}/login`,
                timeout: 600_000,
                reuseExistingServer: false,
                env: { MOCK_CHAT_REPLY_MS: '20000' },
              },
            ]
          : []),
        ...(wantMock
          ? [
              {
                command: `node scripts/with-mock.mjs next dev --turbopack -p ${port}`,
                url: `${baseURL}/login`,
                timeout: 180_000,
                reuseExistingServer: true,
                env: { NEXT_DIST_DIR: '.next-e2e' },
              },
            ]
          : []),
      ],
});
