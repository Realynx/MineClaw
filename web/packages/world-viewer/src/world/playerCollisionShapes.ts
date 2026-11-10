// Collision is independent of textures/visible geometry. State shapes share the bot's checked-in 1.12.2 data;
// neighbour-dependent stairs, doors, fences, walls and chests mirror Shapes1122.cs.
import {
  isDoor,
  isFence,
  isFenceGate,
  isPane,
  isStairs,
  isWoodFence,
  stairFacing,
  stairShape,
  type Nb,
} from '../mesh/models/states';
import { COLLISION_SHAPES, COLLISION_STATES, type CollisionTuple } from './playerCollisionData';
import type { CollisionLookup, PlayerCollisionBox } from './playerCollisionTypes';
import type { GameVersion } from './blocks';

const directions = ['north', 'east', 'south', 'west'] as const;
type Direction = (typeof directions)[number];
const cw = (f: Direction) => directions[(directions.indexOf(f) + 1) % 4];
const ccw = (f: Direction) => directions[(directions.indexOf(f) + 3) % 4];
const opposite = (f: Direction) => cw(cw(f));
const offsets = [
  [0, -1],
  [1, 0],
  [0, 1],
  [-1, 0],
] as const;
const full: CollisionTuple = [0, 0, 0, 1, 1, 1];
const betaLadder: Partial<Record<number, CollisionTuple>> = {
  2: [0, 0, 0.875, 1, 1, 1],
  3: [0, 0, 0, 1, 1, 0.125],
  4: [0.875, 0, 0, 1, 1, 1],
  5: [0, 0, 0, 0.125, 1, 1],
};
const shapeId = (id: number, meta: number): number => {
  const states = COLLISION_STATES[id];
  return typeof states === 'number' ? states : (states?.[meta & 15] ?? 1);
};

/** World-space collision boxes. Unknown blocks stay solid; missing columns are handled by the caller. */
export function playerBlockBoxes(
  read: CollisionLookup,
  x: number,
  y: number,
  z: number,
  version: GameVersion,
): PlayerCollisionBox[] {
  const { id, meta } = read(x, y, z);
  const boxes: CollisionTuple[] = [];
  const nb: Nb = {
    wx: x,
    wy: y,
    wz: z,
    id: (dx, dy, dz) => read(x + dx, y + dy, z + dz).id,
    meta: (dx, dy, dz) => read(x + dx, y + dy, z + dz).meta,
  };
  if (version === 'b1.8.1' && id === 54) boxes.push(full);
  else if (version === 'b1.8.1' && id === 44) boxes.push([0, 0, 0, 1, 0.5, 1]);
  else if (version === 'b1.8.1' && id === 85) boxes.push([0, 0, 0, 1, 1.5, 1]);
  // Beta's ladder is 0.125 thick (1.12.2's is 0.1875, as in the shared data).
  else if (version === 'b1.8.1' && id === 65) boxes.push(betaLadder[meta] ?? [0, 0, 0, 1, 1, 0.125]);
  else if (isStairs(id)) stairBoxes(nb, meta, version, boxes);
  else if (isDoor(id)) doorBoxes(nb, id, meta, boxes);
  else if (isFence(id) || isPane(id) || id === 139) joinedBoxes(nb, id, boxes);
  else if (id === 199) chorusBoxes(nb, boxes);
  else if (id === 54 || id === 146) {
    let minX = 0.0625,
      minZ = 0.0625,
      maxX = 0.9375,
      maxZ = 0.9375;
    if (nb.id(0, 0, -1) === id) minZ = 0;
    else if (nb.id(0, 0, 1) === id) maxZ = 1;
    else if (nb.id(-1, 0, 0) === id) minX = 0;
    else if (nb.id(1, 0, 0) === id) maxX = 1;
    boxes.push([minX, 0, minZ, maxX, 0.875, maxZ]);
  } else boxes.push(...(COLLISION_SHAPES[shapeId(id, meta)] ?? [full]));
  return boxes.map(([a, b, c, d, e, f]) => ({
    minX: x + a,
    minY: y + b,
    minZ: z + c,
    maxX: x + d,
    maxY: y + e,
    maxZ: z + f,
  }));
}

/** BlockChorusPlant: centre and arms to plants/flowers, with end stone supporting an arm only below. */
function chorusBoxes(nb: Nb, boxes: CollisionTuple[]) {
  const plant = (id: number) => id === 199 || id === 200;
  boxes.push([0.1875, 0.1875, 0.1875, 0.8125, 0.8125, 0.8125]);
  if (plant(nb.id(-1, 0, 0))) boxes.push([0, 0.1875, 0.1875, 0.1875, 0.8125, 0.8125]);
  if (plant(nb.id(1, 0, 0))) boxes.push([0.8125, 0.1875, 0.1875, 1, 0.8125, 0.8125]);
  if (plant(nb.id(0, 1, 0))) boxes.push([0.1875, 0.8125, 0.1875, 0.8125, 1, 0.8125]);
  const below = nb.id(0, -1, 0);
  if (plant(below) || below === 121) boxes.push([0.1875, 0, 0.1875, 0.8125, 0.1875, 0.8125]);
  if (plant(nb.id(0, 0, -1))) boxes.push([0.1875, 0.1875, 0, 0.8125, 0.8125, 0.1875]);
  if (plant(nb.id(0, 0, 1))) boxes.push([0.1875, 0.1875, 0.8125, 0.8125, 0.8125, 1]);
}

function stairBoxes(nb: Nb, meta: number, version: GameVersion, boxes: CollisionTuple[]) {
  const top = version !== 'b1.8.1' && (meta & 4) !== 0;
  const f = (['east', 'west', 'south', 'north'] as const)[meta & 3];
  const shape = version === 'b1.8.1' ? 'straight' : stairShape(nb, meta);
  const lo = top ? 0 : 0.5,
    hi = top ? 0.5 : 1;
  boxes.push([0, top ? 0.5 : 0, 0, 1, top ? 1 : 0.5, 1]);
  if (shape === 'straight' || shape.startsWith('inner')) {
    const horizontal: Record<Direction, [number, number, number, number]> = {
      north: [0, 0, 1, 0.5],
      south: [0, 0.5, 1, 1],
      west: [0, 0, 0.5, 1],
      east: [0.5, 0, 1, 1],
    };
    const [x, z, X, Z] = horizontal[f];
    boxes.push([x, lo, z, X, hi, Z]);
  }
  if (shape !== 'straight') {
    const eighth =
      shape === 'outer_right' ? cw(f) : shape === 'inner_right' ? opposite(f) : shape === 'inner_left' ? ccw(f) : f;
    const horizontal: Record<Direction, [number, number, number, number]> = {
      north: [0, 0, 0.5, 0.5],
      south: [0.5, 0.5, 1, 1],
      west: [0, 0.5, 0.5, 1],
      east: [0.5, 0, 1, 0.5],
    };
    const [x, z, X, Z] = horizontal[eighth];
    boxes.push([x, lo, z, X, hi, Z]);
  }
}

function doorBoxes(nb: Nb, id: number, meta: number, boxes: CollisionTuple[]) {
  const upper = (meta & 8) !== 0;
  const below = nb.id(0, -1, 0) === id ? nb.meta(0, -1, 0) : 8;
  const lower = upper ? below : meta;
  const top = upper ? meta : nb.id(0, 1, 0) === id ? nb.meta(0, 1, 0) : 8;
  let facing: Direction = lower & 8 ? 'north' : (['east', 'south', 'west', 'north'] as const)[lower & 3];
  if (!(lower & 8) && lower & 4) facing = top & 1 ? ccw(facing) : cw(facing);
  const planes: Record<Direction, CollisionTuple> = {
    north: [0, 0, 0.8125, 1, 1, 1],
    south: [0, 0, 0, 1, 1, 0.1875],
    west: [0.8125, 0, 0, 1, 1, 1],
    east: [0, 0, 0, 0.1875, 1, 1],
  };
  boxes.push(planes[facing]);
}

function joinedBoxes(nb: Nb, id: number, boxes: CollisionTuple[]) {
  const links = offsets.map(([dx, dz], i) => connects(nb, id, dx, dz, opposite(directions[i])));
  if (id === 139) {
    const [n, e, s, w] = links;
    boxes.push([
      w ? 0 : n && s && !e ? 0.3125 : 0.25,
      0,
      n ? 0 : e && w && !s ? 0.3125 : 0.25,
      e ? 1 : n && s && !w ? 0.6875 : 0.75,
      1.5,
      s ? 1 : e && w && !n ? 0.6875 : 0.75,
    ]);
    return;
  }
  const lo = isPane(id) ? 0.4375 : 0.375,
    hi = 1 - lo,
    height = isPane(id) ? 1 : 1.5;
  boxes.push([lo, 0, lo, hi, height, hi]);
  if (links[0]) boxes.push([lo, 0, 0, hi, height, lo]);
  if (links[1]) boxes.push([hi, 0, lo, 1, height, hi]);
  if (links[2]) boxes.push([lo, 0, hi, hi, height, 1]);
  if (links[3]) boxes.push([0, 0, lo, lo, height, hi]);
}

// Shapes1122's face-shape rules differ from collision volume: glass is a full cube but fences do not attach to it,
// while the back of a stair is SOLID even though its collision shape is not a cube.
const fenceExceptions = new Set([18, 161, 96, 167, 138, 118, 20, 89, 79, 169, 95, 33, 29, 34, 166, 103, 86, 91]);
const paneExceptions = new Set([18, 161, 138, 118, 89, 79, 169, 33, 29, 34, 103, 86, 91, 166]);
const sideNeverSolid = new Set([
  0, 6, 26, 27, 28, 30, 31, 32, 34, 36, 44, 50, 51, 54, 55, 59, 60, 63, 64, 65, 66, 68, 69, 70, 71, 72, 75, 76, 77, 78,
  81, 83, 85, 90, 92, 93, 94, 96, 101, 102, 104, 105, 106, 107, 111, 113, 115, 116, 117, 119, 120, 122, 126, 127, 130,
  131, 132, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 154, 157, 160, 167, 171, 175, 176, 177,
  178, 182, 205, 207, 208, 209, 217,
]);

function connects(nb: Nb, self: number, dx: number, dz: number, face: Direction): boolean {
  const id = nb.id(dx, 0, dz),
    meta = nb.meta(dx, 0, dz);
  if (id < 0) return true; // Unknown adjacent terrain remains conservative until the cache refreshes.
  if (isPane(self)) {
    if (isPane(id)) return true;
    if (paneExceptions.has(id) || (id >= 219 && id <= 234)) return false;
  } else {
    if (self === 139 && id === 139) return true;
    if (isFence(self) && isFence(id)) return isWoodFence(self) === isWoodFence(id);
    if (isFenceGate(id)) return ((meta & 3) === 0 || (meta & 3) === 2) === (face === 'east' || face === 'west');
    if (fenceExceptions.has(id) || (id >= 219 && id <= 234)) return false;
  }
  return sideSolid(nb, dx, dz, id, meta, face);
}

function sideSolid(nb: Nb, dx: number, dz: number, id: number, meta: number, face: Direction): boolean {
  if (isStairs(id)) {
    const facing = stairFacing(meta);
    const neighbor: Nb = {
      wx: nb.wx + dx,
      wy: nb.wy,
      wz: nb.wz + dz,
      id: (x, y, z) => nb.id(dx + x, y, dz + z),
      meta: (x, y, z) => nb.meta(dx + x, y, dz + z),
    };
    switch (stairShape(neighbor, meta)) {
      case 'straight':
        return facing === face;
      case 'inner_right':
        return facing === face || facing === ccw(face);
      case 'inner_left':
        return facing === face || facing === cw(face);
      default:
        return false;
    }
  }
  if (id === 118) return true;
  return !sideNeverSolid.has(id) && !(id >= 8 && id <= 11) && !(id >= 37 && id <= 40) && !(id >= 183 && id <= 200);
}
