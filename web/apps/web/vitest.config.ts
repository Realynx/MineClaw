import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const here = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': path.join(here, 'src') } },
  test: {
    include: ['test/**/*.test.{ts,tsx}'],
    environment: 'happy-dom',
    setupFiles: ['test/setup.ts'],
    css: false,
  },
});
