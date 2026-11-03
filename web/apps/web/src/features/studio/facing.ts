/**
 * Block facing in the Design Studio: the meta a block gets when placed facing a direction, and how a meta turns when a
 * selection or the whole design is rotated 90° around Y or mirrored. One rule per block family, matching the bot's
 * `BuildBlocks.MetaFor` / `BuildBlocks.Rotate` (mc-dev for b1.8.1) and the 1.12.2 metas the designs system stores:
 * stairs (meta & 3 = the way up E W S N, bit 4 upside down), doors (lower half meta & 3 = the placer's facing E S W N; the
 * upper half = lower | 8, as the designs system stores it), fronts 2..5 (chests, furnaces, dispensers, ladders:
 * N S W E), torches 1..4 (the wall W E N S; 5 = floor), trapdoors (meta & 3 = the wall S N E W), fence gates (meta & 3 =
 * S W N E), pumpkins (the front S W N E) and 1.12.2 logs (axis bits: 4 = x, 8 = z).
 */
export type Version = 'b1.8.1' | '1.12.2';
export type Dir = 'north' | 'east' | 'south' | 'west';

/** Clockwise from above (north up): N → E → S → W. */
export const DIRS: readonly Dir[] = ['north', 'east', 'south', 'west'];

export const turn = (d: Dir, quarters: number): Dir => DIRS[(((DIRS.indexOf(d) + quarters) % 4) + 4) % 4]!;
export const opposite = (d: Dir): Dir => turn(d, 2);
export const mirror = (d: Dir, axis: 'x' | 'z'): Dir =>
  axis === 'x' ? (d === 'east' ? 'west' : d === 'west' ? 'east' : d) : d === 'north' ? 'south' : d === 'south' ? 'north' : d;

/** The horizontal direction a camera looks (yaw in radians, three.js: 0 = looking -z = north). */
export function dirOfYaw(yaw: number): Dir {
  const a = (((-yaw * 180) / Math.PI) % 360 + 360) % 360; // 0 = north, 90 = east
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

const STAIRS = new Set([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203]);
const DOORS = new Set([64, 71, 193, 194, 195, 196, 197]);
/** Front / wall side in metas 2..5. */
const FRONT = new Set([23, 54, 61, 62, 65, 68, 130, 146, 158]);
const TORCH = new Set([50, 75, 76]);
const TRAPDOOR = new Set([96, 167]);
const GATES = new Set([107, 183, 184, 185, 186, 187]);
const PUMPKIN = new Set([86, 91]);
const LOGS = new Set([17, 162]);

export const isDoor = (id: number) => DOORS.has(id);
export const isStairs = (id: number) => STAIRS.has(id);

/** Blocks whose meta says where they face (placement asks for a direction). */
export function hasFacing(id: number): boolean {
  return STAIRS.has(id) || DOORS.has(id) || FRONT.has(id) || TORCH.has(id) || TRAPDOOR.has(id) || GATES.has(id) || PUMPKIN.has(id);
}

const STAIRS_DIRS: Dir[] = ['east', 'west', 'south', 'north'];
const DOOR_DIRS: Dir[] = ['east', 'south', 'west', 'north'];
const FRONT_DIRS: Dir[] = ['north', 'south', 'west', 'east']; // metas 2..5
const TORCH_WALLS: Dir[] = ['west', 'east', 'north', 'south']; // metas 1..4
const TRAP_WALLS: Dir[] = ['south', 'north', 'east', 'west'];
const GATE_DIRS: Dir[] = ['south', 'west', 'north', 'east'];
const PUMPKIN_FRONTS: Dir[] = ['south', 'west', 'north', 'east'];

/** One family's meta ↔ direction codec (null = this meta has no horizontal facing). */
interface Codec {
  get(meta: number): Dir | null;
  set(meta: number, d: Dir): number;
}
const low2 = (dirs: Dir[]): Codec => ({ get: (m) => dirs[m & 3]!, set: (m, d) => (m & ~3) | dirs.indexOf(d) });

function codecOf(id: number): Codec | null {
  if (STAIRS.has(id)) return low2(STAIRS_DIRS);
  if (DOORS.has(id)) return low2(DOOR_DIRS); // both halves: the designs system stores the upper as lower | 8 in both versions
  if (FRONT.has(id))
    return { get: (m) => (m >= 2 && m <= 5 ? FRONT_DIRS[m - 2]! : null), set: (m, d) => (m >= 2 && m <= 5 ? FRONT_DIRS.indexOf(d) + 2 : m) };
  if (TORCH.has(id))
    return { get: (m) => (m >= 1 && m <= 4 ? TORCH_WALLS[m - 1]! : null), set: (m, d) => (m >= 1 && m <= 4 ? TORCH_WALLS.indexOf(d) + 1 : m) };
  if (TRAPDOOR.has(id)) return low2(TRAP_WALLS);
  if (GATES.has(id)) return low2(GATE_DIRS);
  if (PUMPKIN.has(id)) return low2(PUMPKIN_FRONTS);
  return null;
}

/** The meta's facing, or null (no facing, a floor torch, a 1.12.2 upper door half). */
export function facingOf(id: number, meta: number): Dir | null {
  return codecOf(id)?.get(meta) ?? null;
}

/**
 * The meta of a block placed by someone looking `look` (horizontal), on face `face` of the block it was placed against
 * (Minecraft side order), as the game does it: stairs go up away from the player, a door faces the way the player looks,
 * chests/furnaces/pumpkins turn their front to the player, ladders and wall torches hang on the clicked block, a gate opens
 * along the look, a 1.12.2 log lies along the clicked face's axis.
 */
export function placeMeta(id: number, baseMeta: number, look: Dir, face: number, version: Version): number {
  const fd = FACE_DIR[face];
  const wall = fd ? opposite(fd) : null; // the clicked block is on this side of the new one
  if (STAIRS.has(id)) return low2(STAIRS_DIRS).set(baseMeta & ~3, look);
  if (DOORS.has(id)) return low2(DOOR_DIRS).set(baseMeta & 4, look);
  if (id === 65) return FRONT_DIRS.indexOf(opposite(wall ?? look)) + 2;
  if (FRONT.has(id)) return FRONT_DIRS.indexOf(opposite(look)) + 2;
  if (TORCH.has(id)) return wall ? TORCH_WALLS.indexOf(wall) + 1 : 5;
  if (TRAPDOOR.has(id)) return low2(TRAP_WALLS).set(baseMeta & ~3, wall ?? look);
  if (GATES.has(id)) return low2(GATE_DIRS).set(0, look);
  if (PUMPKIN.has(id)) return low2(PUMPKIN_FRONTS).set(0, opposite(look));
  if (LOGS.has(id) && version === '1.12.2') return (baseMeta & 3) | (face === 2 || face === 3 ? 8 : face === 4 || face === 5 ? 4 : 0);
  return baseMeta;
}

/** A meta turned `quarters` clockwise (seen from above) with its block. */
export function rotateMeta(id: number, meta: number, quarters: number, version: Version): number {
  const q = ((quarters % 4) + 4) % 4;
  if (q === 0) return meta;
  if (LOGS.has(id) && version === '1.12.2' && q % 2 === 1) {
    const axis = (meta >> 2) & 3;
    return axis === 1 ? (meta & ~12) | 8 : axis === 2 ? (meta & ~12) | 4 : meta;
  }
  if (id === 63) return (meta + q * 4) & 15; // standing sign rotation
  const c = codecOf(id);
  const d = c?.get(meta);
  return c && d ? c.set(meta, turn(d, q)) : meta;
}

/** A meta mirrored across x (east ↔ west) or z (north ↔ south). */
export function mirrorMeta(id: number, meta: number, axis: 'x' | 'z'): number {
  if (id === 63) return axis === 'x' ? (16 - meta) & 15 : (8 - meta) & 15;
  const c = codecOf(id);
  const d = c?.get(meta);
  return c && d ? c.set(meta, mirror(d, axis)) : meta;
}

/** The upper half of a door placed with lower meta `lower` (the designs system's convention in both versions: lower | 8). */
export const doorUpper = (lower: number): number => (lower & 7) | 8;
