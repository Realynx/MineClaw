/**
 * Picking a saved design up on the Story map (`/story?design=N`, the design library, a drop on the map): a redstone design
 * is refused, because pawns cannot build a working circuit yet and its saved plan holds only the blocks the classic
 * builder knows. Every other design is picked up as before.
 */
import type { DesignerSession } from '@mineclaw/contracts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DesignLibrary } from '@/features/story/DesignLibrary';
import type { Story } from '@/features/story/mode/commands';
import { dropDesign, pickUpDesign } from '@/features/story/mode/designParam';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { StaticAuthProvider } from '@/lib/auth';

const ID = 9;
const REFUSAL = "Adder is a redstone circuit: pawns can't build those yet";

/** What the classic builder sliced out of the design: the plan a circuit must never be built from. */
const PLAN = {
  id: ID,
  blocks: [
    [2, 64, 2, 4, 0],
    [3, 64, 2, 4, 0],
  ],
};
const PLAN_BLOCKS = [
  [0, 0, 0, 4, 0],
  [1, 0, 0, 4, 0],
];

const studioSession = (kind: DesignerSession['kind']): DesignerSession => ({
  id: 3,
  owner: 'poofimafox',
  model: 'main',
  designId: ID,
  name: 'Adder',
  rev: 0,
  blocks: [[30, 0, 30, 55, 0]],
  dug: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: [],
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind,
});

interface Served {
  /** The kind the design's summary reports (null: a summary without one). */
  summary: string | null;
  /** The design's summary cannot be read at all. */
  unreadable?: boolean;
  plan?: object;
  session?: DesignerSession;
}

/** A fake API for design 9; returns the requests made, in order. */
function serve(d: Served): string[] {
  const calls: string[] = [];
  const json = (body: unknown, status = 200) =>
    Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));
  const routes: Record<string, () => Promise<Response>> = {
    [`GET /api/designs/${ID}`]: () =>
      d.unreadable
        ? json({ error: 'the bridge is not reachable' }, 503)
        : json({
            design: {
              id: ID,
              name: 'Adder',
              status: 'accepted',
              rev: 2,
              revisions: 2,
              hasPicture: false,
              kind: d.summary,
            },
            revisions: [],
            defaultDiff: { a: '1', b: '2' },
          }),
    [`GET /api/blueprints/${ID}`]: () => json(d.plan ?? {}),
    [`GET /api/buildplans/${ID}`]: () => json({}),
    'GET /api/designer/models': () => json({ models: [], version: '1.12.2' }),
    'POST /api/designer/sessions': () => (d.session ? json(d.session) : json({ error: 'no such design' }, 404)),
  };
  setCsrfToken('test-csrf-token-000000000');
  setFetch((input, init) => {
    const url = new URL(
      typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
      'http://localhost',
    );
    const call = `${init?.method ?? 'GET'} ${url.pathname}`;
    calls.push(call);
    return routes[call]?.() ?? json({ error: 'not served' }, 404);
  });
  return calls;
}

const env = (design = true) => ({ say: vi.fn(), can: vi.fn(() => design), setTool: vi.fn() });

afterEach(() => {
  setCsrfToken(null);
  setFetch(() => Promise.reject(new Error('no request expected')));
});

describe('picking up a redstone design on the Story map', () => {
  it('is refused before any saved plan is read, even when a saved plan exists', async () => {
    const calls = serve({ summary: 'redstone', plan: PLAN, session: studioSession('redstone') });
    const e = env();
    await pickUpDesign(e, ID);
    expect(e.say).toHaveBeenCalledExactlyOnceWith(REFUSAL, 'warn');
    expect(e.setTool).not.toHaveBeenCalled();
    expect(calls).toEqual([`GET /api/designs/${ID}`]);
  });

  it('is refused when only the studio session knows it is redstone', async () => {
    const calls = serve({ summary: 'build', session: studioSession('redstone') });
    const e = env();
    await pickUpDesign(e, ID);
    expect(e.say).toHaveBeenCalledExactlyOnceWith(REFUSAL, 'warn');
    expect(e.setTool).not.toHaveBeenCalled();
    expect(calls).toContain('POST /api/designer/sessions');
  });

  it('a design whose kind cannot be read is not picked up from its saved plan', async () => {
    const calls = serve({ summary: null, unreadable: true, plan: PLAN });
    const e = env();
    await pickUpDesign(e, ID);
    expect(e.say).toHaveBeenCalledExactlyOnceWith(
      `Design #${ID} could not be read: The bot is not reachable right now.`,
      'warn',
    );
    expect(e.setTool).not.toHaveBeenCalled();
    expect(calls).toEqual([`GET /api/designs/${ID}`]);
  });

  it('dropped on the map, queues nothing', async () => {
    serve({ summary: 'redstone', plan: PLAN });
    const c = { say: vi.fn(), can: () => true, run: vi.fn(), write: vi.fn(), canWrite: true };
    await dropDesign(c as unknown as Story, ID, [10, 64, 10]);
    expect(c.say).toHaveBeenCalledExactlyOnceWith(REFUSAL, 'warn');
    expect(c.run).not.toHaveBeenCalled();
    expect(c.write).not.toHaveBeenCalled();
  });
});

describe('picking up any other design', () => {
  it.each(['build', null])('still takes the saved plan of a %s design', async (summary) => {
    serve({ summary, plan: PLAN });
    const e = env();
    await pickUpDesign(e, ID);
    expect(e.setTool).toHaveBeenCalledExactlyOnceWith({ kind: 'design', id: ID, name: 'Adder', blocks: PLAN_BLOCKS });
    expect(e.say).toHaveBeenCalledExactlyOnceWith('Adder: move to a spot and click to queue it (Q/E turns)');
  });

  it('still takes the studio session of a build design that has no saved plan', async () => {
    serve({ summary: 'build', session: studioSession('build') });
    const e = env();
    await pickUpDesign(e, ID);
    expect(e.setTool).toHaveBeenCalledExactlyOnceWith({
      kind: 'design',
      id: ID,
      name: 'Adder',
      blocks: [[0, 0, 0, 55, 0]],
    });
  });

  it('without the design role and without a saved plan there is still nothing to build from', async () => {
    const calls = serve({ summary: 'build', session: studioSession('build') });
    const e = env(false);
    await pickUpDesign(e, ID);
    expect(e.say).toHaveBeenCalledExactlyOnceWith('Adder has no block list the colony can build from', 'warn');
    expect(calls).not.toContain('POST /api/designer/sessions');
  });
});

describe('the design library on the Story map', () => {
  it('labels a redstone design beside its revision, as it does a mine', () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    const design = (id: number, name: string, kind: string | null) => ({
      id,
      name,
      status: 'accepted',
      rev: 2,
      revisions: 2,
      hasPicture: false,
      kind,
    });
    qc.setQueryData(qk('designs.list', {}, { q: undefined, limit: 24, offset: 0 }), {
      total: 3,
      revisionsTotal: 6,
      limit: 24,
      offset: 0,
      items: [design(9, 'Adder', 'redstone'), design(8, 'Shaft', 'mine'), design(7, 'Storehouse', 'build')],
    });
    const me = {
      username: 'builder',
      player: null,
      roles: ['design' as const],
      totpEnrolled: false,
      sessionCreatedAt: '',
      idleExpiresAt: '',
      absoluteExpiresAt: '',
    };
    const library = createElement(DesignLibrary, { onPick: vi.fn() });
    render(
      createElement(QueryClientProvider, { client: qc }, createElement(StaticAuthProvider, { me, children: library })),
    );
    expect(screen.getByTitle('Place Adder (#9)').textContent).toContain('#9 · r2 · Redstone');
    expect(screen.getByTitle('Place Shaft (#8)').textContent).toContain('#8 · r2 · Mine');
    const plain = screen.getByTitle('Place Storehouse (#7)').textContent ?? '';
    expect(plain).toContain('#7 · r2');
    expect(plain).not.toMatch(/Redstone|Mine/);
  });
});
