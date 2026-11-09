import type { MeshHost } from '../mesh/host';
import type { GameVersion } from '../world/blocks';
import { playerBlockBoxes } from '../world/playerCollisionShapes';
import type { CollisionCell, PlayerCollisionBox, PlayerCollisionResult } from '../world/playerCollisionTypes';
export type { PlayerCollisionBox, PlayerCollisionResult } from '../world/playerCollisionTypes';

const missing: CollisionCell = { id: -1, meta: 0 };
const key = (x: number, y: number, z: number) => `${x},${y},${z}`;
const intersects = (a: PlayerCollisionBox, b: PlayerCollisionBox) => a.minX < b.maxX && a.maxX > b.minX
  && a.minY < b.maxY && a.maxY > b.minY && a.minZ < b.maxZ && a.maxZ > b.minZ;

/** A small collision neighbourhood copied from the existing local mesh worker. Frames never wait for worker/network IO. */
export class PlayerCollisionCache {
  private cells = new Map<string, CollisionCell>();
  private dim: number | null = null;
  private sampledAt = -Infinity;
  private requested = '';
  private attemptedAt = -Infinity;
  private pending = false;
  private disposed = false;
  private revision = 0;
  constructor(private readonly host: Pick<MeshHost, 'blocksAt'>, private readonly version: () => GameVersion) { }

  query(box: PlayerCollisionBox, dim: number, now = performance.now()): PlayerCollisionResult {
    // Bound coordinates before incrementing integer scan loops, including malformed server state.
    const coordinates = [box.minX, box.minY, box.minZ, box.maxX, box.maxY, box.maxZ];
    if (this.disposed || !coordinates.every(value => Number.isFinite(value) && Math.abs(value) <= 32_000_000)
      || !Number.isSafeInteger(dim) || box.maxX < box.minX || box.maxY < box.minY || box.maxZ < box.minZ
      || box.maxX - box.minX > 8 || box.maxY - box.minY > 8 || box.maxZ - box.minZ > 8)
      return { loaded: false, boxes: [] };
    if (this.dim !== dim) { this.dim = dim; this.cells.clear(); this.revision++; }
    this.refresh(box, dim, now);
    const read = (x: number, y: number, z: number) => this.cells.get(key(x, y, z)) ?? missing;
    const result: PlayerCollisionResult = { loaded: now - this.sampledAt < 500, boxes: [], medium: null, slipperiness: .6 };
    const boxes: PlayerCollisionBox[] = [];
    // Piston heads extend outside their cell; fences/walls extend above it. Match the bot's collision scan margin.
    for (let y = Math.floor(box.minY) - 1; y <= Math.floor(box.maxY) + 1; y++)
      for (let z = Math.floor(box.minZ) - 1; z <= Math.floor(box.maxZ) + 1; z++)
        for (let x = Math.floor(box.minX) - 1; x <= Math.floor(box.maxX) + 1; x++) {
          const cell = read(x, y, z);
          if (cell.id < 0) { result.loaded = false; continue; }
          boxes.push(...playerBlockBoxes(read, x, y, z, this.version()).filter(b => intersects(b, box)));
          if (intersects({ minX: x, minY: y, minZ: z, maxX: x + 1, maxY: y + 1, maxZ: z + 1 }, box)) {
            if (cell.id === 8 || cell.id === 9) result.medium = 'water';
            else if (cell.id === 10 || cell.id === 11) result.medium = 'lava';
            else if (cell.id === 30) result.medium = 'web';
            else if ((cell.id === 65 || cell.id === 106) && !result.medium) result.medium = 'ladder';
          }
        }
    const floor = read(Math.floor((box.minX + box.maxX) / 2), Math.floor(box.minY - .01), Math.floor((box.minZ + box.maxZ) / 2)).id;
    result.slipperiness = [79, 174, 212].includes(floor) ? .98 : floor === 165 ? .8 : .6;
    result.boxes = boxes;
    return result;
  }

  private refresh(box: PlayerCollisionBox, dim: number, now: number) {
    const x = Math.floor((box.minX + box.maxX) / 2), y = Math.floor((box.minY + box.maxY) / 2), z = Math.floor((box.minZ + box.maxZ) / 2);
    const center = key(x, y, z);
    const requestKey = `${dim}:${center}`;
    if (this.pending || requestKey === this.requested && now - this.attemptedAt < 100) return;
    this.pending = true; this.requested = requestKey; this.attemptedAt = now; const revision = this.revision;
    const points: number[] = [];
    for (let yy = y - 4; yy <= y + 4; yy++)for (let zz = z - 3; zz <= z + 3; zz++)for (let xx = x - 3; xx <= x + 3; xx++)points.push(xx, yy, zz);
    void this.host.blocksAt(dim, points).then(values => {
      if (this.disposed || revision !== this.revision) return;
      const cells = new Map<string, CollisionCell>();
      for (let i = 0; i < points.length; i += 3)cells.set(key(points[i], points[i + 1], points[i + 2]), { id: values[i] ?? -1, meta: values[i + 1] ?? 0 });
      this.cells = cells; this.sampledAt = now;
    }).catch(() => { }).finally(() => { this.pending = false; });
  }
  dispose() { this.disposed = true; this.cells.clear(); this.revision++; }
}
