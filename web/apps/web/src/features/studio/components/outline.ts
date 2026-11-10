import type { V3 } from '../canvas';
import { volumeOutline } from '../mine/minePreview';
import type { Overlay3D, PortArrow } from '../overlay3d';

/** Past this many cells a moving component shows its bounds instead of its exact outline. */
export const MAX_PLACEMENT_OUTLINE_CELLS = 4096;

export interface PreparedOutline {
  /** The cells' bounds relative to the corner, or null when there are none. */
  bounds: { min: V3; max: V3 } | null;
  /** Line segments relative to the corner: the overlay uploads them once and then only translates them. */
  positions: Float32Array;
  cells: number;
  simplified: boolean;
}

/** Twelve complete edges instead of a costly voxel scan for a large moving component. */
function boundsOutline(min: V3, max: V3): Float32Array {
  const positions: number[] = [];
  for (let axis = 0; axis < 3; axis++) {
    const u = (axis + 1) % 3,
      v = (axis + 2) % 3;
    for (let a = 0; a < 2; a++)
      for (let b = 0; b < 2; b++) {
        const from: V3 = [...min];
        from[u] = a ? max[u]! + 1 : min[u]!;
        from[v] = b ? max[v]! + 1 : min[v]!;
        const to: V3 = [...from];
        to[axis] = max[axis]! + 1;
        positions.push(...from, ...to);
      }
  }
  return new Float32Array(positions);
}

/** A component's outline, prepared once per drag or piece in hand from its cells relative to its corner. */
export function prepareOutline(cells: readonly (readonly [number, number, number, ...number[]])[]): PreparedOutline {
  const min: V3 = [Infinity, Infinity, Infinity],
    max: V3 = [-Infinity, -Infinity, -Infinity];
  for (const cell of cells)
    for (let axis = 0; axis < 3; axis++) {
      min[axis] = Math.min(min[axis]!, cell[axis]!);
      max[axis] = Math.max(max[axis]!, cell[axis]!);
    }
  const simplified = cells.length > MAX_PLACEMENT_OUTLINE_CELLS;
  return {
    bounds: cells.length ? { min, max } : null,
    positions: simplified ? boundsOutline(min, max) : volumeOutline(cells.map((c) => [c[0], c[1], c[2]])),
    cells: cells.length,
    simplified,
  };
}

/** What a moving piece's preview draws on. */
export type PreviewOverlay = Pick<Overlay3D, 'setMinePlacementGeometry' | 'setComponentPorts'> &
  Partial<Pick<Overlay3D, 'setPortArrows'>>;

/**
 * One frame of a piece in hand (a drag or a placing ghost): its prepared outline moved to `at`, red where `refused`, its
 * port squares (amber open, green joined) and, when given, each port's arrow about `at`. Only translations and small
 * marker buffers: nothing is rebuilt from the cells.
 */
export function showDragPreview(
  overlay: PreviewOverlay,
  positions: Float32Array,
  at: V3,
  refused: boolean,
  open: readonly (readonly [number, number, number])[],
  joined: readonly (readonly [number, number, number])[],
  arrows?: readonly PortArrow[],
): void {
  overlay.setMinePlacementGeometry(positions, at, refused);
  overlay.setComponentPorts(open, joined);
  if (arrows) overlay.setPortArrows?.(arrows, at, joined);
}
