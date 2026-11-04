import { afterEach, describe, expect, it, vi } from 'vitest';

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0;
  return h;
}

afterEach(() => {
  vi.useRealTimers();
});

describe('mock RAPTOR tree and story ground (golden, pinned before they were split)', () => {
  it('builds the same tree', async () => {
    vi.useFakeTimers({ now: new Date('2026-11-01T12:00:00Z'), toFake: ['Date'] });
    vi.resetModules();
    const { cloudTree, raptorNode } = await import('@/mocks/data/raptor');
    const nodes: unknown[] = [];
    for (let id = 0; id < 600; id++) nodes.push(raptorNode(id) ?? null);
    expect(hash(JSON.stringify(nodes))).toMatchSnapshot();
    expect(hash(JSON.stringify(cloudTree()))).toMatchSnapshot();
  });

  it('finds the same ground', async () => {
    const { storySim } = await import('@/mocks/data/story');
    const world = (storySim as unknown as { opts: { world?: { ground: (x: number, z: number) => number } } }).opts.world;
    const ground = world?.ground ?? (() => NaN);
    const out: number[] = [];
    for (let x = -20; x <= 20; x += 3) for (let z = -20; z <= 20; z += 3) out.push(ground(x, z));
    expect(out).toMatchSnapshot();
  });
});
