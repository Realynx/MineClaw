/**
 * What the API does with a Story colony frame from a colony with a huge strip mine queued (live 11-10: 47k open build cells,
 * the API's node process at ~109% CPU). A synthetic colony of that shape (30 pawns, 374 storages, 47k ghosts) in the bot's
 * snake_case, as the full state (the REST answer, and what the bridge reads to learn a colony's revision) and as a delta (the
 * `story` frame at 2 Hz): each step the API takes, timed, the way it was before the lag lane and the way it is now. Numbers
 * are printed for the lane's diagnosis; the assertions pin that the REST body, the SSE frame and the socket message are the
 * same text both ways (no timing limits: machines vary).
 */
import { performance } from 'node:perf_hooks';
import {
  INT64_KEYS,
  StoryColonyFrame,
  StoryColonyState,
  formatSseFrame,
  parseBridgeJson,
  toCamel,
  toCamelJson,
} from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import type WebSocket from 'ws';
import { contractSample } from '../src/live/contract-sample.js';
import { DEFAULT_HUB_OPTIONS } from '../src/live/live-hub.js';
import { SocketSink } from '../src/live/socket-sink.js';
import { SseParser } from '../src/live/sse-parser.js';

type Obj = Record<string, unknown>;

const WORK = ['guard', 'doctor', 'cook', 'hunt', 'fish', 'build', 'farm', 'mine', 'chop', 'craft', 'haul'];
const AT = '2026-11-10T12:00:00+00:00';

/** One mine build's dig cells: a corridor along x at z 1122 and twenty branches along z (1026..1218). */
function mineCells(x0: number, y: number): Array<[number, number, number]> {
  const cells: Array<[number, number, number]> = [];
  for (let x = x0; x < x0 + 80; x++) cells.push([x, y, 1122]);
  for (let k = 0; k < 20; k++) for (let z = 1026; z <= 1218; z++) if (z !== 1122) cells.push([x0 + 4 * k, y, z]);
  return cells;
}

/** Ghost rows as the bot writes them: [x, y, z, id, meta, build, state, kind, reason?]. */
function ghostRows(): unknown[][] {
  const rows: unknown[][] = [];
  const levels = [17, 16, 12, 11];
  for (let i = 0; i < 12; i++) {
    const id = `b${300000 + i}`;
    for (const [x, y, z] of mineCells([1680, 1760, 1840][i % 3]! + (i % 6 >= 3 ? 2 : 0), levels[Math.floor(i / 3)]!)) {
      const ore = (x * 13 + y * 7 + z * 29) % 53 === 0 ? 16 : 1;
      rows.push([x, y, z, ore, 0, id, i === 0 && z % 97 === 0 ? 'leased' : 'todo', 'dig']);
    }
  }
  for (let k = 0; k < 74; k++)
    rows.push([1920, 10, 1100 + k, k < 20 ? 1 : 50, k < 20 ? 0 : 4, 'b300012', 'todo', k < 20 ? 'dig' : 'place']);
  for (let k = 0; k < 142; k++)
    rows.push([1820 + (k % 21), 64, 1176 + Math.floor(k / 21), 18, 4, 'b200002', 'todo', 'place']);
  rows.push([
    1838,
    83,
    1194,
    98,
    0,
    'b200001',
    'blocked',
    'place',
    "can't place at 1838,83,1194: no path to block (timeout)",
  ]);
  for (let k = 0; k < 1366; k++)
    rows.push([1822 + (k % 17), 63 + Math.floor(k / 64), 1178 + (k % 13), 98, 0, 'b200001', 'done', 'place']);
  return rows;
}

function pawn(i: number): Obj {
  const miner = i % 15 >= 8;
  const priorities = Object.fromEntries(
    WORK.map((t) => [t, miner ? (t === 'mine' ? 1 : t === 'haul' ? 3 : t === 'build' ? 4 : 0) : t === 'haul' ? 2 : 3]),
  );
  return {
    name: `maid_chan_${101 + i}`,
    display: `Maid ${i}`,
    kind: 'pawn',
    online: true,
    hp: 20,
    food: 18,
    pos: [1800.5 + i, 64, 1210.5],
    dim: 0,
    drafted: false,
    state: 'working',
    leaving: false,
    stuck: null,
    idle_reason: null,
    offline_reason: null,
    job: {
      type: 'build',
      label: 'removing a block (mine (3919 blocks))',
      target: [1758, 40, 1100],
      progress: 0.5,
      since: AT,
    },
    queue: [],
    skills: Object.fromEntries(WORK.map((t, k) => [t, { level: (i + k) % 12, xp: 120.5, passion: k % 4 === 0 }])),
    traits: [{ id: 'industrious', label: 'Industrious', effect: 'works faster' }],
    priorities,
    role: miner ? 'miner' : 'generalist',
    schedule: Array.from({ length: 24 }, () => 'anything'),
    held: 'stone_pickaxe',
    inventory: [
      [274, 0, 1],
      [4, 0, 20],
      [3, 0, 7],
    ],
    equipment: null,
    policy: null,
    gear_tasks: [],
    xp_level: 3,
    speed: null,
  };
}

function storage(i: number): Obj {
  const n = i % 16 < 10 ? 0 : (i % 16) - 9;
  return {
    chest: [1790 + 3 * (i % 10), 64, 1196 + 2 * Math.floor(i / 20)],
    double_with: null,
    label: null,
    priority: i % 41 === 5 ? 'important' : 'normal',
    filter: { allow: i % 11 === 7 ? [] : ['*'], deny: [] },
    signs: [],
    pending_filter: null,
    pending_priority: null,
    contents: Array.from({ length: n }, (_, k) => [[4, 3, 263, 15, 5][k % 5], 0, 8 + k]),
    free_slots: 27 - n,
    known_at: AT,
    enchanted: [],
  };
}

function build(id: string, kind: string, total: number, status: string): Obj {
  return {
    id,
    kind,
    label: `${kind} (${total} blocks)`,
    status,
    reason: null,
    blocks_total: total,
    blocks_done: 0,
    material: kind === 'mine' ? '' : 'stonebrick',
    order: 132,
    created: AT,
    materials: [],
    no_craft: [],
    station: null,
    liquid: null,
    renewable: null,
  };
}

/** The colony's state as the bot answers it: everything (`full`), or a delta after a dig. */
function colony(full: boolean): Obj {
  const builds = full
    ? [
        ...Array.from({ length: 13 }, (_, i) => build(`b${300000 + i}`, 'mine', 3920, i < 3 ? 'active' : 'queued')),
        build('b200001', 'block', 1367, 'active'),
      ]
    : [build('b300000', 'mine', 3920, 'active')];
  const rows = ghostRows();
  return {
    id: 'story-1',
    rev: full ? 6_154_000 : 6_154_020,
    since: full ? null : 6_154_010,
    full,
    at: AT,
    time_of_day: 6000,
    time_frozen: false,
    hour: 12,
    paused: false,
    active: true,
    home: [1807, 63, 1213],
    tie_break: 'column',
    version: 'b1.8.1',
    work_types: WORK,
    pawns: Array.from({ length: 30 }, (_, i) => pawn(i)),
    unstored_kinds: [],
    blocked_work: [
      { reason: 'build b200002: need 142 oak leaves', count: 2, pawns: ['maid_chan_101', 'maid_chan_102'] },
    ],
    zones: [
      {
        id: 'z1',
        kind: 'farm',
        area: [1778, 63, 1179, 1837, 63, 1219],
        rows: Array.from({ length: 41 }, (_, k) => [1179 + k, 1778, 1837]),
        name: null,
      },
    ],
    designations: full ? [{ id: 'd1', kind: 'mine', target: [1818, 63, 1201], status: 'open', fails: 0 }] : [],
    orders: [],
    alerts: Array.from({ length: full ? 50 : 2 }, (_, k) => ({
      at: AT,
      level: 'info',
      text: `maid ${k} is idle: waiting`,
      pawn: null,
    })),
    builds,
    ghosts: { rev: 6_154_020, blocks: full ? rows : rows.slice(0, 4).map((g) => [...g.slice(0, 6), 'done', 'dig']) },
    storages: full ? Array.from({ length: 374 }, (_, i) => storage(i)) : [],
    reservations: [],
    craft_tasks: [],
    workbenches: full
      ? Array.from({ length: 30 }, (_, i) => ({
          pos: [1778 + i, 64, 1193],
          kind: i === 0 ? 'crafting_table' : 'furnace',
          bills: [],
        }))
      : [],
    removed: full ? null : {},
  };
}

/** A socket that takes what the sink sends (the browser's end of the live socket). */
function fakeSocket(): { socket: WebSocket; sent: string[] } {
  const sent: string[] = [];
  const socket = {
    readyState: 1,
    bufferedAmount: 0,
    send(text: string, cb: (error?: Error) => void) {
      sent.push(text);
      cb();
    },
    on() {},
    close() {},
    terminate() {},
  } as unknown as WebSocket;
  return { socket, sent };
}

/** The mean time of `run` over `n` calls (after one untimed call), in ms. */
function time<T>(n: number, run: () => T): { ms: number; out: T } {
  let out = run();
  const t0 = performance.now();
  for (let i = 0; i < n; i++) out = run();
  return { ms: (performance.now() - t0) / n, out };
}

/** `parseBridgeJson` before the lag lane: the int64 reviver on every value of every text. */
function reviverParse(text: string): unknown {
  return JSON.parse(text, function (this: unknown, key: string, value: unknown, context?: { source?: string }) {
    if (typeof value === 'number' && INT64_KEYS.has(key)) {
      if (context?.source !== undefined) return context.source;
      if (Number.isSafeInteger(value)) return String(value);
    }
    return value;
  }) as unknown;
}

/** The socket sink before the lag lane: every SSE frame parsed, its data parsed and the message stringified again. */
function reparsedSocketMessages(sseText: string): string[] {
  const out: string[] = [];
  new SseParser((f) =>
    out.push(JSON.stringify({ event: f.event, id: f.id, data: JSON.parse(f.data) as unknown })),
  ).push(sseText);
  return out;
}

describe('a Story colony frame with a 47k-cell strip mine', () => {
  for (const full of [true, false]) {
    it(`${full ? 'the full state' : 'a delta'}: what each step costs`, () => {
      const text = JSON.stringify(colony(full));
      const n = full ? 4 : 50;
      const plain = time(n, () => JSON.parse(text) as unknown);
      const parsed = time(n, () => reviverParse(text));
      const camel = time(n, () => toCamel<Obj>(parsed.out));
      const rest = time(n, () => StoryColonyState.safeParse(camel.out));
      const sse = time(n, () => StoryColonyFrame.safeParse(camel.out));
      const frame = time(n, () => formatSseFrame('story', 1, camel.out));
      const relay = time(n, () => reparsedSocketMessages(frame.out));
      // After (S4): the reviver only when an int64 key can be in the text, cached keys and shared primitive tuples, the frame
      // formatted once for every client, the socket message spliced from the frame text once, contracts on sampled tuples.
      const parsedNow = time(n, () => parseBridgeJson(text));
      const camelNow = time(n, () => toCamelJson<Obj>(parsedNow.out));
      const restNow = time(n, () => StoryColonyState.safeParse(contractSample(camelNow.out)));
      const sseNow = time(n, () => StoryColonyFrame.safeParse(contractSample(camelNow.out)));
      const frameNow = formatSseFrame('story', 1, camelNow.out);
      const other = formatSseFrame('story', 2, camelNow.out);
      const { socket, sent } = fakeSocket();
      const sink = new SocketSink(socket);
      let k = 0;
      // Alternating two frames, so every write builds its socket message (the first client of a frame).
      const relayNow = time(n, () => sink.write(k++ % 2 === 0 ? frameNow : other));
      const relayMore = time(n, () => new SocketSink(socket).write(frameNow));
      const lines = [
        `${full ? 'full state' : 'delta'}: ${(text.length / 1024).toFixed(0)} KB from the bot, ${(frame.out.length / 1024).toFixed(0)} KB as an SSE frame ` +
          `(the hub drops a congested client's queue past ${DEFAULT_HUB_OPTIONS.maxQueueBytes / 1024} KB)`,
        `  step                         before      after`,
        `  JSON.parse (no reviver)      ${plain.ms.toFixed(2)} ms`,
        `  parseBridgeJson              ${parsed.ms.toFixed(2)} ms   ${parsedNow.ms.toFixed(2)} ms`,
        `  toCamel / toCamelJson        ${camel.ms.toFixed(2)} ms   ${camelNow.ms.toFixed(2)} ms`,
        `  zod StoryColonyState (REST)  ${rest.ms.toFixed(2)} ms   ${restNow.ms.toFixed(2)} ms  (validateResponses: off in production, warn in dev)`,
        `  zod StoryColonyFrame (SSE)   ${sse.ms.toFixed(2)} ms   ${sseNow.ms.toFixed(2)} ms  (hub validate: off by default)`,
        `  formatSseFrame (stringify)   ${frame.ms.toFixed(2)} ms   (before: per client; after: once per frame)`,
        `  socket sink, first client    ${relay.ms.toFixed(2)} ms   ${relayNow.ms.toFixed(3)} ms`,
        `  socket sink, each other one  ${relay.ms.toFixed(2)} ms   ${relayMore.ms.toFixed(3)} ms`,
        `  hub path per frame (parse + camel + one socket client): ` +
          `${(parsed.ms + camel.ms + frame.ms + relay.ms).toFixed(2)} ms   ${(parsedNow.ms + camelNow.ms + frame.ms + relayNow.ms).toFixed(2)} ms`,
        `  each further socket client: ${(frame.ms + relay.ms).toFixed(2)} ms   ${relayMore.ms.toFixed(3)} ms`,
      ];
      // The wire output is the same text, byte for byte: the REST body, the SSE frame and the socket message.
      expect(JSON.stringify(camelNow.out)).toBe(JSON.stringify(camel.out));
      expect(frameNow).toBe(frame.out);
      expect(new Set(sent)).toEqual(new Set([...reparsedSocketMessages(frameNow), ...reparsedSocketMessages(other)]));
      expect(restNow.out.success).toBe(rest.out.success);
      expect(sseNow.out.success).toBe(sse.out.success);
      // A mine build's material is "" as the bot writes it; StoryBuild.material accepts it, so the frame passes the contract.
      expect(rest.out.success).toBe(true);
      expect(sse.out.success).toBe(true);
      for (const [what, r] of [
        ['REST', rest.out],
        ['SSE', sse.out],
      ] as const)
        if (!r.success)
          lines.push(
            `  ${what} contract: ${r.error.issues.length} issue(s), e.g. ${r.error.issues[0]?.path.join('.')}: ${r.error.issues[0]?.message}`,
          );
      // eslint-disable-next-line no-console -- the measured costs, for the lane's diagnosis
      console.log(lines.join('\n'));
      // The socket client gets the same payload the hub formatted.
      const last = JSON.parse(sent.at(-1)!) as { event: string; data: Obj };
      expect(last.event).toBe('story');
      expect((last.data.ghosts as { blocks: unknown[] }).blocks.length).toBe(
        (camel.out.ghosts as { blocks: unknown[] }).blocks.length,
      );
    }, 120_000);
  }
});
