/**
 * The mock bridge's per-route replies (see mock-bridge.ts). `replyFor` runs the stages in the bridge's order: Story Mode,
 * a replayed capture, the Design Studio, the design list, the special routes below, then the generic reply (a PNG for
 * image routes, else fixture or schema-generated data).
 */
import { createHash } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import {
  ROUTES,
  toSnake,
  type CloudArrangement,
  type DesignerMock,
  type MemoryCloudResponse,
  type RouteDef,
  type RouteId,
} from '@mineclaw/contracts';
import { cloudMockDistance, mockContainer, mockContainers, mockInventory } from '@mineclaw/contracts/testing';
import type { BridgeIdentity } from '../bridge/signing.js';
import { makeMcwc, makeZip } from './binary.js';
import type { MockWorld } from './mock-bridge.js';

type Obj = Record<string, unknown>;

export interface Reply {
  status?: number;
  json?: unknown;
  raw?: string;
  bytes?: Buffer;
  type?: string;
  headers?: Record<string, string>;
}

/** One request routed to a bridge route id. */
export interface MockCall {
  id: RouteId;
  req: IncomingMessage;
  identity: BridgeIdentity;
  params: Record<string, string>;
  query: URLSearchParams;
  body: Buffer;
}

/** What the replies draw on: the shared world, the fake designer and the bridge's caches. */
export interface MockEnv {
  world: MockWorld;
  designer: DesignerMock;
  worldIndexRaw: unknown;
  png(w: number, h: number, seed: number): Buffer;
  cloudOf(a: CloudArrangement): MemoryCloudResponse;
  dataFor(id: RouteId, params: Record<string, string>): unknown;
}

/** A route's reply, or null to fall through to the generic reply. */
type Handler = (env: MockEnv, call: MockCall) => Reply | null;

export const etagOf = (b: Buffer | string) => `"${createHash('sha256').update(b).digest('hex').slice(0, 16)}"`;

const iso = (ms = Date.now()) => new Date(ms).toISOString().replace('Z', '+00:00');

const REPLIES = [
  'On it.',
  'Sure, give me a moment.',
  'Done! Anything else?',
  'I can do that after this stack of cobble.',
];

const hasRole = (identity: BridgeIdentity, ...roles: string[]) =>
  identity.roles.split(',').some((r) => roles.includes(r));

const forbidden = (error: string): Reply => ({ status: 403, json: { error, code: 'forbidden' } });
const badRequest = (error: string): Reply => ({ status: 400, json: { error, code: 'bad_request' } });
const notFound = (error: string): Reply => ({ status: 404, json: { error, code: 'not_found' } });

/** Bytes with an ETag; a matching If-None-Match is a 304. */
function conditional(call: MockCall, bytes: Buffer, type: string, headers: Record<string, string> = {}): Reply {
  const etag = etagOf(bytes);
  if (call.req.headers['if-none-match'] === etag) return { status: 304, raw: '', headers: { ETag: etag } };
  return { bytes, type, headers: { ETag: etag, ...headers } };
}

const colsOf = (query: URLSearchParams) =>
  (query.get('cols') ?? '0,0').split(';').map((p) => p.split(',').map(Number) as [number, number]);

// ------------------------------------------------------------------ stages before the route table

/** Like the real bridge: Story Mode writes need the story or the admin role. */
function storyReply(env: MockEnv, call: MockCall): Reply | null {
  if (!call.id.startsWith('story.')) return null;
  if (ROUTES[call.id].method === 'POST' && !hasRole(call.identity, 'story', 'admin'))
    return forbidden('story role required');
  const r = env.world.story.handle(call.id, call.params, call.query, call.body, call.identity.player);
  return { status: r.status, json: r.json };
}

/** route id → the captured topic a replay serves it from. */
const CAPTURED: Partial<Record<RouteId, string>> = {
  'bots.list': 'bots',
  'bodies.scripts': 'scripts',
  'world.overlays': 'overlays',
  events: 'events',
};

function replayReply(env: MockEnv, call: MockCall): Reply | null {
  const replay = env.world.replay;
  if (!replay) return null;
  const { id, query } = call;
  const name = CAPTURED[id];
  if (name && replay.get(name)) return { json: replay.get(name) };
  if (id === 'world.index' && replay.text('index')) return { raw: replay.text('index') };
  const dim = Number(query.get('dim') ?? 0);
  if (id === 'world.chunks') return conditional(call, replay.chunks(dim, colsOf(query)), 'application/octet-stream');
  if (id === 'world.blockEntities') return { json: replay.blockEntityColumns(dim, colsOf(query)) };
  return null;
}

/** snake_case JSON body → camelCase keys (one level, like the bot's designer endpoints). */
function camel(b: Buffer): Obj {
  const raw = JSON.parse(b.toString('utf8') || '{}') as Obj;
  const out: Obj = {};
  for (const [k, v] of Object.entries(raw)) out[k.replace(/_([a-z])/g, (_m, c: string) => c.toUpperCase())] = v;
  return out;
}

type DesignerOp = (d: DesignerMock, sid: number, body: Buffer, who: string) => unknown;

const DESIGNER_OPS: Partial<Record<RouteId, DesignerOp>> = {
  'designer.models': (d) => d.models(),
  'designer.create': (d, _sid, body, who) => d.create(camel(body), who),
  'designer.version': (d, sid, body) => d.version(sid, camel(body) as never),
  'designer.get': (d, sid) => d.get(sid),
  'designer.message': (d, sid, body) => d.message(sid, camel(body) as never),
  'designer.canvas': (d, sid, body) => d.canvas(sid, camel(body) as never),
  'designer.save': (d, sid, body, who) => d.save(sid, camel(body) as never, who),
};

/** The Design Studio: the shared fake designer (contracts), replies snake-cased like the bot's. */
export function designerReply(env: MockEnv, call: MockCall): Reply | null {
  const { id, identity } = call;
  if (!id.startsWith('designer.')) return null;
  const who = identity.player || identity.user || 'poofimafox';
  const write = id !== 'designer.models' && id !== 'designer.get';
  if (write && !hasRole(identity, 'design', 'admin')) return forbidden('design role required');
  const op = DESIGNER_OPS[id];
  if (!op) return null;
  try {
    return { json: toSnake(op(env.designer, Number(call.params.id), call.body, who)) };
  } catch (e) {
    return badRequest(e instanceof Error ? e.message : 'bad request');
  }
}

/** The fixture's designs with the studio's saved ones first. */
function designsListReply(env: MockEnv, call: MockCall): Reply | null {
  if (call.id === 'designs.picture') {
    const picture = env.designer.saved.find((d) => d.id === Number(call.params.id))?.previewPng;
    return picture ? { bytes: Buffer.from(picture.split(',')[1]!, 'base64'), type: 'image/png' } : null;
  }
  if (call.id !== 'designs.list') return null;
  const base = env.dataFor(call.id, call.params) as { items?: Obj[]; total?: number };
  const saved = env.designer.saved.map((d) => ({
    id: d.id,
    name: d.name,
    status: 'accepted',
    requester: d.requester,
    rev: d.revisions[0]?.rev ?? 1,
    revisions: d.revisions.length,
    updated: d.updated,
    has_picture: !!d.previewPng,
    version: d.version ?? null,
    kind: d.kind ?? null,
  }));
  return { json: { ...base, items: [...saved, ...(base.items ?? [])], total: (base.total ?? 0) + saved.length } };
}

// ------------------------------------------------------------------ the special routes

const ARRANGEMENTS = ['mds', 'umap', 'pca', 'tree'];

const cloudReply: Handler = (env, { query }) => {
  const a = query.get('arrangement') ?? 'mds';
  if (!ARRANGEMENTS.includes(a)) return badRequest('arrangement must be one of mds|umap|pca|tree');
  return { json: toSnake(env.cloudOf(a as CloudArrangement)) };
};

const isMemoryId = (n: number) => Number.isInteger(n) && n >= 0;

/** Like the bridge: the viewer role, two integer ids, 404 for an unknown memory. */
const cloudDistanceReply: Handler = (env, { identity, query }) => {
  if (!hasRole(identity, 'viewer', 'admin')) return forbidden('viewer role required');
  const a = Number(query.get('a'));
  const b = Number(query.get('b'));
  if (!isMemoryId(a) || !isMemoryId(b)) return badRequest('a and b required');
  const d = cloudMockDistance(env.cloudOf('mds'), a, b);
  return d ? { json: toSnake(d) } : notFound('no such memory');
};

/** Like the bridge in front of the bot: any body by login or short name (snake_case); unknown = the ApiError 404. */
const inventoryReply: Handler = (env, { params }) => {
  const found = env.world.hive.find(params.bot ?? '');
  if (!found) return notFound('no such body');
  const index = env.world.hive.names().indexOf(found.name);
  return { json: toSnake(mockInventory(found.name, index, env.world.tick)) };
};

/** Both public routes reach the bot's one /api/world/containers: a box (x0..z1) lists, x/y/z answers one container. */
const containersReply: Handler = (env, { query }) => {
  const n = (k: string) => Number(query.get(k) ?? 0);
  const dim = n('dim');
  if (!query.has('x0')) return { json: toSnake(mockContainer(dim, n('x'), n('y'), n('z'), env.world.tick)) };
  const box = {
    x0: n('x0'),
    z0: n('z0'),
    x1: n('x1'),
    z1: n('z1'),
    ...(query.has('y0') ? { y0: n('y0') } : {}),
    ...(query.has('y1') ? { y1: n('y1') } : {}),
  };
  if (Math.abs(box.x1 - box.x0) > 512 || Math.abs(box.z1 - box.z0) > 512)
    return badRequest('containers box is at most 512 blocks wide');
  return { json: toSnake(mockContainers(dim, box, query.has('limit') ? n('limit') : undefined, env.world.tick)) };
};

/** A viewer line in the feed, answered by a `said` line later (MOCK_CHAT_REPLY_MS: a slow bot). */
const chatReply: Handler = (env, { identity, body }) => {
  if (!identity.player) return forbidden('no player');
  const b = JSON.parse(body.toString('utf8') || '{}') as Obj;
  if ('player' in b || 'allow_commands' in b) return badRequest('player/allow_commands are not accepted');
  const world = env.world;
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
  );
  t.unref();
  return { status: 202, json: { accepted: true, id: line.seq, feed_seq: line.seq } };
};

const chunksReply: Handler = (_env, call) => {
  const dim = Number(call.query.get('dim') ?? 0);
  const bytes = makeMcwc(dim, colsOf(call.query), Date.UTC(2026, 10, 2, 10));
  return conditional(call, bytes, 'application/octet-stream');
};

/** A sign and a red bed in the first column asked for; other columns have none. */
const blockEntitiesReply: Handler = (_env, { query }) => {
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
};

const VISION_META = {
  width: 320,
  height: 180,
  tokens: 77,
  bytes: 5120,
  legend: 'S stone · G grass · @ you',
  facts: ['creeper 4 m north-east'],
};

const visionReply: Handler = (env, { query }) => {
  if (query.get('meta') === '1') return { json: { ...VISION_META, seen: iso() } };
  return {
    bytes: env.png(320, 180, 3),
    type: 'image/png',
    headers: {
      'X-Tokens': '77',
      'X-Width': '320',
      'X-Height': '180',
      'X-Sha256': etagOf(env.png(320, 180, 3)).replace(/"/g, ''),
    },
  };
};

const shardExportReply: Handler = () => {
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
};

/** A shard upload must be multipart with a `shard` part; a good one gets the generic (fixture) reply. */
const shardUploadReply: Handler = (_env, { req, body }) =>
  /^multipart\/form-data/.test(String(req.headers['content-type'])) && body.includes('name="shard"')
    ? null
    : badRequest('expected a multipart shard upload');

const ROUTE_REPLIES: Partial<Record<RouteId, Handler>> = {
  'memoryActivity.cloud': cloudReply,
  'memoryActivity.cloudDistance': cloudDistanceReply,
  'bots.now': (env, { params, query }) => ({ json: env.world.now(params.bot ?? query.get('bot') ?? 'maid_chan') }),
  'bots.inventory': inventoryReply,
  'world.container': containersReply,
  'world.containers': containersReply,
  'bots.list': (env) => ({ json: env.world.hive.tiles() }),
  status: (env) => ({ json: env.world.status() }),
  'hive.tree': (env) => ({ json: env.world.tree() }),
  'bodies.scripts': (env) => ({ json: env.world.hive.scripts() }),
  'world.entities': (env) => ({ json: { ...env.world.entities(), full: true } }),
  'world.blocks': (env) => ({
    json: { at: Date.now(), rev: env.world.blocksRev, full: true, more: false, blocks: {}, columns: [] },
  }),
  feed: (env, { query }) => ({ json: env.world.feedSince(Number(query.get('since') ?? 0)) }),
  'bots.chat': chatReply,
  'world.index': (env) => (typeof env.worldIndexRaw === 'string' ? { raw: env.worldIndexRaw } : null),
  'world.chunks': chunksReply,
  'world.blockEntities': blockEntitiesReply,
  'vision.render': visionReply,
  'shards.export': shardExportReply,
  'shards.importPreview': shardUploadReply,
  'admin.shardImport': shardUploadReply,
};

/** Every other route: a PNG for image routes, else the fixture or schema-generated data. */
function genericReply(env: MockEnv, call: MockCall): Reply {
  const route: RouteDef = ROUTES[call.id];
  if (route.responseKind !== 'png') return { status: route.status ?? 200, json: env.dataFor(call.id, call.params) };
  const seed = [...Object.values(call.params).join('')].reduce((n, c) => n + c.charCodeAt(0), 0);
  const bytes = env.png(128, 128, seed);
  return conditional(call, bytes, 'image/png', {
    'X-Width': '128',
    'X-Height': '128',
    'X-Tokens': '40',
    'X-Sha256': etagOf(bytes).replace(/"/g, ''),
  });
}

/** The reply for one routed request. */
export function replyFor(env: MockEnv, call: MockCall): Reply {
  return (
    storyReply(env, call) ??
    replayReply(env, call) ??
    designerReply(env, call) ??
    designsListReply(env, call) ??
    ROUTE_REPLIES[call.id]?.(env, call) ??
    genericReply(env, call)
  );
}
