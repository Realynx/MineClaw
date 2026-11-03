import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Library build: ES modules; React and three stay with the app. The mesher runs in a module worker emitted beside the bundle
// (`new Worker(new URL('./mesh/worker.ts', import.meta.url))`, which webpack/Turbopack and Vite all understand).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    emptyOutDir: false,
    sourcemap: true,
    target: 'es2022',
    lib: {
      entry: { index: 'src/index.ts', testing: 'src/testing.ts' },
      formats: ['es'],
    },
    rollupOptions: {
      external: [/^react($|\/)/, /^react-dom($|\/)/, /^three($|\/)/],
    },
  },
  worker: { format: 'es' },
});
