/**
 * The mock bridge's world (b1.8.1, H = 128): flat grass over dirt and stone with a few ores, plus a settlement for Story
 * Mode: an oak grove east of the hive, a ripe wheat field, tall grass and flowers, and a camp (crafting table, furnace, a
 * double chest and a single chest). One function answers every block, so the MCWC chunks and the colony director agree;
 * `setBlock` keeps the director's changes (felled trees, built ghosts) and they flow out as `blocks` frames.
 */
type Cell = readonly [number, number];

/** Ground height of a column (the grass block's y). */
export function topAt(x: number, z: number): number {
  const cx = x >> 4, cz = z >> 4, lx = x & 15, lz = z & 15;
  return 62 + ((lx + cx * 3 + lz + cz * 5) % 3 === 0 ? 1 : 0);
}

const features = new Map<string, Map<number, Cell>>();
const ck = (x: number, z: number) => `${x},${z}`;
const has = (m: Map<string, Map<number, Cell>>, x: number, y: number, z: number) => m.get(ck(x, z))?.has(y) ?? false;
function set(m: Map<string, Map<number, Cell>>, x: number, y: number, z: number, c: Cell): void {
  let col = m.get(ck(x, z));
  if (!col) m.set(ck(x, z), (col = new Map<number, Cell>()));
  col.set(y, c);
}

function put(x: number, y: number, z: number, id: number, meta = 0): void {
  set(features, x, y, z, [id, meta]);
}

/** An oak: a 5-high trunk with a round canopy. */
function tree(x: number, z: number, h = 5): void {
  const b = topAt(x, z);
  for (let dx = -2; dx <= 2; dx++)
    for (let dz = -2; dz <= 2; dz++)
      for (let dy = h - 2; dy <= h + 1; dy++) {
        if (Math.abs(dx) + Math.abs(dz) + Math.max(0, dy - (h - 1)) * 2 > 3) continue;
        if (!has(features, x + dx, b + dy, z + dz)) put(x + dx, b + dy, z + dz, 18);
      }
  for (let y = 1; y <= h; y++) put(x, b + y, z, 17);
}

/** Where the mock colony settles by default (the camp's middle). */
export const CAMP: [number, number, number] = [20, 63, 10];

(function build() {
  // The grove: twelve oaks on a jittered grid east of the camp.
  for (let i = 0; i < 12; i++) tree(30 + (i % 4) * 5 + ((i * 7) % 3) - 1, 6 + Math.floor(i / 4) * 5 + ((i * 5) % 3) - 1, 4 + (i % 3));
  // A ripe wheat field south of the camp (farmland + fully grown wheat), a water channel in the middle.
  for (let x = 12; x <= 19; x++)
    for (let z = 18; z <= 23; z++) {
      const t = topAt(x, z);
      if (z === 20) {
        put(x, t, z, 9);
        continue;
      }
      put(x, t, z, 60, 7);
      put(x, t + 1, z, 59, (x + z) % 4 === 0 ? 4 : 7);
    }
  // Tall grass and flowers to cut, west of the camp.
  for (let x = 4; x <= 12; x++)
    for (let z = 2; z <= 9; z++) {
      const n = (x * 7 + z * 13) % 5;
      if (n === 0) put(x, topAt(x, z) + 1, z, 31, 1);
      else if (n === 2) put(x, topAt(x, z) + 1, z, (x & 1) === 0 ? 37 : 38);
    }
  // The camp: a crafting table, a furnace, a double chest and a single chest.
  const [cx, , cz] = CAMP;
  put(cx - 3, topAt(cx - 3, cz - 3) + 1, cz - 3, 58);
  put(cx - 2, topAt(cx - 2, cz - 3) + 1, cz - 3, 61, 3);
  put(cx + 1, topAt(cx + 1, cz - 3) + 1, cz - 3, 54, 3);
  put(cx + 2, topAt(cx + 2, cz - 3) + 1, cz - 3, 54, 3);
  put(cx + 4, topAt(cx + 4, cz - 3) + 1, cz - 3, 54, 3);
})();

const overrides = new Map<string, Map<number, Cell>>();
const changes: Array<[number, number, number, number, number]> = [];

/** The block at a position. */
export function mockBlock(x: number, y: number, z: number): Cell {
  const key = ck(x, z);
  const o = overrides.get(key)?.get(y) ?? features.get(key)?.get(y);
  if (o) return o;
  if (y < 0 || y > 127) return [0, 0];
  const top = topAt(x, z);
  if (y > top) return [0, 0];
  if (y === 0) return [7, 0];
  if (y === top) return [2, 0];
  if (y >= top - 2) return [3, 0];
  const lx = x & 15, lz = z & 15;
  if (y < 16 && (lx * 7 + lz * 13 + y) % 41 === 0) return [56, 0];
  if ((lx * 5 + lz * 3 + y) % 23 === 0) return [16, 0];
  return [1, 0];
}

/** Changes a block (the director built or broke it); queued for the next `blocks` frame. */
export function setMockBlock(x: number, y: number, z: number, id: number, meta: number): void {
  set(overrides, x, y, z, [id, meta]);
  changes.push([x, y, z, id, meta]);
}

/** The block changes since the last call (the `blocks` stream drains them). */
export function drainBlockChanges(): Array<[number, number, number, number, number]> {
  return changes.splice(0, changes.length);
}

/** Y of the highest non-air, non-plant block of a column. */
export function groundAt(x: number, z: number): number {
  for (let y = 80; y > 0; y--) {
    const [id] = mockBlock(x, y, z);
    if (id !== 0 && id !== 18 && id !== 17 && id !== 31 && id !== 37 && id !== 38 && id !== 59) return y;
  }
  return topAt(x, z);
}
