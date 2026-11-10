import type { V3 } from '../canvas';
import { prepareOutline } from '../components/outline';
import { moduleCells, moduleLocalPorts, type MineModulePort, type MineModuleSpec } from './modules';

export { MAX_PLACEMENT_OUTLINE_CELLS } from '../components/outline';
export interface PlacementPreview {
  bounds: { min: V3; max: V3 } | null;
  entry: V3;
  ports: readonly MineModulePort[];
  positions: Float32Array;
  cells: number;
  simplified: boolean;
}
const cache = new WeakMap<MineModuleSpec, PlacementPreview>();

/** Prepare once per immutable spec; subsequent pointer steps only translate this small result. */
export function modulePlacementPreview(spec: MineModuleSpec): PlacementPreview {
  const previous = cache.get(spec);
  if (previous) return previous;
  const shape = moduleCells(spec, [0, 0, 0]);
  const outline = prepareOutline(shape.cells);
  const result: PlacementPreview = {
    bounds: outline.bounds,
    entry: shape.entry,
    ports: moduleLocalPorts(spec),
    positions: outline.positions,
    cells: outline.cells,
    simplified: outline.simplified,
  };
  cache.set(spec, result);
  return result;
}
