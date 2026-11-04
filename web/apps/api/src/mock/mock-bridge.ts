/**
 * `--mock-bridge` / `Bridge:Mock=true` (dev only; refused when NODE_ENV=production): an in-process HTTP server on an
 * ephemeral loopback port that speaks the bridge `/v1` protocol, so the whole Nest pipeline (signing, identity, streaming,
 * snake→camel, validation) runs exactly as against the real bridge.
 *
 * - Verifies every request's HMAC signature, skew and nonce (401 otherwise) and enforces the bridge's identity rules
 *   (admin role for `/v1/sup/admin/*` and `/v1/admin/nodes*`, a player for chat, no `player`/`allow_commands` in the chat body).
 * - Routes are generated from the contracts `ROUTES` bridge targets, so new routes are served without code changes:
 *   data comes from the contracts test fixtures (snake_case, realistic) or, when a route has none, is generated from its
 *   response schema. Binary routes serve real PNGs, MCWC v1 chunk replies and a `.mcshard` zip.
 * - `GET /v1/live/stream` is a synthetic stream: vitals and positions drift, the `now` snapshot cycles thoughts and tools,
 *   mobs wander, events and memory accesses trickle in, and a viewer chat line gets a `said` reply from the bot ~1.5 s later.
 */
import { createHash } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import {
  ROUTES,
  SSE_EVENTS,
  createDesignerMock,
  parseBridgeJson,
  toSnake,
  type RouteDef,
  type RouteId,
  type SseEventName,
} from '@mineclaw/contracts';
import { mockInventory } from '@mineclaw/contracts/testing';
import { SignatureVerifier, type BridgeIdentity } from '../bridge/signing.js';
import { makeMcwc, makePng, makeZip } from './binary.js';
import { generate } from './generate.js';
import { MockHive } from './hive.js';
import { Replay } from './replay.js';
import { MockStory } from './story.js';
import { drainBlockChanges } from './terrain.js';
import { memoryCloudMock, type CloudMockNode } from '@mineclaw/contracts/testing';

type Obj = Record<string, unknown>;
type Fixtures = Record<'db' | 'tools' | 'live', Obj>;

/** route id → [fixture file, key]. Everything else is generated from the response schema. */
const FIXTURE_OF: Partial<Record<RouteId, [keyof Fixtures, string]>> = {
  overview: ['db', 'overview'],
  'memory.kinds': ['db', 'memoryKinds'],
  'memory.list': ['db', 'memoryPage'],
  'memory.get': ['db', 'memoryDetailClaim'],
  search: ['db', 'search'],
  'raptor.graph': ['db', 'raptorGraph'],
  'raptor.leaves': ['db', 'raptorLeaves'],
  'raptor.node': ['db', 'raptorNode'],
  personality: ['db', 'personality'],
  'shards.packs': ['tools', 'shardPacks'],
  'shards.exportPreview': ['tools', 'shardPreview'],
  'shards.importPreview': ['tools', 'shardImportPreview'],
  'admin.shardImport': ['tools', 'shardImportResult'],
  'scripts.list': ['tools', 'scriptLibrary'],
  'scripts.get': ['tools', 'scriptDetail'],
  'scripts.diff': ['tools', 'scriptDiff'],
  'designs.get': ['tools', 'designDetail'],
  'designs.diff': ['tools', 'designDiff'],
  'admin.schema': ['tools', 'editSchema'],
  'admin.memoryEdit': ['tools', 'memoryEditResult'],
  'admin.rebuildJob': ['tools', 'rebuildJob'],
  'admin.audit': ['tools', 'adminAudit'],
  'admin.compact': ['tools', 'compaction'],
  'bots.list': ['live', 'bots'],
  status: ['live', 'status'],
  events: ['live', 'events'],
  'hive.tree': ['live', 'hiveTree'],
  locate: ['live', 'locate'],
  'world.entities': ['live', 'worldEntities'],
  'world.overlays': ['live', 'worldOverlays'],
  'bodies.controller': ['live', 'controller'],
  'brains.context': ['live', 'brainContext'],
  'brains.turn': ['live', 'brainTurn'],
  'vision.list': ['live', 'visionList'],
  'bodies.scripts': ['live', 'bodyScripts'],
  'bodies.script': ['live', 'bodyScript'],
  'memoryActivity.list': ['live', 'memoryActivity'],
  'memoryActivity.heat': ['live', 'memoryHeat'],
};

export function loadFixtures(): Fixtures {
  const empty: Fixtures = { db: {}, tools: {}, live: {} };
  try {
    const require = createRequire(import.meta.url);
    const dir = join(dirname(require.resolve('@mineclaw/contracts/package.json')), 'test', 'fixtures');
    for (const name of ['db', 'tools', 'live'] as const) {
      const file = join(dir, `${name}.json`);
      if (existsSync(file)) empty[name] = parseBridgeJson(readFileSync(file, 'utf8')) as Obj;
    }
  } catch {
    // generated data only
  }
  return empty;
}

interface Matcher {
  id: RouteId;
  method: string;
  re: RegExp;
  names: string[];
  /** `{param}`s taken from the fixed query of the template (`/v1/sup/viewer/now?bot={bot}`). */
  queryParams: Array<[string, string]>;
}

function buildMatchers(): Matcher[] {
  const out: Matcher[] = [];
  for (const id of Object.keys(ROUTES) as RouteId[]) {
    const r: RouteDef = ROUTES[id];
    if (!r.bridge) continue;
    const [path, fixed] = r.bridge.path.split('?') as [string, string | undefined];
    const names: string[] = [];
    const re = new RegExp(
      `^${path
        .split(/(\{[A-Za-z0-9_]+\})/)
        .map((part) => {
          const m = /^\{([A-Za-z0-9_]+)\}$/.exec(part);
          if (m) {
            names.push(m[1]!);
            return '([^/]+)';
          }
          return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        })
        .join('')}$`,
    );
    const queryParams: Array<[string, string]> = [];
    for (const [k, v] of new URLSearchParams(fixed ?? '')) {
      const m = /^\{([A-Za-z0-9_]+)\}$/.exec(v);
      if (m) queryParams.push([k, m[1]!]);
    }
    out.push({ id, method: r.bridge.method, re, names, queryParams });
  }
  return out;
}

interface Reply {
  status?: number;
  json?: unknown;
  raw?: string;
  bytes?: Buffer;
  type?: string;
  headers?: Record<string, string>;
}

/**
 * A memory cloud of 3000 leaves under 60 layer-1 and 10 layer-2 summaries (2 roots), laid out by the shared synthetic generator
 * (`@mineclaw/contracts/testing`). The fixture graph is too small to show what the view is for.
 */
function syntheticCloud(): Obj {
  const topics = [
    ['cobble', 'generator'], ['wheat', 'farm'], ['oak', 'trees'], ['storage', 'chests'], ['nether', 'portal'],
    ['mob', 'grinder'], ['fox', 'requests'], ['recipes', 'crafting'], ['island', 'platform'], ['scripts', 'loops'],
  ];
  const kinds = ['episode', 'activity', 'fact', 'recipe', 'chat', 'world', 'self'];
  const tree: CloudMockNode[] = [];
  for (let i = 0; i < 3000; i++) {
    const l1 = 3000 + (i % 60);
    const t = topics[(l1 - 3000) % 10]!;
    tree.push({ id: i, layer: 0, parents: [l1], title: `${t[0]} ${t[1]} memory ${i}`, kind: kinds[(i * 7) % kinds.length]! });
  }
  for (let j = 0; j < 60; j++) tree.push({ id: 3000 + j, layer: 1, parents: [3100 + (j % 10)], title: `L1 #${3000 + j}`, kind: 'summary' });
  for (let k = 0; k < 10; k++)
    tree.push({ id: 3100 + k, layer: 2, parents: [3200 + (k % 2)], title: `L2 #${3100 + k}`, kind: 'summary', terms: topics[k]!, text: `Memories about ${topics[k]!.join(' and ')}.` });
  for (let m = 0; m < 2; m++) tree.push({ id: 3200 + m, layer: 3, parents: [], title: `root ${m}`, kind: 'summary' });
  return toSnake<Obj>(memoryCloudMock(tree, { stateKey: 'mock-cloud-3000' }));
}

const iso = (ms = Date.now()) => new Date(ms).toISOString().replace('Z', '+00:00');
const clone = <T>(v: T): T => structuredClone(v);

const THOUGHTS: Array<{ text: string; thinking: string; tool: string; args: string; task: string; line: number }> = [
  {
    text: 'Topping up the cobble chest, then back to the generator.',
    thinking: 'Chest is 80% full.',
    tool: 'move_to',
    args: '[12,64,3]',
    task: 'mining cobble',
    line: 12,
  },
  {
    text: "A creeper is near Robin's farm; keeping my distance.",
    thinking: 'Danger event 4 m away.',
    tool: 'look',
    args: 'creeper',
    task: 'mining cobble',
    line: 18,
  },
  {
    text: 'Pickaxe is worn, crafting a new one from the spare cobble.',
    thinking: 'Durability 7/132.',
    tool: 'craft',
    args: 'stone_pickaxe x1',
    task: 'crafting',
    line: 24,
  },
  {
    text: 'Back to mining.',
    thinking: 'Inventory has space again.',
    tool: 'dig',
    args: '[5,63,7]',
    task: 'mining cobble',
    line: 31,
  },
];

const REPLIES = [
  'On it.',
  'Sure, give me a moment.',
  'Done! Anything else?',
  'I can do that after this stack of cobble.',
];

/** Shared state so REST replies and the stream agree. */
class MockWorld {
  feedSeq = 2000;
  eventId = 1000;
  activitySeq = 100;
  entitiesRev = 6000;
  blocksRev = 900;
  tick = 0;
  readonly hive = new MockHive();
  readonly story = new MockStory(this.hive);
  readonly feed: Obj[] = [];
  private readonly listeners = new Set<(event: SseEventName, data: unknown) => void>();

  constructor(
    private readonly fx: Fixtures,
    readonly replay: Replay | null = Replay.fromEnv(),
  ) {
    const lines = (fx.live.feed as { lines?: Obj[] } | undefined)?.lines ?? [];
    for (const l of lines) this.pushFeed({ ...clone(l) }, false);
  }

  on(fn: (event: SseEventName, data: unknown) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  get listening(): number {
    return this.listeners.size;
  }

  emit(event: SseEventName, data: unknown): void {
    for (const fn of this.listeners) fn(event, data);
  }

  pushFeed(line: Obj, emit = true): Obj {
    const seq = ++this.feedSeq;
    const full = { at: iso(), origin: 'game', ...line, seq };
    this.feed.push(full);
    while (this.feed.length > 1000) this.feed.shift();
    if (emit) this.emit('feed', { lines: [full], last: seq });
    return full;
  }

  feedSince(since: number): Obj {
    const lines = this.feed.filter((l) => (l.seq as number) > since);
    return { lines, last: this.feedSeq };
  }

  botNames(): string[] {
    const replayed = this.replay?.get('status')?.bots as Array<{ name: string }> | undefined;
    return replayed?.length ? replayed.map((b) => b.name) : this.hive.names();
  }

  now(bot: string): Obj {
    const replayed = this.replay?.get('now');
    if (replayed) return { ...replayed, bot, at: iso() };
    const t = THOUGHTS[(this.tick + bot.length) % THOUGHTS.length]!;
    const n = this.hive.now(bot, t);
    if (n) return n;
    const base = clone<Obj>((this.fx.live.now as Obj | undefined) ?? toSnake<Obj>(generate(SSE_EVENTS.now)));
    return { ...base, bot, at: iso() };
  }

  status(): Obj {
    const replayed = this.replay?.get('status');
    if (replayed) return { ...replayed, at: iso() };
    const fixture = (this.fx.live.status as { bots?: Obj[] } | undefined)?.bots?.[0];
    return this.hive.status(fixture ? clone(fixture) : undefined);
  }

  tree(): Obj {
    return this.replay?.get('tree') ?? this.hive.tree();
  }

  entities(): Obj {
    const replayed = this.replay?.get('entities');
    if (replayed) return { ...replayed, at: Date.now(), rev: ++this.entitiesRev, full: true, removed: [] };
    return this.hive.entities(++this.entitiesRev);
  }

  blocks(): Obj {
    const frame = this.hive.blocks(++this.blocksRev);
    // The colony director's changes (felled trees, built ghosts) ride along.
    const story = drainBlockChanges();
    if (story.length) (frame.blocks as Record<string, unknown[]>)['0']!.push(...story);
    return frame;
  }

  event(): Obj {
    const texts = [
      'maid_chan: picked up 3 cobblestone',
      'maid_chan_67: creeper 6 m',
      'maid_chan: chest at home 82% full',
      'maid_chan_67: arrived at the sand pit',
    ];
    const id = ++this.eventId;
    return { events: [{ id, at: iso(), kind: 'info', level: 'info', text: texts[id % texts.length] }], last: id };
  }

  activity(): Obj {
    const base = clone<Obj>(
      (this.fx.live.memoryActivity as Obj | undefined) ?? toSnake<Obj>(generate(SSE_EVENTS.memoryActivity)),
    );
    const seq = ++this.activitySeq;
    const evs = (base.events as Obj[] | undefined) ?? [];
    const ev = { ...(evs[seq % Math.max(1, evs.length)] ?? {}), seq, at: iso() };
    return { events: [ev], in_context: base.in_context ?? [], last: seq };
  }
}

export interface MockBridge {
  url: string;
  key: string;
  world: MockWorld;
  close(): Promise<void>;
}

async function readAll(req: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  return Buffer.concat(chunks);
}

function send(res: ServerResponse, r: Reply): void {
  res.statusCode = r.status ?? 200;
  for (const [k, v] of Object.entries(r.headers ?? {})) res.setHeader(k, v);
  if (r.bytes) {
    res.setHeader('Content-Type', r.type ?? 'application/octet-stream');
    res.setHeader('Content-Length', r.bytes.length);
    res.end(r.bytes);
    return;
  }
  const text = r.raw ?? JSON.stringify(r.json ?? {});
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Content-Length', Buffer.byteLength(text));
  res.end(text);
}

const etagOf = (b: Buffer | string) => `"${createHash('sha256').update(b).digest('hex').slice(0, 16)}"`;

export async function startMockBridge(key: string, opts: { streamIntervalMs?: number } = {}): Promise<MockBridge> {
  const fx = loadFixtures();
  const world = new MockWorld(fx);
  const verifier = new SignatureVerifier(key);
  const matchers = buildMatchers();
  const pngCache = new Map<string, Buffer>();
  const png = (w: number, h: number, seed: number) => {
    const k = `${w}x${h}:${seed}`;
    let b = pngCache.get(k);
    if (!b) pngCache.set(k, (b = makePng(w, h, seed)));
    return b;
  };
  const interval = opts.streamIntervalMs ?? 1000;
  // The Design Studio: the shared fake designer (contracts), replies snake-cased like the bot's.
  const designer = createDesignerMock(100);
  const camel = (b: Buffer): Obj => {
    const raw = JSON.parse(b.toString('utf8') || '{}') as Obj;
    const out: Obj = {};
    for (const [k, v] of Object.entries(raw)) out[k.replace(/_([a-z])/g, (_m, c: string) => c.toUpperCase())] = v;
    return out;
  };
  const designerReply = (id: RouteId, params: Record<string, string>, body: Buffer, identity: BridgeIdentity): Reply | null => {
    const sid = Number(params.id);
    const who = identity.player || identity.user || 'poofimafox';
    const write = id !== 'designer.models' && id !== 'designer.get';
    if (write && !identity.roles.split(',').some((r) => r === 'design' || r === 'admin'))
      return { status: 403, json: { error: 'design role required', code: 'forbidden' } };
    try {
      switch (id) {
        case 'designer.models':
          return { json: toSnake(designer.models()) };
        case 'designer.create':
          return { json: toSnake(designer.create(camel(body) as Parameters<typeof designer.create>[0], who)) };
        case 'designer.version':
          return { json: toSnake(designer.version(sid, camel(body) as never)) };
        case 'designer.get':
          return { json: toSnake(designer.get(sid)) };
        case 'designer.message':
          return { json: toSnake(designer.message(sid, camel(body) as never)) };
        case 'designer.canvas':
          return { json: toSnake(designer.canvas(sid, camel(body) as never)) };
        case 'designer.save':
          return { json: toSnake(designer.save(sid, camel(body) as never, who)) };
        default:
          return null;
      }
    } catch (e) {
      return { status: 400, json: { error: e instanceof Error ? e.message : 'bad request', code: 'bad_request' } };
    }
  };

  let cloud: Obj | undefined;
  const dataFor = (id: RouteId, params: Record<string, string>): unknown => {
    const route: RouteDef = ROUTES[id];
    const f = FIXTURE_OF[id];
    if (id === 'memory.get' && params.kind === 'chests' && fx.db.memoryDetailChest)
      return clone(fx.db.memoryDetailChest);
    if (f && fx[f[0]][f[1]] !== undefined) return clone(fx[f[0]][f[1]]);
    return route.response ? toSnake(generate(route.response)) : {};
  };

  const handle = (
    id: RouteId,
    req: IncomingMessage,
    identity: BridgeIdentity,
    params: Record<string, string>,
    query: URLSearchParams,
    body: Buffer,
  ): Reply => {
    const route: RouteDef = ROUTES[id];
    const inm = req.headers['if-none-match'];
    const conditional = (bytes: Buffer, type: string, headers: Record<string, string> = {}): Reply => {
      const etag = etagOf(bytes);
      if (inm === etag) return { status: 304, raw: '', headers: { ETag: etag } };
      return { bytes, type, headers: { ETag: etag, ...headers } };
    };
    if (id.startsWith('story.')) {
      // Like the real bridge: Story Mode writes need the story or the admin role.
      const roles = identity.roles.split(',');
      if (route.method === 'POST' && !roles.includes('story') && !roles.includes('admin'))
        return { status: 403, json: { error: 'story role required', code: 'forbidden' } };
      const r = world.story.handle(id, params, query, body, identity.player);
      return { status: r.status, json: r.json };
    }
    const replay = world.replay;
    if (replay) {
      const cols = () =>
        (query.get('cols') ?? '0,0').split(';').map((p) => p.split(',').map(Number) as [number, number]);
      const dim = Number(query.get('dim') ?? 0);
      const captured: Partial<Record<RouteId, string>> = {
        'bots.list': 'bots',
        'bodies.scripts': 'scripts',
        'world.overlays': 'overlays',
        events: 'events',
      };
      const name = captured[id];
      if (name && replay.get(name)) return { json: replay.get(name) };
      if (id === 'world.index' && replay.text('index')) return { raw: replay.text('index') };
      if (id === 'world.chunks') return conditional(replay.chunks(dim, cols()), 'application/octet-stream');
      if (id === 'world.blockEntities') return { json: replay.blockEntityColumns(dim, cols()) };
    }
    if (id.startsWith('designer.')) {
      const r = designerReply(id, params, body, identity);
      if (r) return r;
    }
    if (id === 'designs.list') {
      const base = dataFor(id, params) as { items?: Obj[]; total?: number };
      const saved = designer.saved.map((d) => ({
        id: d.id, name: d.name, status: 'accepted', requester: d.requester, rev: d.revisions[0]?.rev ?? 1, revisions: d.revisions.length,
        updated: d.updated, has_picture: false, version: d.version ?? null, kind: d.kind ?? null,
      }));
      return { json: { ...base, items: [...saved, ...(base.items ?? [])], total: (base.total ?? 0) + saved.length } };
    }
    switch (id) {
      case 'memoryActivity.cloud':
        return { json: (cloud ??= syntheticCloud()) };
      case 'bots.now':
        return { json: world.now(params.bot ?? query.get('bot') ?? 'maid_chan') };
      case 'bots.inventory': {
        // Like the bridge in front of the bot: any body by login or short name (snake_case); unknown = the ApiError 404.
        const found = world.hive.find(params.bot ?? '');
        if (!found) return { status: 404, json: { error: 'no such body', code: 'not_found' } };
        const index = world.hive.names().indexOf(found.name);
        return { json: toSnake(mockInventory(found.name, index, world.tick)) };
      }
      case 'bots.list':
        return { json: world.hive.tiles() };
      case 'status':
        return { json: world.status() };
      case 'hive.tree':
        return { json: world.tree() };
      case 'bodies.scripts':
        return { json: world.hive.scripts() };
      case 'world.entities':
        return { json: { ...world.entities(), full: true } };
      case 'world.blocks':
        return { json: { at: Date.now(), rev: world.blocksRev, full: true, more: false, blocks: {}, columns: [] } };
      case 'feed':
        return { json: world.feedSince(Number(query.get('since') ?? 0)) };
      case 'bots.chat': {
        if (!identity.player) return { status: 403, json: { error: 'no player', code: 'forbidden' } };
        const b = JSON.parse(body.toString('utf8') || '{}') as Obj;
        if ('player' in b || 'allow_commands' in b)
          return { status: 400, json: { error: 'player/allow_commands are not accepted', code: 'bad_request' } };
        const bot = typeof b.bot === 'string' ? b.bot : 'maid_chan';
        const line = world.pushFeed({
          bot,
          kind: 'viewer',
          from: identity.player,
          text: typeof b.text === 'string' ? b.text : '',
          origin: 'viewer',
          request_id: b.request_id,
        });
        const t = setTimeout(
          () => {
            world.pushFeed({
              bot,
              kind: 'said',
              from: bot,
              text: REPLIES[(line.seq as number) % REPLIES.length],
              origin: 'game',
              reply_to: line.seq,
            });
          },
          Number(process.env.MOCK_CHAT_REPLY_MS) || 1500,
        ); // a slow bot: MOCK_CHAT_REPLY_MS
        t.unref();
        return { status: 202, json: { accepted: true, id: line.seq, feed_seq: line.seq } };
      }
      case 'world.index':
        if (typeof fx.live.worldIndexRaw === 'string') return { raw: fx.live.worldIndexRaw };
        break;
      case 'world.chunks': {
        const dim = Number(query.get('dim') ?? 0);
        const cols = (query.get('cols') ?? '0,0').split(';').map((p) => p.split(',').map(Number) as [number, number]);
        const bytes = makeMcwc(dim, cols, Date.UTC(2026, 10, 2, 10));
        return conditional(bytes, 'application/octet-stream');
      }
      case 'world.blockEntities': {
        // A sign and a red bed in the first column asked for; other columns have none.
        const [cx = 0, cz = 0] = (query.get('cols') ?? '0,0').split(';', 1)[0]?.split(',').map(Number) ?? [];
        const x = cx * 16 + 4,
          z = cz * 16 + 4;
        return {
          json: {
            dim: Number(query.get('dim') ?? 0),
            columns: [
              {
                cx,
                cz,
                hash: 1,
                entities: [
                  { x, y: 65, z, kind: 'sign', lines: ['Mock sign', 'line 2', '', ''] },
                  { x: x + 2, y: 65, z, kind: 'bed', color: 14 },
                ],
              },
            ],
          },
        };
      }
      case 'vision.render':
        if (query.get('meta') === '1') {
          return {
            json: {
              width: 320,
              height: 180,
              tokens: 77,
              bytes: 5120,
              legend: 'S stone · G grass · @ you',
              facts: ['creeper 4 m north-east'],
              seen: iso(),
            },
          };
        }
        return {
          bytes: png(320, 180, 3),
          type: 'image/png',
          headers: {
            'X-Tokens': '77',
            'X-Width': '320',
            'X-Height': '180',
            'X-Sha256': etagOf(png(320, 180, 3)).replace(/"/g, ''),
          },
        };
      case 'shards.export': {
        const manifest = { format_version: 1, name: 'mock export', created_at: iso(), counts: { facts: 1 } };
        const zip = makeZip({
          'manifest.json': JSON.stringify(manifest, null, 2),
          'records/facts.jsonl': '{"id":"note:12","text":"Fox prefers daytime"}\n',
          'summaries.jsonl': '',
          'embeddings.bin': Buffer.alloc(0),
        });
        return {
          bytes: zip,
          type: 'application/zip',
          headers: { 'Content-Disposition': 'attachment; filename="mock-export-20261102-1000.mcshard"' },
        };
      }
      case 'shards.importPreview':
      case 'admin.shardImport':
        if (!/^multipart\/form-data/.test(String(req.headers['content-type'])) || !body.includes('name="shard"')) {
          return { status: 400, json: { error: 'expected a multipart shard upload', code: 'bad_request' } };
        }
        break;
      default:
        break;
    }
    if (route.responseKind === 'png') {
      const seed = [...Object.values(params).join('')].reduce((n, c) => n + c.charCodeAt(0), 0);
      const bytes = png(128, 128, seed);
      return conditional(bytes, 'image/png', {
        'X-Width': '128',
        'X-Height': '128',
        'X-Tokens': '40',
        'X-Sha256': etagOf(bytes).replace(/"/g, ''),
      });
    }
    return { status: route.status ?? 200, json: dataFor(id, params) };
  };

  const stream = (req: IncomingMessage, res: ServerResponse) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache' });
    let id = Number(req.headers['last-event-id'] ?? 0) || 0;
    const frame = (event: SseEventName, data: unknown) => {
      res.write(`event: ${event}\nid: ${++id}\ndata: ${JSON.stringify(data)}\n\n`);
    };
    res.write('retry: 2000\n\n');
    frame('bots', world.status());
    frame('hive', world.tree());
    for (const b of world.botNames()) frame('now', world.now(b));
    const off = world.on(frame);
    // Production rates (the bridge's pollers): entities and blocks 5 Hz, `now` 2 Hz per body, bots 1 Hz, events and memory
    // activity 2 Hz, the hive tree every 3 s; feed lines and pings as the bot makes them. `interval` = one second of mock time.
    let n = 0;
    const timer = setInterval(
      () => {
        n++;
        world.hive.advance();
        world.story.sim.step(100);
        if (n % 5 === 0) for (const f of world.story.frames()) frame('story', f);
        if (n % 2 === 0) {
          frame('entities', world.entities());
          frame('blocks', world.blocks());
        }
        if (n % 5 === 0) {
          for (const b of world.botNames()) frame('now', world.now(b));
          frame('event', world.event());
          frame('memoryActivity', world.activity());
        }
        if (n % 30 === 0) frame('hive', world.tree());
        if (n % 10 !== 0) return;
        world.tick++;
        const t = world.tick;
        frame('bots', world.status());
        if (t % 9 === 0)
          world.pushFeed({
            bot: 'maid_chan_109',
            kind: 'chat',
            from: 'kiwi',
            text: 'the sand pit is past the oak tree',
          });
        if (t % 7 === 0)
          world.pushFeed({
            bot: 'maid_chan',
            kind: 'internal',
            from: 'maid_chan',
            text: '(the chest by the door is nearly full)',
          });
        if (t % 15 === 0) res.write(': ping\n\n');
      },
      Math.max(1, interval / 10),
    );
    const stop = () => {
      clearInterval(timer);
      off();
    };
    req.on('close', stop);
    res.on('close', stop);
  };

  const server: Server = createServer((req, res) => {
    void (async () => {
      try {
        const body = await readAll(req);
        const rawTarget = req.url ?? '/';
        const v = verifier.verify({ method: req.method ?? 'GET', rawTarget, headers: req.headers, body });
        if (!v.ok) return send(res, { status: 401, json: { error: 'unauthorized', code: 'unauthorized' } });
        const url = new URL(rawTarget, 'http://mock');
        const path = url.pathname;
        if (path === '/v1/health') return send(res, { json: { ok: true, version: 'mock', supervisor: 'up' } });
        if (path === '/v1/live/stream' && req.method === 'GET') return stream(req, res);
        if ((path.startsWith('/v1/sup/admin/') || path.startsWith('/v1/admin/nodes')) && !v.identity.roles.split(',').includes('admin')) {
          return send(res, { status: 403, json: { error: 'admin role required', code: 'forbidden' } });
        }
        for (const m of matchers) {
          if (m.method !== req.method) continue;
          const match = m.re.exec(path);
          if (!match) continue;
          const params: Record<string, string> = {};
          m.names.forEach((n, i) => (params[n] = decodeURIComponent(match[i + 1]!)));
          for (const [q, n] of m.queryParams) {
            const val = url.searchParams.get(q);
            if (val !== null) params[n] = val;
          }
          const reply = handle(m.id, req, v.identity, params, url.searchParams, body);
          return send(res, reply);
        }
        send(res, { status: 404, json: { error: 'not found', code: 'not_found' } });
      } catch (e) {
        send(res, { status: 500, json: { error: e instanceof Error ? e.message : 'mock error' } });
      }
    })();
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    key,
    world,
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
