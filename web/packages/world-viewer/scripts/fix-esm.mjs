#!/usr/bin/env node
// After `tsc`, makes dist/ valid strict ESM: relative specifiers get their file extension ("./types" → "./types.js",
// "./dir" → "./dir/index.js"). webpack and Turbopack require fully specified imports inside "type": "module" packages;
// the source keeps extensionless imports for Vite and vitest. `new URL('./worker.js', import.meta.url)` already names a file.

import { readdirSync, readFileSync, writeFileSync, statSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const dist = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'dist');

function walk(dir) {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

const SPEC = /(\bfrom\s*|\bimport\s*\(\s*|\bimport\s+|\bexport\s*\*\s*from\s*)(['"])(\.{1,2}\/[^'"]*)\2/g;
let fixed = 0;
for (const file of walk(dist).filter((f) => f.endsWith('.js') || f.endsWith('.d.ts'))) {
  const src = readFileSync(file, 'utf8');
  const out = src.replace(SPEC, (m, pre, q, spec) => {
    if (/\.(js|json|css|png|wasm)$/.test(spec)) return m;
    const base = resolve(dirname(file), spec);
    const ext = file.endsWith('.d.ts') ? '.js' : '.js';
    if (existsSync(base + '.js') || existsSync(base + '.d.ts')) {
      fixed++;
      return `${pre}${q}${spec}${ext}${q}`;
    }
    if (existsSync(join(base, 'index.js'))) {
      fixed++;
      return `${pre}${q}${spec}/index${ext}${q}`;
    }
    return m;
  });
  if (out !== src) writeFileSync(file, out);
}
console.log(`fix-esm: ${fixed} relative specifiers made explicit`);
