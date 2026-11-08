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
