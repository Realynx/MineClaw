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

const TORCH_CODEC: Codec = {
  get: (m) => (m >= 1 && m <= 4 ? TORCH_WALLS[m - 1]! : null),
  set: (m, d) => (m >= 1 && m <= 4 ? TORCH_WALLS.indexOf(d) + 1 : m),
};
const FRONT_CODEC = span(FRONT_DIRS, 2);
const BUTTON_CODEC = span(BUTTON_FACES, 1);

/** Each facing family's codec; families not listed have none. */
const CODECS: Partial<Record<Family, Codec>> = {
  stairs: low2(STAIRS_DIRS),
  door: low2(DOOR_DIRS), // both halves: the designs system stores the upper as lower | 8 in both versions
  bed: low2(SWNE),
  horizontal: low2(SWNE),
  gate: low2(SWNE),
  trapdoor: low2(TRAP_WALLS),
  torch: TORCH_CODEC,
  wall: FRONT_CODEC,
  front: FRONT_CODEC,
  facing6: FRONT_CODEC,
  hopper: FRONT_CODEC,
  skull: FRONT_CODEC,
  button: BUTTON_CODEC,
  lever: BUTTON_CODEC,
};

function codecOf(id: number): Codec | null {
  return CODECS[familyOf(id)] ?? null;
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

/** One placement: the block, the look, the clicked face and what follows from it. */
interface Placing {
  id: number;
  baseMeta: number;
  look: Dir;
  face: number;
  /** The clicked face's direction (null for the top or bottom). */
  fd: Dir | null;
  /** The side of the new block the clicked block is on (null for the top or bottom). */
  wall: Dir | null;
  modern: boolean;
  /** A 1.12.2 log's axis bits for the clicked face: 8 = z (north / south), 4 = x (west / east), 0 = y. */
  axis: number;
}

/** face → axis bits (faces 2, 3 → z; 4, 5 → x; 0, 1 → y). */
const AXIS_OF_FACE = [0, 0, 8, 8, 4, 4];
const VINE_SIDE: Record<Dir, number> = { south: 1, west: 2, north: 4, east: 8 };
const alongX = (d: Dir) => d === 'east' || d === 'west';

/** End rods and shulker boxes point off the clicked face; pistons, dispensers and droppers face the player; observers look away. */
function placeFacing6(p: Placing): number {
  if (p.id === 198 || (p.id >= 219 && p.id <= 234)) return p.face === 0 || p.face === 1 ? p.face : FRONT_OF(p.fd!);
  return FRONT_OF(p.id === 218 ? p.look : opposite(p.look));
}

function placeButton(p: Placing): number {
  if (p.wall) return BUTTON_FACES.indexOf(opposite(p.wall)) + 1;
  if (!p.modern) return p.baseMeta;
  return p.face === 0 ? 0 : 5;
}

function placeLever(p: Placing): number {
  if (p.wall) return BUTTON_FACES.indexOf(opposite(p.wall)) + 1;
  if (p.face === 0 && p.modern) return alongX(p.look) ? 0 : 7;
  return alongX(p.look) ? 6 : 5;
}

/** A tripwire hook faces off its wall; the rest of the family turns its face to the player. */
function placeHorizontal(p: Placing): number {
  if (p.id === 131) return low2(SWNE).set(0, opposite(p.wall ?? p.look));
  return low2(SWNE).set(p.baseMeta & ~3, opposite(p.look));
}

function placeQuartz(p: Placing): number {
  if (p.baseMeta < 2) return p.baseMeta;
  if (p.axis === 4) return 3;
  return p.axis === 8 ? 4 : 2;
}

const placeRail = (p: Placing) => (alongX(p.look) ? 1 : 0);

/** The placement rule per family; a family not listed keeps its base meta. */
const PLACE: Partial<Record<Family, (p: Placing) => number>> = {
  stairs: (p) => low2(STAIRS_DIRS).set(p.baseMeta & ~3, p.look),
  door: (p) => low2(DOOR_DIRS).set(p.baseMeta & 4, p.look),
  bed: (p) => low2(SWNE).set(0, p.look),
  wall: (p) => FRONT_OF(opposite(p.wall ?? p.look)),
  skull: (p) => (p.wall ? FRONT_OF(opposite(p.wall)) : 1),
  front: (p) => FRONT_OF(opposite(p.look)),
  facing6: placeFacing6,
  hopper: (p) => (p.wall ? FRONT_OF(p.wall) : 0),
  torch: (p) => (p.wall ? TORCH_WALLS.indexOf(p.wall) + 1 : 5),
  button: placeButton,
  lever: placeLever,
  vine: (p) => (p.wall ? VINE_SIDE[p.wall] : p.baseMeta),
  trapdoor: (p) => low2(TRAP_WALLS).set(p.baseMeta & ~3, p.wall ?? p.look),
  gate: (p) => low2(SWNE).set(0, p.look),
  horizontal: placeHorizontal,
  rail: placeRail,
  poweredrail: placeRail,
  rotation16: (p) => ROT16[opposite(p.look)],
  log: (p) => (p.modern ? (p.baseMeta & 3) | p.axis : p.baseMeta),
  pillar: (p) => p.axis,
  quartz: placeQuartz,
};

/**
 * The meta of a block placed by someone looking `look` (horizontal), on face `face` of the block it was placed against
 * (Minecraft side order), as the game does it: stairs go up away from the player, a door faces the way the player looks, a bed
 * lies along the look (head away), chests/furnaces/pumpkins/pistons/dispensers turn their front to the player, ladders, wall
 * torches, buttons, levers, wall signs and vines hang on the clicked block, a gate opens along the look, a rail runs along it,
 * a sign stands facing the player, a 1.12.2 log or pillar lies along the clicked face's axis.
 */
export function placeMeta(id: number, baseMeta: number, look: Dir, face: number, version: Version): number {
  const fd = FACE_DIR[face] ?? null;
  const p: Placing = {
    id,
    baseMeta,
    look,
    face,
    fd,
    wall: fd ? opposite(fd) : null, // the clicked block is on this side of the new one
    modern: version === '1.12.2',
    axis: AXIS_OF_FACE[face] ?? 0,
  };
  const rule = PLACE[familyOf(id)];
  return rule ? rule(p) : baseMeta;
}

/** Rail shapes turned clockwise: 0 N-S ↔ 1 E-W, ascending E → S → W → N (2 → 5 → 3 → 4), curves SE → SW → NW → NE (6 → 9). */
function railTurn(shape: number, q: number): number {
  const next: Record<number, number> = { 0: 1, 1: 0, 2: 5, 5: 3, 3: 4, 4: 2, 6: 7, 7: 8, 8: 9, 9: 6 };
  for (let i = 0; i < q; i++) shape = next[shape] ?? shape;
  return shape;
}

/** A log or pillar's axis turned a quarter: x (4) ↔ z (8); upright (0) stays. */
function swapAxis(meta: number): number {
  const axis = meta & 12;
  if (axis === 4) return (meta & ~12) | 8;
  return axis === 8 ? (meta & ~12) | 4 : meta;
}

/** Vine side bits turned `q` quarters: S → W → N → E → S. */
function turnVine(meta: number, q: number): number {
  let m = meta & 15;
  for (let i = 0; i < q; i++) m = ((m << 1) | (m >> 3)) & 15;
  return m;
}

/** A floor or ceiling lever's two orientations swap on a quarter turn. */
const LEVER_FLOOR_TURN: Record<number, number> = { 5: 6, 6: 5, 0: 7, 7: 0 };

/** Families whose quarter turns are not a plain codec turn; undefined = use the codec. */
function specialTurn(fam: Family, meta: number, q: number, version: Version): number | undefined {
  const quarter = q % 2 === 1 ? QUARTER_TURNS[fam]?.(meta, version) : undefined;
  return quarter ?? TURNS[fam]?.(meta, q);
}

/** A quartz pillar's x (3) and z (4) swap on a quarter turn. */
const swapQuartz = (meta: number) => (meta === 3 ? 4 : meta === 4 ? 3 : meta);

/** Odd quarter turns that swap an axis or orientation; undefined = no special rule for this meta. */
const QUARTER_TURNS: Partial<Record<Family, (meta: number, version: Version) => number | undefined>> = {
  log: (meta, version) => (version === '1.12.2' ? swapAxis(meta) : undefined),
  pillar: (meta) => swapAxis(meta),
  quartz: swapQuartz,
  lever: (meta) => ((meta & 7) in LEVER_FLOOR_TURN ? (meta & 8) | LEVER_FLOOR_TURN[meta & 7]! : undefined),
};

const TURNS: Partial<Record<Family, (meta: number, q: number) => number>> = {
  rotation16: (meta, q) => (meta + q * 4) & 15,
  rail: (meta, q) => railTurn(meta, q),
  poweredrail: (meta, q) => (meta & 8) | railTurn(meta & 7, q),
  vine: turnVine,
};

/** A meta turned `quarters` clockwise (seen from above) with its block. */
export function rotateMeta(id: number, meta: number, quarters: number, version: Version): number {
  const q = ((quarters % 4) + 4) % 4;
  if (q === 0) return meta;
  const special = specialTurn(familyOf(id), meta, q, version);
  if (special !== undefined) return special;
  return codecTurn(id, meta, (d) => turn(d, q));
}

/** The meta with its codec's facing moved by `f`; unchanged without a codec or a facing. */
function codecTurn(id: number, meta: number, f: (d: Dir) => Dir): number {
  const c = codecOf(id);
  const d = c?.get(meta);
  return c && d ? c.set(meta, f(d)) : meta;
}

const CURVE_MIRROR: Record<number, [number, number]> = { 6: [7, 9], 7: [6, 8], 8: [9, 7], 9: [8, 6] };

/** A rail shape mirrored: ascending E/W (2, 3) flip across x, N/S (4, 5) across z, curves swap. */
function mirrorRailShape(s: number, axis: 'x' | 'z'): number {
  if ((s === 2 || s === 3) && axis === 'x') return s ^ 1;
  if ((s === 4 || s === 5) && axis === 'z') return s ^ 1;
  return s >= 6 ? CURVE_MIRROR[s]![axis === 'x' ? 0 : 1] : s;
}

function mirrorRail(fam: Family, meta: number, axis: 'x' | 'z'): number {
  const mask = fam === 'rail' ? 15 : 7;
  return (meta & ~mask) | mirrorRailShape(meta & mask, axis);
}

/** Vine side bits mirrored: across x the W (2) and E (8) bits swap, across z S (1) and N (4). */
function mirrorVine(meta: number, axis: 'x' | 'z'): number {
  const m = meta & 15;
  return axis === 'x' ? (m & 5) | ((m & 2) << 2) | ((m & 8) >> 2) : (m & 10) | ((m & 1) << 2) | ((m & 4) >> 2);
}

const MIRRORS: Partial<Record<Family, (fam: Family, meta: number, axis: 'x' | 'z') => number>> = {
  rotation16: (_fam, meta, axis) => (axis === 'x' ? (16 - meta) & 15 : (8 - meta) & 15),
  rail: mirrorRail,
  poweredrail: mirrorRail,
  vine: (_fam, meta, axis) => mirrorVine(meta, axis),
};

/** A meta mirrored across x (east ↔ west) or z (north ↔ south). */
export function mirrorMeta(id: number, meta: number, axis: 'x' | 'z'): number {
  const fam = familyOf(id);
  const special = MIRRORS[fam];
  return special ? special(fam, meta, axis) : codecTurn(id, meta, (d) => mirror(d, axis));
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
