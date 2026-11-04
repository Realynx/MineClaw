import type { WorldEntities } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { drawDotMap } from '@/features/world/DotMap';

function recorder() {
  const log: string[] = [];
  const fmt = (v: unknown) => (typeof v === 'number' ? String(Math.round(v * 100) / 100) : JSON.stringify(v));
  const ctx = new Proxy(
    {},
    {
      get: (_t, k: string) => (...a: unknown[]) => void log.push(`${k}(${a.map(fmt).join(',')})`),
      set: (_t, k: string, v: unknown) => {
        log.push(`${k}=${fmt(v)}`);
        return true;
      },
    },
  ) as unknown as CanvasRenderingContext2D;
  return { ctx, log };
}

const DATA = {
  at: 0,
  rev: 1,
  full: true,
  bots: [
    { name: 'maid_chan', short: 'maid', dim: 0, x: 100.5, y: 64, z: -40.2, yaw: 135 },
    { name: 'other', short: 'o', dim: 0, x: 110, y: 64, z: -35, yaw: 0 },
    { name: 'nether', short: 'n', dim: -1, x: 100, y: 64, z: -40, yaw: 0 },
    { name: 'noyaw', short: 'ny', dim: 0, x: 90, y: 64, z: -50 },
  ],
  entities: [
    { kind: 'player', dim: 0, x: 102, y: 64, z: -38 },
    { kind: 'item', dim: 0, x: 95, y: 64, z: -45 },
    { kind: 'mob', dim: 0, x: 120, y: 64, z: -20 },
    { kind: 'mob', dim: 0, x: 400, y: 64, z: -20 },
    { kind: 'mob', dim: -1, x: 101, y: 64, z: -40 },
  ],
  removed: [],
} as unknown as WorldEntities;

describe('the dot map (golden, pinned before its passes were split)', () => {
  it('draws the same for a followed bot, a short name, no yaw, and no position', () => {
    const out: string[][] = [];
    for (const [follow, radius] of [
      ['maid_chan', 24],
      ['maid', 40],
      ['noyaw', 16],
      ['nobody', 24],
    ] as const) {
      const r = recorder();
      drawDotMap(r.ctx, 320, 200, DATA, follow, radius);
      out.push(r.log);
    }
    const r = recorder();
    drawDotMap(r.ctx, 100, 100, undefined, 'maid', 24);
    out.push(r.log);
    expect(out).toMatchSnapshot();
  });
});
