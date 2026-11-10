// [holograms] The plain part of the build holograms (no three.js: the app's light `settings` entry re-exports it): the
// states a queued cell is drawn in, their looks, and how a queue is split into per-state groups and per-build outline boxes.

/** How a queued cell is drawn: to do, being built (a pawn holds it), waiting (its build is paused or lacks materials), or
 * blocked (it failed, or pawns can't build it). */
export type HoloState = 'todo' | 'active' | 'waiting' | 'blocked';
export const HOLO_STATES: readonly HoloState[] = ['todo', 'active', 'waiting', 'blocked'];

/** One queued cell: where, what (id 0 = dig it out), whose build, how it is drawn. */
export interface HoloCell {
  x: number;
  y: number;
  z: number;
  id: number;
  meta: number;
  build: string | number;
  state: HoloState;
}

/** Most cells drawn as blocks; the rest of their builds show as outline boxes. */
export const HOLO_CAP = 20_000;
/** Below this many screen pixels per block the view shows outline boxes per build instead of cells. */
export const HOLO_LOD_PX = 3;

/**
 * The state a Story ghost (`todo | leased | done | blocked | unsupported | misrotated | clear`) is drawn in, given its build's status
 * (`queued | active | paused | blocked | done`); null = not drawn (done). A misrotated cell (placed, turned another way than the
 * design) stays drawn as blocked over its block even once its build is done: the design's turn, in warning stripes. A cell to clear
 * (it holds another block, dug out before the design's goes in) is drawn the same way over the block in the way.
 */
export function holoState(ghost: string, buildStatus?: string | null): HoloState | null {
  if (ghost === 'misrotated' || ghost === 'clear') return 'blocked';
  if (ghost === 'done' || buildStatus === 'done') return null;
  if (ghost === 'blocked' || ghost === 'unsupported') return 'blocked';
  if (ghost === 'leased') return 'active';
  if (buildStatus === 'paused' || buildStatus === 'blocked') return 'waiting';
  return 'todo';
}

export interface HoloStyle {
  /** Linear rgb the block's texture is recoloured into. */
  tint: [number, number, number];
  opacity: number;
  /** 0 = grey, 1 = the full tint. */
  saturation: number;
  /** Diagonal warning stripes. */
  stripes: number;
  /** Overall brightness. */
  boost: number;
}

/** The look of every state, and of a placement preview (fits / blocked). Calm: nothing pulses or flashes. */
export const HOLO_STYLES: Readonly<Record<HoloState | 'place' | 'placeBlocked', HoloStyle>> = {
  todo: { tint: [0.35, 0.78, 1], opacity: 0.38, saturation: 1, stripes: 0, boost: 1 },
  active: { tint: [0.5, 0.92, 1], opacity: 0.5, saturation: 1, stripes: 0, boost: 1.3 },
  waiting: { tint: [0.5, 0.66, 0.78], opacity: 0.3, saturation: 0.25, stripes: 0, boost: 0.85 },
  blocked: { tint: [1, 0.38, 0.38], opacity: 0.42, saturation: 1, stripes: 1, boost: 1 },
  place: { tint: [0.42, 1, 0.55], opacity: 0.42, saturation: 1, stripes: 0, boost: 1.1 },
  placeBlocked: { tint: [1, 0.33, 0.33], opacity: 0.45, saturation: 1, stripes: 1, boost: 1.1 },
};
export const DIG_RGB: [number, number, number] = [1, 0.42, 0.42];
export const BOX_RGB: Record<HoloState | 'dig', [number, number, number]> = {
  todo: [0.35, 0.78, 1],
  active: [0.6, 0.95, 1],
  waiting: [0.55, 0.65, 0.72],
  blocked: [1, 0.42, 0.42],
  dig: DIG_RGB,
};

/** A build's outline box (the far zoom's stand-in for its cells). */
export interface HoloBox {
  build: string;
  min: [number, number, number];
  /** Exclusive. */
  max: [number, number, number];
  state: HoloState | 'dig';
}

export interface HoloPlan {
  /** Per state, the cells drawn as blocks: flat [x, y, z, id, meta, …]. */
  groups: Record<HoloState, number[]>;
  /** Cells to dig, flat [x, y, z, 0, 0, …]. */
  dig: number[];
  /** One per build. */
  boxes: HoloBox[];
  /** Builds with cells past the cap (their box always shows). */
  capped: Set<string>;
  /** Cells planned (blocks and digs). */
  cells: number;
}

const BOX_RANK: Record<HoloState | 'dig', number> = { active: 4, todo: 3, waiting: 2, blocked: 1, dig: 0 };

/** Splits the cells into per-state groups (queue order, at most `cap` cells) and one outline box per build. */
export function planHolograms(cells: readonly HoloCell[], cap = HOLO_CAP): HoloPlan {
  const groups: Record<HoloState, number[]> = { todo: [], active: [], waiting: [], blocked: [] };
  const dig: number[] = [];
  const boxes = new Map<string, HoloBox>();
  const capped = new Set<string>();
  let n = 0;
  // A queue lists a build's cells together: its box is looked up once per run of them.
  let last: string | number | undefined,
    build = '',
    b: HoloBox | undefined;
  for (const c of cells) {
    if (c.build !== last) {
      last = c.build;
      build = String(c.build);
      b = boxes.get(build);
    }
    if (!b)
      boxes.set(
        build,
        (b = { build, min: [c.x, c.y, c.z], max: [c.x + 1, c.y + 1, c.z + 1], state: c.id === 0 ? 'dig' : c.state }),
      );
    else {
      // In place (the box's own arrays): no two arrays per cell of a 47k-cell queue.
      if (c.x < b.min[0]) b.min[0] = c.x;
      if (c.y < b.min[1]) b.min[1] = c.y;
      if (c.z < b.min[2]) b.min[2] = c.z;
      if (c.x + 1 > b.max[0]) b.max[0] = c.x + 1;
      if (c.y + 1 > b.max[1]) b.max[1] = c.y + 1;
      if (c.z + 1 > b.max[2]) b.max[2] = c.z + 1;
      const s = c.id === 0 ? 'dig' : c.state;
      if (BOX_RANK[s] > BOX_RANK[b.state]) b.state = s;
    }
    if (n >= cap) {
      capped.add(build);
      continue;
    }
    n++;
    if (c.id === 0) dig.push(c.x, c.y, c.z, 0, 0);
    else groups[c.state].push(c.x, c.y, c.z, c.id, c.meta);
  }
  return { groups, dig, boxes: [...boxes.values()], capped, cells: n };
}

/** Queues of more cells than this are meshed by region (`HOLO_REGION` blocks square, x and z), and a change re-meshes only the
 * regions it touches; a smaller queue is one region, meshed whole as it always was. */
export const HOLO_REGION_MIN = 4096;
export const HOLO_REGION = 32;

/** One region's share of a plan: its per-state groups and its digs (flat, in plan order). */
export interface HoloRegionPlan {
  /** "rx,rz" (region coordinates). */
  key: string;
  groups: Record<HoloState, number[]>;
  dig: number[];
}

/** A region's number (from block x and z): the map key, no string per cell. */
export function holoRegionId(x: number, z: number): number {
  return (Math.floor(x / HOLO_REGION) + 1048576) * 2097152 + (Math.floor(z / HOLO_REGION) + 1048576);
}

/** Splits a plan's drawn cells by region (each region's cells keep the plan's order). */
export function planRegions(plan: HoloPlan): Map<number, HoloRegionPlan> {
  const out = new Map<number, HoloRegionPlan>();
  const regionOf = (x: number, z: number) => {
    const id = holoRegionId(x, z);
    let r = out.get(id);
    if (!r)
      out.set(
        id,
        (r = {
          key: `${Math.floor(x / HOLO_REGION)},${Math.floor(z / HOLO_REGION)}`,
          groups: { todo: [], active: [], waiting: [], blocked: [] },
          dig: [],
        }),
      );
    return r;
  };
  for (const s of HOLO_STATES) {
    const flat = plan.groups[s];
    for (let i = 0; i + 4 < flat.length; i += 5)
      regionOf(flat[i], flat[i + 2]).groups[s].push(flat[i], flat[i + 1], flat[i + 2], flat[i + 3], flat[i + 4]);
  }
  for (let i = 0; i + 4 < plan.dig.length; i += 5)
    regionOf(plan.dig[i], plan.dig[i + 2]).dig.push(plan.dig[i], plan.dig[i + 1], plan.dig[i + 2], 0, 0);
  return out;
}
