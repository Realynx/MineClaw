// Every model and how each entity the wire can report looks, for both versions: mob type ids (0x18 / 1.12.2 Spawn Mob
// types; the b1.8.1 ids are the same numbers), object types (0x17 / Spawn Object), drops, XP orbs, players and bots.

import type { GameVersion } from '../../world/blocks';
import * as animals from './models/animals';
import * as common from './models/common';
import * as misc from './models/misc';
import type { Look, LookFn, ModelDef } from './types';

export const ALL_MODELS: ModelDef[] = [...common.MODELS, ...animals.MODELS, ...misc.MODELS];
export const MODEL_BY_ID: Record<string, ModelDef> = Object.fromEntries(ALL_MODELS.map((m) => [m.id, m]));

/** Mob type id → canonical (1.12.2 registry) name. */
export const MOB_NAMES: Record<number, string> = {
  4: 'elder_guardian', 5: 'wither_skeleton', 6: 'stray', 23: 'husk', 27: 'zombie_villager', 28: 'skeleton_horse', 29: 'zombie_horse',
  31: 'donkey', 32: 'mule', 34: 'evocation_illager', 35: 'vex', 36: 'vindication_illager', 37: 'illusion_illager', 50: 'creeper',
  51: 'skeleton', 52: 'spider', 53: 'giant', 54: 'zombie', 55: 'slime', 56: 'ghast', 57: 'zombie_pigman', 58: 'enderman',
  59: 'cave_spider', 60: 'silverfish', 61: 'blaze', 62: 'magma_cube', 63: 'ender_dragon', 64: 'wither', 65: 'bat', 66: 'witch',
  67: 'endermite', 68: 'guardian', 69: 'shulker', 90: 'pig', 91: 'sheep', 92: 'cow', 93: 'chicken', 94: 'squid', 95: 'wolf',
  96: 'mooshroom', 97: 'snowman', 98: 'ocelot', 99: 'villager_golem', 100: 'horse', 101: 'rabbit', 102: 'polar_bear', 103: 'llama',
  105: 'parrot', 120: 'villager', 200: 'ender_crystal',
};

/** Object type id → name (1.12.2; b1.8.1 shares 1, 10, 50, 60–65, 90 and has 11/12 carts, 70/71 falling sand/gravel). */
export function objectName(type: number, beta: boolean): string | null {
  if (beta) return ({ 1: 'boat', 10: 'minecart', 11: 'chest_minecart', 12: 'furnace_minecart', 50: 'tnt', 60: 'arrow', 61: 'snowball', 62: 'egg', 63: 'fireball', 65: 'ender_pearl', 70: 'falling_block', 71: 'falling_block', 90: 'fishing_hook' } as Record<number, string>)[type] ?? null;
  return ({ 1: 'boat', 2: 'item', 10: 'minecart', 50: 'tnt', 51: 'ender_crystal', 60: 'arrow', 61: 'snowball', 62: 'egg', 63: 'fireball', 64: 'small_fireball', 65: 'ender_pearl', 66: 'wither_skull', 67: 'shulker_bullet', 70: 'falling_block', 71: 'item_frame', 72: 'eye_of_ender_signal', 73: 'potion', 75: 'xp_bottle', 76: 'fireworks_rocket', 77: 'leash_knot', 78: 'armor_stand', 90: 'fishing_hook', 91: 'spectral_arrow', 93: 'dragon_fireball' } as Record<number, string>)[type] ?? null;
}

/** 1.12.2 EntitySheep wool colours (dye colour × 0.75; white 0.9). */
const FLEECE_1122 = [0xf9fffe, 0xf9801d, 0xc74ebd, 0x3ab3da, 0xfed83d, 0x80c71f, 0xf38baa, 0x474f52, 0x9d9d97, 0x169c9c, 0x8932b8, 0x3c44aa, 0x835432, 0x5e7c16, 0xb02e26, 0x1d1d21];
/** b1.8.1 EntitySheep.fleeceColorTable. */
const FLEECE_BETA: Array<[number, number, number]> = [
  [1, 1, 1], [0.95, 0.7, 0.2], [0.9, 0.5, 0.85], [0.6, 0.7, 0.95], [0.9, 0.9, 0.2], [0.5, 0.8, 0.1], [0.95, 0.7, 0.8], [0.3, 0.3, 0.3],
  [0.6, 0.6, 0.6], [0.3, 0.6, 0.7], [0.7, 0.4, 0.9], [0.2, 0.4, 0.8], [0.5, 0.4, 0.3], [0.4, 0.5, 0.2], [0.8, 0.3, 0.3], [0.1, 0.1, 0.1],
];

export function fleece(colour: number, beta: boolean): number {
  const c = colour & 15;
  if (beta) {
    const [r, g, b] = FLEECE_BETA[c];
    return (Math.round(r * 255) << 16) | (Math.round(g * 255) << 8) | Math.round(b * 255);
  }
  if (c === 0) return 0xe6e6e6;
  const v = FLEECE_1122[c], k = 0.75;
  return (Math.round(((v >> 16) & 255) * k) << 16) | (Math.round(((v >> 8) & 255) * k) << 8) | Math.round((v & 255) * k);
}

const b = (beta: boolean, modern: string, old: string) => (beta ? old : modern);

/** The common mobs (models in common.ts); both versions. */
const COMMON_LOOKS: Record<string, LookFn> = {
  zombie: (_v, beta) => ({ model: beta ? 'zombie_beta' : 'zombie', texture: b(beta, 'entity/zombie/zombie', 'mob/zombie'), colour: 0x3a7a5a }),
  husk: () => ({ model: 'zombie', texture: 'entity/zombie/husk', scale: 1.0625, colour: 0x8a7a50 }),
  giant: (_v, beta) => ({ model: beta ? 'zombie_beta' : 'zombie', texture: b(beta, 'entity/zombie/zombie', 'mob/zombie'), scale: 6, colour: 0x3a7a5a }),
  zombie_pigman: (_v, beta) => ({ model: beta ? 'zombie_beta' : 'zombie', texture: b(beta, 'entity/zombie_pigman', 'mob/pigzombie'), colour: 0xe0a0a0 }),
  skeleton: (_v, beta) => ({ model: 'skeleton', texture: b(beta, 'entity/skeleton/skeleton', 'mob/skeleton'), colour: 0xd0d0d0 }),
  stray: () => ({ model: 'skeleton', texture: 'entity/skeleton/stray', layers: [{ layer: 'overlay', texture: 'entity/skeleton/stray_overlay' }], colour: 0x8aa0a0 }),
  wither_skeleton: () => ({ model: 'skeleton', texture: 'entity/skeleton/wither_skeleton', scale: 1.2, colour: 0x2a2a2a }),
  creeper: (_v, beta) => ({ model: beta ? 'creeper_beta' : 'creeper', texture: b(beta, 'entity/creeper/creeper', 'mob/creeper'), colour: 0x4caf50 }),
  spider: (_v, beta) => ({ model: 'spider', texture: b(beta, 'entity/spider/spider', 'mob/spider'), layers: [{ texture: b(beta, 'entity/spider_eyes', 'mob/spider_eyes'), emissive: true }], colour: 0x3a2e28 }),
  cave_spider: (_v, beta) => ({ model: 'spider', texture: b(beta, 'entity/spider/cave_spider', 'mob/cavespider'), scale: 0.7, layers: [{ texture: b(beta, 'entity/spider_eyes', 'mob/spider_eyes'), emissive: true }], colour: 0x1c3a40 }),
  enderman: (_v, beta) => ({ model: 'enderman', texture: b(beta, 'entity/enderman/enderman', 'mob/enderman'), layers: [{ texture: b(beta, 'entity/enderman/enderman_eyes', 'mob/enderman_eyes'), emissive: true }], colour: 0x1a1020 }),
  pig: (v, beta) => ({ model: 'pig', texture: b(beta, 'entity/pig/pig', 'mob/pig'), layers: v & 1 ? [{ layer: 'saddle', texture: b(beta, 'entity/pig/pig_saddle', 'mob/saddle') }] : [], colour: 0xf0a0a0 }),
  cow: (_v, beta) => ({ model: 'cow', texture: b(beta, 'entity/cow/cow', 'mob/cow'), colour: 0x5a3a24 }),
  mooshroom: () => ({ model: 'cow', texture: 'entity/cow/mooshroom', colour: 0xb02020 }),
  sheep: (v, beta) => ({
    model: 'sheep',
    texture: b(beta, 'entity/sheep/sheep', 'mob/sheep'),
    layers: v & 16 ? [] : [{ layer: 'wool', texture: b(beta, 'entity/sheep/sheep_fur', 'mob/sheep_fur'), tint: fleece(v, beta) }],
    colour: v & 16 ? 0xc8b49c : fleece(v, beta),
  }),
  chicken: (_v, beta) => ({ model: 'chicken', texture: b(beta, 'entity/chicken', 'mob/chicken'), colour: 0xf8f8f8 }),
  wolf: (v, beta) => {
    const tex = v & 2 ? 'angry' : v & 4 ? 'tame' : '';
    return {
      model: beta ? 'wolf_beta' : 'wolf',
      texture: beta ? `mob/wolf${tex ? '_' + tex : ''}` : `entity/wolf/wolf${tex ? '_' + tex : ''}`,
      // The collar colour is not on the wire: vanilla's default red.
      layers: !beta && v & 4 ? [{ texture: 'entity/wolf/wolf_collar', tint: 0x993333 }] : [],
      colour: 0xd8d8d8,
    };
  },
  slime: (v, beta) => ({ model: 'slime', texture: b(beta, 'entity/slime/slime', 'mob/slime'), scale: Math.max(1, v || 1), layers: [{ layer: 'outer', texture: b(beta, 'entity/slime/slime', 'mob/slime'), translucent: true }], colour: 0x78c868 }),
  ghast: (_v, beta) => ({ model: 'ghast', texture: b(beta, 'entity/ghast/ghast', 'mob/ghast'), colour: 0xf0f0f0 }),
  squid: (_v, beta) => ({ model: 'squid', texture: b(beta, 'entity/squid', 'mob/squid'), colour: 0x2a3a5a }),
};

const ALL_LOOKS: Record<string, LookFn> = { ...animals.LOOKS, ...misc.LOOKS, ...COMMON_LOOKS };
const FALLBACK: Look = { model: 'box', texture: '', colour: 0xb05050 };

/** The canonical name of a mob type, or of a mob name the wire sends ("Zombie", "PigZombie"). */
export function mobName(type: number, name: string | null): string | null {
  const byId = MOB_NAMES[type];
  if (byId) return byId;
  if (!name) return null;
  const n = name.toLowerCase().replace(/[\s-]+/g, '_');
  return { pigzombie: 'zombie_pigman', pig_zombie: 'zombie_pigman', cavespider: 'cave_spider', lavaslime: 'magma_cube', snow_golem: 'snowman', iron_golem: 'villager_golem', mushroomcow: 'mooshroom' }[n] ?? n;
}

/** A mob's look (FALLBACK box when the type is unknown). */
export function mobLook(version: GameVersion, type: number, name: string | null, variant: number, id: number): Look {
  const n = mobName(type, name);
  const f = n ? ALL_LOOKS[n] : undefined;
  return f ? f(variant, version === 'b1.8.1', id) : FALLBACK;
}

/** An object's look by name (boat, minecart, arrow, ender crystal…) or null (drawn by the item/block paths or as a box). */
export function objectLook(version: GameVersion, name: string, variant: number, id: number): Look | null {
  const n = name === 'chest_minecart' || name === 'furnace_minecart' || name.endsWith('_minecart') ? 'minecart' : name;
  const f = ALL_LOOKS[`object:${n}`];
  return f ? f(variant, version === 'b1.8.1', id) : null;
}

function nameHash(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return h >>> 0;
}

/**
 * A player's look. Skins are not on the wire: bots wear Steve; other players get Steve or Alex the way vanilla picks the
 * default skin (by a hash; vanilla hashes the UUID, we hash the name).
 */
export function playerLook(version: GameVersion, name: string, bot: boolean): Look {
  if (version === 'b1.8.1') return { model: 'player_beta', texture: 'mob/char', colour: 0x4a7ad8 };
  const alex = !bot && (nameHash(name) & 1) === 1;
  return { model: alex ? 'player_slim' : 'player', texture: alex ? 'entity/alex' : 'entity/steve', colour: 0x4a7ad8 };
}

export function lookKeys(): string[] {
  return Object.keys(ALL_LOOKS);
}

export function lookFn(key: string): LookFn | undefined {
  return ALL_LOOKS[key];
}
