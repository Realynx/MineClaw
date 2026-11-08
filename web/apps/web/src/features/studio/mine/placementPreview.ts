import type { V3 } from '../canvas';
import { volumeOutline } from './minePreview';
import { moduleCells, moduleLocalPorts, type MineModulePort, type MineModuleSpec } from './modules';

export const MAX_PLACEMENT_OUTLINE_CELLS = 4096;
export interface PlacementPreview {
  bounds: { min: V3; max: V3 } | null;
  entry: V3;
  ports: readonly MineModulePort[];
  positions: Float32Array;
  cells: number;
  simplified: boolean;
}
const cache = new WeakMap<MineModuleSpec, PlacementPreview>();

/** Twelve complete edges instead of a costly voxel scan for a large moving component. */
function boundsOutline(min: V3, max: V3): Float32Array {
  const positions: number[] = [];
  for (let axis = 0; axis < 3; axis++) {
    const u = (axis + 1) % 3, v = (axis + 2) % 3;
    for (let a = 0; a < 2; a++) for (let b = 0; b < 2; b++) {
      const from: V3 = [...min];
      from[u] = a ? max[u]! + 1 : min[u]!;
      from[v] = b ? max[v]! + 1 : min[v]!;
      const to: V3 = [...from]; to[axis] = max[axis]! + 1;
      positions.push(...from, ...to);
    }
  }
  return new Float32Array(positions);
}

/** Prepare once per immutable spec; subsequent pointer steps only translate this small result. */
export function modulePlacementPreview(spec: MineModuleSpec): PlacementPreview {
  const previous = cache.get(spec);
  if (previous) return previous;
  const shape = moduleCells(spec, [0, 0, 0]);
  const min: V3 = [Infinity, Infinity, Infinity], max: V3 = [-Infinity, -Infinity, -Infinity];
  for (const cell of shape.cells) for (let axis = 0; axis < 3; axis++) {
    min[axis] = Math.min(min[axis]!, cell[axis]!);
    max[axis] = Math.max(max[axis]!, cell[axis]!);
  }
  const simplified = shape.cells.length > MAX_PLACEMENT_OUTLINE_CELLS;
  const result: PlacementPreview = {
    bounds: shape.cells.length ? { min, max } : null,
    entry: shape.entry,
    ports: moduleLocalPorts(spec),
    positions: simplified ? boundsOutline(min, max) : volumeOutline(shape.cells.map((c) => [c[0], c[1], c[2]])),
    cells: shape.cells.length,
    simplified,
  };
  cache.set(spec, result);
  return result;
}
