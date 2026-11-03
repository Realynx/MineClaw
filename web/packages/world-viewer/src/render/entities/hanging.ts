// Hanging entities: paintings (EntityPainting.EnumArt: the motive's size and its place in the painting sheet) and item
// frames, and the facing both hang by.

/** Motive → [width, height, u, v] in pixels of the 256×256 painting sheet (1.12.2 paintings_kristoffer_zetterstrand.png,
 * b1.8.1 art/kz.png; b1.8.1 has no Wither). */
export const MOTIVES: Record<string, readonly [number, number, number, number]> = {
  kebab: [16, 16, 0, 0],
  aztec: [16, 16, 16, 0],
  alban: [16, 16, 32, 0],
  aztec2: [16, 16, 48, 0],
  bomb: [16, 16, 64, 0],
  plant: [16, 16, 80, 0],
  wasteland: [16, 16, 96, 0],
  pool: [32, 16, 0, 32],
  courbet: [32, 16, 32, 32],
  sea: [32, 16, 64, 32],
  sunset: [32, 16, 96, 32],
  creebet: [32, 16, 128, 32],
  wanderer: [16, 32, 0, 64],
  graham: [16, 32, 16, 64],
  match: [32, 32, 0, 128],
  bust: [32, 32, 32, 128],
  stage: [32, 32, 64, 128],
  void: [32, 32, 96, 128],
  skullandroses: [32, 32, 128, 128],
  wither: [32, 32, 160, 128],
  fighters: [64, 32, 0, 96],
  pointer: [64, 64, 0, 192],
  pigscene: [64, 64, 64, 192],
  burningskull: [64, 64, 128, 192],
  skeleton: [64, 48, 192, 64],
  donkeykong: [64, 48, 192, 112],
};

/** A motive by any spelling ("SkullAndRoses", "skull_and_roses", "minecraft:burning_skull"); Kebab when unknown. */
export function motive(name: string | null | undefined): { key: string; w: number; h: number; u: number; v: number } {
  const key = (name ?? '').toLowerCase().replace(/^minecraft:/, '').replace(/[^a-z0-9]/g, '');
  const m = MOTIVES[key] ?? MOTIVES.kebab;
  return { key: MOTIVES[key] ? key : 'kebab', w: m[0], h: m[1], u: m[2], v: m[3] };
}

/**
 * The yaw (degrees, mob convention: 0 = facing south / +z) a hanging entity's front faces, from the wire's facing
 * (0 south, 1 west, 2 north, 3 east — EnumFacing horizontal index — or a name).
 */
export function facingYaw(f: number | string | null | undefined): number {
  const n = typeof f === 'string' ? ['south', 'west', 'north', 'east'].indexOf(f.toLowerCase()) : (f ?? 0);
  return (((n < 0 ? 0 : n) % 4) + 4) % 4 * 90;
}

/** The painting sheet's entity-sheet key per version. */
export const paintingSheet = (beta: boolean) => (beta ? 'art/kz' : 'painting/paintings_kristoffer_zetterstrand');

/** Minecart contents: type (number or name) → the block id shown inside (0 = none). */
export function minecartBlock(t: number | string | null | undefined): number {
  const i = typeof t === 'string' ? ['minecart', 'chest', 'furnace', 'tnt', 'spawner', 'hopper', 'command_block'].findIndex((n) => t.toLowerCase().replace(/^minecraft:|_?minecart$/g, '') === n || t.toLowerCase() === `${n}_minecart`) : (t ?? 0);
  return [0, 54, 61, 46, 52, 154, 137][i < 0 ? 0 : i] ?? 0;
}
