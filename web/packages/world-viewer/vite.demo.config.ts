import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev`: the fixture-backed demo page (index.html → demo/main.tsx); no bot needed.
export default defineConfig({
  plugins: [react()],
  worker: { format: 'es' },
  server: { port: 5178 },
});
