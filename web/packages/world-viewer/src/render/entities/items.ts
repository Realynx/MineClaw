// What an item looks like when it is held or dropped: a flat sprite (an icon cell of the items atlas or a block texture
// cell of the blocks atlas) or a small textured cube (block items), resolved from a held-item name ("stone pickaxe",
// "cobblestone x12") or a numeric id + damage, for each version.
//
// 1.12.2: the packed item models (item/<name> → parent chain → item/generated layer0, or block/<x> → its element faces and
// textures, tint indices → grass/foliage tint). b1.8.1: Item icon indices into gui/items.png, block terrain tiles per face
// (the vanilla Block.getBlockTextureFromSideAndMetadata values), sprites for the cross/flat render types.

import type { Pack, Pack1122 } from '../../assets/bundle';
import { BLOCK_NAMES_1122, FOLIAGE_TINT, GRASS_TINT, type GameVersion } from '../../world/blocks';

export type ItemLook =
  | {
      kind: 'sprite';
      atlas: 'items' | 'blocks';
      cell: number;
      tint: number;
      /** A layer drawn beneath the sprite by icon renderers (a potion's tinted liquid under its bottle); the 3D view ignores it. */
      under?: { atlas: 'items' | 'blocks'; cell: number; tint: number };
      /**
       * Icon renderers draw this pixel rect of the bundled entity sheet instead (x, y, w, h): a `builtin/entity` item with no
       * sprite of its own (the shield's front face). `atlas` / `cell` stay a usable stand-in for the 3D view.
       */
      entityRect?: [number, number, number, number];
    }
  /** Six terrain cells in face order down, up, north, south, west, east; `tintMask` bit f = face f takes `tint`. */
  | { kind: 'cube'; faces: [number, number, number, number, number, number]; tint: number; tintMask: number };

/** Item names by id (256..453 and records), the same keys both versions' tables use. */
const ITEMS_256 =
  'iron_shovel iron_pickaxe iron_axe flint_and_steel apple bow arrow coal diamond iron_ingot gold_ingot iron_sword wooden_sword wooden_shovel wooden_pickaxe wooden_axe stone_sword stone_shovel stone_pickaxe stone_axe diamond_sword diamond_shovel diamond_pickaxe diamond_axe stick bowl mushroom_stew golden_sword golden_shovel golden_pickaxe golden_axe string feather gunpowder wooden_hoe stone_hoe iron_hoe diamond_hoe golden_hoe wheat_seeds wheat bread leather_helmet leather_chestplate leather_leggings leather_boots chainmail_helmet chainmail_chestplate chainmail_leggings chainmail_boots iron_helmet iron_chestplate iron_leggings iron_boots diamond_helmet diamond_chestplate diamond_leggings diamond_boots golden_helmet golden_chestplate golden_leggings golden_boots flint porkchop cooked_porkchop painting golden_apple sign wooden_door bucket water_bucket lava_bucket minecart saddle iron_door redstone snowball boat leather milk_bucket brick clay_ball reeds paper book slime_ball chest_minecart furnace_minecart egg compass fishing_rod clock glowstone_dust fish cooked_fish dye bone sugar cake bed repeater cookie filled_map shears melon pumpkin_seeds melon_seeds beef cooked_beef chicken cooked_chicken rotten_flesh ender_pearl blaze_rod ghast_tear gold_nugget nether_wart potion glass_bottle spider_eye fermented_spider_eye blaze_powder magma_cream brewing_stand cauldron ender_eye speckled_melon spawn_egg experience_bottle fire_charge writable_book written_book emerald item_frame flower_pot carrot potato baked_potato poisonous_potato map golden_carrot skull carrot_on_a_stick nether_star pumpkin_pie fireworks firework_charge enchanted_book comparator netherbrick quartz tnt_minecart hopper_minecart prismarine_shard prismarine_crystals rabbit cooked_rabbit rabbit_stew rabbit_foot rabbit_hide armor_stand iron_horse_armor golden_horse_armor diamond_horse_armor lead name_tag command_block_minecart mutton cooked_mutton banner end_crystal spruce_door birch_door jungle_door acacia_door dark_oak_door chorus_fruit chorus_fruit_popped beetroot beetroot_seeds beetroot_soup dragon_breath splash_potion spectral_arrow tipped_arrow lingering_potion shield elytra spruce_boat birch_boat jungle_boat acacia_boat dark_oak_boat totem_of_undying shulker_shell  iron_nugget knowledge_book'.split(
    ' ',
  );
const RECORDS = '13 cat blocks chirp far mall mellohi stal strad ward 11 wait'.split(' ');

export function itemName(id: number): string | null {
  if (id > 0 && id < 256) return BLOCK_NAMES_1122[id] || null;
  if (id >= 256 && id < 256 + ITEMS_256.length) return ITEMS_256[id - 256] || null;
  if (id >= 2256 && id < 2256 + RECORDS.length) return `record_${RECORDS[id - 2256]}`;
  return null;
}

const NAME_TO_ID = (() => {
  const m = new Map<string, number>();
  ITEMS_256.forEach((n, i) => n && m.set(n, 256 + i));
  BLOCK_NAMES_1122.forEach((n, i) => n && i > 0 && !m.has(n) && m.set(n, i));
  RECORDS.forEach((r, i) => m.set(`record_${r}`, 2256 + i));
  // Names the wire (b1.8.1 Items/Blocks tables) or people use for the same things.
  for (const [k, v] of Object.entries({ map: 358, seeds: 295, wood_pickaxe: 270, gold_pickaxe: 285, sugar_cane: 338, slimeball: 341, raw_fish: 349, steak: 364 })) m.set(k, v);
  return m;
})();

/** "stone pickaxe x12" → { name: "stone_pickaxe", count: 12 }. */
export function parseHeld(held: string): { name: string; count: number } {
  const m = /^(.*?)(?:\s+x(\d+))?$/i.exec(held.trim());
  const name = (m?.[1] ?? held).trim().toLowerCase().replace(/\s+/g, '_');
  return { name, count: m?.[2] ? Number(m[2]) : 1 };
}

export function itemIdByName(name: string): number | null {
  return NAME_TO_ID.get(name) ?? null;
}

// ------------------------------------------------------------------------------------------------------------ 1.12.2

const DYES = 'white orange magenta light_blue yellow lime pink gray silver cyan purple blue brown green red black'.split(' ');
const DYE_ITEMS = 'black red green brown blue purple cyan silver gray pink lime yellow light_blue magenta orange white'.split(' ');
const WOODS = 'oak spruce birch jungle acacia dark_oak'.split(' ');

/** `names[d]`, or `fallback` past the list. */
const nth = (names: readonly string[], fallback: string) => (d: number) => names[d] ?? fallback;
const woods = (suffix: string, index: (d: number) => number) => (d: number) => `${WOODS[index(d)]}_${suffix}`;
const dyed = (suffix: string) => (d: number) => `${DYES[d]}_${suffix}`;

/** 1.12.2 item model names of variant stacks: id → model name by damage (& 15). */
const VARIANTS_1122 = new Map<number, (d: number) => string>([
  [1, nth(['stone', 'granite', 'granite_smooth', 'diorite', 'diorite_smooth', 'andesite', 'andesite_smooth'], 'stone')],
  [3, nth(['dirt', 'coarse_dirt', 'podzol'], 'dirt')],
  [5, woods('planks', (d) => d % 6)],
  [6, woods('sapling', (d) => d % 6)],
  [12, (d) => (d === 1 ? 'red_sand' : 'sand')],
  [17, woods('log', (d) => d & 3)],
  [162, woods('log', (d) => 4 + (d & 1))],
  [18, woods('leaves', (d) => d & 3)],
  [161, woods('leaves', (d) => 4 + (d & 1))],
  [24, nth(['sandstone', 'chiseled_sandstone', 'smooth_sandstone'], 'sandstone')],
  [31, (d) => (d === 2 ? 'fern' : 'tall_grass')],
  [35, dyed('wool')],
  [95, dyed('stained_glass')],
  [159, dyed('stained_hardened_clay')],
  [171, dyed('carpet')],
  [251, dyed('concrete')],
  [252, dyed('concrete_powder')],
  [38, nth(['poppy', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'], 'poppy')],
  [37, () => 'dandelion'],
  [44, (d) => ['stone_slab', 'sandstone_slab', 'stone_slab', 'cobblestone_slab', 'brick_slab', 'stone_brick_slab', 'nether_brick_slab', 'quartz_slab'][d & 7]],
  [126, woods('slab', (d) => d % 6)],
  [98, (d) => ['stonebrick', 'mossy_stonebrick', 'cracked_stonebrick', 'chiseled_stonebrick'][d & 3]],
  [175, nth(['sunflower', 'syringa', 'double_grass', 'double_fern', 'double_rose', 'paeonia'], 'sunflower')],
  [145, (d) => ['anvil_intact', 'anvil_slightly_damaged', 'anvil_very_damaged'][(d >> 2) & 3] ?? 'anvil_intact'],
  [263, (d) => (d === 1 ? 'charcoal' : 'coal')],
  [349, (d) => ['cod', 'salmon', 'clownfish', 'pufferfish'][d & 3]],
  [350, (d) => (d === 1 ? 'cooked_salmon' : 'cooked_cod')],
  [351, (d) => `dye_${DYE_ITEMS[d]}`],
  [397, nth(['skull_skeleton', 'skull_wither', 'skull_zombie', 'skull_char', 'skull_creeper', 'skull_dragon'], 'skull_skeleton')],
]);

/** 1.12.2 item model names of variant stacks (id → by damage). */
function variant1122(id: number, damage: number): string | null {
  return VARIANTS_1122.get(id)?.(damage & 15) ?? null;
}
const ALIASES_1122: Record<string, string> = {
  yellow_flower: 'dandelion',
  red_flower: 'poppy',
  fence: 'oak_fence',
  fence_gate: 'oak_fence_gate',
  wooden_door: 'oak_door',
  boat: 'oak_boat',
  tallgrass: 'tall_grass',
  wooden_slab: 'oak_slab',
  log: 'oak_log',
  log2: 'acacia_log',
  planks: 'oak_planks',
  sapling: 'oak_sapling',
  leaves: 'oak_leaves',
  leaves2: 'acacia_leaves',
  wool: 'white_wool',
  stained_glass: 'white_stained_glass',
  carpet: 'white_carpet',
  stained_hardened_clay: 'white_stained_hardened_clay',
  concrete: 'white_concrete',
  dye: 'dye_black',
  fish: 'cod',
  cooked_fish: 'cooked_cod',
  skull: 'skull_skeleton',
  anvil: 'anvil_intact',
  double_plant: 'sunflower',
  monster_egg: 'stone_monster_egg',
  sponge: 'sponge',
  map: 'map',
  sugar_cane: 'reeds',
  lit_furnace: 'furnace',
  double_stone_slab: 'stone_slab',
  // 1.12.2 potions have no item/potion model: the bottle models (potion_overlay liquid under the bottle).
  potion: 'bottle_drinkable',
  splash_potion: 'bottle_splash',
  lingering_potion: 'bottle_lingering',
};

/** A potion's liquid when the stack does not say its effect (1.12.2 keeps it in NBT): vanilla's water colour. */
export const POTION_TINT = 0x385dc6;

interface ModelJson {
  parent?: string;
  textures?: Record<string, string>;
  elements?: Array<{ from?: number[]; to?: number[]; faces?: Record<string, { texture?: string; tintindex?: number }> }>;
}

const FACE_NAMES = ['down', 'up', 'north', 'south', 'west', 'east'];

function resolveTex(tex: Record<string, string>, ref: string | undefined): string | null {
  let r = ref;
  for (let i = 0; i < 8 && r && r.startsWith('#'); i++) r = tex[r.slice(1)];
  return r && !r.startsWith('#') ? r : null;
}

/** Parents that make an item a flat generated sprite. */
const GENERATED_PARENTS = new Set(['item/generated', 'item/handheld', 'builtin/generated', 'item/handheld_rod']);

/** An item model with its parent chain folded in: the textures (nearest first), the first elements, and whether it ends in a
 * generated sprite. */
interface ResolvedModel {
  textures: Record<string, string>;
  elements: ModelJson['elements'] | undefined;
  generated: boolean;
}

function resolveModel(pack: Pack1122, first: ModelJson): ResolvedModel {
  const models = pack.itemModels as Record<string, ModelJson>, blocks = pack.models as Record<string, ModelJson>;
  const out: ResolvedModel = { textures: {}, elements: undefined, generated: false };
  let cur: ModelJson | undefined = first;
  for (let guard = 0; cur && guard < 16; guard++) {
    mergeTextures(out.textures, cur.textures);
    if (!out.elements && cur.elements) out.elements = cur.elements;
    const parent: string | undefined = cur.parent;
    if (!parent || parent === 'builtin/entity') break;
    if (GENERATED_PARENTS.has(parent)) {
      out.generated = true;
      break;
    }
    cur = parent.startsWith('block/') ? blocks[parent] : models[parent];
  }
  return out;
}

/** Adds the texture variables a child model doesn't already define. */
function mergeTextures(into: Record<string, string>, textures: Record<string, string> | undefined): void {
  if (!textures) return;
  for (const [k, v] of Object.entries(textures)) if (!(k in into)) into[k] = v;
}

/** A texture's cell in the items atlas, else the blocks atlas. */
function cellOf(pack: Pack1122, name: string | null): { atlas: 'items' | 'blocks'; cell: number } | null {
  if (!name) return null;
  const it = pack.items.tiles[name];
  if (it) return { atlas: 'items', cell: it[1] * pack.items.columns + it[0] };
  const bl = pack.blocks.tiles[name];
  if (bl) return { atlas: 'blocks', cell: bl[1] * pack.blocks.columns + bl[0] };
  return null;
}

/** Follows an item model's parent chain: a generated sprite (layer0) or the block model's cube faces. */
export function look1122(pack: Pack1122, modelName: string): ItemLook | null {
  const first = (pack.itemModels as Record<string, ModelJson>)[`item/${modelName}`];
  if (!first) return null;
  const { textures, elements, generated } = resolveModel(pack, first);
  if (generated || (!elements && textures.layer0)) return spriteLook(pack, textures, modelName);
  if (elements?.length) return cubeLook(pack, textures, elements, modelName);
  return builtinLook(pack, modelName);
}

/** item/generated: layer0 as a sprite (grass and leaves tinted); potions and tipped arrows draw layer1 over the liquid. */
function spriteLook(pack: Pack1122, textures: Record<string, string>, modelName: string): ItemLook | null {
  const layer0 = resolveTex(textures, textures.layer0 ?? '#layer0');
  const layer1 = textures.layer1 ? resolveTex(textures, textures.layer1) : null;
  // Potions (and tipped arrows): layer0 is the tinted liquid, layer1 the bottle drawn over it.
  if (layer0 === 'items/potion_overlay' && layer1) {
    const top = cellOf(pack, layer1), liquid = cellOf(pack, layer0);
    if (top) return { kind: 'sprite', atlas: top.atlas, cell: top.cell, tint: 0, ...(liquid ? { under: { ...liquid, tint: POTION_TINT } } : {}) };
  }
  const c = cellOf(pack, layer0);
  if (!c) return null;
  return { kind: 'sprite', atlas: c.atlas, cell: c.cell, tint: spriteTint(modelName) };
}

/** Grass, ferns, vines, leaves and lily pads take the biome tint (leaves the foliage one); dead plants don't. */
function spriteTint(modelName: string): number {
  if (!/grass|fern|vine|leaves|lily/.test(modelName) || /dead/.test(modelName)) return 0;
  return modelName.includes('leaves') ? FOLIAGE_TINT : GRASS_TINT;
}

/** The largest element stands for the block (stairs/slabs/fences become a mini cube: close enough at item size). */
function largestElement(elements: NonNullable<ModelJson['elements']>): NonNullable<ModelJson['elements']>[number] {
  let best = elements[0], vol = -1;
  for (const e of elements) {
    const f = e.from ?? [0, 0, 0], t = e.to ?? [16, 16, 16];
    const v = Math.abs((t[0] - f[0]) * (t[1] - f[1]) * (t[2] - f[2]));
    if (v > vol) {
      vol = v;
      best = e;
    }
  }
  return best;
}

/** A block item as a cube: the largest element's face textures (blocks atlas), tinted faces by their tint index. */
function cubeLook(pack: Pack1122, textures: Record<string, string>, elements: NonNullable<ModelJson['elements']>, modelName: string): ItemLook | null {
  const best = largestElement(elements);
  const faces: number[] = [];
  let mask = 0;
  for (let f = 0; f < 6; f++) {
    const face = elementFace(best, f);
    faces.push(faceTile(pack, textures, face));
    if (face?.tintindex != null && face.tintindex >= 0) mask |= 1 << f;
  }
  if (faces.every((f) => f < 0)) return null;
  const fill = faces.find((f) => f >= 0)!;
  const tint = mask ? (/leaves/.test(modelName) ? FOLIAGE_TINT : GRASS_TINT) : 0;
  return { kind: 'cube', faces: faces.map((f) => (f < 0 ? fill : f)) as [number, number, number, number, number, number], tint, tintMask: mask };
}

type ElementFace = NonNullable<NonNullable<ModelJson['elements']>[number]['faces']>[string];

/** An element's face `f` (down, up, north, south, west, east), else its first face. */
function elementFace(e: NonNullable<ModelJson['elements']>[number], f: number): ElementFace | undefined {
  return e.faces?.[FACE_NAMES[f]] ?? Object.values(e.faces ?? {})[0];
}

/** The face's texture as a blocks-atlas cell (−1 = none: items-atlas or missing textures). */
function faceTile(pack: Pack1122, textures: Record<string, string>, face: ElementFace | undefined): number {
  const c = cellOf(pack, resolveTex(textures, face?.texture ?? '#particle'));
  return c && c.atlas === 'blocks' ? c.cell : -1;
}

/** builtin/entity (chests, banners, shields, skulls): a plank cube for chests, the shield's front face, else nothing. */
function builtinLook(pack: Pack1122, modelName: string): ItemLook | null {
  if (/chest/.test(modelName)) {
    const c = cellOf(pack, 'blocks/planks_oak');
    if (c) return { kind: 'cube', faces: [c.cell, c.cell, c.cell, c.cell, c.cell, c.cell], tint: 0, tintMask: 0 };
  }
  if (modelName !== 'shield' && modelName !== 'shield_blocking') return null;
  // ModelShield's plate (12×22×1 at texture offset 0,0): its front face is the 64×64 texture's (1, 1, 12, 22). The 3D view
  // keeps the GUI's shield outline sprite as its stand-in.
  const r = pack.entity.rects['entity/shield_base_nopattern'];
  const slot = cellOf(pack, 'items/empty_armor_slot_shield');
  if (!r || !slot) return null;
  const k = r[2] / 64;
  return { kind: 'sprite', atlas: slot.atlas, cell: slot.cell, tint: 0, entityRect: [r[0] + k, r[1] + k, 12 * k, 22 * k] };
}
// ------------------------------------------------------------------------------------------------------------ b1.8.1

/** b1.8.1 Item icon indices (gui/items.png cell = y·16 + x) of items 256+. */
const ICONS_BETA: Record<number, number> = {
  256: 82, 257: 98, 258: 114, 259: 5, 260: 10, 261: 21, 262: 37, 263: 7, 264: 55, 265: 23, 266: 39, 267: 66, 268: 64, 269: 80, 270: 96, 271: 112, 272: 65, 273: 81,
  274: 97, 275: 113, 276: 67, 277: 83, 278: 99, 279: 115, 280: 53, 281: 71, 282: 72, 283: 68, 284: 84, 285: 100, 286: 116, 287: 8, 288: 24, 289: 40, 290: 128,
  291: 129, 292: 130, 293: 131, 294: 132, 295: 9, 296: 25, 297: 41, 298: 0, 299: 16, 300: 32, 301: 48, 302: 1, 303: 17, 304: 33, 305: 49, 306: 2, 307: 18,
  308: 34, 309: 50, 310: 3, 311: 19, 312: 35, 313: 51, 314: 4, 315: 20, 316: 36, 317: 52, 318: 6, 319: 87, 320: 88, 321: 26, 322: 11, 323: 42, 324: 43, 325: 74,
  326: 75, 327: 76, 328: 135, 329: 104, 330: 44, 331: 56, 332: 14, 333: 136, 334: 103, 335: 77, 336: 22, 337: 57, 338: 27, 339: 58, 340: 59, 341: 30, 342: 151,
  343: 167, 344: 12, 345: 54, 346: 69, 347: 70, 348: 73, 349: 89, 350: 90, 351: 78, 352: 28, 353: 13, 354: 29, 355: 45, 356: 86, 357: 92, 358: 60, 359: 93,
  360: 109, 361: 61, 362: 62, 363: 105, 364: 106, 365: 121, 366: 122, 367: 91, 368: 107, 2256: 240, 2257: 241,
};

const WOOL_BETA = [64, 210, 194, 178, 162, 146, 130, 114, 225, 209, 193, 177, 161, 145, 129, 113];

/** Blocks the b1.8.1 item renderer draws flat (cross / flat / special render types) and their terrain tile. */
const FLAT_BETA: Record<number, (meta: number) => number> = {
  6: (m) => [15, 63, 79][m & 3] ?? 15,
  27: (m) => ((m & 8) !== 0 ? 179 : 163),
  28: () => 195,
  30: () => 11,
  31: (m) => [55, 39, 56][m & 3] ?? 39,
  32: () => 55,
  37: () => 13,
  38: () => 12,
  39: () => 29,
  40: () => 28,
  50: () => 80,
  51: () => 31,
  55: () => 164,
  59: (m) => 88 + Math.min(7, m),
  63: () => 4,
  64: () => 97,
  65: () => 83,
  66: () => 128,
  68: () => 4,
  69: () => 96,
  71: () => 98,
  75: () => 115,
  76: () => 99,
  83: () => 73,
  101: () => 85,
  102: () => 49,
  106: () => 143,
};

type Faces = [number, number, number, number, number, number];
/** One tile on every face. */
const all = (t: number): Faces => [t, t, t, t, t, t];
/** Top (and bottom, unless given) and sides. */
const tb = (top: number, side: number, bottom = top): Faces => [bottom, top, side, side, side, side];
/** Top and bottom, sides, and a front face (south: the item view's front). */
const front = (top: number, side: number, face: number): Faces => [top, top, side, face, side, side];

/** b1.8.1 blocks with one terrain tile on every face: [id, tile]. */
const CUBE_ALL_BETA: ReadonlyArray<readonly [number, number]> = [
  [1, 1], [3, 2], [4, 16], [5, 4], [7, 17], [12, 18], [13, 19], [14, 32], [15, 33], [16, 34], [19, 48], [20, 49], [21, 160],
  [22, 144], [25, 74], [41, 23], [42, 22], [45, 7], [48, 36], [49, 37], [52, 65], [53, 4], [56, 50], [57, 24], [67, 16],
  [73, 51], [74, 51], [78, 66], [80, 66], [79, 67], [82, 72], [85, 4], [87, 103], [88, 104], [89, 105], [96, 84], [107, 4],
  [108, 7], [109, 54],
];

/** Slabs (and double slabs) by material: stone, sandstone, wood, cobblestone ([top, side]). */
const SLABS_BETA: ReadonlyArray<readonly [number, number]> = [[6, 5], [176, 192], [4, 4], [16, 16]];

/** Terrain tiles [down, up, north, south, west, east] of the b1.8.1 blocks drawn as a cube, by id (from the metadata). */
const CUBES_BETA = new Map<number, (m: number) => Faces>([
  ...CUBE_ALL_BETA.map(([id, t]) => [id, () => all(t)] as const),
  [2, () => tb(0, 3, 2)],
  [17, (m) => tb(21, [20, 116, 117][m & 3] ?? 20)],
  [18, (m) => all((m & 3) === 1 ? 132 : 52)],
  [23, () => front(62, 45, 46)],
  [24, () => tb(176, 192, 208)],
  [35, (m) => all(WOOL_BETA[m & 15])],
  [43, (m) => tb(...SLABS_BETA[m & 3])],
  [44, (m) => tb(...SLABS_BETA[m & 3])],
  [46, () => tb(9, 8, 10)],
  [47, () => tb(4, 35)],
  [54, () => front(25, 26, 27)],
  [58, () => [4, 43, 60, 59, 60, 59]],
  [60, (m) => tb(m > 0 ? 86 : 87, 2, 2)],
  [61, () => front(62, 45, 44)],
  [62, () => front(62, 45, 61)],
  [81, () => tb(69, 70, 71)],
  [84, () => tb(75, 74)],
  [86, () => front(102, 118, 119)],
  [91, () => front(102, 118, 120)],
  [98, (m) => all([54, 100, 101][m & 3] ?? 54)],
  [103, () => tb(137, 136)],
]);

/** Terrain tiles [down, up, north, south, west, east] of a b1.8.1 block drawn as a cube (−1 = no tile). */
function cubeBeta(id: number, m: number): Faces | null {
  return CUBES_BETA.get(id)?.(m) ?? null;
}
function lookBeta(id: number, damage: number): ItemLook | null {
  if (id <= 0) return null;
  if (id < 256) return blockLookBeta(id, damage);
  if (id === 351) return { kind: 'sprite', atlas: 'items', cell: 78 + ((damage & 15) % 8) * 16 + Math.floor((damage & 15) / 8), tint: 0 };
  const icon = ICONS_BETA[id];
  return icon == null ? null : { kind: 'sprite', atlas: 'items', cell: icon, tint: 0 };
}

/** A b1.8.1 block item: a flat sprite (cross / flat render types; tall grass and vines tinted) or a cube (grass tops and
 * leaves tinted). */
function blockLookBeta(id: number, damage: number): ItemLook | null {
  const flat = FLAT_BETA[id];
  if (flat) return { kind: 'sprite', atlas: 'blocks', cell: flat(damage), tint: flatTintBeta(id, damage) };
  const faces = cubeBeta(id, damage);
  if (!faces) return null;
  const mask = id === 2 ? 1 << 1 : id === 18 ? 63 : 0;
  return { kind: 'cube', faces, tint: id === 2 ? GRASS_TINT : id === 18 ? FOLIAGE_TINT : 0, tintMask: mask };
}

function flatTintBeta(id: number, damage: number): number {
  return id === 31 && (damage & 3) !== 0 ? GRASS_TINT : id === 106 ? FOLIAGE_TINT : 0;
}

// ------------------------------------------------------------------------------------------------------------ entry points

/** An item stack by id and damage (dropped items, players' held item ids). */
export function itemLookById(version: GameVersion, pack: Pack | null, id: number, damage = 0): ItemLook | null {
  if (version === 'b1.8.1' || !pack || pack.version !== '1.12.2') return lookBeta(id, damage);
  const v = variant1122(id, damage);
  const n = itemName(id);
  for (const cand of [v, n, n ? ALIASES_1122[n] : null]) {
    if (!cand) continue;
    const l = look1122(pack, cand);
    if (l) return l;
  }
  return null;
}

/** A held item by its wire name ("stone pickaxe", "torch x3"). */
export function itemLookByName(version: GameVersion, pack: Pack | null, held: string): ItemLook | null {
  const { name } = parseHeld(held);
  if (version === '1.12.2' && pack?.version === '1.12.2') {
    for (const cand of [name, ALIASES_1122[name]]) {
      if (!cand) continue;
      const l = look1122(pack, cand);
      if (l) return l;
    }
  }
  const id = itemIdByName(name);
  return id == null ? null : itemLookById(version, pack, id, 0);
}

/** Tools, swords, hoes, rods: held diagonally like vanilla's handheld items (vs flat items held upright). */
export function isHandheld(name: string): boolean {
  return /(_sword|_pickaxe|_axe|_shovel|_hoe|^stick|^bone|fishing_rod|carrot_on_a_stick|blaze_rod)$/.test(name);
}
