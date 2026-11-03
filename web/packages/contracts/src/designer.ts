/**
 * The Design Studio (docs/web/api.md, "Design Studio"): a creative-mode sandbox canvas the user co-designs with a model of
 * their choice, saved as an ordinary design (designs.ts) through the bot's Git-style revisions. The canvas is the bot's
 * `/api/designer/*` (src/MineClaw.App/Supervisor/DesignerRoutes.cs), passed through by the bridge (`/v1/sup/designer/*`): reads
 * for any signed-in user, writes for the `design` (or `admin`) role, the designer's identity added by the bridge.
 *
 * Coordinates are canvas-local: x, z 0..63, y 0..47, y = 0 standing on the plot's grass (+x east, +z south).
 * A cell is `[x, y, z, id, meta]` in the server version's own ids (id 0 = air).
 */
import { z } from 'zod';
import { QInt, UnixMs, Vec3i } from './common.js';
import { DesignRevision } from './designs.js';

export const DesignerCell = z.tuple([
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
]);
export type DesignerCell = z.infer<typeof DesignerCell>;

export const DESIGNER_PLOT = { size: 64, height: 48 } as const;

export const DesignerModel = z.object({
  id: z.string().describe('"main" | "worker" | "cheap" (the bot\'s tiers) or "profile:<name>"'),
  label: z.string().describe('"Opus 5.5 (brain)", "deepseek-v3.1:671b-cloud (deepseek)"'),
  provider: z.string().describe('anthropic | ollama'),
  model: z.string(),
  tier: z.string().nullish(),
  profile: z.string().nullish(),
  costHint: z.string().nullish(),
  default: z.boolean(),
});
export type DesignerModel = z.infer<typeof DesignerModel>;

export const DesignerModelsResponse = z.object({
  models: z.array(DesignerModel),
  default: z.string().nullish(),
  version: z.string().describe('the server version whose block ids the canvas uses ("b1.8.1" | "1.12.2")'),
  budget: z
    .object({ mode: z.string(), todayUsd: z.number(), dailyCapUsd: z.number() })
    .nullish()
    .describe("the bot's shared spend (the studio counts against it)"),
});
export type DesignerModelsResponse = z.infer<typeof DesignerModelsResponse>;

export const DesignerPaletteEntry = z.object({
  id: z.number().int(),
  meta: z.number().int(),
  name: z.string(),
  facing: z.boolean().describe('placed with a facing (stairs, doors, ladders, chests, torches…)'),
});
export type DesignerPaletteEntry = z.infer<typeof DesignerPaletteEntry>;

/** An op as stored in the transcript: what it was and how many cells it changed (cells are not kept per message). */
export const DesignerOpSummary = z.object({ kind: z.string(), label: z.string(), cells: z.number().int() });
export type DesignerOpSummary = z.infer<typeof DesignerOpSummary>;

/** An applied op with its cells (the new states). */
export const DesignerOp = z.object({
  kind: z
    .string()
    .describe('fill | hollow | walls | line | place | remove | replace | clear | rotate | move | brush | paste …'),
  label: z.string(),
  by: z.enum(['ai', 'user']).nullish(),
  cells: z.array(DesignerCell),
});
export type DesignerOp = z.infer<typeof DesignerOp>;

/** The user's edits as sent: an op label and the cells' new states. */
export const DesignerEdit = z.object({
  kind: z.string().max(32),
  label: z.string().max(200).optional(),
  cells: z.array(DesignerCell).max(20000),
});
export type DesignerEdit = z.infer<typeof DesignerEdit>;

export const DesignerMessage = z.object({
  seq: z.number().int(),
  role: z.enum(['user', 'assistant']),
  text: z.string(),
  at: UnixMs,
  model: z.string().nullish(),
  costUsd: z.number().nullish(),
  ops: z.array(DesignerOpSummary).nullish(),
});
export type DesignerMessage = z.infer<typeof DesignerMessage>;

export const DesignerSession = z.object({
  id: z.number().int(),
  owner: z.string(),
  model: z.string().describe('a DesignerModel id'),
  designId: z.number().int().nullish().describe('the design this session edits (null = not saved yet)'),
  name: z.string(),
  rev: z.number().int().describe('canvas revision (bumped by every applied change set)'),
  blocks: z.array(DesignerCell),
  anchor: Vec3i.nullish().describe('the design\'s anchor cell ("move origin"); null = the footprint centre'),
  messages: z.array(DesignerMessage),
  costUsd: z.number(),
  calls: z.number().int(),
  version: z.string(),
  palette: z.array(DesignerPaletteEntry),
  plot: z.object({ size: z.number().int(), height: z.number().int() }),
  revisions: z.array(DesignRevision).describe("the saved design's revisions, newest first"),
  created: UnixMs.nullish(),
  updated: UnixMs.nullish(),
});
export type DesignerSession = z.infer<typeof DesignerSession>;

export const DesignerSessionParams = z.object({ id: QInt.min(1) });

export const DesignerCreateRequest = z
  .object({
    model: z.string().max(100).optional(),
    designId: z.number().int().min(1).optional().describe("open this design's newest revision on the canvas"),
  })
  .strict();
export type DesignerCreateRequest = z.infer<typeof DesignerCreateRequest>;

export const DesignerSelection = z.object({ min: Vec3i, max: Vec3i });
export type DesignerSelection = z.infer<typeof DesignerSelection>;

export const DesignerMessageRequest = z
  .object({
    text: z.string().trim().min(1).max(2000),
    model: z.string().max(100).optional(),
    canvas: z.object({ rev: z.number().int().min(0), edits: z.array(DesignerEdit).max(64).optional() }),
    selection: DesignerSelection.nullish().describe('"the selection": its box goes to the model with the message'),
  })
  .strict();
export type DesignerMessageRequest = z.infer<typeof DesignerMessageRequest>;

export const DesignerMessageResponse = z.object({
  reply: z.string(),
  ops: z.array(DesignerOp).describe('in the order the model applied them'),
  rev: z.number().int(),
  costUsd: z.number(),
  sessionCostUsd: z.number(),
  model: z.string(),
  calls: z.number().int(),
  usage: z.object({ input: z.number(), output: z.number(), cacheRead: z.number(), cacheWrite: z.number() }),
  message: DesignerMessage,
  userMessage: DesignerMessage,
  refusal: z.string().nullish(),
});
export type DesignerMessageResponse = z.infer<typeof DesignerMessageResponse>;

export const DesignerCanvasRequest = z
  .object({
    rev: z.number().int().min(0),
    ops: z.array(DesignerEdit).max(64),
    anchor: Vec3i.optional(),
    clearAnchor: z.boolean().optional(),
  })
  .strict();
export type DesignerCanvasRequest = z.infer<typeof DesignerCanvasRequest>;

export const DesignerCanvasResponse = z.object({
  rev: z.number().int(),
  applied: z.number().int(),
  stale: z.boolean().nullish(),
});
export type DesignerCanvasResponse = z.infer<typeof DesignerCanvasResponse>;

export const DesignerSaveRequest = z
  .object({
    name: z.string().trim().min(1).max(48),
    summary: z.string().max(120).optional().describe('the revision message'),
    asNew: z.boolean().optional().describe('save as a new design instead of a new revision of the loaded one'),
  })
  .strict();
export type DesignerSaveRequest = z.infer<typeof DesignerSaveRequest>;

export const DesignerSaveResponse = z.object({
  designId: z.number().int(),
  rev: z.number().int(),
  name: z.string(),
  blocks: z.number().int(),
  created: z.boolean(),
  text: z.string(),
});
export type DesignerSaveResponse = z.infer<typeof DesignerSaveResponse>;

// ---------------------------------------------------------------------------------------------------------- mock model

/**
 * The mock modes' fake designer (Nest's mock bridge and the browser's MSW): a little house when asked to build, a door when
 * asked for one, a roof, a clear; otherwise a polite line. b1.8.1/1.12.2 share these ids (cobblestone 4, planks 5, glass 20,
 * log 17, wooden door 64 + its upper half 72 = 64|8 meta, oak stairs 53).
 */
export function fakeDesignerTurn(
  text: string,
  blocks: readonly DesignerCell[],
  selection?: DesignerSelection | null,
): { reply: string; ops: DesignerOp[] } {
  const t = text.toLowerCase();
  const at = new Map(blocks.map((b) => [`${b[0]},${b[1]},${b[2]}`, b] as const));
  const ops: DesignerOp[] = [];
  const box = (
    x0: number,
    y0: number,
    z0: number,
    x1: number,
    y1: number,
    z1: number,
    id: number,
    meta = 0,
    keep?: (x: number, y: number, z: number) => boolean,
  ) => {
    const cells: DesignerCell[] = [];
    for (let y = y0; y <= y1; y++)
      for (let z = z0; z <= z1; z++)
        for (let x = x0; x <= x1; x++) if (!keep || keep(x, y, z)) cells.push([x, y, z, id, meta]);
    return cells;
  };
  // The house: 7×7 cobblestone floor, plank walls with log corners, glass windows, a slab of planks for a roof.
  const [x0, z0] = selection ? [selection.min[0], selection.min[2]] : [29, 29];
  const x1 = x0 + 6,
    z1 = z0 + 6;
  if (/\bclear\b|start over|empty/.test(t)) {
    ops.push({
      kind: 'clear',
      label: 'cleared the canvas',
      by: 'ai',
      cells: blocks.map(([x, y, z]) => [x, y, z, 0, 0]),
    });
    return { reply: 'Cleared the canvas: a fresh plot to build on.', ops };
  }
  if (/\bdoor\b/.test(t)) {
    // The front (south) wall's middle, or the selection's south edge.
    const dx = Math.floor((x0 + x1) / 2);
    const front = [...at.values()].filter((b) => b[1] === 1 && b[3] !== 0).reduce((m, b) => Math.max(m, b[2]), z1);
    ops.push({
      kind: 'place',
      label: 'placed an oak door',
      by: 'ai',
      cells: [
        [dx, 1, front, 64, 1],
        [dx, 2, front, 64, 9],
      ],
    });
    return { reply: 'Added an oak door in the middle of the front wall.', ops };
  }
  if (/\broof\b/.test(t)) {
    ops.push({
      kind: 'fill',
      label: 'filled a plank roof',
      by: 'ai',
      cells: box(x0 - 1, 4, z0 - 1, x1 + 1, 4, z1 + 1, 5),
    });
    return { reply: 'Put a flat plank roof on it, one block over the walls.', ops };
  }
  if (/house|hut|build|cabin|home|shelter|make/.test(t)) {
    ops.push({
      kind: 'fill',
      label: 'filled a 7×1×7 cobblestone floor',
      by: 'ai',
      cells: box(x0, 0, z0, x1, 0, z1, 4),
    });
    const wall = (x: number, _y: number, z: number) => x === x0 || x === x1 || z === z0 || z === z1;
    const walls = box(x0, 1, z0, x1, 3, z1, 5, 0, wall).map((c): DesignerCell => {
      const corner = (c[0] === x0 || c[0] === x1) && (c[2] === z0 || c[2] === z1);
      const window =
        c[1] === 2 && !corner && (c[0] === x0 + 2 || c[0] === x1 - 2 || c[2] === z0 + 2 || c[2] === z1 - 2);
      return corner ? [c[0], c[1], c[2], 17, 0] : window ? [c[0], c[1], c[2], 20, 0] : c;
    });
    ops.push({ kind: 'walls', label: 'walls of planks, log corners and glass windows', by: 'ai', cells: walls });
    ops.push({
      kind: 'fill',
      label: 'filled a plank roof',
      by: 'ai',
      cells: box(x0 - 1, 4, z0 - 1, x1 + 1, 4, z1 + 1, 5),
    });
    return {
      reply:
        'Built a little 7×7 house: cobblestone floor, plank walls with log corners, glass windows and a flat roof. Ask for a door next.',
      ops,
    };
  }
  return { reply: "I'm the mock designer: ask me to build a house, add a door, a roof, or clear the canvas.", ops };
}

// ---------------------------------------------------------------------------------------------------------- mock studio

const WOOL = [
  'white',
  'orange',
  'magenta',
  'light blue',
  'yellow',
  'lime',
  'pink',
  'gray',
  'light gray',
  'cyan',
  'purple',
  'blue',
  'brown',
  'green',
  'red',
  'black',
];

/** The b1.8.1 design palette as the bot serves it (DesignerCanvas.Palette): mock modes only. */
export const MOCK_DESIGNER_PALETTE: DesignerPaletteEntry[] = [
  ...(
    [
      [1, 'stone'],
      [3, 'dirt'],
      [4, 'cobblestone'],
      [5, 'planks'],
      [12, 'sand'],
      [13, 'gravel'],
      [19, 'sponge'],
      [20, 'glass'],
      [22, 'lapis block'],
      [24, 'sandstone'],
      [25, 'note block'],
      [41, 'gold block'],
      [42, 'iron block'],
      [45, 'bricks'],
      [47, 'bookshelf'],
      [48, 'mossy cobblestone'],
      [49, 'obsidian'],
      [57, 'diamond block'],
      [58, 'crafting table'],
      [80, 'snow block'],
      [82, 'clay'],
      [84, 'jukebox'],
      [85, 'fence'],
      [87, 'netherrack'],
      [88, 'soul sand'],
      [89, 'glowstone'],
      [101, 'iron bars'],
      [102, 'glass pane'],
      [103, 'melon'],
    ] as const
  ).map(([id, name]) => ({ id, meta: 0, name, facing: false })),
  ...['oak log', 'spruce log', 'birch log'].map((name, meta) => ({ id: 17, meta, name, facing: false })),
  ...WOOL.map((c, meta) => ({ id: 35, meta, name: `${c} wool`, facing: false })),
  ...['stone slab', 'sandstone slab', 'wooden slab', 'cobblestone slab', 'brick slab', 'stone brick slab'].map(
    (name, meta) => ({ id: 44, meta, name, facing: false }),
  ),
  ...['stone bricks', 'mossy stone bricks', 'cracked stone bricks'].map((name, meta) => ({
    id: 98,
    meta,
    name,
    facing: false,
  })),
  ...(
    [
      [50, 5, 'torch'],
      [53, 0, 'wooden stairs'],
      [54, 0, 'chest'],
      [23, 0, 'dispenser'],
      [61, 0, 'furnace'],
      [64, 0, 'wooden door'],
      [65, 2, 'ladder'],
      [67, 0, 'cobblestone stairs'],
      [71, 0, 'iron door'],
      [86, 0, 'pumpkin'],
      [91, 0, 'jack o lantern'],
      [96, 0, 'trapdoor'],
      [107, 0, 'fence gate'],
      [108, 0, 'brick stairs'],
      [109, 0, 'stone brick stairs'],
    ] as const
  ).map(([id, meta, name]) => ({ id, meta, name, facing: true })),
];

export const MOCK_DESIGNER_MODELS: DesignerModel[] = [
  {
    id: 'main',
    label: 'Opus 5.5 (brain)',
    provider: 'anthropic',
    model: 'claude-opus-5-5',
    tier: 'main',
    profile: null,
    costHint: '$$$',
    default: true,
  },
  {
    id: 'worker',
    label: 'Sonnet 5.5 (worker)',
    provider: 'anthropic',
    model: 'claude-sonnet-5-5',
    tier: 'worker',
    profile: null,
    costHint: '$$',
    default: false,
  },
  {
    id: 'cheap',
    label: 'Haiku 5.5 (memory)',
    provider: 'anthropic',
    model: 'claude-haiku-5-5',
    tier: 'cheap',
    profile: null,
    costHint: '$',
    default: false,
  },
  {
    id: 'profile:deepseek',
    label: 'deepseek-v3.1:671b-cloud (deepseek)',
    provider: 'ollama',
    model: 'deepseek-v3.1:671b-cloud',
    tier: 'main',
    profile: 'deepseek',
    costHint: 'free',
    default: false,
  },
];

/** A design the mock studio saved (shown first in the mock Designs list). */
export interface MockSavedDesign {
  id: number;
  name: string;
  requester: string;
  blocks: DesignerCell[];
  revisions: DesignRevision[];
  updated: number;
}

type MockSession = DesignerSession & { cells: Map<string, DesignerCell> };

/**
 * The whole studio in memory for the mock modes (MSW and Nest's mock bridge share it; Nest snake-cases its replies): sessions,
 * the fake designer's turns, user edits, saves as designs with revisions. Never used against a real bot.
 */
export function createDesignerMock(firstDesignId = 100) {
  const sessions = new Map<number, MockSession>();
  const saved: MockSavedDesign[] = [];
  let nextSession = 1;
  let nextDesign = firstDesignId;
  const key = (c: readonly number[]) => `${c[0]},${c[1]},${c[2]}`;
  const apply = (s: MockSession, cells: readonly DesignerCell[]): DesignerCell[] => {
    const changed: DesignerCell[] = [];
    for (const c of cells) {
      if (
        c[0] < 0 ||
        c[0] >= DESIGNER_PLOT.size ||
        c[2] < 0 ||
        c[2] >= DESIGNER_PLOT.size ||
        c[1] < 0 ||
        c[1] >= DESIGNER_PLOT.height
      )
        continue;
      const k = key(c);
      const old = s.cells.get(k);
      if (c[3] === 0) {
        if (!old) continue;
        s.cells.delete(k);
      } else {
        if (old && old[3] === c[3] && old[4] === c[4]) continue;
        s.cells.set(k, [c[0], c[1], c[2], c[3], c[4]]);
      }
      changed.push([c[0], c[1], c[2], c[3], c[4]]);
    }
    if (changed.length) s.rev++;
    return changed;
  };
  const view = (s: MockSession): DesignerSession => {
    const { cells, ...rest } = s;
    const d = saved.find((x) => x.id === s.designId);
    return {
      ...rest,
      blocks: [...cells.values()].sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]),
      revisions: d ? [...d.revisions] : [],
      messages: [...s.messages],
    };
  };
  const msg = (
    s: DesignerSession,
    role: 'user' | 'assistant',
    text: string,
    extra: Partial<DesignerMessage> = {},
  ): DesignerMessage => {
    const m: DesignerMessage = {
      seq: s.messages.length + 1,
      role,
      text,
      at: Date.now(),
      model: null,
      costUsd: null,
      ops: [],
      ...extra,
    };
    s.messages.push(m);
    return m;
  };
  function make(body: { model?: string; designId?: number }, owner: string, forceId?: number): MockSession {
    const id = forceId ?? nextSession++;
    if (forceId && forceId >= nextSession) nextSession = forceId + 1;
    const d = body.designId ? saved.find((x) => x.id === body.designId) : undefined;
    const s: MockSession = {
      id,
      owner,
      model: body.model ?? 'main',
      designId: d?.id ?? null,
      name: d?.name ?? 'new design',
      rev: 0,
      blocks: [],
      anchor: null,
      messages: [],
      costUsd: 0,
      calls: 0,
      version: 'b1.8.1',
      palette: MOCK_DESIGNER_PALETTE,
      plot: { ...DESIGNER_PLOT },
      revisions: [],
      created: Date.now(),
      updated: Date.now(),
      cells: new Map<string, DesignerCell>(),
    };
    if (d) for (const c of d.blocks) s.cells.set(key(c), c);
    sessions.set(id, s);
    return s;
  }
  const get = (id: number): MockSession => sessions.get(id) ?? make({}, 'poofimafox', id);
  return {
    saved,
    models: (): DesignerModelsResponse => ({
      models: MOCK_DESIGNER_MODELS,
      default: 'main',
      version: 'b1.8.1',
      budget: { mode: 'normal', todayUsd: 1.24, dailyCapUsd: 50 },
    }),
    create: (body: { model?: string; designId?: number }, owner: string): DesignerSession => {
      if (body.designId && !saved.some((x) => x.id === body.designId)) {
        // A design the mock didn't make: a small stand-in so "Open in studio" works for every listed design.
        const blocks = fakeDesignerTurn('build a house', []).ops.flatMap((o) => o.cells);
        saved.push({
          id: body.designId,
          name: `design #${body.designId}`,
          requester: 'poofimafox',
          blocks,
          revisions: [],
          updated: Date.now(),
        });
      }
      return view(make(body, owner));
    },
    get: (id: number): DesignerSession => view(get(id)),
    canvas: (
      id: number,
      body: { ops: DesignerEdit[]; anchor?: [number, number, number]; clearAnchor?: boolean },
    ): DesignerCanvasResponse => {
      const s = get(id);
      let applied = 0;
      for (const op of body.ops) applied += apply(s, op.cells).length;
      if (body.anchor) s.anchor = body.anchor;
      else if (body.clearAnchor) s.anchor = null;
      return { rev: s.rev, applied, stale: false };
    },
    message: (
      id: number,
      body: {
        text: string;
        model?: string;
        canvas: { rev: number; edits?: DesignerEdit[] };
        selection?: DesignerSelection | null;
      },
    ): DesignerMessageResponse => {
      const s = get(id);
      for (const op of body.canvas.edits ?? []) apply(s, op.cells);
      if (body.model) s.model = body.model;
      const userMessage = msg(s, 'user', body.text);
      const turn = fakeDesignerTurn(body.text, [...s.cells.values()], body.selection);
      const ops = turn.ops.map((o) => ({ ...o, cells: apply(s, o.cells) })).filter((o) => o.cells.length > 0);
      const model = MOCK_DESIGNER_MODELS.find((m) => m.id === s.model)?.model ?? 'claude-opus-5-5';
      const costUsd = model.startsWith('deepseek') ? 0 : 0.0123 + 0.002 * ops.length;
      s.costUsd += costUsd;
      s.calls += ops.length + 1;
      const message = msg(s, 'assistant', turn.reply, {
        model,
        costUsd,
        ops: ops.map((o) => ({ kind: o.kind, label: o.label, cells: o.cells.length })),
      });
      return {
        reply: turn.reply,
        ops,
        rev: s.rev,
        costUsd,
        sessionCostUsd: s.costUsd,
        model,
        calls: ops.length + 1,
        usage: { input: 1800, output: 260, cacheRead: 2900, cacheWrite: 0 },
        message,
        userMessage,
        refusal: null,
      };
    },
    save: (
      id: number,
      body: { name: string; summary?: string; asNew?: boolean },
      owner: string,
    ): DesignerSaveResponse => {
      const s = get(id);
      if (s.cells.size === 0) throw new Error('the canvas is empty');
      let d = !body.asNew && s.designId ? saved.find((x) => x.id === s.designId) : undefined;
      const created = !d;
      if (!d) {
        d = { id: nextDesign++, name: body.name, requester: owner, blocks: [], revisions: [], updated: Date.now() };
        saved.unshift(d);
      }
      d.name = body.name;
      d.blocks = [...s.cells.values()];
      d.updated = Date.now();
      const rev = (d.revisions[0]?.rev ?? 0) + 1;
      d.revisions.unshift({
        rev,
        author: owner,
        authorKind: 'player',
        message: body.summary ?? `saved from the Design Studio by ${owner}`,
        tag: null,
        created: Date.now(),
        n: d.blocks.length,
      });
      s.designId = d.id;
      s.name = body.name;
      const text = `${created ? 'saved as design' : 'saved'} #${d.id} r${rev}: ${body.name}, ${d.blocks.length} blocks`;
      msg(s, 'assistant', text);
      return { designId: d.id, rev, name: body.name, blocks: d.blocks.length, created, text };
    },
  };
}
export type DesignerMock = ReturnType<typeof createDesignerMock>;
