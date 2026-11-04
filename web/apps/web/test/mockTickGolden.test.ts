import { afterEach, describe, expect, it, vi } from 'vitest';

/** FNV-1a over a string. */
function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0;
  return h;
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('the mock hive tick (golden, pinned before it was split into steps)', () => {
  it('emits the same frames tick after tick', async () => {
    vi.useFakeTimers({ now: new Date('2026-11-01T12:00:00Z'), toFake: ['Date'] });
    let seed = 12345;
    vi.spyOn(Math, 'random').mockImplementation(() => {
      seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
      return seed / 2 ** 32;
    });
    vi.resetModules();
    const { tick } = await import('@/mocks/data/live');
    const out: string[] = [];
    for (let i = 0; i < 130; i++) {
      vi.setSystemTime(new Date(Date.UTC(2026, 10, 1, 12, 0, i)));
      const frames = tick();
      out.push(frames.map(([kind, body]) => `${kind}:${hash(JSON.stringify(body))}`).join(' '));
    }
    expect(out).toMatchSnapshot();
  });
});
