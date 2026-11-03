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
    apply: z.boolean().optional().describe('false (default) = the compatibility check only; true = convert'),
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
  session: DesignerSession.nullish().describe('the session after a switch or a conversion'),
});
export type DesignerVersionResponse = z.infer<typeof DesignerVersionResponse>;

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


const COLOURS = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];
const WOODS = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'dark_oak'];
const titled = (w: string): string =>
  w === 'silver' ? 'Light Gray' : w.split('_').map((p) => p.charAt(0).toUpperCase() + p.slice(1)).join(' ');
const FACING_FAMILIES = new Set(['stairs', 'door', 'bed', 'torch', 'wall', 'front', 'facing6', 'hopper', 'horizontal', 'gate', 'trapdoor', 'button', 'lever', 'rail', 'poweredrail', 'rotation16', 'skull', 'vine']);

/**
 * A version's design palette as the bot serves it (DesignPalette: every placeable state in creative-tab categories): mock
 * modes only. Close to the bot's table (the same ids, metas, specs and categories for what the studio's tests and screens use),
 * not a copy of it.
 */
export function mockDesignerPalette(version: string): DesignerPaletteEntry[] {
  const out: DesignerPaletteEntry[] = [];
  const add = (id: number, meta: number, name: string, spec: string, cat: string, family = 'none', icon: string | null = null) =>
    out.push({ id, meta, name, spec, cat, family, facing: FACING_FAMILIES.has(family), icon });
  const modern = version === '1.12.2';
  // Building
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
  add(43, 0, 'Double Stone Slab', 'double_stone_slab', 'Building');
  (modern ? [['stone', 0], ['sandstone', 1], ['cobblestone', 3], ['brick', 4], ['stone_brick', 5], ['nether_brick', 6], ['quartz', 7]] : [['stone', 0], ['sandstone', 1], ['wood', 2], ['cobblestone', 3], ['brick', 4], ['stone_brick', 5]]).forEach(([w, m]) =>
    add(44, m as number, w === 'stone' ? 'Stone Slab' : `${titled(w as string)} Slab`, `stone_slab:${w}`, 'Building', 'slab'),
  );
  if (modern) WOODS.forEach((w, i) => add(126, i, `${titled(w)} Slab`, `wooden_slab:${w}`, 'Building', 'slab'));
  const stairs: Array<[number, string, string]> = [[53, 'oak_stairs', modern ? 'Oak Stairs' : 'Wooden Stairs'], [67, 'stone_stairs', 'Cobblestone Stairs'], [108, 'brick_stairs', 'Brick Stairs'], [109, 'stone_brick_stairs', 'Stone Brick Stairs']];
  if (modern)
    stairs.push([134, 'spruce_stairs', 'Spruce Stairs'], [135, 'birch_stairs', 'Birch Stairs'], [136, 'jungle_stairs', 'Jungle Stairs'], [163, 'acacia_stairs', 'Acacia Stairs'], [164, 'dark_oak_stairs', 'Dark Oak Stairs'], [114, 'nether_brick_stairs', 'Nether Brick Stairs'], [128, 'sandstone_stairs', 'Sandstone Stairs'], [156, 'quartz_stairs', 'Quartz Stairs'], [203, 'purpur_stairs', 'Purpur Stairs']);
  for (const [id, spec, name] of stairs) add(id, 0, name, spec, 'Building', 'stairs');
  for (const [id, spec, name] of [[49, 'obsidian', 'Obsidian'], [20, 'glass', 'Glass'], [102, 'glass_pane', 'Glass Pane'], [101, 'iron_bars', 'Iron Bars'], [41, 'gold_block', 'Block of Gold'], [42, 'iron_block', 'Block of Iron'], [57, 'diamond_block', 'Block of Diamond'], [22, 'lapis_block', 'Lapis Lazuli Block'], [14, 'gold_ore', 'Gold Ore'], [15, 'iron_ore', 'Iron Ore'], [19, 'sponge', 'Sponge']] as const)
    add(id, 0, name, spec, 'Building');
  if (modern)
    for (const [id, spec, name] of [[112, 'nether_brick', 'Nether Bricks'], [155, 'quartz_block', 'Block of Quartz'], [168, 'prismarine', 'Prismarine'], [201, 'purpur_block', 'Purpur Block'], [121, 'end_stone', 'End Stone'], [133, 'emerald_block', 'Block of Emerald'], [173, 'coal_block', 'Block of Coal'], [170, 'hay_block', 'Hay Bale'], [216, 'bone_block', 'Bone Block']] as const)
      add(id, 0, name, spec, 'Building', id === 170 || id === 216 ? 'pillar' : 'none');
  // Colored
  COLOURS.forEach((c, i) => add(35, i, `${titled(c)} Wool`, `wool:${c}`, 'Colored'));
  if (modern) {
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
  // Decoration
  add(85, 0, modern ? 'Oak Fence' : 'Fence', 'fence', 'Decoration');
  if (modern)
    [[188, 'spruce'], [189, 'birch'], [190, 'jungle'], [192, 'acacia'], [191, 'dark_oak']].forEach(([id, w]) =>
      add(id as number, 0, `${titled(w as string)} Fence`, `${w}_fence`, 'Decoration'),
    );
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
  if (modern) {
    add(139, 0, 'Cobblestone Wall', 'cobblestone_wall', 'Decoration');
    add(176, 0, 'Banner', 'standing_banner', 'Decoration', 'rotation16', 'banner');
    add(145, 0, 'Anvil', 'anvil', 'Decoration', 'horizontal', 'anvil_intact');
    add(169, 0, 'Sea Lantern', 'sea_lantern', 'Decoration');
    add(198, 1, 'End Rod', 'end_rod', 'Decoration', 'facing6');
    add(140, 0, 'Flower Pot', 'flower_pot', 'Decoration', 'none', 'flower_pot');
  }
  // Nature
  if (modern) add(2, 0, 'Grass Block', 'grass', 'Nature');
  add(3, 0, 'Dirt', 'dirt', 'Nature');
  for (const [id, spec, name] of [[12, 'sand', 'Sand'], [13, 'gravel', 'Gravel'], [82, 'clay', 'Clay'], [87, 'netherrack', 'Netherrack'], [88, 'soul_sand', 'Soul Sand'], [80, 'snow', 'Snow Block'], [86, 'pumpkin', 'Pumpkin'], [103, 'melon_block', 'Melon'], [81, 'cactus', 'Cactus'], [37, 'yellow_flower', 'Dandelion']] as const)
    add(id, 0, name, spec, 'Nature', id === 86 ? 'horizontal' : 'none');
  (modern ? WOODS : WOODS.slice(0, 3)).forEach((w, i) => add(6, i, `${titled(w)} Sapling`, `sapling:${w}`, 'Nature'));
  if (modern) {
    WOODS.slice(0, 4).forEach((w, i) => add(18, i + 4, `${titled(w)} Leaves`, `leaves:${w}`, 'Nature', 'leaves'));
    ['poppy', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'].forEach((w, i) =>
      add(38, i, w === 'houstonia' ? 'Azure Bluet' : titled(w), `red_flower:${w}`, 'Nature'),
    );
    ['sunflower', 'syringa', 'double_grass', 'double_fern', 'double_rose', 'paeonia'].forEach((w, i) =>
      add(175, i, ['Sunflower', 'Lilac', 'Tall Grass (double)', 'Large Fern', 'Rose Bush', 'Peony'][i]!, `double_plant:${w}`, 'Nature', 'tall'),
    );
    add(79, 0, 'Ice', 'ice', 'Nature');
  } else add(38, 0, 'Rose', 'red_flower', 'Nature');
  add(31, 1, modern ? 'Grass' : 'Tall Grass', 'tallgrass:grass', 'Nature');
  add(83, 0, 'Sugar Cane', modern ? 'reeds' : 'sugar_cane', 'Nature', 'age', modern ? 'reeds' : 'item:27');
  add(106, 1, 'Vines', 'vine', 'Nature', 'vine');
  add(59, 0, 'Wheat Crops', 'wheat', 'Nature', 'age', modern ? 'wheat_seeds' : 'item:9');
  // Redstone
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
  add(64, 0, modern ? 'Oak Door' : 'Wooden Door', 'wooden_door', 'Redstone', 'door', modern ? 'oak_door' : 'item:43');
  if (modern)
    [[193, 'spruce'], [194, 'birch'], [195, 'jungle'], [196, 'acacia'], [197, 'dark_oak']].forEach(([id, w]) =>
      add(id as number, 0, `${titled(w as string)} Door`, `${w}_door`, 'Redstone', 'door', `${w}_door`),
    );
  add(71, 0, 'Iron Door', 'iron_door', 'Redstone', 'door', modern ? 'iron_door' : 'item:44');
  add(96, 0, modern ? 'Oak Trapdoor' : 'Trapdoor', 'trapdoor', 'Redstone', 'trapdoor');
  add(107, 0, modern ? 'Oak Fence Gate' : 'Fence Gate', 'fence_gate', 'Redstone', 'gate');
  if (modern) {
    add(149, 0, 'Redstone Comparator', 'unpowered_comparator', 'Redstone', 'horizontal', 'comparator');
    add(123, 0, 'Redstone Lamp', 'redstone_lamp', 'Redstone');
    add(152, 0, 'Block of Redstone', 'redstone_block', 'Redstone');
    add(218, 3, 'Observer', 'observer', 'Redstone', 'facing6');
    add(154, 0, 'Hopper', 'hopper', 'Redstone', 'hopper', 'hopper');
  }
  // Transport and misc
  add(66, 0, 'Rail', 'rail', 'Transport', 'rail');
  add(27, 0, 'Powered Rail', 'golden_rail', 'Transport', 'poweredrail');
  add(28, 0, 'Detector Rail', 'detector_rail', 'Transport', 'poweredrail');
  if (modern) add(157, 0, 'Activator Rail', 'activator_rail', 'Transport', 'poweredrail');
  add(9, 0, 'Water (source)', 'water', 'Misc');
  add(11, 0, 'Lava (source)', 'lava', 'Misc');
  if (modern) add(138, 0, 'Beacon', 'beacon', 'Misc');
  return out;
}

export const DESIGNER_CATEGORIES = ['Building', 'Decoration', 'Colored', 'Nature', 'Redstone', 'Transport', 'Misc'] as const;

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
  let same = 0;
  for (const c of cells) {
    if (from === to || to === '1.12.2') {
      out.push(c);
      same++;
      continue;
    }
    const [x, y, z, id, meta] = c;
    if (id === 95) {
      out.push([x, y, z, 20, 0]);
      substituted.set(`${name(id, meta)} → Glass`, (substituted.get(`${name(id, meta)} → Glass`) ?? 0) + 1);
    } else if (id === 5 && meta > 0) {
      out.push([x, y, z, 5, 0]);
      substituted.set(`${name(id, meta)} → Wooden Planks`, (substituted.get(`${name(id, meta)} → Wooden Planks`) ?? 0) + 1);
    } else if (id > 109) missing.set(name(id, meta), (missing.get(name(id, meta)) ?? 0) + 1);
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
  /** The game version it is drawn for. */
  version?: string;
}

type MockSession = DesignerSession & { cells: Map<string, DesignerCell>; holes: Set<string> };

/**
 * The whole studio in memory for the mock modes (MSW and Nest's mock bridge share it; Nest snake-cases its replies): sessions,
 * the fake designer's turns, user edits, saves as designs with revisions. Never used against a real bot.
 */
export function createDesignerMock(
  firstDesignId = 100,
  /** Keeps the studio across reloads (the browser mock: sessionStorage), like the bot's db keeps sessions. */
  persist?: { load(): string | null; save(json: string): void },
) {
  const sessions = new Map<number, MockSession>();
  const saved: MockSavedDesign[] = [];
  let nextSession = 1;
  let nextDesign = firstDesignId;
  type Stored = { sessions: Array<MockSession & { cellList: DesignerCell[]; holeList: string[] }>; saved: MockSavedDesign[]; nextSession: number; nextDesign: number };
  try {
    const raw = persist?.load();
    if (raw) {
      const st = JSON.parse(raw) as Stored;
      for (const s of st.sessions) {
        const { cellList, holeList, ...rest } = s;
        sessions.set(s.id, { ...rest, cells: new Map(cellList.map((c) => [`${c[0]},${c[1]},${c[2]}`, c] as const)), holes: new Set(holeList) });
      }
      saved.push(...st.saved);
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
        nextSession,
        nextDesign,
      };
      persist.save(JSON.stringify(st));
    } catch {
      /* storage full or blocked: the mock just forgets on reload */
    }
  };
  const key = (c: readonly number[]) => `${c[0]},${c[1]},${c[2]}`;
  const apply = (s: MockSession, cells: readonly DesignerCell[]): DesignerCell[] => {
    const changed: DesignerCell[] = [];
    for (const c of cells) {
      if (
        c[0] < 0 ||
        c[0] >= DESIGNER_PLOT.size ||
        c[2] < 0 ||
        c[2] >= DESIGNER_PLOT.size ||
        c[1] < -DESIGNER_PLOT.depth ||
        c[1] >= DESIGNER_PLOT.height
      )
        continue;
      const k = key(c);
      const old = s.cells.get(k);
      const below = c[1] < 0;
      if (c[3] === 0 || c[3] === DESIGNER_EARTH) {
        // Below ground: air digs the cell out, earth fills it back; above it both are air.
        const dig = below && c[3] === 0;
        const was = old ? 'block' : below && !s.holes.has(k) ? 'earth' : 'air';
        const now = dig ? 'air' : below ? 'earth' : 'air';
        if (was === now) continue;
        s.cells.delete(k);
        if (dig) s.holes.add(k);
        else s.holes.delete(k);
        changed.push([c[0], c[1], c[2], below && !dig ? DESIGNER_EARTH : 0, 0]);
        continue;
      } else {
        if (old && old[3] === c[3] && old[4] === c[4]) continue;
        s.cells.set(k, [c[0], c[1], c[2], c[3], c[4]]);
        s.holes.delete(k);
      }
      changed.push([c[0], c[1], c[2], c[3], c[4]]);
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
  /** The session's version and the palette, categories and server compatibility that follow from it. */
  const setVersion = (s: MockSession, version: string) => {
    s.version = version;
    s.palette = mockDesignerPalette(version);
    s.categories = [...DESIGNER_CATEGORIES];
    s.serverVersion = MOCK_SERVER_VERSION;
    s.versions = ['b1.8.1', '1.12.2'];
  };
  function make(
    body: { model?: string; designId?: number; version?: string; convertTo?: string },
    owner: string,
    forceId?: number,
  ): MockSession {
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
      holes: new Set<string>(d?.dug ?? []),
    };
    setVersion(s, d?.version ?? body.version ?? MOCK_SERVER_VERSION);
    let blocks = d?.blocks ?? [];
    if (d && body.convertTo && body.convertTo !== s.version) {
      const conv = mockConvert(blocks, s.version, body.convertTo);
      blocks = conv.cells;
      s.compat = conv.report;
      setVersion(s, body.convertTo);
    }
    for (const c of blocks) s.cells.set(key(c), c);
    sessions.set(id, s);
    return s;
  }
  const get = (id: number): MockSession => sessions.get(id) ?? make({}, 'poofimafox', id);
  const api = {
    saved,
    models: (): DesignerModelsResponse => ({
      models: MOCK_DESIGNER_MODELS,
      default: 'main',
      version: MOCK_SERVER_VERSION,
      versions: ['b1.8.1', '1.12.2'],
      budget: { mode: 'normal', todayUsd: 1.24, dailyCapUsd: 50 },
    }),
    /** The canvas's version: an empty one switches; a full one is checked, and converted with `apply`. */
    version: (id: number, body: { version: string; apply?: boolean }): DesignerVersionResponse => {
      const s = get(id);
      const target = body.version;
      if (target === s.version) return { version: s.version, target, changed: false, report: null, session: view(s) };
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
    create: (body: { model?: string; designId?: number; version?: string; convertTo?: string }, owner: string): DesignerSession => {
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
      if (s.cells.size === 0 && s.holes.size === 0) throw new Error('the canvas is empty');
      let d = !body.asNew && s.designId ? saved.find((x) => x.id === s.designId) : undefined;
      const created = !d;
      if (!d) {
        d = { id: nextDesign++, name: body.name, requester: owner, blocks: [], revisions: [], updated: Date.now() };
        saved.unshift(d);
      }
      d.name = body.name;
      d.blocks = [...s.cells.values()];
      d.dug = [...s.holes];
      d.version = s.version;
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
        version: s.version,
      });
      s.designId = d.id;
      s.name = body.name;
      const text = `${created ? 'saved as design' : 'saved'} #${d.id} r${rev} (${s.version}): ${body.name}, ${d.blocks.length} blocks`;
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
