// Behaviour pin for the complexity refactor of render/icons.ts (docs/dev/complexity.md, WP12): drawBlockIcon over every
// id × meta of both versions (no hint, and a set of palette hints: b1.8.1 item cells, 1.12.2 item models), at two sizes and
// offsets, into a recording stand-in canvas (every call and property write, tinted cell canvases included). The digest was
// taken from the code before it was reshaped.

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack } from '../src/assets/bundle';
import { drawBlockIcon } from '../src/render/icons';
import { Digest } from './digest';
import { noPacks, pack1122, packBeta } from './pack';

let log: string[] = [];
const fmt = (x: unknown): string => {
  if (typeof x === 'number' || typeof x === 'string' || typeof x === 'boolean') return String(x);
  if (x && typeof x === 'object' && 'tag' in x) return String((x as { tag: string }).tag);
  return typeof x;
};
let canvases = 0;
function recorder(tag: string): CanvasRenderingContext2D {
  return new Proxy({} as Record<string, unknown>, {
    get: (t, p: string) => (p in t ? t[p] : (...a: unknown[]) => void log.push(`${tag}.${p}(${a.map(fmt).join(',')})`)),
    set: (t, p: string, v) => {
      t[p] = v;
      log.push(`${tag}.${p}=${fmt(v)}`);
      return true;
    },
  }) as unknown as CanvasRenderingContext2D;
}

const bundleOf = (pack: Pack): AssetBundle => ({ version: pack.version, pack, blocks: { tag: 'blocks' }, items: { tag: 'items' } }) as unknown as AssetBundle;

describe.skipIf(noPacks)('icon goldens', () => {
  const g = globalThis as unknown as { document?: unknown };
  let had: unknown;
  beforeAll(() => {
    had = g.document;
    g.document = {
      createElement: () => {
        const tag = `canvas${canvases++}`;
        const ctx = recorder(tag);
        return { tag, width: 0, height: 0, getContext: () => ctx };
      },
    };
  });
  afterAll(() => {
    g.document = had;
  });

  it('drawBlockIcon over every state and hint, both versions', () => {
    const d = new Digest();
    const ctx = recorder('g');
    const HINTS: Record<string, Array<string | null>> = {
      'b1.8.1': [null, 'item:43', 'item:0', 'item:255', 'oak_door', ''],
      '1.12.2': [null, 'oak_door', 'bed', 'sign', 'reeds', 'stone', 'redstone', 'repeater', 'item:43', 'no_such', 'sapling', 'tallgrass', 'vine', 'double_plant', 'cauldron'],
    };
    for (const pack of [packBeta(), pack1122()]) {
      const bundle = bundleOf(pack);
      const maxId = pack.version === 'b1.8.1' ? 130 : 256;
      for (let id = 0; id < maxId; id++)
        for (let meta = 0; meta < 16; meta++) {
          const big = (id + meta) % 3 === 0;
          try {
            drawBlockIcon(ctx, bundle, id, meta, big ? 48 : 32, big ? 5 : 0, big ? 7 : 0);
          } catch (e) {
            log.push(`throw ${(e as Error).message}`);
          }
          d.add(`${pack.version} ${id}:${meta} ${log.join(';')}`);
          log = [];
        }
      for (const icon of HINTS[pack.version])
        for (const [id, meta] of [[64, 0], [26, 3], [63, 0], [83, 0], [1, 0], [55, 0], [93, 1], [6, 2], [31, 1], [106, 0], [175, 2], [118, 0]]) {
          drawBlockIcon(ctx, bundle, id, meta, 32, 3, 3, icon);
          d.add(`${pack.version} ${icon} ${id}:${meta} ${log.join(';')}`);
          log = [];
        }
    }
    expect(d.hex()).toBe('21d2a803676ee8d5fe232b1d2c3ba177');
  });
});
