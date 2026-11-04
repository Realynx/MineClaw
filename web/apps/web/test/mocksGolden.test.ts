import { afterEach, describe, expect, it, vi } from 'vitest';

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0;
  return h;
}
const h = (v: unknown) => hash(JSON.stringify(v));

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('mock data builders (golden, pinned before they were split)', () => {
  it('build the same bodies, scripts, controllers, contexts, heat, audit and diffs', async () => {
    vi.useFakeTimers({ now: new Date('2026-11-01T12:00:00Z'), toFake: ['Date'] });
    let seed = 777;
    vi.spyOn(Math, 'random').mockImplementation(() => {
      seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
      return seed / 2 ** 32;
    });
    vi.resetModules();
    const live = await import('@/mocks/data/live');
    const misc = await import('@/mocks/data/misc');
    const scripts = await import('@/mocks/data/scripts');
    const util = await import('@/mocks/util');
    for (let i = 0; i < 40; i++) live.tick(); // some activity, thoughts and calls in flight
    const names = [...live.BOTS.map((b) => b.name), 'nobody'];
    const out: Record<string, unknown> = {
      now: live.BOTS.map((b) => h(live.botNow(b))),
      scripts: h(live.bodyScripts()),
      script: names.flatMap((n) => [h(live.bodyScript(n, true)), h(live.bodyScript(n, false))]),
      controller: names.map((n) => h(live.controller(n))),
      context: ['main', 'maid_chan_67', 'other'].map((b) => h(live.brainContext(b, 0))),
      turns: ['main', 'maid_chan_67'].flatMap((b) => [0, 3, 7, 11].map((n) => h(live.brainTurn(b, 100 + n)))),
      heat: [h(live.memoryHeat(undefined, 900, 'k')), h(live.memoryHeat('main', 60, 'k')), h(live.memoryHeat('nobody', 900, 'k'))],
      audit: h(misc.webAudit),
      diffs: ['cobble_loop', 'nether_scout', 'none'].flatMap((n) => [h(scripts.diff(n, 1, 2)), h(scripts.diff(n, 2, 7)), h(scripts.diff(n, 6, 7, 1)), h(scripts.diff(n, 3, 3))]),
      lineDiff: [
        h(util.lineDiff([], [])),
        h(util.lineDiff(['a', 'b', 'c'], ['a', 'x', 'c', 'd'])),
        h(util.lineDiff(['a', 'b'], [])),
        h(util.lineDiff([], ['q'])),
        h(util.lineDiff(['x', 'y', 'z', 'x'], ['y', 'x', 'z'])),
      ],
    };
    expect(out).toMatchSnapshot();
  });
});
