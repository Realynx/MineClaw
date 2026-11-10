import { plotMode, type V3 } from '../canvas';
import { levelPoint, rayBox, type PlacementRay } from '../components/levelRay';

export type { PlacementRay };

/** Place at the chosen mining level, independently of surface terrain and cutaway height. */
export function minePlanePoint(ray: PlacementRay, level: number): V3 | null {
  const { minY, maxY } = plotMode();
  if (!Number.isFinite(level) || level < minY + 1 || level > maxY - 2) return null;
  return levelPoint(ray, level);
}

/** Distance along a ray to a cell volume; used to select modules even through the terrain. */
export const mineRayBox = rayBox;
