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

/** 1.12.2 item model names of variant stacks (id → by damage). */
function variant1122(id: number, damage: number): string | null {
  const d = damage & 15;
  switch (id) {
    case 1:
      return ['stone', 'granite', 'granite_smooth', 'diorite', 'diorite_smooth', 'andesite', 'andesite_smooth'][d] ?? 'stone';
    case 3:
      return ['dirt', 'coarse_dirt', 'podzol'][d] ?? 'dirt';
    case 5:
      return `${WOODS[d % 6]}_planks`;
    case 6:
      return `${WOODS[d % 6]}_sapling`;
    case 12:
      return d === 1 ? 'red_sand' : 'sand';
    case 17:
      return `${WOODS[d & 3]}_log`;
    case 162:
      return `${WOODS[4 + (d & 1)]}_log`;
    case 18:
      return `${WOODS[d & 3]}_leaves`;
    case 161:
      return `${WOODS[4 + (d & 1)]}_leaves`;
    case 24:
      return ['sandstone', 'chiseled_sandstone', 'smooth_sandstone'][d] ?? 'sandstone';
    case 31:
      return d === 2 ? 'fern' : 'tall_grass';
    case 35:
      return `${DYES[d]}_wool`;
    case 95:
      return `${DYES[d]}_stained_glass`;
    case 159:
      return `${DYES[d]}_stained_hardened_clay`;
    case 171:
      return `${DYES[d]}_carpet`;
    case 251:
      return `${DYES[d]}_concrete`;
    case 252:
      return `${DYES[d]}_concrete_powder`;
    case 38:
      return ['poppy', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'][d] ?? 'poppy';
    case 37:
      return 'dandelion';
    case 44:
      return ['stone_slab', 'sandstone_slab', 'stone_slab', 'cobblestone_slab', 'brick_slab', 'stone_brick_slab', 'nether_brick_slab', 'quartz_slab'][d & 7];
    case 126:
      return `${WOODS[d % 6]}_slab`;
    case 98:
      return ['stonebrick', 'mossy_stonebrick', 'cracked_stonebrick', 'chiseled_stonebrick'][d & 3];
    case 175:
      return ['sunflower', 'syringa', 'double_grass', 'double_fern', 'double_rose', 'paeonia'][d] ?? 'sunflower';
    case 145:
      return ['anvil_intact', 'anvil_slightly_damaged', 'anvil_very_damaged'][(d >> 2) & 3] ?? 'anvil_intact';
    case 263:
      return d === 1 ? 'charcoal' : 'coal';
    case 349:
      return ['cod', 'salmon', 'clownfish', 'pufferfish'][d & 3];
    case 350:
      return d === 1 ? 'cooked_salmon' : 'cooked_cod';
    case 351:
      return `dye_${DYE_ITEMS[d]}`;
    case 397:
      return ['skull_skeleton', 'skull_wither', 'skull_zombie', 'skull_char', 'skull_creeper', 'skull_dragon'][d] ?? 'skull_skeleton';
    default:
      return null;
  }
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

/** Follows an item model's parent chain: a generated sprite (layer0) or the block model's cube faces. */
export function look1122(pack: Pack1122, modelName: string): ItemLook | null {
  const models = pack.itemModels as Record<string, ModelJson>, blocks = pack.models as Record<string, ModelJson>;
  let cur: ModelJson | undefined = models[`item/${modelName}`];
  if (!cur) return null;
  const textures: Record<string, string> = {};
  let elements: ModelJson['elements'] | undefined;
  let generated = false;
  for (let guard = 0; cur && guard < 16; guard++) {
    for (const [k, v] of Object.entries(cur.textures ?? {})) if (!(k in textures)) textures[k] = v;
    if (!elements && cur.elements) elements = cur.elements;
    const parent: string | undefined = cur.parent;
    if (!parent) break;
    if (parent === 'item/generated' || parent === 'item/handheld' || parent === 'builtin/generated' || parent === 'item/handheld_rod') {
      generated = true;
      break;
    }
    if (parent === 'builtin/entity') break;
    cur = parent.startsWith('block/') ? blocks[parent] : models[parent];
  }
  const cellOf = (name: string | null): { atlas: 'items' | 'blocks'; cell: number } | null => {
    if (!name) return null;
    const it = pack.items.tiles[name];
    if (it) return { atlas: 'items', cell: it[1] * pack.items.columns + it[0] };
    const bl = pack.blocks.tiles[name];
    if (bl) return { atlas: 'blocks', cell: bl[1] * pack.blocks.columns + bl[0] };
    return null;
  };
  if (generated || (!elements && textures.layer0)) {
    const layer0 = resolveTex(textures, textures.layer0 ?? '#layer0');
    const layer1 = textures.layer1 ? resolveTex(textures, textures.layer1) : null;
    // Potions (and tipped arrows): layer0 is the tinted liquid, layer1 the bottle drawn over it.
    if (layer0 === 'items/potion_overlay' && layer1) {
      const top = cellOf(layer1), liquid = cellOf(layer0);
      if (top) return { kind: 'sprite', atlas: top.atlas, cell: top.cell, tint: 0, ...(liquid ? { under: { ...liquid, tint: POTION_TINT } } : {}) };
    }
    const c = cellOf(layer0);
    if (!c) return null;
    const tint = /grass|fern|vine|leaves|lily/.test(modelName) && !/dead/.test(modelName) ? (modelName.includes('leaves') ? FOLIAGE_TINT : GRASS_TINT) : 0;
    return { kind: 'sprite', atlas: c.atlas, cell: c.cell, tint };
  }
  if (elements?.length) {
    // The largest element stands for the block (stairs/slabs/fences become a mini cube: close enough at item size).
    let best = elements[0], vol = -1;
    for (const e of elements) {
      const f = e.from ?? [0, 0, 0], t = e.to ?? [16, 16, 16];
      const v = Math.abs((t[0] - f[0]) * (t[1] - f[1]) * (t[2] - f[2]));
      if (v > vol) {
        vol = v;
        best = e;
      }
    }
    const faces: number[] = [];
    let mask = 0;
    for (let f = 0; f < 6; f++) {
      const face = best.faces?.[FACE_NAMES[f]] ?? Object.values(best.faces ?? {})[0];
      const c = cellOf(resolveTex(textures, face?.texture ?? '#particle'));
      faces.push(c && c.atlas === 'blocks' ? c.cell : -1);
      if (face?.tintindex != null && face.tintindex >= 0) mask |= 1 << f;
    }
    if (faces.every((f) => f < 0)) return null;
    const fill = faces.find((f) => f >= 0)!;
    const tint = mask ? (/leaves/.test(modelName) ? FOLIAGE_TINT : GRASS_TINT) : 0;
    return { kind: 'cube', faces: faces.map((f) => (f < 0 ? fill : f)) as [number, number, number, number, number, number], tint, tintMask: mask };
  }
  // builtin/entity (chests, banners, shields, skulls): a plank cube for chests, the shield's front face, else nothing.
  if (/chest/.test(modelName)) {
    const c = cellOf('blocks/planks_oak');
    if (c) return { kind: 'cube', faces: [c.cell, c.cell, c.cell, c.cell, c.cell, c.cell], tint: 0, tintMask: 0 };
  }
  if (modelName === 'shield' || modelName === 'shield_blocking') {
    // ModelShield's plate (12×22×1 at texture offset 0,0): its front face is the 64×64 texture's (1, 1, 12, 22). The 3D view
    // keeps the GUI's shield outline sprite as its stand-in.
    const r = pack.entity.rects['entity/shield_base_nopattern'];
    const slot = cellOf('items/empty_armor_slot_shield');
    if (r && slot) {
      const k = r[2] / 64;
      return { kind: 'sprite', atlas: slot.atlas, cell: slot.cell, tint: 0, entityRect: [r[0] + k, r[1] + k, 12 * k, 22 * k] };
    }
  }
  return null;
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

/** Terrain tiles [down, up, north, south, west, east] of a b1.8.1 block drawn as a cube (−1 = no tile). */
function cubeBeta(id: number, m: number): [number, number, number, number, number, number] | null {
  const all = (t: number) => [t, t, t, t, t, t] as [number, number, number, number, number, number];
  const tb = (top: number, side: number, bottom = top) => [bottom, top, side, side, side, side] as [number, number, number, number, number, number];
  const front = (top: number, side: number, face: number) => [top, top, side, face, side, side] as [number, number, number, number, number, number];
  switch (id) {
    case 1: return all(1);
    case 2: return tb(0, 3, 2);
    case 3: return all(2);
    case 4: return all(16);
    case 5: return all(4);
    case 7: return all(17);
    case 12: return all(18);
    case 13: return all(19);
    case 14: return all(32);
    case 15: return all(33);
    case 16: return all(34);
    case 17: return tb(21, [20, 116, 117][m & 3] ?? 20);
    case 18: return all((m & 3) === 1 ? 132 : 52);
    case 19: return all(48);
    case 20: return all(49);
    case 21: return all(160);
    case 22: return all(144);
    case 23: return front(62, 45, 46);
    case 24: return tb(176, 192, 208);
    case 25: return all(74);
    case 35: return all(WOOL_BETA[m & 15]);
    case 41: return all(23);
    case 42: return all(22);
    case 43:
    case 44: return [[6, 5], [176, 192], [4, 4], [16, 16]].map(([t, s]) => tb(t, s))[m & 3] ?? tb(6, 5);
    case 45: return all(7);
    case 46: return tb(9, 8, 10);
    case 47: return tb(4, 35);
    case 48: return all(36);
    case 49: return all(37);
    case 52: return all(65);
    case 53: return all(4);
    case 54: return front(25, 26, 27);
    case 56: return all(50);
    case 57: return all(24);
    case 58: return [4, 43, 60, 59, 60, 59];
    case 60: return tb(m > 0 ? 86 : 87, 2, 2);
    case 61: return front(62, 45, 44);
    case 62: return front(62, 45, 61);
    case 67: return all(16);
    case 73:
    case 74: return all(51);
    case 78:
    case 80: return all(66);
    case 79: return all(67);
    case 81: return tb(69, 70, 71);
    case 82: return all(72);
    case 84: return tb(75, 74);
    case 85: return all(4);
    case 86: return front(102, 118, 119);
    case 87: return all(103);
    case 88: return all(104);
    case 89: return all(105);
    case 91: return front(102, 118, 120);
    case 96: return all(84);
    case 98: return all([54, 100, 101][m & 3] ?? 54);
    case 103: return tb(137, 136);
    case 107: return all(4);
    case 108: return all(7);
    case 109: return all(54);
    default: return null;
  }
}

function lookBeta(id: number, damage: number): ItemLook | null {
  if (id <= 0) return null;
  if (id < 256) {
    const flat = FLAT_BETA[id];
    if (flat) return { kind: 'sprite', atlas: 'blocks', cell: flat(damage), tint: id === 31 && (damage & 3) !== 0 ? GRASS_TINT : id === 106 ? FOLIAGE_TINT : 0 };
    const faces = cubeBeta(id, damage);
    if (!faces) return null;
    const mask = id === 2 ? 1 << 1 : id === 18 ? 63 : 0;
    return { kind: 'cube', faces, tint: id === 2 ? GRASS_TINT : id === 18 ? FOLIAGE_TINT : 0, tintMask: mask };
  }
  if (id === 351) return { kind: 'sprite', atlas: 'items', cell: 78 + ((damage & 15) % 8) * 16 + Math.floor((damage & 15) / 8), tint: 0 };
  const icon = ICONS_BETA[id];
  return icon == null ? null : { kind: 'sprite', atlas: 'items', cell: icon, tint: 0 };
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
