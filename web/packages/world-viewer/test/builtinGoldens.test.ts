// Behaviour pin for the built-in block geometry (docs/dev/complexity.md, WP12): every built-in id over the property
// combinations its geometry branches on (facings, chest pairs and kinds, bed parts, skull types and rotations, banner
// layers, shulker colours), with a sheet lookup that lacks some sheets, hashed in memory.

import { describe, expect, it } from 'vitest';
import { builtinQuads, type SheetLookup } from '../src/mesh/models/builtin';
import type { Quad } from '../src/mesh/quad';
import { Digest } from './digest';

const MISSING = new Set(['entity/bed/blue', 'entity/chest/christmas_double', 'entity/enderdragon/dragon']);
const sheets: SheetLookup = (name) => (MISSING.has(name) ? null : { c: name.length % 7, r: name.length % 5, wc: name.includes('dragon') ? 16 : 4, hc: 4 });
const bigDragon: SheetLookup = (name) => ({ c: 1, r: 1, wc: name.includes('dragon') ? 16 : 4, hc: 4 });

const FACINGS = [undefined, 'north', 'south', 'west', 'east', 'up', 'down'];
const IDS = [54, 130, 146, 26, 63, 68, 176, 177, 144, 119, 209, 36, 166, 217, 219, 226, 234, 1, 300];

function props(): Array<Record<string, string>> {
  const out: Array<Record<string, string>> = [];
  for (const facing of FACINGS) {
    const base: Record<string, string> = facing ? { facing } : {};
    out.push(base);
    for (const pair of ['1,0', '-1,0', '0,1', '0,-1']) for (const kind of ['normal', 'trapped', 'christmas']) out.push({ ...base, pair, kind });
    for (const part of ['head', 'foot']) for (const color of ['red', 'blue', 'lime']) out.push({ ...base, part, color });
    for (const type of ['skeleton', 'wither', 'zombie', 'player', 'creeper', 'dragon', 'nope']) for (const rotation of ['0', '5', '15', 'x']) out.push({ ...base, type, rotation });
    out.push({ ...base, layer: 'true' }, { ...base, rotation: '7' }, { ...base, color: 'cyan' });
  }
  return out;
}

const show = (q: Quad) => JSON.stringify(q, (_k, v) => (typeof v === 'number' ? Math.round(v * 1e6) / 1e6 : v));

describe('built-in geometry golden', () => {
  it('pins every built-in id over its properties', () => {
    const d = new Digest();
    for (const lookup of [sheets, bigDragon])
      for (const id of IDS)
        for (const p of props()) {
          const out: Quad[] = [];
          const handled = builtinQuads(id, { file: 'x', props: p, key: null, builtin: true }, null, lookup, out);
          d.add(`${id} ${JSON.stringify(p)} ${handled} ${out.length}`);
          for (const q of out) d.add(show(q));
        }
    expect(d.hex()).toBe('90e0489ad2564a6f9bbc828cbde1bd07');
  });
});
