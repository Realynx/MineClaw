import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    environment: 'node',
    // Every suite boots its own app on ephemeral ports with its own temp dir; keep them in separate workers.
    pool: 'forks',
    testTimeout: 20000,
    hookTimeout: 20000,
    env: { NODE_ENV: 'test' },
  },
});
