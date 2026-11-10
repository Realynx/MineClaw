// The free-cam crosshair: what the centre of the screen points at (vanilla's objectMouseOver). A bot's hit box is the player's
// (0.6 wide, 1.8 tall, from its feet); a block is the worker's first drawn block along the ray. The nearer of the two wins, as
// in the game, where an entity in front of a block is what you hit.

import type { V3 } from '../mesh/query';
import type { PickHit } from '../mesh/query';

export interface AimBot {
  name: string;
  x: number;
  y: number;
  z: number;
  /** Hit box width and height (default the player's 0.6 × 1.8). */
  w?: number;
  h?: number;
}

export interface AimRay { origin: V3; direction: V3; max: number }

export type AimTarget =
  | { kind: 'bot'; name: string; x: number; y: number; z: number; distance: number }
  | {
      kind: 'block';
      x: number;
      y: number;
      z: number;
      id: number;
      meta: number;
      face: number;
      name: string;
      distance: number;
      /** An application-owned plan cell that the ray hit before the physical world. */
      plan?: string | number;
    };

/** Slab test: the distance along the (unit) ray where it enters the box, or null when it misses or the box is behind. */
export function rayBox(o: V3, d: V3, min: V3, max: V3): number | null {
  let t0 = 0,
    t1 = Infinity;
  for (let i = 0; i < 3; i++) {
    const oi = o[i],
      di = d[i];
    if (Math.abs(di) < 1e-12) {
      if (oi < min[i] || oi > max[i]) return null;
      continue;
    }
    let a = (min[i] - oi) / di,
      b = (max[i] - oi) / di;
    if (a > b) [a, b] = [b, a];
    t0 = Math.max(t0, a);
    t1 = Math.min(t1, b);
    if (t0 > t1) return null;
  }
  return t0;
}

const unit = (d: V3): V3 => {
  const l = Math.hypot(d[0], d[1], d[2]) || 1;
  return [d[0] / l, d[1] / l, d[2] / l];
};

/** The bot whose hit box the ray enters first, within `max` blocks. */
export function botAlong(
  o: V3,
  dir: V3,
  bots: readonly AimBot[],
  max: number,
): { bot: AimBot; distance: number } | null {
  const d = unit(dir);
  let best: { bot: AimBot; distance: number } | null = null;
  for (const b of bots) {
    const hw = (b.w ?? 0.6) / 2,
      h = b.h ?? 1.8;
    const t = rayBox(o, d, [b.x - hw, b.y, b.z - hw], [b.x + hw, b.y + h, b.z + hw]);
    if (t !== null && t <= max && (!best || t < best.distance)) best = { bot: b, distance: t };
  }
  return best;
}

/** A block pick along a ray: the worker's whole-cell walk, or a wrapper around it. */
export type BlockPick = (o: V3, d: V3, max: number) => Promise<PickHit | null>;

/** The face a ray enters a cell by when it crosses into it along +x, +y or +z; the next number along -x, -y or -z. */
const ENTRY_FACE = [4, 0, 2];
/** Far too small to see, yet still a different number beside any block coordinate of a world (up to 3e7). */
const HAIR = 1e-7;

/**
 * Where a ray leaves the cell it starts in: a point just inside the next cell, the distance travelled to it and the
 * face of that cell the ray enters by. Sides and ties (z first, then y) are the worker's. Null without a direction.
 */
export function leaveCell(o: V3, d: V3): { origin: V3; travelled: number; face: number } | null {
  const u = unit(d);
  let axis = -1,
    t = Infinity;
  for (let i = 0; i < 3; i++) {
    if (u[i] === 0) continue;
    const low = Math.floor(o[i]);
    const ti = (u[i] > 0 ? low + 1 - o[i] : o[i] - low) / Math.abs(u[i]);
    if (ti <= t) {
      t = ti;
      axis = i;
    }
  }
  if (axis < 0) return null;
  const origin: V3 = [0, 0, 0];
  for (let i = 0; i < 3; i++) {
    const low = Math.floor(o[i]);
    // A cell owns its lower faces, so a hair below a boundary is the cell under it. Across the exit axis the point
    // goes just inside the next cell; along the others it stays within the old one, so a ray through an edge or a
    // corner still visits the cells in the worker's order.
    if (i === axis) origin[i] = u[i] > 0 ? low + 1 : low - HAIR;
    else origin[i] = Math.min(Math.max(o[i] + u[i] * t, low), low + 1 - HAIR);
  }
  return { origin, travelled: t, face: ENTRY_FACE[axis] + (u[axis] > 0 ? 0 : 1) };
}

/**
 * A pick that starts where the ray leaves the cell it begins in, so that cell is never the answer. In first person the
 * eye can sit inside a plant, a ladder or a door, and the whole-cell pick would return that cell whichever way the
 * player looks. `max` stays the reach from the true origin.
 */
export function beyondOwnCell(pick: BlockPick): BlockPick {
  return async (o, d, max) => {
    const out = leaveCell(o, d);
    if (!out || out.travelled > max) return null;
    const hit = await pick(out.origin, d, max - out.travelled);
    // The worker names no face for the cell a walk starts in; here that cell was entered from the eye's own.
    return hit && hit.face < 0 ? { ...hit, face: out.face } : hit;
  };
}

/** Distance from the eye to where the ray enters block (x, y, z). */
function blockDistance(o: V3, d: V3, x: number, y: number, z: number): number {
  return (
    rayBox(o, unit(d), [x, y, z], [x + 1, y + 1, z + 1]) ?? Math.hypot(x + 0.5 - o[0], y + 0.5 - o[1], z + 0.5 - o[2])
  );
}

/**
 * What the crosshair is on: the first bot or block along the ray from `o` toward `d`, whichever is nearer (null = sky).
 * `pickBlock` is the worker's block pick (fluids are looked through, like the game's crosshair).
 */
export async function aimAt(
  o: V3,
  d: V3,
  bots: readonly AimBot[],
  pickBlock: (o: V3, d: V3, max: number) => Promise<PickHit | null>,
  nameOf: (id: number) => string,
  max = 160,
): Promise<AimTarget | null> {
  const bot = botAlong(o, d, bots, max);
  const hit = await pickBlock(o, d, bot ? bot.distance + 1 : max).catch(() => null);
  const hitD = hit ? blockDistance(o, d, hit.x, hit.y, hit.z) : Infinity;
  if (bot && bot.distance <= hitD)
    return { kind: 'bot', name: bot.bot.name, x: bot.bot.x, y: bot.bot.y, z: bot.bot.z, distance: bot.distance };
  if (!hit) return null;
  return {
    kind: 'block',
    x: hit.x,
    y: hit.y,
    z: hit.z,
    id: hit.id,
    meta: hit.meta,
    face: hit.face,
    name: nameOf(hit.id),
    distance: hitD,
  };
}

/** The outline box drawn around a target: a block's cell (vanilla's selection box, a hair larger) or a bot's hit box. */
export function outlineOf(t: AimTarget): { min: V3; max: V3 } {
  if (t.kind === 'block')
    return { min: [t.x - 0.002, t.y - 0.002, t.z - 0.002], max: [t.x + 1.002, t.y + 1.002, t.z + 1.002] };
  return { min: [t.x - 0.35, t.y, t.z - 0.35], max: [t.x + 0.35, t.y + 1.85, t.z + 0.35] };
}
