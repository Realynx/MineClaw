import type { MeshHost } from '../mesh/host';
import type { GameVersion } from '../world/blocks';
import { playerBlockBoxes } from '../world/playerCollisionShapes';
import type { CollisionCell, CollisionLookup, PlayerCollisionBox, PlayerCollisionResult } from '../world/playerCollisionTypes';
export type { PlayerCollisionBox, PlayerCollisionResult } from '../world/playerCollisionTypes';

const missing: CollisionCell = { id: -1, meta: 0 };
const intersects = (a: PlayerCollisionBox, b: PlayerCollisionBox) => a.minX < b.maxX && a.maxX > b.minX
  && a.minY < b.maxY && a.maxY > b.minY && a.minZ < b.maxZ && a.maxZ > b.minZ;

/** The region fetched around the body's middle cell: 13 × 17 × 13 cells. */
const HALF_X = 6, HALF_Y = 8, HALF_Z = 6;
const SIZE_X = HALF_X * 2 + 1, SIZE_Y = HALF_Y * 2 + 1, SIZE_Z = HALF_Z * 2 + 1;
const CELLS = SIZE_X * SIZE_Y * SIZE_Z;
/** Cells kept between the body's cells and the region's edge; closer, a region around the body is fetched. */
const EDGE_MARGIN = 2;
/** A region is fetched again this often: it catches what the change feed cannot say (reloaded columns, a reconnect). */
const REFRESH_MS = 2000;
/** With no fetch answered for this long (the worker keeps failing) the region is no longer trusted. */
const STALE_MS = 6000;
/** The least time between two requests. */
const RETRY_MS = 100;
/** Changes kept while a fetch is out, to lay over its reply; past this the reply is dropped. */
const MAX_REPLAY = 4096;
/** Changes received this recently are laid over every fetched region too: the worker learns of a change only when the delta
 * feed flushes it (a flush waits while an earlier one is out), so a region it answers can still hold the old block. */
const RECENT_MS = 2000;
const MAX_RECENT = 1024;

type BlockChange = readonly number[];

interface Region {
  dim: number;
  x0: number;
  y0: number;
  z0: number;
  cells: CollisionCell[];
  sampledAt: number;
}

interface Override {
  dim: number;
  x: number;
  y: number;
  z: number;
  cell: CollisionCell;
  until: number;
}

/**
 * A collision neighbourhood copied from the local mesh worker. Frames never wait for worker or network IO: a region around
 * the body is held while the body stays inside it, block changes are laid in as they arrive (`patch`), and the takeover's
 * own confirmed digs and placements can be laid in before the world says so (`override`).
 */
export class PlayerCollisionCache {
  private region: Region | null = null;
  private dim: number | null = null;
  private attemptedAt = -Infinity;
  private pending = false;
  /** Changes received while a fetch is out (null: none out, or too many to replay). */
  private replay: BlockChange[] | null = null;
  private readonly overrides: Override[] = [];
  /** Changes received in the last RECENT_MS (oldest first), stamped with the time of the latest query. */
  private readonly recent: Array<{ at: number; block: BlockChange }> = [];
  private now = -Infinity;
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
    if (this.dim !== dim) { this.dim = dim; this.region = null; this.attemptedAt = -Infinity; this.revision++; this.recent.length = 0; }
    this.now = Math.max(this.now, now);
    this.expire(now);
    this.refresh(box, dim, now);
    const result = scanCollision(this.read, box, this.version());
    // Loaded = every scanned cell is held (a cell outside the region reads as missing) and the region is not stale.
    if (!this.region || now - this.region.sampledAt >= STALE_MS) result.loaded = false;
    return result;
  }

  /** Block changes as they arrive ([x, y, z, id, meta], in order): the held region takes them at once. */
  patch(dim: number, blocks: ReadonlyArray<BlockChange>): void {
    if (this.disposed || dim !== this.dim) return;
    const r = this.region;
    for (const b of blocks) {
      if (r) this.setCell(r, b[0], b[1], b[2], b[3], b[4]);
      this.dropOverride(dim, b[0], b[1], b[2]);
      this.recent.push({ at: this.now, block: b });
    }
    if (this.recent.length > MAX_RECENT) this.recent.splice(0, this.recent.length - MAX_RECENT);
    if (!this.replay) return;
    if (this.replay.length + blocks.length > MAX_REPLAY) this.replay = null;
    else for (const b of blocks) this.replay.push(b);
  }

  /** The cell reads as (id, meta) for `ttlMs`, whatever the region says, until a change for it arrives: the takeover's own
   * dig that the server confirmed (air) or its placement (the block). */
  override(dim: number, x: number, y: number, z: number, id: number, meta: number, ttlMs: number, now = performance.now()): void {
    if (this.disposed) return;
    this.dropOverride(dim, x, y, z);
    this.overrides.push({ dim, x, y, z, cell: { id, meta }, until: now + ttlMs });
    if (this.overrides.length > 16) this.overrides.shift();
  }

  /** The collision boxes of a held cell (world coordinates), or null when the cell is not held. */
  boxesAt(dim: number, x: number, y: number, z: number): PlayerCollisionBox[] | null {
    if (this.disposed || dim !== this.dim || this.read(x, y, z).id < 0) return null;
    return playerBlockBoxes(this.read, x, y, z, this.version());
  }

  /** The block (id, meta) of a held cell as the prediction sees it, or null when the cell is not held. */
  blockAt(dim: number, x: number, y: number, z: number): { id: number; meta: number } | null {
    if (this.disposed || dim !== this.dim) return null;
    const cell = this.read(x, y, z);
    return cell.id < 0 ? null : cell;
  }

  private readonly read = (x: number, y: number, z: number): CollisionCell => {
    for (const o of this.overrides) if (o.x === x && o.y === y && o.z === z && o.dim === this.dim) return o.cell;
    const r = this.region;
    if (!r) return missing;
    const i = cellIndex(r, x, y, z);
    return i < 0 ? missing : r.cells[i];
  };

  private refresh(box: PlayerCollisionBox, dim: number, now: number): void {
    if (this.pending || now - this.attemptedAt < RETRY_MS) return;
    const r = this.region;
    if (r && now - r.sampledAt < REFRESH_MS && holds(r, box)) return;
    const x0 = Math.floor((box.minX + box.maxX) / 2) - HALF_X,
      y0 = Math.floor((box.minY + box.maxY) / 2) - HALF_Y,
      z0 = Math.floor((box.minZ + box.maxZ) / 2) - HALF_Z;
    let old = 0;
    while (old < this.recent.length && now - this.recent[old].at >= RECENT_MS) old++;
    if (old) this.recent.splice(0, old);
    // The recent changes first: the worker's answer may not have them yet.
    this.pending = true; this.attemptedAt = now; this.replay = this.recent.map(r => r.block); const revision = this.revision;
    const points: number[] = [];
    for (let y = y0; y < y0 + SIZE_Y; y++)for (let z = z0; z < z0 + SIZE_Z; z++)for (let x = x0; x < x0 + SIZE_X; x++)points.push(x, y, z);
    void this.host.blocksAt(dim, points).then(values => {
      const replay = this.replay;
      if (this.disposed || revision !== this.revision || !replay) return;
      const cells = new Array<CollisionCell>(CELLS);
      for (let i = 0; i < CELLS; i++) cells[i] = { id: values[i * 3] ?? -1, meta: values[i * 3 + 1] ?? 0 };
      const region: Region = { dim, x0, y0, z0, cells, sampledAt: now };
      // Changes that arrived recently or while the worker answered are newer than (or the same as) what it read.
      for (const b of replay) this.setCell(region, b[0], b[1], b[2], b[3], b[4]);
      this.region = region;
    }).catch(() => { }).finally(() => { this.pending = false; this.replay = null; });
  }

  private setCell(r: Region, x: number, y: number, z: number, id: number, meta: number): void {
    const i = cellIndex(r, x, y, z);
    if (i >= 0 && (r.cells[i].id !== id || r.cells[i].meta !== meta)) r.cells[i] = { id, meta };
  }

  private dropOverride(dim: number, x: number, y: number, z: number): void {
    for (let i = this.overrides.length - 1; i >= 0; i--) {
      const o = this.overrides[i];
      if (o.x === x && o.y === y && o.z === z && o.dim === dim) this.overrides.splice(i, 1);
    }
  }

  private expire(now: number): void {
    for (let i = this.overrides.length - 1; i >= 0; i--) if (this.overrides[i].until <= now) this.overrides.splice(i, 1);
  }

  dispose() { this.disposed = true; this.region = null; this.overrides.length = 0; this.recent.length = 0; this.revision++; }
}

/**
 * What the body in `box` touches, read cell by cell (a cell with an id below 0 is not known, and the answer is then not
 * loaded): the collision boxes near it, the medium it moves through, the floor's slipperiness and whether it climbs.
 * The media use the game client's own boxes: water reaches into the body shrunk by 0.4 vertically (wading in water a
 * block deep is walking, not swimming), lava into the body shrunk by 0.1 sideways and 0.4 vertically, a web into the body
 * shrunk by 0.001.
 */
export function scanCollision(read: CollisionLookup, box: PlayerCollisionBox, version: GameVersion): PlayerCollisionResult {
  const result: PlayerCollisionResult = { loaded: true, boxes: [], medium: null, slipperiness: .6, climbable: false };
  const boxes: PlayerCollisionBox[] = [];
  const water = shrink(box, .001, .401), lava = shrink(box, .1, .4), web = shrink(box, .001, .001);
  let inWater = false, inLava = false, inWeb = false, nearLadder = false;
  // Piston heads extend outside their cell; fences/walls extend above it. Match the bot's collision scan margin.
  for (let y = Math.floor(box.minY) - 1; y <= Math.floor(box.maxY) + 1; y++)
    for (let z = Math.floor(box.minZ) - 1; z <= Math.floor(box.maxZ) + 1; z++)
      for (let x = Math.floor(box.minX) - 1; x <= Math.floor(box.maxX) + 1; x++) {
        const cell = read(x, y, z);
        if (cell.id < 0) { result.loaded = false; continue; }
        boxes.push(...playerBlockBoxes(read, x, y, z, version).filter(b => intersects(b, box)));
        const id = cell.id;
        if (id === 8 || id === 9) inWater ||= cellTouches(x, y, z, water);
        else if (id === 10 || id === 11) inLava ||= cellTouches(x, y, z, lava);
        else if (id === 30) inWeb ||= cellTouches(x, y, z, web);
        else if (id === 65 || id === 106) nearLadder ||= cellTouches(x, y, z, box);
      }
  result.medium = inWater ? 'water' : inLava ? 'lava' : inWeb ? 'web' : nearLadder ? 'ladder' : null;
  const cx = Math.floor((box.minX + box.maxX) / 2), cz = Math.floor((box.minZ + box.maxZ) / 2);
  result.climbable = climbable(read, cx, Math.floor(box.minY), cz, version);
  const floor = read(cx, Math.floor(box.minY - .01), cz).id;
  result.slipperiness = [79, 174, 212].includes(floor) ? .98 : floor === 165 ? .8 : .6;
  result.boxes = boxes;
  return result;
}

const shrink = (b: PlayerCollisionBox, side: number, vertical: number): PlayerCollisionBox => ({
  minX: b.minX + side, minY: b.minY + vertical, minZ: b.minZ + side, maxX: b.maxX - side, maxY: b.maxY - vertical, maxZ: b.maxZ - side,
});
const cellTouches = (x: number, y: number, z: number, b: PlayerCollisionBox) =>
  x < b.maxX && x + 1 > b.minX && y < b.maxY && y + 1 > b.minY && z < b.maxZ && z + 1 > b.minZ;

/** isOnLadder for the feet cell: b1.8.1 climbs ladders only; 1.12.2 also vines and an open trapdoor on top of a ladder
 * that faces the same way (as MineClaw.World's Shapes1122.IsClimbable). */
function climbable(read: CollisionLookup, x: number, y: number, z: number, version: GameVersion): boolean {
  const { id, meta } = read(x, y, z);
  if (id === 65) return true;
  if (version === 'b1.8.1') return false;
  if (id === 106) return true;
  if ((id !== 96 && id !== 167) || (meta & 4) === 0) return false;
  const below = read(x, y - 1, z);
  if (below.id !== 65) return false;
  return Math.max(2, below.meta % 6) === [2, 3, 4, 5][meta & 3];
}

/** The region's index of a cell, -1 outside it. */
function cellIndex(r: Region, x: number, y: number, z: number): number {
  const dx = x - r.x0, dy = y - r.y0, dz = z - r.z0;
  if (dx < 0 || dy < 0 || dz < 0 || dx >= SIZE_X || dy >= SIZE_Y || dz >= SIZE_Z) return -1;
  return (dy * SIZE_Z + dz) * SIZE_X + dx;
}

/** Whether the body's cells keep the edge margin inside the region. */
function holds(r: Region, box: PlayerCollisionBox): boolean {
  return Math.floor(box.minX) >= r.x0 + EDGE_MARGIN && Math.floor(box.maxX) < r.x0 + SIZE_X - EDGE_MARGIN
    && Math.floor(box.minY) >= r.y0 + EDGE_MARGIN && Math.floor(box.maxY) < r.y0 + SIZE_Y - EDGE_MARGIN
    && Math.floor(box.minZ) >= r.z0 + EDGE_MARGIN && Math.floor(box.maxZ) < r.z0 + SIZE_Z - EDGE_MARGIN;
}
