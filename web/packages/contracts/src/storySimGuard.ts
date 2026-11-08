/** Guard-zone selection for the synthetic director. The real director uses Minecraft collision/visibility queries. */
import type { StoryZone } from './story.js';
import type { SimEntity, StoryWorld } from './storySim.js';
import { cellKey, zoneCellSet } from './storyZones.js';

export type GuardPosition = readonly [number, number, number];

/** Guard areas are full-height columns; keep-out always wins. Players and drops are never guard targets. */
export function guardedMobs(world: StoryWorld, zones: readonly StoryZone[]): SimEntity[] {
  const guards = zones.filter((z) => z.kind === 'guard').map(zoneCellSet);
  if (!guards.length) return [];
  const forbidden = zones.filter((z) => z.kind === 'forbidden').map(zoneCellSet);
  return world.entities().filter((e) => {
    const key = cellKey(Math.floor(e.x), Math.floor(e.z));
    return e.kind === 'mob' && guards.some((s) => s.has(key)) && !forbidden.some((s) => s.has(key));
  });
}

/** Conservative mock LOS through empty cells. A fixture may supply exact visibility for its geometry. */
export function guardVisible(world: StoryWorld, from: GuardPosition, target: SimEntity): boolean {
  if (world.canSee) return world.canSee(from, target);
  const dx = target.x - from[0],
    dy = target.y + 0.9 - (from[1] + 1.62),
    dz = target.z - from[2];
  const distance = Math.hypot(dx, dy, dz);
  if (distance > 32) return false;
  const steps = Math.max(1, Math.ceil(distance * 4));
  for (let i = 1; i < steps; i++) {
    const t = i / steps;
    if (
      world.blockAt(
        Math.floor(from[0] + dx * t),
        Math.floor(from[1] + 1.62 + dy * t),
        Math.floor(from[2] + dz * t),
      )[0] !== 0
    )
      return false;
  }
  return true;
}
