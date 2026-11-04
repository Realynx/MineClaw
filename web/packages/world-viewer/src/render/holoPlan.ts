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
 * The state a Story ghost (`todo | leased | done | blocked | unsupported`) is drawn in, given its build's status
 * (`queued | active | paused | blocked | done`); null = not drawn (done).
 */
export function holoState(ghost: string, buildStatus?: string | null): HoloState | null {
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
  for (const c of cells) {
    const build = String(c.build);
    const b = boxes.get(build);
    if (!b) boxes.set(build, { build, min: [c.x, c.y, c.z], max: [c.x + 1, c.y + 1, c.z + 1], state: c.id === 0 ? 'dig' : c.state });
    else {
      b.min = [Math.min(b.min[0], c.x), Math.min(b.min[1], c.y), Math.min(b.min[2], c.z)];
      b.max = [Math.max(b.max[0], c.x + 1), Math.max(b.max[1], c.y + 1), Math.max(b.max[2], c.z + 1)];
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
