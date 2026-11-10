/**
 * The Design Studio (docs/web/api.md, "Design Studio"): a creative-mode sandbox canvas the user co-designs with a model of
 * their choice, saved as an ordinary design (designs.ts) through the bot's Git-style revisions. The canvas is the bot's
 * `/api/designer/*` (src/MineClaw.App/Supervisor/DesignerRoutes.cs), passed through by the bridge (`/v1/sup/designer/*`): reads
 * for any signed-in user, writes for the `design` (or `admin`) role, the designer's identity added by the bridge.
 *
 * Coordinates are canvas-local: x, z 0..63 (+x east, +z south) and y -16..47: y = 0 is the first layer above the plot's
 * grass, y = -1 the grass layer and y -16..-1 the ground under it, solid earth until it is dug (a basement, a cellar).
 * A cell is `[x, y, z, id, meta]` in the server version's own ids: id 0 = air (below ground: dug out), id -1
 * (`DESIGNER_EARTH`) = undisturbed ground again (a dug cell filled back; below ground only). A session lists its blocks and
 * its dug cells; a save draws the dug cells as forced air (a build digs them) and the depth as the design's `ground N`.
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

export const DESIGNER_PLOT = { size: 64, height: 48, depth: 16 } as const;
/** A mine session's plot: absolute world y 0..127 (depth 0: the canvas y is the world's). */
export const DESIGNER_MINE_PLOT = { size: 256, height: 128, depth: 0 } as const;

/** A session's (and a design's) kind: a build stands on the grass; a mine is dug into generated terrain at absolute y. */
export const DesignerKind = z.enum(['build', 'mine']);
export type DesignerKind = z.infer<typeof DesignerKind>;
/** The cell id of undisturbed ground (below y 0 only): a dug cell filled back in. */
export const DESIGNER_EARTH = -1;

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
  version: z.string().describe('the server version ("b1.8.1" | "1.12.2"): a new design starts in it'),
  versions: z.array(z.string()).nullish().describe('the versions a design can be drawn for'),
  serverSeed: z
    .string()
    .nullish()
    .describe("the server's world seed when the bot knows it (b1.8.1's login tells it): a mine canvas can use it"),
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
  spec: z.string().nullish().describe('the canonical spelling the AI and the DSL use ("wool:red", "spruce_door")'),
  cat: z.string().nullish().describe('Building | Decoration | Colored | Nature | Redstone | Transport | Misc'),
  family: z.string().nullish().describe('none | stairs | slab | door | bed | tall | torch | wall | front | … (DesignFamily)'),
  icon: z
    .string()
    .nullish()
    .describe('icon hint: 1.12.2 an item model ("oak_door"), b1.8.1 "item:N" (items.png tile); none = the block textures'),
});
export type DesignerPaletteEntry = z.infer<typeof DesignerPaletteEntry>;

/** What converting a block set to another version does (DesignConvert): kept, substituted, missing (left out / refused). */
export const DesignerCompatReport = z.object({
  from: z.string(),
  to: z.string(),
  same: z.number().int(),
  compatible: z.boolean().describe('every block exists there (as is or substituted)'),
  exact: z.boolean().nullish(),
  text: z.string().describe('"1.12.2 → b1.8.1: 120 the same; 20 Spruce Planks → Wooden Planks; not in b1.8.1: 12 White Concrete"'),
  substituted: z.array(z.object({ name: z.string(), count: z.number().int() })).default([]),
  missing: z.array(z.object({ name: z.string(), count: z.number().int() })).default([]),
});
export type DesignerCompatReport = z.infer<typeof DesignerCompatReport>;

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
  dug: z
    .array(Vec3i)
    .nullish()
    .describe(
      'cells below ground (y < 0) dug out to air: the basement the build digs; every other cell under y 0 is earth',
    ),
  anchor: Vec3i.nullish().describe('the design\'s anchor cell ("move origin"); null = the footprint centre'),
  messages: z.array(DesignerMessage),
  costUsd: z.number(),
  calls: z.number().int(),
  version: z.string().describe("the version the design is drawn for (its ids, its palette, its viewer assets)"),
  serverVersion: z.string().nullish().describe("the server's version (a build there converts or refuses)"),
  versions: z.array(z.string()).nullish(),
  palette: z.array(DesignerPaletteEntry).describe("every placeable block state of the session's version"),
  categories: z.array(z.string()).nullish(),
  compat: DesignerCompatReport.nullish().describe('a design opened with convertTo: what the conversion did'),
  serverCompat: DesignerCompatReport.nullish().describe(
    "drawn for another version than the server's: how it would build there (null = the same version)",
  ),
  plot: z.object({ size: z.number().int(), height: z.number().int(), depth: z.number().int().nullish() }),
  kind: DesignerKind.nullish().describe('build (default: on the grass) | mine (dug into generated terrain, absolute y)'),
  seed: z.string().nullish().describe("a mine session's terrain seed (the generator is MineClaw's own)"),
  mineAuthoring: z.string().max(2_000_000).nullish().describe('Revision-specific mine components and editor settings; executable work remains in cells'),
  designRevision: z.number().int().nonnegative().nullish().describe('Opened or last saved design revision, separate from the canvas edit revision'),
  preferredFor: z
    .string()
    .nullish()
    .describe('a version-preferred mine design: the version it is preferred for (saved as `preferred_for`)'),
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
    rev: z.number().int().min(1).optional().describe('with designId: open this revision instead of the newest'),
    kind: DesignerKind.optional().describe('the kind of a new canvas (default build)'),
    seed: z.string().max(32).optional().describe("a new mine canvas's seed (default: random)"),
    version: z.enum(['b1.8.1', '1.12.2']).optional().describe("a new canvas's version (default: the server's)"),
    convertTo: z
      .enum(['b1.8.1', '1.12.2'])
      .optional()
      .describe("the design converted to this version (Story: a world of another version); see `compat`"),
  })
  .strict();
export type DesignerCreateRequest = z.infer<typeof DesignerCreateRequest>;

export const DesignerVersionRequest = z
  .object({
    version: z.enum(['b1.8.1', '1.12.2']),
    kind: DesignerKind.optional().describe('switch build ↔ mine (a full canvas needs apply: it is cleared)'),
    seed: z.string().max(32).optional().describe("a mine canvas's terrain seed"),
    preferredFor: z.string().max(16).nullish().describe('tag (or untag: null) the design as preferred for a version'),
    apply: z.boolean().optional().describe('false (default) = the compatibility check only; true = convert (or clear)'),
  })
  .strict();
export type DesignerVersionRequest = z.infer<typeof DesignerVersionRequest>;

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

/**
 * The largest canvas (or message) body the bridge and the bot read, in bytes: `BridgeAuthMiddleware.JsonBodyLimit` and
 * `DesignerRoutes.MaxBodyBytes` in C#. The three must agree: a larger body is answered 413 before it is looked at.
 */
export const DESIGNER_CANVAS_BODY_CAP = 256 * 1024;
/**
 * What the browser puts into one canvas request at most, in bytes of the body the bot reads. The room up to the cap is for
 * what the bridge adds on the way (who is designing) and for the rest of the request around the edits.
 */
export const DESIGNER_CANVAS_BATCH_BYTES = 200_000;

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
  fixed: z
    .array(DesignerCell)
    .nullish()
    .describe(
      'wall torches / ladders of these edits the bot turned to their solid side (or onto the floor): apply them too',
    ),
});
export type DesignerCanvasResponse = z.infer<typeof DesignerCanvasResponse>;

export const DesignerVersionResponse = z.object({
  version: z.string().describe("the session's version now"),
  target: z.string(),
  changed: z.boolean(),
  report: DesignerCompatReport.nullish().describe('null for an empty canvas (it just switches)'),
  needsClear: z.boolean().nullish().describe('a kind switch of a full canvas: say apply to clear it'),
  session: DesignerSession.nullish().describe('the session after a switch or a conversion'),
});
export type DesignerVersionResponse = z.infer<typeof DesignerVersionResponse>;

export const DesignerSaveRequest = z
  .object({
    name: z.string().trim().min(1).max(48),
    summary: z.string().max(120).optional().describe('the revision message'),
    asNew: z.boolean().optional().describe('save as a new design instead of a new revision of the loaded one'),
    previewPng: z.string().max(2_000_000).regex(/^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/).optional()
      .describe('PNG of the designer camera, at most 1280 by 960 pixels'),
    previewOnly: z.boolean().optional().describe('update the loaded design picture without saving a revision'),
    mineAuthoring: z.string().max(2_000_000).optional(),
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

export const DesignerDeleteRequest = z.object({ name: z.string().min(1).max(48) }).strict();
export const DesignerDeleteResponse = z.object({ id: z.number().int(), text: z.string() });

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
    const walls = box(x0, 1, z0, x1, 3, z1, 5, 0, wall).map((c) => houseWallCell(c, x0, z0, x1, z1));
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

/** A plank wall cell of the mock house: log at the corners, glass windows two in from them on the middle layer. */
function houseWallCell(c: DesignerCell, x0: number, z0: number, x1: number, z1: number): DesignerCell {
  const corner = (c[0] === x0 || c[0] === x1) && (c[2] === z0 || c[2] === z1);
  if (corner) return [c[0], c[1], c[2], 17, 0];
  const window = c[1] === 2 && (c[0] === x0 + 2 || c[0] === x1 - 2 || c[2] === z0 + 2 || c[2] === z1 - 2);
  return window ? [c[0], c[1], c[2], 20, 0] : c;
}

// ---------------------------------------------------------------------------------------------------------- mock studio


const COLOURS = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];
const WOODS = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'dark_oak'];
const titled = (w: string): string =>
  w === 'silver' ? 'Light Gray' : w.split('_').map((p) => p.charAt(0).toUpperCase() + p.slice(1)).join(' ');
const FACING_FAMILIES = new Set(['stairs', 'door', 'bed', 'torch', 'wall', 'front', 'facing6', 'hopper', 'horizontal', 'gate', 'trapdoor', 'button', 'lever', 'rail', 'poweredrail', 'rotation16', 'skull', 'vine']);

type AddEntry = (id: number, meta: number, name: string, spec: string, cat: string, family?: string, icon?: string | null) => void;

/** Stone variants, cobble, planks and logs. */
function paletteStoneAndWood(add: AddEntry, modern: boolean): void {
  add(1, 0, 'Stone', 'stone', 'Building');
  if (modern)
    ['granite', 'polished_granite', 'diorite', 'polished_diorite', 'andesite', 'polished_andesite'].forEach((w, i) =>
      add(1, i + 1, titled(w), `stone:${w}`, 'Building'),
    );
  add(4, 0, 'Cobblestone', 'cobblestone', 'Building');
  add(48, 0, 'Mossy Cobblestone', 'mossy_cobblestone', 'Building');
  if (modern) WOODS.forEach((w, i) => add(5, i, `${titled(w)} Planks`, `planks:${w}`, 'Building'));
  else add(5, 0, 'Wooden Planks', 'planks', 'Building');
  (modern ? WOODS.slice(0, 4) : WOODS.slice(0, 3)).forEach((w, i) => add(17, i, `${titled(w)} Log`, `log:${w}`, 'Building', 'log'));
  if (modern) WOODS.slice(4).forEach((w, i) => add(162, i, `${titled(w)} Log`, `log2:${w}`, 'Building', 'log'));
}

/** Bricks, stone bricks and sandstone. */
function paletteBricks(add: AddEntry, modern: boolean): void {
  add(45, 0, 'Bricks', 'brick_block', 'Building');
  const sb = modern ? 'stonebrick' : 'stone_bricks';
  add(98, 0, 'Stone Bricks', sb, 'Building');
  (modern ? ['mossy', 'cracked', 'chiseled'] : ['mossy', 'cracked']).forEach((w, i) => add(98, i + 1, `${titled(w)} Stone Bricks`, `${sb}:${w}`, 'Building'));
  add(24, 0, 'Sandstone', 'sandstone', 'Building');
  if (modern) {
    add(24, 1, 'Chiseled Sandstone', 'sandstone:chiseled', 'Building');
    add(24, 2, 'Smooth Sandstone', 'sandstone:smooth', 'Building');
    add(179, 0, 'Red Sandstone', 'red_sandstone', 'Building');
  }
}

const SLABS_MODERN = [['stone', 0], ['sandstone', 1], ['cobblestone', 3], ['brick', 4], ['stone_brick', 5], ['nether_brick', 6], ['quartz', 7]] as const;
const SLABS_BETA = [['stone', 0], ['sandstone', 1], ['wood', 2], ['cobblestone', 3], ['brick', 4], ['stone_brick', 5]] as const;
const STAIRS_MODERN: Array<[number, string, string]> = [[134, 'spruce_stairs', 'Spruce Stairs'], [135, 'birch_stairs', 'Birch Stairs'], [136, 'jungle_stairs', 'Jungle Stairs'], [163, 'acacia_stairs', 'Acacia Stairs'], [164, 'dark_oak_stairs', 'Dark Oak Stairs'], [114, 'nether_brick_stairs', 'Nether Brick Stairs'], [128, 'sandstone_stairs', 'Sandstone Stairs'], [156, 'quartz_stairs', 'Quartz Stairs'], [203, 'purpur_stairs', 'Purpur Stairs']];

/** Slabs (stone and, in 1.12.2, wooden) and stairs. */
function paletteSlabsAndStairs(add: AddEntry, modern: boolean): void {
  add(43, 0, 'Double Stone Slab', 'double_stone_slab', 'Building');
  (modern ? SLABS_MODERN : SLABS_BETA).forEach(([w, m]) =>
    add(44, m, w === 'stone' ? 'Stone Slab' : `${titled(w)} Slab`, `stone_slab:${w}`, 'Building', 'slab'),
  );
  if (modern) WOODS.forEach((w, i) => add(126, i, `${titled(w)} Slab`, `wooden_slab:${w}`, 'Building', 'slab'));
  const stairs: Array<[number, string, string]> = [[53, 'oak_stairs', modern ? 'Oak Stairs' : 'Wooden Stairs'], [67, 'stone_stairs', 'Cobblestone Stairs'], [108, 'brick_stairs', 'Brick Stairs'], [109, 'stone_brick_stairs', 'Stone Brick Stairs']];
  if (modern) stairs.push(...STAIRS_MODERN);
  for (const [id, spec, name] of stairs) add(id, 0, name, spec, 'Building', 'stairs');
}

const MINERAL_BLOCKS = [[49, 'obsidian', 'Obsidian'], [20, 'glass', 'Glass'], [102, 'glass_pane', 'Glass Pane'], [101, 'iron_bars', 'Iron Bars'], [41, 'gold_block', 'Block of Gold'], [42, 'iron_block', 'Block of Iron'], [57, 'diamond_block', 'Block of Diamond'], [22, 'lapis_block', 'Lapis Lazuli Block'], [14, 'gold_ore', 'Gold Ore'], [15, 'iron_ore', 'Iron Ore'], [19, 'sponge', 'Sponge']] as const;
const MODERN_BUILDING_BLOCKS = [[112, 'nether_brick', 'Nether Bricks'], [155, 'quartz_block', 'Block of Quartz'], [168, 'prismarine', 'Prismarine'], [201, 'purpur_block', 'Purpur Block'], [121, 'end_stone', 'End Stone'], [133, 'emerald_block', 'Block of Emerald'], [173, 'coal_block', 'Block of Coal'], [170, 'hay_block', 'Hay Bale'], [216, 'bone_block', 'Bone Block']] as const;

/** Glass, metal and ore blocks; 1.12.2's nether, quartz, prismarine, purpur and the hay / bone pillars. */
function paletteMinerals(add: AddEntry, modern: boolean): void {
  for (const [id, spec, name] of MINERAL_BLOCKS) add(id, 0, name, spec, 'Building');
  if (modern)
    for (const [id, spec, name] of MODERN_BUILDING_BLOCKS)
      add(id, 0, name, spec, 'Building', id === 170 || id === 216 ? 'pillar' : 'none');
}

/** Wool; in 1.12.2 also carpet, stained glass, terracotta, concrete, glazed terracotta and shulker boxes. */
function paletteColored(add: AddEntry, modern: boolean): void {
  COLOURS.forEach((c, i) => add(35, i, `${titled(c)} Wool`, `wool:${c}`, 'Colored'));
  if (!modern) return;
  COLOURS.forEach((c, i) => add(171, i, `${titled(c)} Carpet`, `carpet:${c}`, 'Colored'));
  COLOURS.forEach((c, i) => add(95, i, `${titled(c)} Stained Glass`, `stained_glass:${c}`, 'Colored'));
  COLOURS.forEach((c, i) => add(160, i, `${titled(c)} Stained Glass Pane`, `stained_glass_pane:${c}`, 'Colored'));
  add(172, 0, 'Terracotta', 'hardened_clay', 'Colored');
  COLOURS.forEach((c, i) => add(159, i, `${titled(c)} Terracotta`, `stained_hardened_clay:${c}`, 'Colored'));
  COLOURS.forEach((c, i) => add(251, i, `${titled(c)} Concrete`, `concrete:${c}`, 'Colored'));
  COLOURS.forEach((c, i) => add(252, i, `${titled(c)} Concrete Powder`, `concrete_powder:${c}`, 'Colored'));
  COLOURS.forEach((c, i) => add(235 + i, 0, `${titled(c)} Glazed Terracotta`, `${c}_glazed_terracotta`, 'Colored', 'horizontal'));
  COLOURS.forEach((c, i) => add(219 + i, 1, `${titled(c)} Shulker Box`, `${c}_shulker_box`, 'Colored', 'facing6'));
}

const MODERN_FENCES = [[188, 'spruce'], [189, 'birch'], [190, 'jungle'], [192, 'acacia'], [191, 'dark_oak']] as const;

function paletteDecoration(add: AddEntry, modern: boolean): void {
  add(85, 0, modern ? 'Oak Fence' : 'Fence', 'fence', 'Decoration');
  if (modern) MODERN_FENCES.forEach(([id, w]) => add(id, 0, `${titled(w)} Fence`, `${w}_fence`, 'Decoration'));
  add(65, 2, 'Ladder', 'ladder', 'Decoration', 'wall');
  add(50, 5, 'Torch', 'torch', 'Decoration', 'torch');
  add(26, 0, 'Bed', 'bed', 'Decoration', 'bed', modern ? 'bed' : 'item:45');
  add(63, 0, 'Sign', 'standing_sign', 'Decoration', 'rotation16', modern ? 'sign' : 'item:42');
  add(54, 2, 'Chest', 'chest', 'Decoration', 'front');
  add(58, 0, 'Crafting Table', 'crafting_table', 'Decoration');
  add(61, 2, 'Furnace', 'furnace', 'Decoration', 'front');
  add(84, 0, 'Jukebox', 'jukebox', 'Decoration');
  add(47, 0, 'Bookshelf', 'bookshelf', 'Decoration');
  add(89, 0, 'Glowstone', 'glowstone', 'Decoration');
  add(91, 0, "Jack o'Lantern", 'lit_pumpkin', 'Decoration', 'horizontal');
  if (!modern) return;
  add(139, 0, 'Cobblestone Wall', 'cobblestone_wall', 'Decoration');
  add(176, 0, 'Banner', 'standing_banner', 'Decoration', 'rotation16', 'banner');
  add(145, 0, 'Anvil', 'anvil', 'Decoration', 'horizontal', 'anvil_intact');
  add(169, 0, 'Sea Lantern', 'sea_lantern', 'Decoration');
  add(198, 1, 'End Rod', 'end_rod', 'Decoration', 'facing6');
  add(140, 0, 'Flower Pot', 'flower_pot', 'Decoration', 'none', 'flower_pot');
}

const NATURE_BLOCKS = [[12, 'sand', 'Sand'], [13, 'gravel', 'Gravel'], [82, 'clay', 'Clay'], [87, 'netherrack', 'Netherrack'], [88, 'soul_sand', 'Soul Sand'], [80, 'snow', 'Snow Block'], [86, 'pumpkin', 'Pumpkin'], [103, 'melon_block', 'Melon'], [81, 'cactus', 'Cactus'], [37, 'yellow_flower', 'Dandelion']] as const;
const FLOWERS_MODERN = ['poppy', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'];
const DOUBLE_PLANTS = ['sunflower', 'syringa', 'double_grass', 'double_fern', 'double_rose', 'paeonia'];
const DOUBLE_PLANT_NAMES = ['Sunflower', 'Lilac', 'Tall Grass (double)', 'Large Fern', 'Rose Bush', 'Peony'];

/** Ground blocks, saplings and (1.12.2) leaves, flowers and ice. */
function paletteGround(add: AddEntry, modern: boolean): void {
  if (modern) add(2, 0, 'Grass Block', 'grass', 'Nature');
  add(3, 0, 'Dirt', 'dirt', 'Nature');
  for (const [id, spec, name] of NATURE_BLOCKS) add(id, 0, name, spec, 'Nature', id === 86 ? 'horizontal' : 'none');
  (modern ? WOODS : WOODS.slice(0, 3)).forEach((w, i) => add(6, i, `${titled(w)} Sapling`, `sapling:${w}`, 'Nature'));
  if (!modern) {
    add(38, 0, 'Rose', 'red_flower', 'Nature');
    return;
  }
  WOODS.slice(0, 4).forEach((w, i) => add(18, i + 4, `${titled(w)} Leaves`, `leaves:${w}`, 'Nature', 'leaves'));
  FLOWERS_MODERN.forEach((w, i) => add(38, i, w === 'houstonia' ? 'Azure Bluet' : titled(w), `red_flower:${w}`, 'Nature'));
  DOUBLE_PLANTS.forEach((w, i) => add(175, i, DOUBLE_PLANT_NAMES[i]!, `double_plant:${w}`, 'Nature', 'tall'));
  add(79, 0, 'Ice', 'ice', 'Nature');
}

/** Grass, cane, vines and wheat. */
function palettePlants(add: AddEntry, modern: boolean): void {
  add(31, 1, modern ? 'Grass' : 'Tall Grass', 'tallgrass:grass', 'Nature');
  add(83, 0, 'Sugar Cane', modern ? 'reeds' : 'sugar_cane', 'Nature', 'age', modern ? 'reeds' : 'item:27');
  add(106, 1, 'Vines', 'vine', 'Nature', 'vine');
  add(59, 0, 'Wheat Crops', 'wheat', 'Nature', 'age', modern ? 'wheat_seeds' : 'item:9');
}

/** Wire, torches, repeaters, switches and pistons. */
function paletteRedstoneParts(add: AddEntry, modern: boolean): void {
  add(55, 0, 'Redstone Wire', 'redstone_wire', 'Redstone', 'age', modern ? 'redstone' : 'item:56');
  add(76, 5, 'Redstone Torch', 'redstone_torch', 'Redstone', 'torch');
  add(93, 0, 'Redstone Repeater', 'unpowered_repeater', 'Redstone', 'horizontal', modern ? 'repeater' : 'item:86');
  add(69, 5, 'Lever', 'lever', 'Redstone', 'lever');
  add(77, 1, modern ? 'Stone Button' : 'Button', 'stone_button', 'Redstone', 'button');
  add(70, 0, 'Stone Pressure Plate', 'stone_pressure_plate', 'Redstone', 'age');
  add(33, 1, 'Piston', 'piston', 'Redstone', 'facing6');
  add(29, 1, 'Sticky Piston', 'sticky_piston', 'Redstone', 'facing6');
  add(23, 3, 'Dispenser', 'dispenser', 'Redstone', 'facing6');
  add(25, 0, 'Note Block', 'noteblock', 'Redstone');
  add(46, 0, 'TNT', 'tnt', 'Redstone');
}

const MODERN_DOORS = [[193, 'spruce'], [194, 'birch'], [195, 'jungle'], [196, 'acacia'], [197, 'dark_oak']] as const;

/** Doors, trapdoors and gates; 1.12.2's comparator, lamp, redstone block, observer and hopper. */
function paletteRedstoneBlocks(add: AddEntry, modern: boolean): void {
  add(64, 0, modern ? 'Oak Door' : 'Wooden Door', 'wooden_door', 'Redstone', 'door', modern ? 'oak_door' : 'item:43');
  if (modern) MODERN_DOORS.forEach(([id, w]) => add(id, 0, `${titled(w)} Door`, `${w}_door`, 'Redstone', 'door', `${w}_door`));
  add(71, 0, 'Iron Door', 'iron_door', 'Redstone', 'door', modern ? 'iron_door' : 'item:44');
  add(96, 0, modern ? 'Oak Trapdoor' : 'Trapdoor', 'trapdoor', 'Redstone', 'trapdoor');
  add(107, 0, modern ? 'Oak Fence Gate' : 'Fence Gate', 'fence_gate', 'Redstone', 'gate');
  if (!modern) return;
  add(149, 0, 'Redstone Comparator', 'unpowered_comparator', 'Redstone', 'horizontal', 'comparator');
  add(123, 0, 'Redstone Lamp', 'redstone_lamp', 'Redstone');
  add(152, 0, 'Block of Redstone', 'redstone_block', 'Redstone');
  add(218, 3, 'Observer', 'observer', 'Redstone', 'facing6');
  add(154, 0, 'Hopper', 'hopper', 'Redstone', 'hopper', 'hopper');
}

/** Rails, fluids and the beacon. */
function paletteTransportAndMisc(add: AddEntry, modern: boolean): void {
  add(66, 0, 'Rail', 'rail', 'Transport', 'rail');
  add(27, 0, 'Powered Rail', 'golden_rail', 'Transport', 'poweredrail');
  add(28, 0, 'Detector Rail', 'detector_rail', 'Transport', 'poweredrail');
  if (modern) add(157, 0, 'Activator Rail', 'activator_rail', 'Transport', 'poweredrail');
  add(9, 0, 'Water (source)', 'water', 'Misc');
  add(11, 0, 'Lava (source)', 'lava', 'Misc');
  if (modern) add(138, 0, 'Beacon', 'beacon', 'Misc');
}

/** The palette's sections in the order the bot lists them. */
const PALETTE_SECTIONS = [
  paletteStoneAndWood,
  paletteBricks,
  paletteSlabsAndStairs,
  paletteMinerals,
  paletteColored,
  paletteDecoration,
  paletteGround,
  palettePlants,
  paletteRedstoneParts,
  paletteRedstoneBlocks,
  paletteTransportAndMisc,
];

/**
 * A version's design palette as the bot serves it (DesignPalette: every placeable state in creative-tab categories): mock
 * modes only. Close to the bot's table (the same ids, metas, specs and categories for what the studio's tests and screens use),
 * not a copy of it.
 */
export function mockDesignerPalette(version: string): DesignerPaletteEntry[] {
  const out: DesignerPaletteEntry[] = [];
  const add: AddEntry = (id, meta, name, spec, cat, family = 'none', icon = null) =>
    out.push({ id, meta, name, spec, cat, family, facing: FACING_FAMILIES.has(family), icon });
  const modern = version === '1.12.2';
  for (const section of PALETTE_SECTIONS) section(add, modern);
  return out;
}

export const DESIGNER_CATEGORIES = ['Building', 'Decoration', 'Colored', 'Nature', 'Redstone', 'Transport', 'Misc'] as const;

/** Going back to b1.8.1: stained glass becomes glass, every plank colour oak planks; undefined when a block carries over. */
function betaSubstitute(id: number, meta: number): [number, string] | undefined {
  if (id === 95) return [20, 'Glass'];
  if (id === 5 && meta > 0) return [5, 'Wooden Planks'];
  return undefined;
}

/** A mock conversion (DesignConvert's shape): ids b1.8.1 doesn't have (and 1.12.2's stained glass) don't carry over going back. */
export function mockConvert(
  cells: readonly DesignerCell[],
  from: string,
  to: string,
): { cells: DesignerCell[]; report: DesignerCompatReport } {
  const src = mockDesignerPalette(from);
  const name = (id: number, meta: number) => src.find((p) => p.id === id && p.meta === meta)?.name ?? src.find((p) => p.id === id)?.name ?? `block ${id}`;
  const out: DesignerCell[] = [];
  const missing = new Map<string, number>();
  const substituted = new Map<string, number>();
  const count = (m: Map<string, number>, k: string) => m.set(k, (m.get(k) ?? 0) + 1);
  let same = 0;
  const lossless = from === to || to === '1.12.2';
  for (const c of cells) {
    const [x, y, z, id, meta] = c;
    const sub = lossless ? undefined : betaSubstitute(id, meta);
    if (sub) {
      out.push([x, y, z, sub[0], 0]);
      count(substituted, `${name(id, meta)} → ${sub[1]}`);
    } else if (!lossless && id > 109) count(missing, name(id, meta));
    else {
      out.push(c);
      same++;
    }
  }
  const list = (m: Map<string, number>) => [...m].map(([n, count]) => ({ name: n, count }));
  const text =
    `${from} → ${to}: ${same} the same` +
    (substituted.size ? `; ${list(substituted).map((s) => `${s.count} ${s.name}`).join(', ')}` : '') +
    (missing.size ? `; not in ${to}: ${list(missing).map((s) => `${s.count} ${s.name}`).join(', ')}` : '');
  return {
    cells: out,
    report: { from, to, same, compatible: missing.size === 0, exact: missing.size === 0 && substituted.size === 0, text, substituted: list(substituted), missing: list(missing) },
  };
}

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

/** The b1.8.1 design palette (the mock server's version). */
export const MOCK_DESIGNER_PALETTE: DesignerPaletteEntry[] = mockDesignerPalette('b1.8.1');
/** The mock server's version (a new design starts in it). */
export const MOCK_SERVER_VERSION = 'b1.8.1';

/** A design the mock studio saved (shown first in the mock Designs list). */
export interface MockSavedDesign {
  id: number;
  name: string;
  requester: string;
  blocks: DesignerCell[];
  /** Dug cells below ground as "x,y,z" keys. */
  dug?: string[];
  revisions: DesignRevision[];
  updated: number;
  previewPng?: string;
  mineAuthoring?: string | null;
  /** The game version it is drawn for. */
  version?: string;
  /** build | mine, the mine's seed, its version tag, its anchor (a mine's entry). */
  kind?: DesignerKind;
  seed?: string | null;
  preferredFor?: string | null;
  anchor?: [number, number, number] | null;
  /** Each revision's cells (opened with `rev`). */
  revs?: Record<number, { blocks: DesignerCell[]; dug: string[]; mineAuthoring?: string | null }>;
}

type MockSession = DesignerSession & { cells: Map<string, DesignerCell>; holes: Set<string> };

/**
 * The whole studio in memory for the mock modes (MSW and Nest's mock bridge share it; Nest snake-cases its replies): sessions,
 * the fake designer's turns, user edits, saves as designs with revisions. Never used against a real bot.
 */
const cellKey = (c: readonly number[]) => `${c[0]},${c[1]},${c[2]}`;

function inPlot(c: DesignerCell, plot: { size: number; height: number; depth: number }): boolean {
  const inSquare = c[0] >= 0 && c[0] < plot.size && c[2] >= 0 && c[2] < plot.size;
  return inSquare && c[1] >= -plot.depth && c[1] < plot.height;
}

/** In the plot; a mine has no terrain in the mock: every cell is ground but the bedrock floor (y 0), which never digs. */
function editable(c: DesignerCell, plot: { size: number; height: number; depth: number }, mine: boolean): boolean {
  return inPlot(c, plot) && !(mine && c[1] === 0 && c[3] === 0);
}

/** What a cell holds now: a block, undisturbed earth (below ground, not dug) or air. */
function groundState(s: MockSession, k: string, below: boolean): 'block' | 'earth' | 'air' {
  if (s.cells.get(k)) return 'block';
  return below && !s.holes.has(k) ? 'earth' : 'air';
}

/** Air or earth. Below ground air digs the cell out and earth fills it back; above it both are air. The change, or null. */
function applyGround(s: MockSession, c: DesignerCell, below: boolean): DesignerCell | null {
  const k = cellKey(c);
  const dig = below && c[3] === 0;
  const now = below && !dig ? 'earth' : 'air';
  if (groundState(s, k, below) === now) return null;
  s.cells.delete(k);
  if (dig) s.holes.add(k);
  else s.holes.delete(k);
  return [c[0], c[1], c[2], below && !dig ? DESIGNER_EARTH : 0, 0];
}

/** A block (the same block again is no change). The change, or null. */
function applyBlock(s: MockSession, c: DesignerCell): DesignerCell | null {
  const k = cellKey(c);
  const old = s.cells.get(k);
  if (old && old[3] === c[3] && old[4] === c[4]) return null;
  s.cells.set(k, [c[0], c[1], c[2], c[3], c[4]]);
  s.holes.delete(k);
  return [c[0], c[1], c[2], c[3], c[4]];
}

const randomSeed = () => String(Math.floor(Math.random() * 1e9) + 1);

type MakeBody = { model?: string; designId?: number; version?: string; convertTo?: string; rev?: number; kind?: DesignerKind; seed?: string };

/** What a session opened from a saved design takes from it (a fresh one: the request's kind and the defaults). */
function fromDesign(body: MakeBody, d: MockSavedDesign | undefined) {
  return {
    designId: d?.id ?? null,
    name: d?.name ?? 'new design',
    kind: d?.kind ?? body.kind ?? 'build',
    preferredFor: d?.preferredFor ?? null,
  };
}

/** A fresh session, or one opened from a saved design (its name, dug cells, kind, version tag and anchor). */
function newMockSession(id: number, owner: string, body: MakeBody, d: MockSavedDesign | undefined): MockSession {
  const from = fromDesign(body, d);
  const s: MockSession = {
    id,
    owner,
    model: body.model ?? 'main',
    designId: from.designId,
    designRevision: d ? body.rev ?? d.revisions[0]?.rev ?? null : null,
    name: from.name,
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
    holes: new Set<string>(d?.dug ?? []),
    kind: from.kind,
    seed: null,
    preferredFor: from.preferredFor,
  };
  if (s.kind === 'mine') s.seed = d?.seed ?? body.seed ?? randomSeed();
  if (d?.anchor) s.anchor = d.anchor;
  return s;
}

/** The session's version and the palette, categories and server compatibility that follow from it. */
function setMockVersion(s: MockSession, version: string): void {
  s.version = version;
  s.palette = mockDesignerPalette(version);
  s.categories = [...DESIGNER_CATEGORIES];
  s.serverVersion = MOCK_SERVER_VERSION;
  s.versions = ['b1.8.1', '1.12.2'];
}

function revisionOf(body: MakeBody, d: MockSavedDesign | undefined) {
  return d && body.rev ? d.revs?.[body.rev] : undefined;
}

/** The design's cells (a revision's with `rev`), converted on the way in with `convertTo`. */
function openDesignCells(s: MockSession, body: MakeBody, d: MockSavedDesign | undefined): DesignerCell[] {
  const at = revisionOf(body, d);
  s.mineAuthoring = body.rev ? at?.mineAuthoring ?? null : d?.mineAuthoring ?? null;
  if (at) s.holes = new Set(at.dug);
  const blocks = at?.blocks ?? d?.blocks ?? [];
  if (!d || !body.convertTo || body.convertTo === s.version) return blocks;
  const conv = mockConvert(blocks, s.version, body.convertTo);
  s.compat = conv.report;
  setMockVersion(s, body.convertTo);
  return conv.cells;
}

type VersionBody = { version: string; kind?: DesignerKind; seed?: string; preferredFor?: string | null; apply?: boolean };

/** Build <-> mine: a full canvas is cleared on apply (asked first); a mine keeps or gets a seed, a build drops the tag. */
function switchMockKind(s: MockSession, body: VersionBody & { kind: DesignerKind }, view: (s: MockSession) => DesignerSession): DesignerVersionResponse {
  const target = body.version;
  if (s.cells.size + s.holes.size > 0 && !body.apply)
    return { version: s.version, target, changed: false, report: null, session: null, needsClear: true };
  s.cells.clear();
  s.holes.clear();
  s.anchor = null;
  s.kind = body.kind;
  s.seed = body.kind === 'mine' ? (body.seed ?? s.seed ?? randomSeed()) : null;
  if (body.kind === 'build') s.preferredFor = null;
  s.rev++;
  if (target !== s.version) setMockVersion(s, target);
  return { version: s.version, target, changed: true, report: null, session: view(s) };
}

/** A new mine seed or version tag; true when the request asks for one. */
function retagMock(s: MockSession, body: VersionBody): boolean {
  const newSeed = body.seed !== undefined && s.kind === 'mine';
  if (!(newSeed && body.seed !== s.seed) && body.preferredFor === undefined) return false;
  if (newSeed) s.seed = body.seed!.trim() || '0';
  if (body.preferredFor !== undefined) s.preferredFor = body.preferredFor;
  return true;
}

/** The saved design for this save: the session's own (unless `asNew`), else a new one listed first. */
function designToSave(s: MockSession, saved: MockSavedDesign[], body: { name: string; asNew?: boolean }, owner: string, nextId: () => number) {
  const d = !body.asNew && s.designId ? saved.find((x) => x.id === s.designId) : undefined;
  if (d) return { d, created: false };
  const fresh: MockSavedDesign = { id: nextId(), name: body.name, requester: owner, blocks: [], revisions: [], updated: Date.now() };
  saved.unshift(fresh);
  return { d: fresh, created: true };
}

/** The session's canvas, kind, seed, tag and anchor into the design. */
function copyIntoDesign(d: MockSavedDesign, s: MockSession, name: string): void {
  d.name = name;
  d.blocks = [...s.cells.values()];
  d.dug = [...s.holes];
  d.version = s.version;
  d.kind = s.kind ?? 'build';
  d.seed = s.seed ?? null;
  d.preferredFor = s.preferredFor ?? null;
  d.mineAuthoring = s.kind === 'mine' ? s.mineAuthoring ?? null : null;
  d.anchor = s.anchor ?? null;
  d.updated = Date.now();
}

function saveText(s: MockSession, d: MockSavedDesign, rev: number, name: string, created: boolean): string {
  const saved = created ? 'saved as design' : 'saved';
  return s.kind === 'mine'
    ? `${saved} #${d.id} r${rev} (${s.version}, mine, anchor absolute): ${name}, ${d.dug!.length} cells to dig, ${d.blocks.length} blocks`
    : `${saved} #${d.id} r${rev} (${s.version}): ${name}, ${d.blocks.length} blocks`;
}

export function createDesignerMock(
  firstDesignId = 100,
  /** Keeps the studio across reloads (the browser mock: sessionStorage), like the bot's db keeps sessions. */
  persist?: { load(): string | null; save(json: string): void },
) {
  const sessions = new Map<number, MockSession>();
  const saved: MockSavedDesign[] = [];
  const hiddenDesignIds = new Set<number>();
  let nextSession = 1;
  let nextDesign = firstDesignId;
  type Stored = { sessions: Array<MockSession & { cellList: DesignerCell[]; holeList: string[] }>; saved: MockSavedDesign[]; hiddenDesignIds?: number[]; nextSession: number; nextDesign: number };
  try {
    const raw = persist?.load();
    if (raw) {
      const st = JSON.parse(raw) as Stored;
      for (const s of st.sessions) {
        const { cellList, holeList, ...rest } = s;
        sessions.set(s.id, { ...rest, cells: new Map(cellList.map((c) => [`${c[0]},${c[1]},${c[2]}`, c] as const)), holes: new Set(holeList) });
      }
      saved.push(...st.saved);
      for (const id of st.hiddenDesignIds ?? []) hiddenDesignIds.add(id);
      nextSession = st.nextSession;
      nextDesign = st.nextDesign;
    }
  } catch {
    /* a stale or foreign store: start empty */
  }
  const store = () => {
    if (!persist) return;
    try {
      const st: Stored = {
        sessions: [...sessions.values()].map((s) => ({ ...s, cells: undefined as never, holes: undefined as never, cellList: [...s.cells.values()], holeList: [...s.holes] })),
        saved,
        hiddenDesignIds: [...hiddenDesignIds],
        nextSession,
        nextDesign,
      };
      persist.save(JSON.stringify(st));
    } catch {
      /* storage full or blocked: the mock just forgets on reload */
    }
  };
  const key = cellKey;
  const apply = (s: MockSession, cells: readonly DesignerCell[]): DesignerCell[] => {
    const changed: DesignerCell[] = [];
    const mine = s.kind === 'mine';
    const plot = mine ? DESIGNER_MINE_PLOT : DESIGNER_PLOT;
    for (const c of cells) {
      if (!editable(c, plot, mine)) continue;
      const below = mine ? c[1] > 0 : c[1] < 0;
      const done = c[3] === 0 || c[3] === DESIGNER_EARTH ? applyGround(s, c, below) : applyBlock(s, c);
      if (done) changed.push(done);
    }
    if (changed.length) s.rev++;
    return changed;
  };
  const view = (s: MockSession): DesignerSession => {
    const { cells, holes, ...rest } = s;
    const d = saved.find((x) => x.id === s.designId);
    const blocks = [...cells.values()].sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);
    return {
      ...rest,
      blocks,
      dug: [...holes].map((h) => h.split(',').map(Number) as [number, number, number]),
      revisions: d ? [...d.revisions] : [],
      messages: [...s.messages],
      kind: s.kind ?? 'build',
      seed: s.kind === 'mine' ? (s.seed ?? '0') : null,
      preferredFor: s.preferredFor ?? null,
      plot: s.kind === 'mine' ? { ...DESIGNER_MINE_PLOT } : { ...DESIGNER_PLOT },
      serverCompat: s.version !== MOCK_SERVER_VERSION && blocks.length ? mockConvert(blocks, s.version, MOCK_SERVER_VERSION).report : null,
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
  const setVersion = setMockVersion;
  function make(body: MakeBody, owner: string, forceId?: number): MockSession {
    const id = forceId ?? nextSession++;
    if (forceId && forceId >= nextSession) nextSession = forceId + 1;
    const d = body.designId ? saved.find((x) => x.id === body.designId) : undefined;
    const s = newMockSession(id, owner, body, d);
    setVersion(s, d?.version ?? body.version ?? MOCK_SERVER_VERSION);
    for (const c of openDesignCells(s, body, d)) s.cells.set(key(c), c);
    sessions.set(id, s);
    return s;
  }
  const get = (id: number): MockSession => sessions.get(id) ?? make({}, 'poofimafox', id);
  const api = {
    saved,
    hiddenDesignIds,
    deleteDesign: (id: number, name: string) => {
      if (hiddenDesignIds.has(id)) throw new Error('no such saved design');
      const d = saved.find((x) => x.id === id);
      if (d && d.name !== name) throw new Error('the design name changed; reload it and confirm again');
      hiddenDesignIds.add(id);
      store();
      return { id, text: `Removed ${name} from the saved library. Existing builds are unchanged.` };
    },
    models: (): DesignerModelsResponse => ({
      models: MOCK_DESIGNER_MODELS,
      default: 'main',
      version: MOCK_SERVER_VERSION,
      versions: ['b1.8.1', '1.12.2'],
      serverSeed: '8675309',
      budget: { mode: 'normal', todayUsd: 1.24, dailyCapUsd: 50 },
    }),
    /** The canvas's version: an empty one switches; a full one is checked, and converted with `apply`. */
    version: (id: number, body: VersionBody): DesignerVersionResponse => {
      const s = get(id);
      const target = body.version;
      // The mode first: a kind switch (a full canvas is cleared on apply), a seed, a version tag.
      if (body.kind && body.kind !== (s.kind ?? 'build')) return switchMockKind(s, { ...body, kind: body.kind }, view);
      const retagged = retagMock(s, body);
      if (target === s.version) return { version: s.version, target, changed: retagged, report: null, session: view(s) };
      if (s.cells.size === 0 && s.holes.size === 0) {
        setVersion(s, target);
        return { version: s.version, target, changed: true, report: null, session: view(s) };
      }
      const conv = mockConvert([...s.cells.values()], s.version, target);
      if (!body.apply) return { version: s.version, target, changed: false, report: conv.report, session: null };
      const from = s.version;
      s.cells = new Map(conv.cells.map((c) => [key(c), c] as const));
      s.rev++;
      setVersion(s, target);
      msg(s, 'assistant', `Converted the design from ${from} to ${target}: ${conv.report.text}.`);
      return { version: s.version, target, changed: true, report: conv.report, session: view(s) };
    },
    create: (
      body: { model?: string; designId?: number; version?: string; convertTo?: string; rev?: number; kind?: DesignerKind; seed?: string },
      owner: string,
    ): DesignerSession => {
      if (body.designId && hiddenDesignIds.has(body.designId)) throw new Error('this design was removed from the library');
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
      body: DesignerSaveRequest,
      owner: string,
    ): DesignerSaveResponse => {
      const s = get(id);
      if (!body.asNew && s.designId && hiddenDesignIds.has(s.designId)) throw new Error('this design was removed from the library; use Save as new');
      if (body.previewOnly) {
        const d = saved.find((design) => design.id === s.designId);
        if (!d || !body.previewPng || body.asNew) throw new Error('updating a picture requires an existing design and a PNG');
        d.previewPng = body.previewPng;
        d.updated = Date.now();
        return { designId: d.id, rev: d.revisions[0]?.rev ?? 1, name: d.name, blocks: d.blocks.length, created: false, text: `Updated picture for ${d.name}.` };
      }
      if (s.cells.size === 0 && s.holes.size === 0) throw new Error('the canvas is empty');
      const { d, created } = designToSave(s, saved, body, owner, () => nextDesign++);
      if (body.mineAuthoring !== undefined) s.mineAuthoring = body.mineAuthoring;
      copyIntoDesign(d, s, body.name);
      if (body.previewPng) d.previewPng = body.previewPng;
      const rev = (d.revisions[0]?.rev ?? 0) + 1;
      d.revs = { ...(d.revs ?? {}), [rev]: { blocks: [...d.blocks], dug: [...d.dug!], mineAuthoring: d.mineAuthoring } };
      d.revisions.unshift({
        rev,
        author: owner,
        authorKind: 'player',
        message: body.summary ?? `saved from the Design Studio by ${owner}`,
        tag: null,
        created: Date.now(),
        n: d.blocks.length,
        version: s.version,
        kind: s.kind ?? 'build',
      });
      s.designId = d.id;
      s.designRevision = rev;
      s.name = body.name;
      const text = saveText(s, d, rev, body.name, created);
      msg(s, 'assistant', text);
      return { designId: d.id, rev, name: body.name, blocks: d.blocks.length, created, text };
    },
  };
  // Every call that can change the studio is stored after it (a no-op without `persist`).
  const kept = <A extends unknown[], R>(f: (...a: A) => R) => (...a: A): R => {
    const r = f(...a);
    store();
    return r;
  };
  return {
    ...api,
    get: kept(api.get),
    create: kept(api.create),
    canvas: kept(api.canvas),
    message: kept(api.message),
    save: kept(api.save),
    version: kept(api.version),
  };
}
export type DesignerMock = ReturnType<typeof createDesignerMock>;
