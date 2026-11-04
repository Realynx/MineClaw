import { ROUTE_IDS, type LiveStatus, type WorldEntities } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { ApiError, ContractError, errorText } from '@/lib/api/client';
import { staleTimeFor } from '@/lib/api/query';
import { connectionView } from '@/lib/live/connection';
import { mergeEntities } from '@/lib/live/merge';
import { guardedHref } from '@/lib/navGuard';

/** Small lib helpers, pinned over their inputs before they were reshaped into tables and steps. */
describe('lib helpers (golden)', () => {
  it('error text for every status and code', () => {
    const out: string[] = [];
    for (const status of [0, 400, 401, 403, 404, 409, 429, 500, 501, 503])
      for (const code of [undefined, 'csrf', 'busy', 'forbidden']) out.push(`${status} ${code} → ${errorText(new ApiError(status, `msg ${status}`, { code }))}`);
    out.push(errorText(new ContractError('x', [] as never)));
    out.push(errorText(new Error('plain')));
    out.push(errorText('a string'));
    out.push(errorText(42));
    expect(out).toMatchSnapshot();
  });

  it('stale time per route', () => {
    expect(Object.fromEntries(ROUTE_IDS.map((id) => [id, String(staleTimeFor(id))]))).toMatchSnapshot();
  });

  it('the connection indicator for every stream and status', () => {
    const statuses: Array<LiveStatus | undefined> = [
      undefined,
      { upstream: 'up', bot: 'up', latencyMs: 41.6 } as unknown as LiveStatus,
      { upstream: 'up', bot: 'up', latencyMs: null } as unknown as LiveStatus,
      { upstream: 'up', bot: 'up' } as unknown as LiveStatus,
      { upstream: 'down', bot: 'up', message: 'gone' } as unknown as LiveStatus,
      { upstream: 'down', bot: 'up' } as unknown as LiveStatus,
      { upstream: 'reconnecting', bot: 'up' } as unknown as LiveStatus,
      { upstream: 'connecting', bot: 'down', message: 'm' } as unknown as LiveStatus,
      { upstream: 'up', bot: 'down' } as unknown as LiveStatus,
      { upstream: 'up', bot: 'down', message: 'busy' } as unknown as LiveStatus,
    ];
    const out: unknown[] = [];
    for (const stream of ['idle', 'connecting', 'open', 'reconnecting', 'closed'] as const)
      for (const since of [100, 5000]) for (const s of statuses) out.push([stream, since, connectionView(stream, s, since)]);
    expect(out).toMatchSnapshot();
  });

  it('guards plain same-origin link clicks only', () => {
    const ev = (o: Partial<Record<string, number | boolean>> = {}) => ({
      button: 0,
      metaKey: false,
      ctrlKey: false,
      shiftKey: false,
      altKey: false,
      defaultPrevented: false,
      ...o,
    }) as Parameters<typeof guardedHref>[0];
    const a = (href: string, target = '', download = false) => ({ href, target, hasAttribute: (n: string) => n === 'download' && download });
    const here = { origin: 'https://x.test', pathname: '/live' };
    const out = [
      guardedHref(ev(), a('/memory?kind=facts#a'), here),
      guardedHref(ev(), a('/live?view=tree'), here),
      guardedHref(ev(), a('https://other.test/memory'), here),
      guardedHref(ev(), a('http://[bad'), here),
      guardedHref(ev(), null, here),
      guardedHref(ev({ button: 1 }), a('/memory'), here),
      guardedHref(ev({ metaKey: true }), a('/memory'), here),
      guardedHref(ev({ ctrlKey: true }), a('/memory'), here),
      guardedHref(ev({ shiftKey: true }), a('/memory'), here),
      guardedHref(ev({ altKey: true }), a('/memory'), here),
      guardedHref(ev({ defaultPrevented: true }), a('/memory'), here),
      guardedHref(ev(), a('/memory', '_blank'), here),
      guardedHref(ev(), a('/memory', '_self'), here),
      guardedHref(ev(), a('/memory', '', true), here),
    ];
    expect(out).toMatchSnapshot();
  });

  it('merges entity deltas the same', () => {
    const ent = (dim: number, id: number | null, x: number) => ({ dim, id, x, y: 0, z: 0, kind: 'mob' });
    const w = (o: Partial<WorldEntities> & { rev: number | null }): WorldEntities =>
      ({ at: 1, full: false, bots: [{ name: 'b' }], entities: [], removed: [], ...o }) as unknown as WorldEntities;
    const prev = w({ rev: 5, at: 10, entities: [ent(0, 1, 1), ent(0, 2, 2), ent(-1, null, 3)] as never });
    const cases: Array<[WorldEntities | undefined, WorldEntities]> = [
      [undefined, w({ rev: 1 })],
      [prev, w({ rev: 6, full: true })],
      [w({ rev: null }), w({ rev: 6 })],
      [prev, w({ rev: 4, at: 20 })],
      [prev, w({ rev: 5, at: 20, bots: [{ name: 'c' }] as never })],
      [prev, w({ rev: 5, at: 5 })],
      [prev, w({ rev: 6, at: 20, entities: [ent(0, 2, 9), ent(0, 3, 4)] as never, removed: [[0, 1]] as never })],
      [prev, w({ rev: null, at: 20, entities: [ent(0, 4, 4)] as never })],
      [prev, w({ rev: 7, at: 20, removed: undefined })],
    ];
    expect(cases.map(([p, n]) => mergeEntities(p, n))).toMatchSnapshot();
  });
});
