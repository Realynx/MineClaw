import { plotMode, type V3 } from '../canvas';
import { modulePorts, type MineModuleInstance, type MineModuleSpec } from './modules';
import { minePlanePoint, type PlacementRay } from './placementRay';
import { modulePlacementPreview } from './placementPreview';

export type PlacementHeightMode = 'auto' | 'surface' | 'fixed';
export interface PlacementHeight { mode: PlacementHeightMode; surface: boolean; level: number }
type SurfaceTerrain = { surface(x: number, z: number): number };

/** Automatic access starts outdoors; continuing a selected piece uses its lower exit's feet level. */
export function modulePlacementHeight(spec: MineModuleSpec, mode: PlacementHeightMode, level: number, selected?: MineModuleInstance | null): PlacementHeight {
  if (mode === 'surface') return { mode, surface: true, level };
  if (mode === 'fixed') return { mode, surface: false, level };
  if (selected) {
    const ports = modulePorts(selected.spec, selected.at);
    return { mode, surface: false, level: ports.length ? Math.min(...ports.map((port) => port.at[1])) : selected.at[1] };
  }
  return { mode, surface: spec.kind === 'spiral' || spec.kind === 'stairs', level };
}

/** Raycast the visible column surface in O(plot width), without scanning underground caves or every plot cell. */
export function mineSurfacePoint(ray: PlacementRay, terrain: SurfaceTerrain): V3 | null {
  const { size, minY } = plotMode();
  let near = 0, far = Infinity;
  for (const axis of ['x', 'z'] as const) {
    const origin = ray.origin[axis], direction = ray.direction[axis];
    if (Math.abs(direction) < 1e-8) { if (origin < 0 || origin >= size) return null; continue; }
    const a = -origin / direction, b = (size - origin) / direction;
    near = Math.max(near, Math.min(a, b)); far = Math.min(far, Math.max(a, b));
  }
  if (!Number.isFinite(near) || far < near) return null;
  const epsilon = 1e-6;
  for (let steps = 0; steps < size * 2 + 2 && near <= far; steps++) {
    const x = Math.floor(ray.origin.x + ray.direction.x * (near + epsilon));
    const z = Math.floor(ray.origin.z + ray.direction.z * (near + epsilon));
    if (x < 0 || x >= size || z < 0 || z >= size) return null;
    const nextX = Math.abs(ray.direction.x) < 1e-8 ? Infinity : ((ray.direction.x > 0 ? x + 1 : x) - ray.origin.x) / ray.direction.x;
    const nextZ = Math.abs(ray.direction.z) < 1e-8 ? Infinity : ((ray.direction.z > 0 ? z + 1 : z) - ray.origin.z) / ray.direction.z;
    const exit = Math.min(far, nextX, nextZ);
    const feet = terrain.surface(x, z) + 1;
    const entryY = ray.origin.y + ray.direction.y * near;
    // A higher neighbouring column can be hit on its side before its horizontal top plane.
    if (entryY <= feet + epsilon && entryY >= minY) return [x, feet, z];
    const topAt = (feet - ray.origin.y) / ray.direction.y;
    if (Number.isFinite(topAt) && topAt >= near - epsilon && topAt <= exit + epsilon) return [x, feet, z];
    if (!Number.isFinite(exit) || exit >= far) return null;
    near = exit + epsilon;
  }
  return null;
}

/** The footprint is centred under the pointer, but its rotated top entrance rests on its own terrain column. */
export function surfaceModuleAt(spec: MineModuleSpec, at: V3, terrain: SurfaceTerrain): V3 {
  const offset = modulePlacementPreview(spec).entry;
  const entry: V3 = [at[0] + offset[0], offset[1], at[2] + offset[2]];
  return [at[0], terrain.surface(entry[0], entry[2]) + 1 - entry[1], at[2]];
}

export function minePlacementPoint(ray: PlacementRay, terrain: SurfaceTerrain, spec: MineModuleSpec, height: PlacementHeight): V3 | null {
  if (!height.surface) return minePlanePoint(ray, height.level);
  const surface = mineSurfacePoint(ray, terrain);
  return surface ? surfaceModuleAt(spec, surface, terrain) : null;
}
