/**
 * Block facing in the Design Studio: the meta a block gets when placed facing a direction, how a meta turns when a selection
 * or the whole design is rotated 90° around Y or mirrored, and the second cell a two-cell block brings. One rule per block
 * family, matching the bot's `DesignFacing` / `BuildBlocks.Rotate` (mc-dev for b1.8.1, the 1.12.2 metas the designs system
 * stores): stairs (meta & 3 = the way up E W S N, bit 4 upside down), doors (lower meta & 3 = the placer's facing E S W N; upper
 * = lower | 8, the designs system's rule in both versions), beds (meta & 3 = foot → head S W N E, head = foot | 8 one step
 * along it), double plants (upper = lower | 8), fronts 2..5 = N S W E (chests, furnaces, ladders, wall signs/banners/skulls,
 * pistons, dispensers, droppers, observers, end rods, shulker boxes, hoppers; 0/1 = down/up), torches 1..4 (the wall W E N S; 5
 * floor), buttons / levers 1..4 (facing E W S N; floor/ceiling metas), trapdoors (meta & 3 = the wall S N E W), fence gates,
 * pumpkins, glazed terracotta, anvils, repeaters, comparators (meta & 3 = S W N E), rails (shapes), standing signs and banners
 * (rotation 0..15), vines (side bits S W N E), 1.12.2 logs and pillars (axis bits 4 = x, 8 = z; quartz 2 y 3 x 4 z).
 */
export type Version = 'b1.8.1' | '1.12.2';
export type Dir = 'north' | 'east' | 'south' | 'west';

/** Clockwise from above (north up): N → E → S → W. */
export const DIRS: readonly Dir[] = ['north', 'east', 'south', 'west'];

export const turn = (d: Dir, quarters: number): Dir => DIRS[(((DIRS.indexOf(d) + quarters) % 4) + 4) % 4]!;
export const opposite = (d: Dir): Dir => turn(d, 2);
export const mirror = (d: Dir, axis: 'x' | 'z'): Dir =>
  axis === 'x'
    ? d === 'east'
      ? 'west'
      : d === 'west'
        ? 'east'
        : d
    : d === 'north'
      ? 'south'
      : d === 'south'
        ? 'north'
        : d;

const STEP: Record<Dir, [number, number]> = { north: [0, -1], east: [1, 0], south: [0, 1], west: [-1, 0] };

/** The horizontal direction a camera looks (yaw in radians, three.js: 0 = looking -z = north). */
export function dirOfYaw(yaw: number): Dir {
  const a = ((((-yaw * 180) / Math.PI) % 360) + 360) % 360; // 0 = north, 90 = east
  return DIRS[Math.round(a / 90) % 4]!;
}

/** The horizontal direction of a Minecraft yaw in degrees (0 = south, 90 = west, 180 = north, 270 = east). */
export function dirOfMcYaw(yaw: number): Dir {
  const q = Math.round((((yaw % 360) + 360) % 360) / 90) % 4;
  return (['south', 'west', 'north', 'east'] as const)[q]!;
}

/** Minecraft face (0 down, 1 up, 2 north, 3 south, 4 west, 5 east) → its outward normal. */
export const FACE_NORMAL: ReadonlyArray<readonly [number, number, number]> = [
  [0, -1, 0],
  [0, 1, 0],
  [0, 0, -1],
  [0, 0, 1],
  [-1, 0, 0],
  [1, 0, 0],
];
const FACE_DIR: Array<Dir | null> = [null, null, 'north', 'south', 'west', 'east'];

export type Family =
  | 'none'
  | 'stairs'
  | 'slab'
  | 'log'
  | 'pillar'
  | 'quartz'
  | 'leaves'
  | 'door'
  | 'tall'
  | 'bed'
  | 'torch'
  | 'wall'
  | 'front'
  | 'facing6'
  | 'hopper'
  | 'horizontal'
  | 'gate'
  | 'trapdoor'
  | 'button'
  | 'lever'
  | 'rail'
  | 'poweredrail'
  | 'rotation16'
  | 'skull'
  | 'vine'
  | 'age';

const range = (a: number, b: number) => Array.from({ length: b - a + 1 }, (_, i) => a + i);
const FAMILIES = new Map<number, Family>();
const set = (f: Family, ids: number[]) => ids.forEach((i) => FAMILIES.set(i, f));
set('stairs', [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203]);
set('slab', [44, 126, 182, 205]);
set('log', [17, 162]);
set('pillar', [170, 202, 216]);
set('quartz', [155]);
set('leaves', [18, 161]);
set('door', [64, 71, 193, 194, 195, 196, 197]);
set('tall', [175]);
set('bed', [26]);
set('torch', [50, 75, 76]);
set('wall', [65, 68, 177]);
set('front', [54, 61, 62, 130, 146]);
set('facing6', [23, 29, 33, 158, 198, 218, ...range(219, 234)]);
set('hopper', [154]);
set('horizontal', [86, 91, 93, 94, 127, 131, 145, 149, 150, ...range(235, 250)]);
set('gate', [107, 183, 184, 185, 186, 187]);
set('trapdoor', [96, 167]);
set('button', [77, 143]);
set('lever', [69]);
set('rail', [66]);
set('poweredrail', [27, 28, 157]);
set('rotation16', [63, 176]);
set('skull', [144]);
set('vine', [106]);

/** A block id's family (the same table as the bot's DesignFacing). */
export const familyOf = (id: number): Family => FAMILIES.get(id) ?? 'none';

const FACING_FAMILIES = new Set<Family>([
  'stairs',
  'door',
  'bed',
  'torch',
  'wall',
  'front',
  'facing6',
  'hopper',
  'horizontal',
  'gate',
  'trapdoor',
  'button',
  'lever',
  'rail',
  'poweredrail',
  'rotation16',
  'skull',
  'vine',
]);

export const isDoor = (id: number) => familyOf(id) === 'door';
export const isStairs = (id: number) => familyOf(id) === 'stairs';

/** Blocks whose meta says where they face (placement asks for a direction). */
export function hasFacing(id: number): boolean {
  return FACING_FAMILIES.has(familyOf(id));
}

const SWNE: Dir[] = ['south', 'west', 'north', 'east'];
const STAIRS_DIRS: Dir[] = ['east', 'west', 'south', 'north'];
const DOOR_DIRS: Dir[] = ['east', 'south', 'west', 'north'];
const FRONT_DIRS: Dir[] = ['north', 'south', 'west', 'east']; // metas 2..5
const TORCH_WALLS: Dir[] = ['west', 'east', 'north', 'south']; // metas 1..4
const TRAP_WALLS: Dir[] = ['south', 'north', 'east', 'west'];
const BUTTON_FACES: Dir[] = ['east', 'west', 'south', 'north']; // metas 1..4: the way it faces (the wall behind)

/** One family's meta ↔ direction codec (null = this meta has no horizontal facing). */
interface Codec {
  get(meta: number): Dir | null;
  set(meta: number, d: Dir): number;
}
const low2 = (dirs: Dir[]): Codec => ({ get: (m) => dirs[m & 3]!, set: (m, d) => (m & ~3) | dirs.indexOf(d) });
/** Metas `first`..`first + 3` (the low 3 bits), higher bits kept. */
const span = (dirs: Dir[], first: number): Codec => ({
  get: (m) => ((m & 7) >= first && (m & 7) <= first + 3 ? dirs[(m & 7) - first]! : null),
  set: (m, d) => ((m & 7) >= first && (m & 7) <= first + 3 ? (m & 8) | (dirs.indexOf(d) + first) : m),
});

function codecOf(id: number): Codec | null {
  switch (familyOf(id)) {
    case 'stairs':
      return low2(STAIRS_DIRS);
    case 'door':
      return low2(DOOR_DIRS); // both halves: the designs system stores the upper as lower | 8 in both versions
    case 'bed':
    case 'horizontal':
    case 'gate':
      return low2(SWNE);
    case 'trapdoor':
      return low2(TRAP_WALLS);
    case 'torch':
      return {
        get: (m) => (m >= 1 && m <= 4 ? TORCH_WALLS[m - 1]! : null),
        set: (m, d) => (m >= 1 && m <= 4 ? TORCH_WALLS.indexOf(d) + 1 : m),
      };
    case 'wall':
    case 'front':
    case 'facing6':
    case 'hopper':
    case 'skull':
      return span(FRONT_DIRS, 2);
    case 'button':
    case 'lever':
      return span(BUTTON_FACES, 1);
    default:
      return null;
  }
}

/** The meta's facing, or null (no facing, a floor torch, an upright log). */
export function facingOf(id: number, meta: number): Dir | null {
  return codecOf(id)?.get(meta) ?? null;
}

const FRONT_OF = (d: Dir) => FRONT_DIRS.indexOf(d) + 2;

/**
 * A meta turned to face `d` in its family's own sense (the bot's `StoryConstruction.Faced`, the Architect's Q/E turn): a furnace's
 * or chest's front, the way stairs rise, a door's facing, a bed's foot → head, a pumpkin's face, a trapdoor's wall; a piston,
 * dispenser or hopper pointing up or down turns to face `d` too. A floor torch, an upright log or a block with no facing is
 * unchanged.
 */
export function withFacing(id: number, meta: number, d: Dir): number {
  const fam = familyOf(id);
  if (fam === 'wall' || fam === 'front' || ((fam === 'facing6' || fam === 'hopper') && (meta & 7) <= 1)) return (meta & 8) | FRONT_OF(d);
  const c = codecOf(id);
  return c ? c.set(meta, d) : meta;
}
const ROT16: Record<Dir, number> = { south: 0, west: 4, north: 8, east: 12 };

/**
 * The meta of a block placed by someone looking `look` (horizontal), on face `face` of the block it was placed against
 * (Minecraft side order), as the game does it: stairs go up away from the player, a door faces the way the player looks, a bed
 * lies along the look (head away), chests/furnaces/pumpkins/pistons/dispensers turn their front to the player, ladders, wall
 * torches, buttons, levers, wall signs and vines hang on the clicked block, a gate opens along the look, a rail runs along it,
 * a sign stands facing the player, a 1.12.2 log or pillar lies along the clicked face's axis.
 */
export function placeMeta(id: number, baseMeta: number, look: Dir, face: number, version: Version): number {
  const fd = FACE_DIR[face];
  const wall = fd ? opposite(fd) : null; // the clicked block is on this side of the new one
  const modern = version === '1.12.2';
  const axis = face === 2 || face === 3 ? 8 : face === 4 || face === 5 ? 4 : 0;
  switch (familyOf(id)) {
    case 'stairs':
      return low2(STAIRS_DIRS).set(baseMeta & ~3, look);
    case 'door':
      return low2(DOOR_DIRS).set(baseMeta & 4, look);
    case 'bed':
      return low2(SWNE).set(0, look);
    case 'wall':
      return FRONT_OF(opposite(wall ?? look));
    case 'skull':
      return wall ? FRONT_OF(opposite(wall)) : 1;
    case 'front':
      return FRONT_OF(opposite(look));
    case 'facing6':
      // End rods and shulker boxes point off the clicked face; pistons, dispensers and droppers face the player; observers
      // look away from them.
      if (id === 198 || (id >= 219 && id <= 234)) return face === 0 ? 0 : face === 1 ? 1 : FRONT_OF(fd!);
      return FRONT_OF(id === 218 ? look : opposite(look));
    case 'hopper':
      return wall ? FRONT_OF(wall) : 0;
    case 'torch':
      return wall ? TORCH_WALLS.indexOf(wall) + 1 : 5;
    case 'button':
      if (wall) return BUTTON_FACES.indexOf(opposite(wall)) + 1;
      return modern ? (face === 0 ? 0 : 5) : baseMeta;
    case 'lever':
      if (wall) return BUTTON_FACES.indexOf(opposite(wall)) + 1;
      if (face === 0 && modern) return look === 'east' || look === 'west' ? 0 : 7;
      return look === 'east' || look === 'west' ? 6 : 5;
    case 'vine':
      return wall ? { south: 1, west: 2, north: 4, east: 8 }[wall] : baseMeta;
    case 'trapdoor':
      return low2(TRAP_WALLS).set(baseMeta & ~3, wall ?? look);
    case 'gate':
      return low2(SWNE).set(0, look);
    case 'horizontal':
      if (id === 131) return low2(SWNE).set(0, opposite(wall ?? look)); // a tripwire hook faces off its wall
      return low2(SWNE).set(baseMeta & ~3, opposite(look));
    case 'rail':
      return look === 'east' || look === 'west' ? 1 : 0;
    case 'poweredrail':
      return look === 'east' || look === 'west' ? 1 : 0;
    case 'rotation16':
      return ROT16[opposite(look)];
    case 'log':
      return modern ? (baseMeta & 3) | axis : baseMeta;
    case 'pillar':
      return axis;
    case 'quartz':
      return baseMeta >= 2 ? (axis === 4 ? 3 : axis === 8 ? 4 : 2) : baseMeta;
    default:
      return baseMeta;
  }
}

/** Rail shapes turned clockwise: 0 N-S ↔ 1 E-W, ascending E → S → W → N (2 → 5 → 3 → 4), curves SE → SW → NW → NE (6 → 9). */
function railTurn(shape: number, q: number): number {
  const next: Record<number, number> = { 0: 1, 1: 0, 2: 5, 5: 3, 3: 4, 4: 2, 6: 7, 7: 8, 8: 9, 9: 6 };
  for (let i = 0; i < q; i++) shape = next[shape] ?? shape;
  return shape;
}

/** A meta turned `quarters` clockwise (seen from above) with its block. */
export function rotateMeta(id: number, meta: number, quarters: number, version: Version): number {
  const q = ((quarters % 4) + 4) % 4;
  if (q === 0) return meta;
  const fam = familyOf(id);
  if (((fam === 'log' && version === '1.12.2') || fam === 'pillar') && q % 2 === 1) {
    const axis = meta & 12;
    return axis === 4 ? (meta & ~12) | 8 : axis === 8 ? (meta & ~12) | 4 : meta;
  }
  if (fam === 'quartz' && q % 2 === 1) return meta === 3 ? 4 : meta === 4 ? 3 : meta;
  if (fam === 'rotation16') return (meta + q * 4) & 15;
  if (fam === 'rail') return railTurn(meta, q);
  if (fam === 'poweredrail') return (meta & 8) | railTurn(meta & 7, q);
  if (fam === 'vine') {
    let m = meta & 15;
    for (let i = 0; i < q; i++) m = ((m << 1) | (m >> 3)) & 15; // S → W → N → E → S
    return m;
  }
  if (fam === 'lever' && q % 2 === 1 && [0, 5, 6, 7].includes(meta & 7))
    return (meta & 8) | ({ 5: 6, 6: 5, 0: 7, 7: 0 } as Record<number, number>)[meta & 7]!;
  const c = codecOf(id);
  const d = c?.get(meta);
  return c && d ? c.set(meta, turn(d, q)) : meta;
}

/** A meta mirrored across x (east ↔ west) or z (north ↔ south). */
export function mirrorMeta(id: number, meta: number, axis: 'x' | 'z'): number {
  const fam = familyOf(id);
  if (fam === 'rotation16') return axis === 'x' ? (16 - meta) & 15 : (8 - meta) & 15;
  if (fam === 'rail' || fam === 'poweredrail') {
    const mask = fam === 'rail' ? 15 : 7;
    const s = meta & mask;
    const m =
      (s === 2 || s === 3) && axis === 'x'
        ? s ^ 1
        : (s === 4 || s === 5) && axis === 'z'
          ? s ^ 1
          : s >= 6
            ? ({ 6: [7, 9], 7: [6, 8], 8: [9, 7], 9: [8, 6] } as Record<number, [number, number]>)[s]![axis === 'x' ? 0 : 1]
            : s;
    return (meta & ~mask) | m;
  }
  if (fam === 'vine') {
    const m = meta & 15;
    return axis === 'x' ? (m & 5) | ((m & 2) << 2) | ((m & 8) >> 2) : (m & 10) | ((m & 1) << 2) | ((m & 4) >> 2);
  }
  const c = codecOf(id);
  const d = c?.get(meta);
  return c && d ? c.set(meta, mirror(d, axis)) : meta;
}

/** The upper half of a door placed with lower meta `lower` (the designs system's convention in both versions: lower | 8). */
export const doorUpper = (lower: number): number => (lower & 7) | 8;

/** Two cells of one block: doors and double plants (upper above), beds (head one step along the facing). */
export const twoCell = (id: number): boolean => {
  const f = familyOf(id);
  return f === 'door' || f === 'tall' || f === 'bed';
};

/** The second cell a first half brings (relative position and meta), or null (a one-cell block, or itself a second half). */
export function partnerOf(id: number, meta: number): [number, number, number, number] | null {
  const f = familyOf(id);
  if ((meta & 8) !== 0) return null;
  if (f === 'door' || f === 'tall') return [0, 1, 0, (meta & 7) | 8];
  if (f === 'bed') {
    const [dx, dz] = STEP[SWNE[meta & 3]!];
    return [dx, 0, dz, (meta & 3) | 8];
  }
  return null;
}

/** Where a second half's first half stands, relative to it; null = not a second half. */
export function firstHalfOf(id: number, meta: number): [number, number, number] | null {
  const f = familyOf(id);
  if ((meta & 8) === 0) return null;
  if (f === 'door' || f === 'tall') return [0, -1, 0];
  if (f === 'bed') {
    const [dx, dz] = STEP[SWNE[meta & 3]!];
    return [-dx || 0, 0, -dz || 0];
  }
  return null;
}
