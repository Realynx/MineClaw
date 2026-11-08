import { N6, type Cell, type V3 } from '../canvas';
import type { MineTerrain } from './mineTerrain';

export const MINE_PREVIEW_COLORS = { planned: 0x38bdf8, veins: 0xfbbf24, placement: 0xc084fc } as const;

export interface MinePreview {
  /** Only authored air, including intended tunnels passing through natural caves. */
  planned: V3[];
  /** Complete face-connected veins discovered by the authored route, regardless of excavation safety/ownership. */
  veins: V3[];
}

export type PreviewTerrain = Pick<MineTerrain, 'state' | 'version'>;
const key = (x: number, y: number, z: number): string => `${x},${y},${z}`;
const atKey = (p: readonly number[]): string => key(p[0]!, p[1]!, p[2]!);

/** Matches World.OreIdentity: lit redstone is the same vein; beta has no emerald or quartz. */
export function oreFamily(id: number, version: string): number {
  if ([14, 15, 16, 21, 56, 73].includes(id)) return id;
  if (id === 74) return 73;
  return version === '1.12.2' && (id === 129 || id === 153) ? id : 0;
}

/**
 * Discover from the designed excavation, never from all generated air or all seed ore. Six-face connectivity and ore identity
 * match OreIdentity; this preview shows the entire discovered vein, including floor/support/hazard-adjacent ore. It does not
 * authorize mining those cells: execution still applies its protection and safety rules. Authored blocks replace seed ore.
 * The real world's ore locations may differ from this example seed. No camera/selection state is involved in this calculation.
 */
export function minePreview(
  terrain: PreviewTerrain,
  dug: Iterable<readonly [number, number, number]>,
  placed: Iterable<Readonly<Cell>> = [],
): MinePreview {
  const plans = new Map<string, V3>();
  const blocks = new Map<string, number>();
  for (const c of placed) if (c[3] > 0) blocks.set(atKey(c), c[3]);
  for (const p of dug) {
    if (p[1] < 0 || p[1] >= 128 || blocks.has(atKey(p))) continue;
    plans.set(atKey(p), [...p]);
  }
  const veins = new Map<string, V3>();
  const queue: Array<{ p: V3; family: number }> = [];
  const familyAt = (p: V3) => oreFamily(terrain.state(...p) >> 4, terrain.version);
  const expose = (p: V3, family: number) => {
    const k = atKey(p);
    if (veins.has(k) || blocks.has(k) || p[1] < 1 || p[1] >= 128) return;
    const ore = familyAt(p);
    if (!ore || (family !== 0 && family !== ore)) return;
    veins.set(k, p);
    queue.push({ p, family: ore });
  };
  for (const p of plans.values()) {
    const family = familyAt(p);
    if (family) expose(p, family);
    // Every newly visible face can reveal an ore family, including when the planned cell itself contains another ore.
    for (const [dx, dy, dz] of N6) expose([p[0] + dx, p[1] + dy, p[2] + dz], 0);
  }
  for (let i = 0; i < queue.length; i++) {
    const { p, family } = queue[i]!;
    for (const [dx, dy, dz] of N6) expose([p[0] + dx, p[1] + dy, p[2] + dz], family);
  }
  return { planned: [...plans.values()], veins: [...veins.values()] };
}

/**
 * A voxel volume's exterior creases, in one line buffer. Shared faces and coplanar seams disappear; concave corners and
 * diagonally touching volumes retain their edges. No per-block Three objects, no duplicate segments, no internal wire grid.
 */
export function volumeOutline(cells: Iterable<readonly [number, number, number]>): Float32Array {
  const edges = new Map<string, { p: V3; axis: number; mask: number }>();
  const visited = new Set<string>();
  for (const cell of cells) {
    const k = atKey(cell);
    if (visited.has(k)) continue;
    visited.add(k);
    for (let axis = 0; axis < 3; axis++) {
      const u = (axis + 1) % 3,
        v = (axis + 2) % 3;
      for (let a = 0; a < 2; a++)
        for (let b = 0; b < 2; b++) {
          const p: V3 = [...cell];
          p[u]! += a;
          p[v]! += b;
          const edgeKey = `${axis}:${atKey(p)}`,
            bit = 1 << (a + 2 * b);
          const edge = edges.get(edgeKey);
          if (edge) edge.mask |= bit;
          else edges.set(edgeKey, { p, axis, mask: bit });
        }
    }
  }
  const out: number[] = [];
  for (const { p, axis, mask } of edges.values()) {
    // Two adjacent quadrants are a flat seam; all four are an interior edge.
    if (mask === 15 || mask === 3 || mask === 5 || mask === 10 || mask === 12) continue;
    const end: V3 = [...p];
    end[axis]!++;
    out.push(...p, ...end);
  }
  return new Float32Array(out);
}

/** Bound overlay work for huge plans: coarse occupied 8-block cells retain the route shape without millions of edges. */
export function previewOutline(cells: readonly V3[]): Float32Array {
  if (cells.length <= 12_000) return volumeOutline(cells);
  const coarse = new Map<string, V3>();
  for (const p of cells) {
    const q = p.map((n) => Math.floor(n / 8)) as V3;
    coarse.set(q.join(','), q);
  }
  const positions = volumeOutline(coarse.values());
  for (let i = 0; i < positions.length; i++) positions[i]! *= 8;
  return positions;
}
