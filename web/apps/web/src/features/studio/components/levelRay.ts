/**
 * Pointer rays for placing and dragging components (mine and redstone alike). Rays are in world space; cells and levels are
 * canvas coordinates, which sit `plotMode().baseY` lower (0 in a mine, 64 on a build or redstone plot).
 */
import type { Camera } from 'three';
import { inside, plotMode, type V3 } from '../canvas';
import type * as ViewerMod from '../studio-viewer';

export interface PlacementRay {
  origin: { x: number; y: number; z: number };
  direction: { x: number; y: number; z: number };
}

/** The camera ray under a client point on the view, or null before the view has a size. */
export function rayFromPointer(
  mod: Pick<typeof ViewerMod, 'Raycaster' | 'Vector2'>,
  engine: { surface: HTMLElement; camera: Camera } | null | undefined,
  clientX: number,
  clientY: number,
) {
  if (!engine) return null;
  const rect = engine.surface.getBoundingClientRect();
  if (!rect.width || !rect.height) return null;
  const ray = new mod.Raycaster();
  ray.setFromCamera(
    new mod.Vector2(((clientX - rect.left) / rect.width) * 2 - 1, 1 - ((clientY - rect.top) / rect.height) * 2),
    engine.camera,
  );
  return ray;
}

/** The cell where `ray` meets the plane at canvas `level`, or null off the plot, parallel to it or behind the camera. */
export function levelPoint(ray: PlacementRay, level: number): V3 | null {
  const { size, baseY } = plotMode();
  if (!Number.isFinite(level) || Math.abs(ray.direction.y) < 0.000001) return null;
  const t = (level + baseY - ray.origin.y) / ray.direction.y;
  if (!Number.isFinite(t) || t < 0) return null;
  const x = Math.floor(ray.origin.x + ray.direction.x * t);
  const z = Math.floor(ray.origin.z + ray.direction.z * t);
  return x >= 0 && x < size && z >= 0 && z < size ? [x, Math.floor(level), z] : null;
}

/** Distance along a ray to a cell volume (ray and cells in the same space), or null when it misses. */
export function rayBox(ray: PlacementRay, min: V3, max: V3): number | null {
  let near = 0,
    far = Infinity;
  for (const [i, axis] of (['x', 'y', 'z'] as const).entries()) {
    const start = ray.origin[axis],
      direction = ray.direction[axis];
    if (Math.abs(direction) < 0.000001) {
      if (start < min[i]! || start > max[i]! + 1) return null;
      continue;
    }
    const a = (min[i]! - start) / direction,
      b = (max[i]! + 1 - start) / direction;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
    if (near > far) return null;
  }
  return near;
}

/** The item with the box nearest along a world ray (boxes in canvas cells, inclusive corners); the first wins a tie. */
export function nearestHit<T extends { id: string }>(
  ray: PlacementRay,
  items: Iterable<T>,
  boxes: (item: T) => Iterable<readonly [V3, V3]>,
): { id: string; distance: number } | null {
  const baseY = plotMode().baseY;
  const canvasRay: PlacementRay = baseY
    ? { origin: { ...ray.origin, y: ray.origin.y - baseY }, direction: ray.direction }
    : ray;
  let nearest: { id: string; distance: number } | null = null;
  for (const item of items) {
    for (const [min, max] of boxes(item)) {
      const distance = rayBox(canvasRay, min, max);
      if (distance !== null && (!nearest || distance < nearest.distance)) nearest = { id: item.id, distance };
    }
  }
  return nearest;
}

/**
 * A cell for which `solid` holds lies on a world ray before `distance` (measured as nearestHit measures): something
 * stands in front of what the ray met there. Walks only the cells the ray crosses inside the plot (Amanatides and Woo).
 */
export function blockedBefore(
  ray: PlacementRay,
  distance: number,
  solid: (x: number, y: number, z: number) => boolean,
): boolean {
  const { size, minY, maxY, baseY } = plotMode();
  const o = { x: ray.origin.x, y: ray.origin.y - baseY, z: ray.origin.z };
  const d = ray.direction;
  const enter = rayBox({ origin: o, direction: d }, [0, minY, 0], [size - 1, maxY, size - 1]);
  if (enter === null || enter >= distance) return false;
  const t0 = enter + 1e-6;
  const axis = (from: number, dir: number) => {
    const p = from + dir * t0;
    const cell = Math.floor(p);
    const step = dir > 0 ? 1 : dir < 0 ? -1 : 0;
    const next = dir === 0 ? Infinity : t0 + ((dir > 0 ? cell + 1 : cell) - p) / dir;
    return { cell, step, next, delta: dir === 0 ? Infinity : Math.abs(1 / dir) };
  };
  const ax = axis(o.x, d.x);
  const ay = axis(o.y, d.y);
  const az = axis(o.z, d.z);
  // A cell entered at or after `distance` is the thing met itself, or behind it.
  for (let t = t0; t < distance - 1e-6;) {
    if (!inside(ax.cell, ay.cell, az.cell)) return false;
    if (solid(ax.cell, ay.cell, az.cell)) return true;
    const a = ax.next < ay.next ? (ax.next < az.next ? ax : az) : ay.next < az.next ? ay : az;
    t = a.next;
    a.cell += a.step;
    a.next += a.delta;
  }
  return false;
}
