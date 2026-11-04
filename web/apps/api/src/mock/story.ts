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
    if (id === 'story.directives') {
      // As the bridge does: the sender is the signed player; the body never names one.
      const raw = rawBody.length ? (JSON.parse(rawBody.toString('utf8')) as Record<string, unknown>) : {};
      if ('player' in raw || 'allow_commands' in raw)
        return { status: 400, json: { error: 'player and allow_commands are set by the bridge', code: 'bad_request' } };
      if (!player)
        return { status: 403, json: { error: 'no Minecraft player is linked to this account', code: 'forbidden' } };
    }
    const body = rawBody.length
      ? toCamel<never>(JSON.parse(rawBody.toString('utf8')), { opaque: REQUEST_OPAQUE_KEYS })
      : ({} as never);
    const cid = params.id ?? '';
    try {
      const out = this.run(id, cid, query, body);
      return { json: toSnake(out) };
    } catch (e) {
      const status = (e as { status?: number }).status ?? 500;
      return {
        status,
        json: {
          error: e instanceof Error ? e.message : 'story error',
          code: status === 404 ? 'not_found' : 'bad_request',
        },
      };
    }
  }

  private run(id: RouteId, cid: string, query: URLSearchParams, body: never): unknown {
    const s = this.sim;
    switch (id) {
      case 'story.list':
        return s.list();
      case 'story.recipes':
        return s.recipes(query.get('workbench') ?? undefined);
      case 'story.create':
        return s.create(body);
      case 'story.get': {
        const since = query.get('since');
        return s.state(cid, since ? Number(since) : undefined);
      }
      case 'story.release':
        return s.release(cid);
      case 'story.resume':
        return s.resume(cid);
      case 'story.online':
        return s.online(cid, body);
      case 'story.pawns':
        return s.pawns(cid, body);
      case 'story.directives.list':
        return { directives: s.directives() };
      case 'story.directive':
        return s.directiveDetail(cid);
      case 'story.beds':
        return s.beds(cid, body);
      case 'story.time':
        return s.time(body);
      case 'story.home':
        return s.home(cid, body);
      case 'story.pause':
        return s.pause(cid, (body as { paused: boolean }).paused);
      case 'story.priorities':
        return s.priorities(cid, body);
      case 'story.schedule':
        return s.schedule(cid, body);
      case 'story.draft':
        return s.draft(cid, body);
      case 'story.orders':
        return s.orders(cid, body);
      case 'story.zones':
        return s.zones(cid, body);
      case 'story.designations':
        return s.designations(cid, body);
      case 'story.builds':
        return s.builds(cid, body);
      case 'story.storage':
        return s.storage(cid, body);
      case 'story.bills':
        return s.bills(cid, body);
      case 'story.workbenches':
        return s.workbenches(cid, body);
      case 'story.options':
        return s.options(cid, body);
      case 'story.architect':
        return s.architect(cid);
      case 'story.directives':
        return s.directive(body);
      default:
        throw Object.assign(new Error('not a story route'), { status: 404 });
    }
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
