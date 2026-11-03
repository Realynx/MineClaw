/**
 * The mock hive: four bodies (main, a hive clone with its own brain, a subhive the main brain drives, a script-only body), a
 * live simulation that advances on every tick (script lines, thoughts, in-flight calls, positions, memory accesses), and the
 * chat path (viewer line → debounced bot reply), so the UI sees the same shapes and cadence as the real stream.
 */
import type {
  BodyControllerInfo,
  BodyScriptReply,
  BotNow,
  BotStatusView,
  BrainContextPage,
  BrainTurnDetail,
  CtxBlock,
  CtxMessage,
  CtxTurnSummary,
  EventsResponse,
  FeedLine,
  HiveTreeNode,
  InContextMemory,
  LiveScriptsResponse,
  LiveStatus,
  MemoryAccessEvent,
  MemoryHeatResponse,
  StatusResponse,
  SupervisorEvent,
  VisionList,
  WorldEntities,
  WorldEntity,
  WorldIndex,
  WorldOverlays,
} from '@mineclaw/contracts';
import { iso, isoAgo, minsAgo, nowIso, pick, rng, T0 } from '../util';
import { raptorLeaves, raptorParents } from './raptor';
import { SCRIPT_CODE } from './scripts';

export interface MockBot {
  name: string;
  short: string;
  main: boolean;
  kind: 'main' | 'hive' | 'subhive' | 'script';
  brain: string | null;
  owner: string | null;
  dim: number;
  pos: [number, number, number];
  hp: number;
  food: number;
  task: string;
  held: string;
  status: 'online' | 'connecting' | 'offline';
  script: { name: string; version: number; line: number; lines: number; status: string; paused: boolean } | null;
  thoughts: string[];
  thinking: string[];
  tools: Array<[string, string]>;
  claims: string[];
}

export const BOTS: MockBot[] = [
  {
    name: 'maid_chan',
    short: 'main',
    main: true,
    kind: 'main',
    brain: 'main',
    owner: null,
    dim: 0,
    pos: [4.5, 64, 6.5],
    hp: 20,
    food: 18,
    task: 'Keeping the cobble generator running',
    held: 'stone pickaxe',
    status: 'online',
    script: { name: 'cobble_loop', version: 7, line: 12, lines: 0, status: 'running', paused: false },
    thoughts: [
      'The generator is flowing nicely. I will keep mining until the chest by the farm is full, then smelt a stack into stone.',
      'Fox asked for day, so the time is set. Back to the cobble — 640 mined so far this session.',
      'Robin is building the melon farm. I should leave the bone meal in the shared chest for her.',
      'Hunger is fine. The loop has been steady for twenty minutes, nothing needs my attention right now.',
    ],
    thinking: [
      'The loop reports 34% toward the 2,000 target. No danger events. Fox is nearby but busy. A short status line is enough; no need to interrupt the script.',
      'Kiwi mentioned melons twice. The farm needs bone meal; there are 12 in the main chest. I can offer them without pausing the loop.',
    ],
    tools: [
      ['run_command', '/time set day'],
      ['inventory', ''],
      ['say', '"Day it is!"'],
      ['remember', 'kiwi likes melons'],
    ],
    claims: ['cobble generator', 'storage room'],
  },
  {
    name: 'maid_chan_67',
    short: '67',
    main: false,
    kind: 'hive',
    brain: 'maid_chan_67',
    owner: 'maid_chan',
    dim: -1,
    pos: [12, 70, -3],
    hp: 5,
    food: 6,
    task: 'Scouting the nether for a fortress',
    held: 'iron sword',
    status: 'online',
    script: { name: 'nether_scout', version: 3, line: 21, lines: 0, status: 'running', paused: false },
    thoughts: [
      'Health is low. I will step back through the portal before pushing further west.',
      'Lava lake to the north. The fortress is probably past it; I will follow the cliff edge instead.',
      'Two ghasts nearby. Staying under the overhang until they drift off.',
    ],
    thinking: [
      'hp 5, food 6. Eating first is cheap and safe; the bread is in slot 3. Then retreat to the portal at -2,64,8.',
    ],
    tools: [
      ['eat', 'bread'],
      ['look', 'north'],
      ['goto', 'portal'],
    ],
    claims: [],
  },
  {
    name: 'maid_chan_68',
    short: '68',
    main: false,
    kind: 'subhive',
    brain: 'main',
    owner: 'maid_chan',
    dim: 0,
    pos: [-14, 63, 22],
    hp: 18,
    food: 15,
    task: 'Tending the wheat farm',
    held: 'iron hoe',
    status: 'online',
    script: { name: 'farm_tend', version: 4, line: 8, lines: 0, status: 'running', paused: false },
    thoughts: ['Replanting row three. The water channel on the east side looks dry; I will tell maid_chan.'],
    thinking: [],
    tools: [['harvest', 'wheat row 3']],
    claims: ['wheat farm'],
  },
  {
    name: 'maid_chan_70',
    short: '70',
    main: false,
    kind: 'script',
    brain: null,
    owner: 'maid_chan',
    dim: 0,
    pos: [9, 64, -4],
    hp: 20,
    food: 20,
    task: 'Sorting the storage room',
    held: 'chest',
    status: 'online',
    script: { name: 'sort_chests', version: 2, line: 30, lines: 0, status: 'running', paused: false },
    thoughts: [],
    thinking: [],
    tools: [],
    claims: [],
  },
];

/** A second hive (an Opus brain) with a lumberjack crew of subhive bodies: the grid's multi-body group. */
export const CREW = ['102', '114', '124', '126', '131', '135'];
BOTS.push(
  {
    name: 'maid_chan_109',
    short: '109',
    main: false,
    kind: 'hive',
    brain: 'maid_chan_109',
    owner: 'maid_chan',
    dim: 0,
    pos: [-10, 64, 14],
    hp: 19,
    food: 17,
    task: 'Running the lumberjack crew',
    held: 'iron axe',
    status: 'online',
    script: null,
    thoughts: ['The crew is spread over the birch grove. 114 is low on food; sending it back to the chest first.', 'Twelve stacks of logs so far.'],
    thinking: ['Bodies 102–135 run chop_crew; I only step in when one gets stuck.'],
    tools: [['hive', 'status bodies=all']],
    claims: ['birch grove'],
  },
  ...CREW.map((short, i): MockBot => ({
    name: `maid_chan_${short}`,
    short,
    main: false,
    kind: 'subhive',
    brain: null,
    owner: 'maid_chan_109',
    dim: 0,
    pos: [-14 + (i % 3) * 4, 64, 18 + Math.floor(i / 3) * 4],
    hp: i === 1 ? 7 : 20 - i,
    food: i === 1 ? 4 : 18 - i,
    task: ['Chopping birch', 'Heading to the food chest', 'Hauling logs', 'Replanting saplings', 'Chopping birch', 'Stripping the tall oak'][i]!,
    held: i === 2 ? 'birch log' : 'iron axe',
    status: i === 5 ? 'connecting' : 'online',
    script: i === 5 ? null : { name: 'chop_crew', version: 3, line: 6 + i * 4, lines: 0, status: 'running', paused: false },
    thoughts: [],
    thinking: [],
    tools: [],
    claims: [],
  })),
);

for (const b of BOTS) {
  if (b.script) b.script.lines = (SCRIPT_CODE[b.script.name]?.[b.script.version] ?? '').split('\n').length;
}

export function findBot(key: string): MockBot | undefined {
  return BOTS.find((b) => b.name === key || b.short === key);
}

// ---------- simulation state ----------

interface Sim {
  tick: number;
  thoughtIdx: Record<string, number>;
  thoughtAt: Record<string, number>;
  inFlight: Record<string, { purpose: string; model: string; since: number } | null>;
  toolIdx: Record<string, number>;
  feed: FeedLine[];
  feedSeq: number;
  events: SupervisorEvent[];
  eventId: number;
  entityRev: number;
  entities: WorldEntity[];
  activity: MemoryAccessEvent[];
  activitySeq: number;
  inContext: InContextMemory[];
}

const r = rng(42);

function seedFeed(): FeedLine[] {
  const lines: Array<Omit<FeedLine, 'seq'>> = [
    { at: isoAgo(42), bot: 'maid_chan', kind: 'system', from: null, text: 'maid_chan_67 joined the game', origin: 'game' },
    { at: isoAgo(38), bot: 'maid_chan', kind: 'chat', from: 'kiwi', text: 'maid can you save me some bone meal for the melons?', origin: 'game' },
    { at: isoAgo(37.6), bot: 'maid_chan', kind: 'internal', from: 'maid_chan', text: 'Robin wants bone meal; 12 in the main chest. Offer without stopping the loop.', origin: null },
    { at: isoAgo(37.4), bot: 'maid_chan', kind: 'said', from: 'maid_chan', text: 'Of course! I left 12 bone meal in the shared chest by the farm.', origin: 'game' },
    { at: isoAgo(30), bot: 'maid_chan', kind: 'chat', from: 'kiwi', text: 'thank you!! 🍈', origin: 'game' },
    { at: isoAgo(22), bot: 'maid_chan_67', kind: 'said', from: 'maid_chan_67', text: 'Heading into the nether to look for a fortress.', origin: 'game' },
    { at: isoAgo(12), bot: 'maid_chan', kind: 'muted', from: 'maid_chan', text: '(muted) Generator flow check: ok.', origin: null },
    { at: isoAgo(6), bot: 'maid_chan', kind: 'viewer', from: 'poofimafox', text: 'claude, set it to day', origin: 'viewer', requestId: 'seedreq000000001' },
    { at: isoAgo(5.9), bot: 'maid_chan', kind: 'said', from: 'maid_chan', text: 'On it — setting the time to day.', origin: 'game' },
    { at: isoAgo(4), bot: 'maid_chan', kind: 'chat', from: 'poofimafox', text: 'how is the cobble going?', origin: 'game' },
    { at: isoAgo(3.8), bot: 'maid_chan', kind: 'whisper', from: 'maid_chan', text: 'About a third of the way to 2,000. Smooth so far!', origin: 'game' },
    { at: isoAgo(2), bot: 'maid_chan_68', kind: 'said', from: 'maid_chan_68', text: 'Row three replanted.', origin: 'game' },
  ];
  return lines.map((l, i) => ({ ...l, seq: i + 1 }));
}

function seedEvents(): SupervisorEvent[] {
  return [
    { id: 1, at: isoAgo(41), kind: 'conn', level: 'info', text: 'maid_chan_67 connected' },
    { id: 2, at: isoAgo(25), kind: 'hive', level: 'info', text: 'maid_chan_70 started sort_chests v2' },
    { id: 3, at: isoAgo(9), kind: 'danger', level: 'warn', text: 'maid_chan_67: hp 5 near lava' },
  ];
}

function seedEntities(): WorldEntity[] {
  const out: WorldEntity[] = [];
  let id = 100;
  for (let i = 0; i < 6; i++)
    out.push({ kind: 'mob', name: pick(r, ['cow', 'chicken', 'sheep']), dim: 0, x: -20 + r() * 40, y: 64, z: -10 + r() * 30, id: id++, type: 92 });
  for (let i = 0; i < 4; i++)
    out.push({ kind: 'item', name: pick(r, ['cobblestone', 'wheat seeds', 'string']), dim: 0, x: -5 + r() * 15, y: 64, z: r() * 10, id: id++, count: 1 + Math.floor(r() * 20) });
  out.push({ kind: 'player', name: 'poofimafox', dim: 0, x: 7, y: 64, z: 9, id: id++ });
  out.push({ kind: 'player', name: 'kiwi', dim: 0, x: -11, y: 63, z: 19, id: id++ });
  for (let i = 0; i < 3; i++) out.push({ kind: 'mob', name: pick(r, ['ghast', 'zombie pigman']), dim: -1, x: r() * 40, y: 70, z: -r() * 30, id: id++ });
  return out;
}

const brains = ['main', 'maid_chan_67'];
const brainBodies: Record<string, string[]> = { main: ['maid_chan', 'maid_chan_68'], maid_chan_67: ['maid_chan_67'] };

/** Memory accesses cluster in "regions" (a few layer-1 subtrees) per brain, so regions of the map light up. */
function regionLeaves(brain: string): number[] {
  const leaves = raptorLeaves();
  const parents = raptorParents();
  const byParent = new Map<number, number[]>();
  for (const l of leaves) {
    const p = parents.get(l)?.[0] ?? -1;
    byParent.set(p, [...(byParent.get(p) ?? []), l]);
  }
  const groups = [...byParent.values()];
  const offset = brain === 'main' ? 0 : 7;
  return [groups[offset], groups[offset + 3], groups[offset + 11], groups[offset + 19]].flat().filter((x): x is number => x !== undefined);
}

function seedActivity(): { events: MemoryAccessEvent[]; inContext: InContextMemory[] } {
  const events: MemoryAccessEvent[] = [];
  let seq = 0;
  for (let m = 55; m >= 0; m -= 0.35) {
    const brain = r() < 0.65 ? 'main' : 'maid_chan_67';
    const leaves = r() < 0.8 ? regionLeaves(brain) : raptorLeaves();
    const leaf = pick(r, leaves);
    events.push({
      seq: ++seq,
      at: isoAgo(m),
      brain,
      body: pick(r, brainBodies[brain] ?? ['maid_chan']),
      memoryKind: pick(r, ['note', 'episode', 'claim', 'gamedata', 'structure']),
      memoryId: `doc:${leaf}`,
      raptorNodeId: leaf,
      source: pick(r, ['rag', 'rag', 'rag', 'tool', 'search', 'recall'] as const),
      score: Number((0.4 + r() * 0.55).toFixed(3)),
      turn: 100 + Math.floor((60 - m) / 2),
    });
  }
  const inContext: InContextMemory[] = [];
  for (const brain of brains) {
    const recent = events.filter((e) => e.brain === brain).slice(-14);
    recent.forEach((e, i) => {
      if (inContext.some((c) => c.brain === brain && c.raptorNodeId === e.raptorNodeId)) return;
      const evicted = i < 3;
      inContext.push({
        brain,
        memoryKind: e.memoryKind,
        memoryId: e.memoryId,
        raptorNodeId: e.raptorNodeId,
        addedAt: e.at,
        lastSeenAt: iso(Date.parse(e.at) + (evicted ? 60_000 : 30_000 + i * 15_000)),
        turnsInContext: 1 + (14 - i),
        positionFromEnd: evicted ? null : (14 - i) * 3,
      });
    });
  }
  return { events, inContext };
}

const seededActivity = seedActivity();

export const sim: Sim = {
  tick: 0,
  thoughtIdx: {},
  thoughtAt: Object.fromEntries(BOTS.map((b) => [b.name, minsAgo(0.5)])),
  inFlight: { maid_chan_67: { purpose: 'turn', model: 'claude-opus-5-5', since: Date.now() - 2200 } },
  toolIdx: {},
  feed: seedFeed(),
  feedSeq: 12,
  events: seedEvents(),
  eventId: 3,
  entityRev: 1,
  entities: seedEntities(),
  activity: seededActivity.events,
  activitySeq: seededActivity.events.length,
  inContext: seededActivity.inContext,
};

// ---------- route bodies ----------

function brainStateOf(b: MockBot, inflight: unknown): string {
  if (b.kind === 'script') return 'script-only';
  if (b.kind === 'subhive') return `driven by ${b.owner === 'maid_chan' ? 'main' : (findBot(b.owner ?? '')?.short ?? b.owner)}`;
  return inflight ? 'thinking' : b.script ? `awake · script ${b.script.name}` : 'awake';
}

export function botNow(b: MockBot): BotNow {
  const ti = sim.thoughtIdx[b.name] ?? 0;
  const inflight = sim.inFlight[b.name];
  const tool = b.tools.length ? b.tools[(sim.toolIdx[b.name] ?? 0) % b.tools.length] : undefined;
  return {
    bot: b.name,
    at: nowIso(),
    brainState: brainStateOf(b, inflight),
    inFlight: inflight ? { purpose: inflight.purpose, model: inflight.model, since: iso(inflight.since) } : null,
    thought: b.thoughts.length
      ? {
          text: b.thoughts[ti % b.thoughts.length],
          thinking: b.thinking.length ? b.thinking[ti % b.thinking.length] : null,
          at: iso(sim.thoughtAt[b.name] ?? T0),
        }
      : null,
    tool: tool ? { name: tool[0], argsShort: tool[1] || null, at: iso(Date.now() - 4000) } : null,
    task: b.task,
    script: b.script
      ? { name: b.script.name, version: b.script.version, status: b.script.status, line: b.script.line, paused: b.script.paused }
      : null,
    body: { hp: b.hp, food: b.food, pos: [b.pos[0], b.pos[1], b.pos[2]], dim: b.dim, held: b.held },
    kind: b.kind,
    owner: b.owner,
  };
}

export function statusResponse(selected?: string): StatusResponse {
  return {
    at: nowIso(),
    main: 'maid_chan',
    bots: BOTS.map((b): BotStatusView => {
      const sel = selected && (b.name === selected || b.short === selected);
      return {
        name: b.name,
        status: b.status,
        at: nowIso(),
        pos: `${Math.floor(b.pos[0])},${Math.floor(b.pos[1])},${Math.floor(b.pos[2])}`,
        dim: b.dim,
        hp: b.hp,
        food: b.food,
        task: b.task,
        brain: b.brain,
        budgetMode: 'Normal',
        header: sel ? `pos ${b.pos.map(Math.floor).join(',')} · hp ${b.hp} · holding ${b.held}` : null,
        usage: b.main
          ? {
              todayUsd: 1.24,
              monthUsd: 31.9,
              dailyCapUsd: 5,
              monthlyCapUsd: 100,
              mode: 'Normal',
              cacheHit: 0.82,
              today: [{ tier: 'opus', usd: 1.1, calls: 40, cacheHit: 0.85, promptTokens: 900000, outputTokens: 20000 }],
              month: [],
              callsLastHour: 12,
              callsPerHour: [0, 0, 1, 4, 12, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
              last: { at: isoAgo(0.5), tier: 'opus', model: 'claude-opus-5-5', purpose: 'turn', costUsd: 0.03 },
              latency: { p50_ms: 2100, p95_ms: 7000 },
            }
          : null,
        thought: null,
        chat: [],
        short: b.short,
        kind: b.kind,
        owner: b.owner,
        script: b.script ? { name: b.script.name, version: b.script.version, status: b.script.status, line: b.script.line, paused: b.script.paused } : null,
      };
    }),
    hive: {
      list: BOTS.filter((b) => !b.main).map((b) => ({ name: b.name })),
      clonesTodayUsd: 0.2,
      clonesMonthUsd: 4.1,
      slots: BOTS.filter((b) => !b.main).map((b, i) => ({
        name: b.name,
        number: Number(b.short),
        state: 'running',
        connected: true,
        task: b.task,
        pending: 0,
        progress: i === 0 ? 'scouting' : null,
        deaths: i === 0 ? 1 : 0,
      })),
    },
    claims: BOTS.flatMap((b) => b.claims.map((what) => ({ owner: b.name, what }))),
  };
}

export function hiveTree(): HiveTreeNode {
  const node = (b: MockBot, children: HiveTreeNode[] = []): HiveTreeNode => ({
    id: b.short,
    name: b.name,
    kind: b.kind,
    brain: b.brain,
    owner: b.owner,
    state: b.status === 'online' ? (b.script ? `running ${b.script.name}` : 'awake') : b.status,
    task: b.task,
    children,
  });
  const build = (b: MockBot): HiveTreeNode => node(b, BOTS.filter((c) => c.owner === b.name).map(build));
  return { ...build(BOTS[0]!), id: 'main' };
}

export function eventsSince(since: number): EventsResponse {
  const events = sim.events.filter((e) => e.id > since).slice(-200);
  return { events, last: sim.eventId };
}

export function feedSince(since: number): { lines: FeedLine[]; last: number } {
  return { lines: sim.feed.filter((l) => l.seq > since), last: sim.feedSeq };
}

export function worldBots() {
  return BOTS.filter((b) => b.status === 'online').map((b) => ({
    name: b.name,
    short: b.short,
    main: b.main,
    dim: b.dim,
    x: b.pos[0],
    y: b.pos[1],
    z: b.pos[2],
    yaw: (sim.tick * 9 + b.pos[0] * 10) % 360,
    pitch: 10,
    poseAge: 0.2,
    hp: b.hp,
    food: b.food,
    job: b.task,
    brain: b.brain,
    held: b.held,
    claims: b.claims,
    live: true,
    ageSeconds: 0.3,
    kind: b.kind,
    owner: b.owner,
  }));
}

export function entitiesSince(since?: number): WorldEntities {
  return {
    at: Date.now(),
    rev: sim.entityRev,
    full: !since,
    bots: worldBots(),
    entities: sim.entities,
    removed: [],
  };
}

export function worldIndex(dim: number, x: number, z: number): WorldIndex {
  const cx0 = Math.floor(x / 16);
  const cz0 = Math.floor(z / 16);
  const columns = [];
  for (let dx = -3; dx <= 3; dx++)
    for (let dz = -3; dz <= 3; dz++)
      columns.push({ cx: cx0 + dx, cz: cz0 + dz, hash: String(1000 + dx * 31 + dz), seen: Date.now() - 20_000, by: 'maid_chan', live: Math.abs(dx) < 2 });
  return { dim, at: Date.now(), columns, version: 'b1.8.1', time: 6000 + sim.tick * 20, rain: 0, thunder: 0 };
}

/** An MCWC payload whose columns are all "unknown" (data_len 0): valid framing without shipping block data. */
export function mcwc(dim: number, cols: Array<[number, number]>): ArrayBuffer {
  const per = 4 + 4 + 8 + 8 + 1 + 1 + 4;
  const buf = new ArrayBuffer(16 + cols.length * per);
  const v = new DataView(buf);
  [77, 67, 87, 67].forEach((c, i) => v.setUint8(i, c));
  v.setUint16(4, 1, true);
  v.setUint16(6, 0, true);
  v.setInt32(8, dim, true);
  v.setInt32(12, cols.length, true);
  let o = 16;
  for (const [cx, cz] of cols) {
    v.setInt32(o, cx, true);
    v.setInt32(o + 4, cz, true);
    v.setBigInt64(o + 8, 0n, true);
    v.setBigInt64(o + 16, BigInt(Date.now()), true);
    v.setUint8(o + 24, 0);
    v.setUint8(o + 25, 0);
    v.setInt32(o + 26, 0, true);
    o += per;
  }
  return buf;
}

export function overlays(dim: number): WorldOverlays {
  return {
    at: nowIso(),
    dim,
    claims: dim === 0 ? [{ kind: 'claim', text: 'cobble generator', min: [2, 63, 4], max: [6, 66, 8], owner: 'maid_chan' }] : [],
    places: dim === 0 ? [{ kind: 'place', text: 'home', min: [0, 64, 0], max: [0, 64, 0], owner: null }] : [],
    points: dim === 0 ? [{ kind: 'chest', text: 'main chest', min: [8, 64, -2], max: [8, 64, -2], owner: null }] : [],
    plans: [],
    dangers: dim === -1 ? [{ kind: 'lava', text: 'lava lake', min: [20, 30, -40], max: [60, 31, -10], owner: null }] : [],
    notes: [],
  };
}

export function bodyScripts(): LiveScriptsResponse {
  return {
    at: nowIso(),
    bodies: BOTS.map((b) => ({
      body: b.name,
      short: b.short,
      script: b.script?.name ?? null,
      status: b.script?.status ?? null,
      line: b.script?.line ?? null,
      paused: b.script?.paused ?? null,
      version: b.script?.version ?? null,
      role: b.kind === 'subhive' ? 'farmer' : null,
      owner: b.owner,
    })),
  };
}

export function bodyScript(key: string, withCode: boolean): BodyScriptReply {
  const b = findBot(key);
  if (!b?.script) return { body: key, running: false, snapshot: null, note: 'no script running' };
  const s = b.script;
  const code = SCRIPT_CODE[s.name]?.[s.version] ?? '';
  return {
    body: b.name,
    running: true,
    snapshot: {
      sessionId: 4410 + BOTS.indexOf(b),
      name: s.name,
      version: s.version,
      lang: 'lua',
      status: s.paused ? 'paused' : 'running',
      live: true,
      body: b.name,
      author: 'Opus (maid_chan)',
      role: b.kind === 'subhive' ? 'farmer' : null,
      crew: b.kind === 'subhive' ? 'farm crew' : null,
      coordinator: b.kind === 'subhive' ? 'maid_chan' : null,
      at: nowIso(),
      started: isoAgo(21),
      seq: sim.tick,
      line: s.line,
      column: 5,
      lineLive: true,
      inSdk: null,
      snippet: code.split('\n')[s.line - 1]?.trim() ?? null,
      stack: [
        { function: 'mine_once', file: `${s.name}.lua`, line: s.line, column: 5, script: true },
        { function: 'main', file: `${s.name}.lua`, line: 40, column: 3, script: true },
        { function: 'sdk.mine', file: 'sdk/mining.lua', line: 88, column: 9, script: false },
      ],
      stackAgeSeconds: 0.3,
      action: { label: 'mine', args: 'cobblestone at 5,64,6', seconds: 0.8, atomic: true, running: true, ok: null },
      actions: 640 + sim.tick,
      elapsedSeconds: 21 * 60 + sim.tick,
      timings: [
        { action: 'mine', calls: 640, fails: 3, seconds: 512, avgMs: 800 },
        { action: 'deposit', calls: 10, fails: 0, seconds: 40, avgMs: 4000 },
        { action: 'eat', calls: 2, fails: 0, seconds: 3.2, avgMs: 1600 },
      ],
      params: { n: 80, store_at: 'home' },
      args: { max_items: 64 },
      checkpoint: 'deposit',
      checkpointsPassed: 2,
      checkpointTotal: 5,
      paused: s.paused,
      pausePending: false,
      pauseKind: null,
      pauseReason: null,
      pausedSeconds: null,
      patching: null,
      patchNote: null,
      patches: 0,
      machine: {
        id: 'cobble',
        state: 'mining',
        transitions: 21,
        history: [
          { from: 'depositing', to: 'mining', why: 'chest ok', at: 60.5 },
          { from: 'mining', to: 'depositing', why: 'inventory full', at: 120.2 },
          { from: 'eating', to: 'mining', why: 'food 18', at: 400 },
        ],
        blackboard: { last_chest: [10, 64, 3], mined_total: 640 + sim.tick },
        blackboardAgeSeconds: 0.4,
      },
      locals: [
        { name: 'mined', value: String(640 + sim.tick), scope: 'main' },
        { name: 'target', value: '2000', scope: 'main' },
        { name: 'pos', value: '{5, 64, 6}', scope: 'mine_once' },
      ],
      localsNote: null,
      localsAgeSeconds: 0.5,
      lanes: b.kind === 'subhive' ? [{ id: 1, body: 'maid_chan_68', status: 'running' }] : [],
      progressPct: 34 + sim.tick * 0.01,
      progressNote: `${640 + sim.tick} / 2000 cobble`,
      lastFailure: 'mine: block changed before the swing (line 14)',
      outcome: null,
      logs: ['deposited 64 cobblestone', 'ate bread (food 18)', 'deposited 64 cobblestone', 'generator flow ok'],
      code: withCode ? code : null,
      controls: [],
    },
    note: null,
  };
}

export function controller(key: string): BodyControllerInfo {
  const b = findBot(key) ?? BOTS[0]!;
  return {
    body: b.name,
    kind: b.kind,
    owner: b.owner,
    brain: b.brain,
    brainKind: b.brain ? (b.kind === 'subhive' ? 'owner brain' : 'own brain') : null,
    model: b.brain ? 'claude-opus-5-5' : null,
    provider: b.brain ? 'anthropic' : null,
    hasScript: !!b.script,
    script: b.script?.name ?? null,
    tabs: b.brain ? (b.script ? ['brain', 'script'] : ['brain']) : ['script'],
    filter: b.kind === 'subhive' ? b.name : null,
    links: { context: b.brain ? `/api/brains/${b.brain}/context` : null, script: `/api/bodies/${b.name}/script`, owner: b.owner },
    note: null,
  };
}

const LANES = ['brain', 'brain', 'reply', 'memory', 'perception', 'background', 'brain', 'compaction', 'worker', 'brain', 'author', 'brain'];

function turnSummary(brain: string, i: number): CtxTurnSummary {
  const lane = LANES[i % LANES.length]!;
  const start = Date.now() - (12 - i) * 95_000;
  const inFlight = brain === 'maid_chan_67' && i === 11;
  return {
    id: 100 + i,
    lane,
    purpose: lane === 'brain' ? 'turn' : lane,
    start: iso(start),
    end: inFlight ? null : iso(start + 3200 + i * 140),
    inFlight,
    calls: lane === 'brain' ? 2 + (i % 3) : 1,
    model: lane === 'brain' || lane === 'author' ? 'claude-opus-5-5' : 'claude-haiku-5-5',
    tier: lane === 'brain' || lane === 'author' ? 'opus' : 'haiku',
    provider: 'anthropic',
    in: 24000 + i * 900,
    out: 380 + i * 40,
    cacheRead: 21000 + i * 800,
    cacheWrite: 1200,
    cost: Number((0.012 + i * 0.002).toFixed(4)),
    latencyMs: 2800 + i * 120,
    preview:
      lane === 'brain'
        ? pick(rng(i), ['Fox: claude, set it to day → run_command /time set day', 'kiwi: bone meal? → say "left 12 in the chest"', 'tick: generator ok · no events', 'danger: hp 5 near lava → eat, retreat'])
        : lane === 'memory'
          ? 'retrieved 6 memories for "melon farm"'
          : `${lane} pass`,
    tools: lane === 'brain' ? ['say', 'run_command'].slice(0, 1 + (i % 2)) : [],
    hasThinking: lane === 'brain',
    parent: lane === 'brain' ? null : 100 + i - 1,
    body: brain === 'main' ? (i % 4 === 0 ? 'maid_chan_68' : 'maid_chan') : 'maid_chan_67',
    targets: true,
    version: i + 1,
    error: i === 7 ? 'overloaded: retried once' : null,
    droppedCalls: 0,
  };
}

function block(kind: CtxBlock['kind'], text: string, extra: Partial<CtxBlock> = {}): CtxBlock {
  return { kind, text, isError: false, target: false, ...extra };
}

function messages(): CtxMessage[] {
  const out: CtxMessage[] = [];
  for (let i = 0; i < 8; i++) {
    out.push({
      index: i * 2,
      role: 'user',
      tag: i === 0 ? 'journal' : 'turn',
      blocks:
        i === 0
          ? [block('text', 'Earlier: set up the cobble generator with Fox, stored 3 stacks, Robin started a melon farm.', { label: 'compaction summary' })]
          : [
              block('text', `[events] ${pick(rng(i), ['kiwi: maid can you save me some bone meal?', 'tick: nothing new', 'poofimafox: claude, set it to day'])}`, { label: 'turn input' }),
              block('text', 'kiwi likes melons · the shared chest is by the farm · bone meal: 12', { label: 'retrieved memory' }),
            ],
    });
    out.push({
      index: i * 2 + 1,
      role: 'assistant',
      tag: 'response',
      blocks: [
        block('thinking', 'Short status is enough; keep the loop running.', { label: 'thinking' }),
        block('tool_use', '', { name: 'say', id: `tu_${i}`, input: JSON.stringify({ text: 'On it!' }) }),
      ],
    });
  }
  return out;
}

export function brainContext(brain: string, since: number): BrainContextPage {
  const turns = Array.from({ length: 12 }, (_, i) => turnSummary(brain, i));
  const version = 12 + Math.floor(sim.tick / 10);
  return {
    brain,
    version,
    at: nowIso(),
    capacity: 30,
    enabled: true,
    inFlight:
      brain === 'maid_chan_67'
        ? { seq: 512, turn: 111, lane: 'brain', purpose: 'turn', model: 'claude-opus-5-5', since: iso(Date.now() - 2400), body: 'maid_chan_67' }
        : null,
    turns: since >= version ? [] : turns,
    window: {
      callSeq: 511,
      turnId: 110,
      at: isoAgo(0.3),
      model: 'claude-opus-5-5',
      tier: 'opus',
      system: [
        { index: 0, text: 'You are maid_chan, a cheerful Minecraft bot on a skyblock server. Be warm and brief.', cache: true, ttl: '1h', tokens: 5200, inCachedPrefix: true },
        { index: 1, text: 'Tools: say, whisper, goto, mine, craft, run_command (op; trusted requests only), remember, see…', cache: true, ttl: '1h', tokens: 9800, inCachedPrefix: true },
        { index: 2, text: 'World: b1.8.1 skyblock. Home at 0,64,0. Generator at 4,64,6.', cache: false, ttl: null, tokens: 600, inCachedPrefix: false },
      ],
      toolCount: 42,
      toolTokens: 8100,
      toolNames: ['say', 'whisper', 'goto', 'mine', 'craft', 'run_command', 'remember', 'see'],
      messages: messages(),
      promptTokens: 61200,
      estimatedTokens: 61800,
      limit: 200000,
      compactAt: 150000,
      cachedPrefixTokens: 52000,
      usage: { in: 61200, out: 420, cacheRead: 52000, cacheWrite: 1800, cacheWrite1h: 0, cost: 0.031, latencyMs: 3100, promptTokens: 61200 },
      inFlight: brain === 'maid_chan_67',
    },
    evicted: [],
    filter: null,
    note: null,
  };
}

export function brainTurn(brain: string, n: number): BrainTurnDetail {
  const i = Math.max(0, Math.min(11, n - 100));
  const turn = turnSummary(brain, i);
  return {
    brain,
    turn,
    system: [],
    toolNames: ['say', 'run_command'],
    calls: Array.from({ length: turn.calls }, (_, c) => ({
      seq: 500 + i * 3 + c,
      at: iso(Date.parse(turn.start) + c * 1500),
      purpose: turn.purpose,
      tier: turn.tier,
      model: turn.model,
      provider: 'anthropic',
      body: turn.body,
      round: c,
      inFlight: turn.inFlight && c === turn.calls - 1,
      stopReason: c === turn.calls - 1 ? 'end_turn' : 'tool_use',
      usage: { in: 24000, out: 200, cacheRead: 21000, cacheWrite: 600, cacheWrite1h: 0, cost: 0.011, latencyMs: 2400, promptTokens: 24000 },
      error: null,
      context: 30 + i * 2,
      added: [
        { index: 0, role: 'user' as const, tag: 'turn' as const, blocks: [block('text', `[events] ${turn.preview}`, { label: 'turn input' })] },
      ],
      response: [
        block('thinking', 'Fox is trusted and asked directly; a request grant lets me run the command.', { label: 'thinking' }),
        block('text', c === turn.calls - 1 ? 'On it — setting the time to day.' : ''),
        ...(c < turn.calls - 1 ? [block('tool_use', '', { name: 'run_command', id: `tu${c}`, input: '{"command":"/time set day"}' })] : []),
      ],
      maxTokens: 4096,
      effort: 'medium',
      thinking: 'adaptive',
    })),
    children: [turnSummary(brain, (i + 3) % 12)],
    filter: null,
  };
}

export function visionList(body: string): VisionList {
  return {
    at: nowIso(),
    body,
    capacity: 20,
    note: null,
    sheets: Array.from({ length: 6 }, (_, i) => ({
      id: 60 - i,
      at: isoAgo(i * 3 + 0.5),
      body,
      kind: i % 2 ? 'brain' : 'perception',
      source: 'see',
      mode: i % 2 ? 'brain' : 'perception',
      views: ['eye', 'iso yaw=200 pitch=30', 'top zoom=1.5'],
      focus: [4, 64, 6],
      radius: 6,
      question: i % 2 ? null : 'Is the generator flowing?',
      call: JSON.stringify({ views: ['eye', 'iso yaw=200 pitch=30', 'top zoom=1.5'], focus: '4,64,6', radius: 6 }),
      width: 1024,
      height: 640,
      tokens: 870,
      bytes: 182_000,
      sha256: 'a3f19c0d4b7e8f2a6c1d9e0b5a4c3d2e1f0a9b8c7d6e5f4a3b2c1d0e9f8a7b6c',
      legend: 'cobblestone generator: water W, lava L; bot at the centre',
      answer: i === 0 ? null : i % 2 ? null : 'Yes — cobblestone is forming at 5,64,6.',
      note: null,
      model: 'claude-haiku-5-5',
      modelInputTokens: 1200,
      modelOutputTokens: 40,
    })),
  };
}

// ---------- memory activity ----------

export function memoryActivitySince(since: number, brain?: string) {
  const events = sim.activity.filter((e) => e.seq > since && (!brain || e.brain === brain));
  return {
    events: events.slice(-2000),
    inContext: sim.inContext.filter((c) => !brain || c.brain === brain),
    last: sim.activitySeq,
  };
}

export function memoryHeat(brain: string | undefined, windowS: number, stateKey: string): MemoryHeatResponse {
  const halfLifeS = 300;
  const now = Date.now();
  const cutoff = now - windowS * 1000;
  const parents = raptorParents();
  const nodes = new Map<number, { last: number; count: number; brains: Map<string, number> }>();
  const touch = (id: number, at: number, b: string) => {
    const n = nodes.get(id) ?? { last: 0, count: 0, brains: new Map<string, number>() };
    n.last = Math.max(n.last, at);
    n.count++;
    n.brains.set(b, Math.max(n.brains.get(b) ?? 0, at));
    nodes.set(id, n);
  };
  for (const e of sim.activity) {
    const at = Date.parse(e.at);
    if (at < cutoff || (brain && e.brain !== brain) || e.raptorNodeId === null || e.raptorNodeId === undefined) continue;
    const seen = new Set<number>();
    const stack = [e.raptorNodeId];
    while (stack.length) {
      const id = stack.pop()!;
      if (seen.has(id)) continue;
      seen.add(id);
      touch(id, at, e.brain);
      for (const p of parents.get(id) ?? []) stack.push(p);
    }
  }
  const ctx = sim.inContext.filter((c) => !brain || c.brain === brain);
  return {
    at: iso(now),
    brain: brain ?? null,
    windowS,
    halfLifeS,
    stateKey,
    brains: [...new Set(sim.activity.map((e) => e.brain))],
    nodes: [...nodes.entries()].map(([id, n]) => ({
      nodeId: id,
      lastAccessAt: iso(n.last),
      accessCount: n.count,
      heat: Math.pow(0.5, (now - n.last) / 1000 / halfLifeS),
      brains: [...n.brains.entries()].map(([b, at]) => {
        const c = ctx.find((x) => x.brain === b && x.raptorNodeId === id);
        return {
          brain: b,
          lastAccessAt: iso(at),
          inContext: !!c && c.positionFromEnd !== null,
          stalenessS: c ? (now - Date.parse(c.lastSeenAt)) / 1000 : null,
          evicted: !!c && c.positionFromEnd === null,
        };
      }),
    })),
  };
}

export function liveStatus(): LiveStatus {
  return { at: nowIso(), upstream: 'connected', bot: 'up', latencyMs: 38 + Math.round(Math.random() * 12), resync: false, message: null };
}

// ---------- chat ----------

const pendingReplies: Array<{ bot: string; seq: number; text: string; player: string }> = [];
let replyTimer: ReturnType<typeof setTimeout> | null = null;
type Emit = (event: string, data: unknown) => void;
let emitter: Emit = () => undefined;
export function setEmitter(e: Emit): void {
  emitter = e;
}

function pushFeed(line: Omit<FeedLine, 'seq'>): FeedLine {
  const full = { ...line, seq: ++sim.feedSeq };
  sim.feed.push(full);
  if (sim.feed.length > 1000) sim.feed.shift();
  emitter('feed', { lines: [full], last: sim.feedSeq });
  return full;
}

function replyFor(texts: string[], player: string): string {
  const joined = texts.join(' ').toLowerCase();
  if (texts.length > 1) return `Got both of those, ${player}! I'll take care of it.`;
  if (/day|time/.test(joined)) return 'Setting the time to day for you!';
  if (/where|location|coords?/.test(joined)) return "I'm by the cobble generator, right next to home.";
  if (/how|going|status/.test(joined)) return 'Going well! The generator is steady and the chests are filling up.';
  if (/hi|hello|hey/.test(joined)) return `Hi ${player}! 👋`;
  return "Okay! I'm on it.";
}

/** Viewer chat: the viewer line enters the feed at once; the bot answers after a debounce (merged when lines arrive together). */
export function acceptChat(bot: MockBot, text: string, requestId: string, player: string): FeedLine {
  const line = pushFeed({ at: nowIso(), bot: bot.name, kind: 'viewer', from: player, text, origin: 'viewer', requestId });
  pendingReplies.push({ bot: bot.name, seq: line.seq, text, player });
  if (replyTimer) clearTimeout(replyTimer);
  replyTimer = setTimeout(() => {
    replyTimer = null;
    const batch = pendingReplies.splice(0);
    const byBot = new Map<string, typeof batch>();
    for (const p of batch) byBot.set(p.bot, [...(byBot.get(p.bot) ?? []), p]);
    for (const [b, items] of byBot) {
      const first = items[0]!;
      if (/secret|think/.test(first.text)) {
        pushFeed({ at: nowIso(), bot: b, kind: 'internal', from: b, text: 'Thinking about how to answer that kindly.', origin: null });
      }
      pushFeed({ at: nowIso(), bot: b, kind: 'said', from: b, text: replyFor(items.map((i) => i.text), first.player), origin: 'game', replyTo: items[items.length - 1]!.seq });
    }
  }, 2600);
  return line;
}

// ---------- the tick ----------

/** Advances the hive one step and returns the frames the bridge would emit for it. */
export function tick(): Array<[string, unknown]> {
  sim.tick++;
  const frames: Array<[string, unknown]> = [];
  const t = sim.tick;
  for (const b of BOTS) {
    let changed = false;
    if (b.script && !b.script.paused && t % 2 === 0) {
      b.script.line = b.script.line >= Math.min(b.script.lines, 44) ? 6 : b.script.line + 1;
      changed = true;
    }
    if (b.kind !== 'script' && t % 9 === BOTS.indexOf(b)) {
      sim.thoughtIdx[b.name] = (sim.thoughtIdx[b.name] ?? 0) + 1;
      sim.thoughtAt[b.name] = Date.now();
      sim.toolIdx[b.name] = (sim.toolIdx[b.name] ?? 0) + 1;
      changed = true;
    }
    if (b.brain && t % 13 === BOTS.indexOf(b) * 2) {
      sim.inFlight[b.name] = sim.inFlight[b.name] ? null : { purpose: pick(rng(t), ['turn', 'reply', 'perception']), model: 'claude-opus-5-5', since: Date.now() };
      changed = true;
    }
    if (b.kind !== 'main') {
      b.pos = [b.pos[0] + Math.sin(t / 5 + b.pos[2]) * 0.4, b.pos[1], b.pos[2] + Math.cos(t / 7) * 0.4];
    }
    if (changed) frames.push(['now', botNow(b)]);
  }
  if (t % 2 === 0) {
    sim.entityRev++;
    for (const e of sim.entities) if (e.kind === 'mob') e.x += Math.sin(t + (e.id ?? 0)) * 0.3;
    frames.push(['entities', { at: Date.now(), rev: sim.entityRev, full: false, bots: worldBots(), entities: sim.entities.filter((e) => e.kind === 'mob'), removed: [] }]);
  }
  if (t % 3 === 0) {
    const brain = Math.random() < 0.6 ? 'main' : 'maid_chan_67';
    const leaf = pick(rng(t * 7), regionLeaves(brain));
    const ev: MemoryAccessEvent = {
      seq: ++sim.activitySeq,
      at: nowIso(),
      brain,
      body: pick(rng(t), brainBodies[brain] ?? ['maid_chan']),
      memoryKind: 'note',
      memoryId: `doc:${leaf}`,
      raptorNodeId: leaf,
      source: 'rag',
      score: 0.8,
      turn: 112,
    };
    sim.activity.push(ev);
    const existing = sim.inContext.find((c) => c.brain === brain && c.raptorNodeId === leaf);
    if (existing) {
      existing.lastSeenAt = ev.at;
      existing.positionFromEnd = 2;
    } else {
      sim.inContext.push({ brain, memoryKind: 'note', memoryId: ev.memoryId, raptorNodeId: leaf, addedAt: ev.at, lastSeenAt: ev.at, turnsInContext: 1, positionFromEnd: 2 });
    }
    frames.push(['memoryActivity', { events: [ev], inContext: sim.inContext, last: sim.activitySeq }]);
  }
  if (t % 40 === 0) {
    sim.eventId++;
    const ev = { id: sim.eventId, at: nowIso(), kind: 'move', level: 'info', text: 'maid_chan_67 crossed into the nether wastes' };
    sim.events.push(ev);
    frames.push(['event', { events: [ev], last: sim.eventId }]);
  }
  if (t % 15 === 0) frames.push(['status', liveStatus()]);
  if (t % 5 === 0) frames.push(['bots', statusResponse()]);
  return frames;
}
