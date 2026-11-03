// Copies @mineclaw/world-viewer's emitted mesh worker into public/assets/.
//
// Why: the package's library build references its worker as an absolute "/assets/worker-<hash>.js" URL (Vite's default
// `base: '/'`), which Turbopack cannot bundle ("Module not found") and Next would answer with a 404. Serving the file from
// public/ at exactly that path makes the reference work in dev and in the standalone server. Remove this once the package
// emits a relative worker URL (Vite `base: './'`).
import { copyFileSync, existsSync, mkdirSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
let distAssets;
try {
  // ESM-only package ("import" condition): resolve it the ESM way, .../dist/index.js → .../dist/assets
  distAssets = path.join(path.dirname(fileURLToPath(import.meta.resolve('@mineclaw/world-viewer'))), 'assets');
} catch {
  console.warn('[copy-viewer-worker] @mineclaw/world-viewer is not installed; skipping');
  process.exit(0);
}
if (!existsSync(distAssets)) {
  console.warn(`[copy-viewer-worker] ${distAssets} does not exist (build the viewer first); skipping`);
  process.exit(0);
}
const out = path.join(here, '..', 'public', 'assets');
mkdirSync(out, { recursive: true });
for (const f of readdirSync(distAssets)) {
  if (/^worker-.*\.js(\.map)?$/.test(f)) {
    copyFileSync(path.join(distAssets, f), path.join(out, f));
    console.log(`[copy-viewer-worker] public/assets/${f}`);
  }
}
