import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseBridgeJson } from '../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));

/** Loads a fixture file as the bridge would send it (snake_case), via `parseBridgeJson`. */
export function loadFixture(name: 'db' | 'tools' | 'live'): Record<string, unknown> {
  const text = readFileSync(join(here, 'fixtures', `${name}.json`), 'utf8');
  return parseBridgeJson(text) as Record<string, unknown>;
}

export const repoRoot = join(here, '..', '..', '..', '..');
