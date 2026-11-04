/**
 * Story Mode in the mock bridge: the shared synthetic director (`@mineclaw/contracts/testing`) playing on the mock terrain,
 * with the mock hive's bodies as the AI bodies. Requests arrive snake_case (as the bridge forwards them) and replies leave
 * snake_case. A colony ("Mock Hollow", `story-1`) is founded at start so mock mode is playable at once.
 */
import { REQUEST_OPAQUE_KEYS, toCamel, toSnake, type RouteId, type StoryAiBody } from '@mineclaw/contracts';
import { StorySim, type SimEntity } from '@mineclaw/contracts/testing';
import type { MockHive } from './hive.js';
import { CAMP, groundAt, mockBlock, setMockBlock } from './terrain.js';

type Obj = Record<string, unknown>;

export interface StoryReply {
  status?: number;
  json: unknown;
}

/** As the bridge does: the sender of a directive is the signed player; the body never names one. */
function directiveRefusal(rawBody: Buffer, player: string): StoryReply | null {
  const raw = rawBody.length ? (JSON.parse(rawBody.toString('utf8')) as Record<string, unknown>) : {};
  if ('player' in raw || 'allow_commands' in raw)
    return { status: 400, json: { error: 'player and allow_commands are set by the bridge', code: 'bad_request' } };
  if (!player) return { status: 403, json: { error: 'no Minecraft player is linked to this account', code: 'forbidden' } };
  return null;
}

const ERROR_CODES: Record<number, string> = { 404: 'not_found', 409: 'conflict' };

/** A sim error as the bridge's error reply (its status, a code for it, and the confirm prompt when it has one). */
function storyError(e: unknown): StoryReply {
  const status = (e as { status?: number }).status ?? 500;
  const confirm = (e as { confirm?: unknown }).confirm;
  return {
    status,
    json: {
      error: e instanceof Error ? e.message : 'story error',
      code: ERROR_CODES[status] ?? 'bad_request',
      ...(confirm ? { confirm } : {}),
    },
  };
}

type StoryOp = (s: StorySim, cid: string, query: URLSearchParams, body: never) => unknown;

/** route id -> the sim call that answers it. */
const STORY_OPS: Partial<Record<RouteId, StoryOp>> = {
  'story.list': (s) => s.list(),
  'story.recipes': (s, _cid, query) => s.recipes(query.get('workbench') ?? undefined),
  'story.create': (s, _cid, _query, body) => s.create(body),
  'story.get': (s, cid, query) => {
    const since = query.get('since');
    return s.state(cid, since ? Number(since) : undefined);
  },
  'story.release': (s, cid) => s.release(cid),
  'story.resume': (s, cid) => s.resume(cid),
  'story.online': (s, cid, _query, body) => s.online(cid, body),
  'story.pawns': (s, cid, _query, body) => s.pawns(cid, body),
  'story.directives.list': (s) => ({ directives: s.directives() }),
  'story.directive': (s, cid) => s.directiveDetail(cid),
  'story.beds': (s, cid, _query, body) => s.beds(cid, body),
  'story.time': (s, _cid, _query, body) => s.time(body),
  'story.home': (s, cid, _query, body) => s.home(cid, body),
  'story.pause': (s, cid, _query, body) => s.pause(cid, (body as { paused: boolean }).paused),
  'story.priorities': (s, cid, _query, body) => s.priorities(cid, body),
  'story.roles': (s, cid, _query, body) => s.roles(cid, body),
  'story.schedule': (s, cid, _query, body) => s.schedule(cid, body),
  'story.draft': (s, cid, _query, body) => s.draft(cid, body),
  'story.orders': (s, cid, _query, body) => s.orders(cid, body),
  'story.zones': (s, cid, _query, body) => s.zones(cid, body),
  'story.designations': (s, cid, _query, body) => s.designations(cid, body),
  'story.builds': (s, cid, _query, body) => s.builds(cid, body),
  'story.storage': (s, cid, _query, body) => s.storage(cid, body),
  'story.bills': (s, cid, _query, body) => s.bills(cid, body),
  'story.workbenches': (s, cid, _query, body) => s.workbenches(cid, body),
  'story.options': (s, cid, _query, body) => s.options(cid, body),
  'story.policies': (s, cid, _query, body) => s.policies(cid, body),
  'story.materialRules': (s, cid, _query, body) => s.materialRules(cid, body),
  'story.gear': (s, cid, _query, body) => s.gear(cid, body),
  'story.architect': (s, cid) => s.architect(cid),
  'story.directives': (s, _cid, _query, body) => s.directive(body),
};

export class MockStory {
  readonly sim: StorySim;
  private readonly sent = new Map<string, number>();

  constructor(private readonly hive: MockHive) {
    this.sim = new StorySim({
      defaultHome: CAMP,
      timeRateMs: 0, // the route sweep calls it once per persona in a row
      world: {
        blockAt: (x, y, z) => mockBlock(x, y, z),
        ground: (x, z) => groundAt(x, z),
        setBlock: (x, y, z, id, meta) => setMockBlock(x, y, z, id, meta),
        entities: () => this.entities(),
        ai: () => this.hive.aiBodies() as unknown as StoryAiBody[],
      },
    });
    const { id } = this.sim.create({ name: 'Mock Hollow', pawns: 5 });
    // Something to do from the start: the grove is marked for chopping and a small shed is queued.
    this.sim.designations(id, { op: 'add', kind: 'chop', area: [26, 60, 2, 50, 75, 22] });
    this.sim.builds(id, {
      op: 'add',
      items: [{ kind: 'wall', area: [22, 63, 12, 26, 63, 16], material: 'cobblestone', height: 2 }],
    });
  }

  private entities(): SimEntity[] {
    const list = (this.hive.entities(0).entities as Obj[]) ?? [];
    return list.map((e) => ({
      id: e.id as number,
      kind: e.kind as string,
      type: e.type as number | undefined,
      item: e.item as number | undefined,
      name: (e.name as string | null | undefined) ?? null,
      x: e.x as number,
      y: e.y as number,
      z: e.z as number,
    }));
  }

  /** Answers a story route (bodies and replies snake_case). */
  handle(
    id: RouteId,
    params: Record<string, string>,
    query: URLSearchParams,
    rawBody: Buffer,
    player = '',
  ): StoryReply {
    const refused = id === 'story.directives' ? directiveRefusal(rawBody, player) : null;
    if (refused) return refused;
    const body = rawBody.length
      ? toCamel<never>(JSON.parse(rawBody.toString('utf8')), { opaque: REQUEST_OPAQUE_KEYS })
      : ({} as never);
    try {
      return { json: toSnake(this.run(id, params.id ?? '', query, body)) };
    } catch (e) {
      return storyError(e);
    }
  }

  private run(id: RouteId, cid: string, query: URLSearchParams, body: never): unknown {
    const op = Object.hasOwn(STORY_OPS, id) ? STORY_OPS[id] : undefined;
    if (!op) throw Object.assign(new Error('not a story route'), { status: 404 });
    return op(this.sim, cid, query, body);
  }

  /** `story` frames (snake_case, with the colony id) for every colony whose revision moved since the last call. */
  frames(): Obj[] {
    const out: Obj[] = [];
    for (const c of this.sim.colonies.values()) {
      const last = this.sent.get(c.id);
      if (last === c.rev) continue;
      const delta = this.sim.state(c.id, last ?? 0);
      this.sent.set(c.id, c.rev);
      if (last === undefined) continue; // the first look only takes the cursor (clients fetch the REST state)
      out.push({ ...toSnake<Obj>(delta), id: c.id });
    }
    return out;
  }
}
