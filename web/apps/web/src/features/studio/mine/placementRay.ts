import { plotMode, type V3 } from '../canvas';

export interface PlacementRay {
  origin: { x: number; y: number; z: number };
  direction: { x: number; y: number; z: number };
}

/** Place at the chosen mining level, independently of surface terrain and cutaway height. */
export function minePlanePoint(ray: PlacementRay, level: number): V3 | null {
  const { size, minY, maxY } = plotMode();
  if (!Number.isFinite(level) || level < minY + 1 || level > maxY - 2 || Math.abs(ray.direction.y) < 0.000001) return null;
  const t = (level - ray.origin.y) / ray.direction.y;
  if (!Number.isFinite(t) || t < 0) return null;
  const x = Math.floor(ray.origin.x + ray.direction.x * t);
  const z = Math.floor(ray.origin.z + ray.direction.z * t);
  return x >= 0 && x < size && z >= 0 && z < size ? [x, Math.floor(level), z] : null;
}

/** Distance along a ray to a cell volume; used to select modules even through the terrain. */
export function mineRayBox(ray: PlacementRay, min: V3, max: V3): number | null {
  let near = 0, far = Infinity;
  for (const [i, axis] of (['x', 'y', 'z'] as const).entries()) {
    const start = ray.origin[axis], direction = ray.direction[axis];
    if (Math.abs(direction) < 0.000001) {
      if (start < min[i]! || start > max[i]! + 1) return null;
      continue;
    }
    const a = (min[i]! - start) / direction, b = (max[i]! + 1 - start) / direction;
    near = Math.max(near, Math.min(a, b));
    far = Math.min(far, Math.max(a, b));
    if (near > far) return null;
  }
  return near;
}
