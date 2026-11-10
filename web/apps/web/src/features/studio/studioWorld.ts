/**
 * The Design Studio's world: an in-memory editable `WorldSource` for @mineclaw/world-viewer. A flat grassland (8×8 columns,
 * grass at y 63, three dirt layers, stone) whose middle 64×64 is the canvas (canvas y 0 = world y 64, canvas y -1 the grass):
 * the canvas's dug cells are air in the ground, its blocks below y 0 replace the ground, noon, no weather, no bots. Edits are pushed as
 * block deltas through `subscribe`, so the viewer patches its store and re-meshes only the touched sections; the column
 * hashes never change (a refetch, if the viewer evicts a column, encodes the current canvas).
 *
 * A mine plot (`terrain` given) is generated terrain instead of the grassland, the ring of columns around the plot too, at
 * absolute y (canvas y = world y): every column is its terrain chunk (./mine/mineTerrain, generated once per chunk and kept),
 * the canvas's dug cells cut out of it. Only sections near the camera are meshed (the viewer's own draw distance).
 */
import type { BlockDeltas, EntityDelta, Overlays, WorldIndex, WorldSource } from '@mineclaw/world-viewer';
import { cellIndex } from '@mineclaw/world-viewer';
import { encodeColumn, encodeMcwc, lightSections, type EncodeEntry } from '@mineclaw/world-viewer/testing';
import { EARTH, posOf, plotMode, type Canvas, type Cell, type V3 } from './canvas';
import type { Version } from './facing';
import type { MineTerrain } from './mine/mineTerrain';

/** World y of canvas y 0 (the grass is one below). */
export const BASE_Y = 64;
/** Columns of grassland around the plot (the plot is columns 0..3). */
const RING = 2;
const C0 = -RING;

const GRASS = 2,
  DIRT = 3,
  STONE = 1,
  BEDROCK = 7;

/** The untouched ground at a world y: grass on top (63), three dirt layers, stone, bedrock at 0; air above. */
export function groundAt(y: number): number {
  const top = BASE_Y - 1;
  return y > top ? 0 : y === top ? GRASS : y >= top - 3 ? DIRT : y > 0 ? STONE : BEDROCK;
}

/** Transparent ids for the sky light estimate (air, glass, panes, fences, doors, torches, ladders, slabs, stairs…). */
const CLEAR = new Set([
  0, 6, 20, 26, 27, 28, 30, 31, 32, 37, 38, 39, 40, 44, 50, 53, 54, 55, 59, 63, 64, 65, 66, 67, 68, 69, 71, 75, 76, 77,
  78, 81, 83, 85, 96, 101, 102, 104, 106, 107, 108, 109, 113, 114, 128, 134, 135, 136, 160,
]);

export class StudioWorld {
  readonly height: number;
  private readonly lastColumn: number;
  private rev = 1;
  private listeners = new Set<(d: BlockDeltas) => void>();
  readonly source: WorldSource;

  /** World y of canvas y 0: 64 over the grassland, 0 on a mine's terrain. */
  readonly base: number;

  constructor(
    readonly version: Version,
    private readonly canvas: Canvas,
    private readonly now: () => number = () => Date.now(),
    readonly terrain: MineTerrain | null = null,
  ) {
    this.height = version === '1.12.2' ? 256 : 128;
    this.base = terrain ? 0 : BASE_Y;
    this.lastColumn = Math.ceil(plotMode().size / 16) - 1;
    const last = this.lastColumn + RING;
    const hashOf = (cx: number, cz: number) => BigInt(((cx + 16) * 64 + (cz + 16)) * 7919 + 1);
    this.source = {
      index: (dim: number): Promise<WorldIndex> => {
        const columns: WorldIndex['columns'] = [];
        if (dim === 0)
          for (let cx = C0; cx <= last; cx++)
            for (let cz = C0; cz <= last; cz++)
              columns.push({ cx, cz, hash: hashOf(cx, cz).toString(), seen: this.now(), by: 'studio', live: true });
        return Promise.resolve({
          dim,
          at: this.now(),
          columns,
          version: this.version,
          time: 6000,
          rain: 0,
          thunder: 0,
        });
      },
      chunks: (dim: number, cols: Array<[number, number]>): Promise<ArrayBuffer> => {
        // One pass over the canvas for the whole request, not one per column (a big mine has many columns and cells).
        let index: ColumnIndex | null = null;
        const columns = () => (index ??= this.indexColumns());
        const entries: EncodeEntry[] = cols.map(([cx, cz]) => {
          if (dim !== 0 || cx < C0 || cx > last || cz < C0 || cz > last)
            return {
              cx,
              cz,
              hash: 0n,
              seenMs: this.now(),
              by: null,
              live: false,
              data: new Uint8Array(0),
              light: null,
            };
          const e = this.encode(cx, cz, columns);
          return {
            cx,
            cz,
            hash: hashOf(cx, cz),
            seenMs: this.now(),
            by: 'studio',
            live: true,
            data: e.data,
            light: e.light,
          };
        });
        return Promise.resolve(encodeMcwc(dim, entries));
      },
      entities: (since?: number): Promise<EntityDelta> =>
        Promise.resolve({
          at: this.now(),
          rev: 1,
          full: since === undefined,
          bots: [],
          entities: [],
          removed: [],
          events: [],
        }),
      overlays: (dim: number): Promise<Overlays> =>
        Promise.resolve({ at: this.now(), dim, claims: [], places: [], points: [], plans: [], dangers: [], notes: [] }),
      blocks: (): Promise<BlockDeltas> => Promise.resolve({ rev: this.rev, full: false, blocks: {}, columns: [] }),
      subscribe: (onBlocks) => {
        this.listeners.add(onBlocks);
        return () => this.listeners.delete(onBlocks);
      },
    };
  }

  /** Pushes canvas cells (local coordinates) to the viewer as one block delta. */
  push(cells: readonly Cell[]): void {
    if (cells.length === 0) return;
    this.rev++;
    const d: BlockDeltas = {
      at: this.now(),
      rev: this.rev,
      full: false,
      blocks: {
        '0': cells.map(([x, y, z, id, meta]): [number, number, number, number, number] =>
          id === EARTH
            ? this.terrain
              ? [x, y, z, ...this.terrain.block(x, y, z)]
              : [x, y + BASE_Y, z, groundAt(y + BASE_Y), 0]
            : [x, y + this.base, z, id, meta],
        ),
      },
      columns: [],
    };
    for (const l of this.listeners) l(d);
  }

  /** One column: bedrock, stone, dirt and grass up to y 63 (or the mine's terrain chunk), plus the canvas blocks over it. */
  private encode(cx: number, cz: number, columns: () => ColumnIndex) {
    const n = 16 * this.height * 16;
    const ids = new Uint8Array(n),
      meta = new Uint8Array(n);
    if (this.terrain) this.fillTerrain(ids, meta, this.terrain, cx, cz);
    else fillGrassland(ids);
    if (cx >= 0 && cx <= this.lastColumn && cz >= 0 && cz <= this.lastColumn)
      this.overlayCanvas(ids, meta, columns().get(columnKey(cx, cz)));
    const tops = skyTops(ids, this.height);
    const light = lightSections(
      this.height,
      (x, y, z) => (y > tops[x * 16 + z]! ? 15 : 0),
      (x, y, z) => blockLight(ids[cellIndex(x, y, z)]!),
    );
    const biomes = this.version === '1.12.2' ? new Uint8Array(256).fill(1) : null;
    return encodeColumn(ids, meta, light, biomes);
  }

  /** The terrain chunk, (y·16 + lz)·16 + lx, id << 4 | meta. */
  private fillTerrain(ids: Uint8Array, meta: Uint8Array, terrain: MineTerrain, cx: number, cz: number) {
    const t = terrain.chunk(cx, cz);
    const h = Math.min(this.height, 128);
    for (let y = 0; y < h; y++)
      for (let lz = 0; lz < 16; lz++)
        for (let lx = 0; lx < 16; lx++) {
          const v = t[(y * 16 + lz) * 16 + lx]!;
          if (v === 0) continue;
          const i = cellIndex(lx, y, lz);
          ids[i] = v >> 4;
          meta[i] = v & 15;
        }
  }

  /** The canvas's dug cells and blocks by plot column, each in the canvas's own order. */
  private indexColumns(): ColumnIndex {
    const index: ColumnIndex = new Map();
    const of = (x: number, z: number) => {
      const k = columnKey(x >> 4, z >> 4);
      let column = index.get(k);
      if (!column) index.set(k, (column = { dug: [], blocks: [] }));
      return column;
    };
    for (const k of this.canvas.dug) {
      const p = posOf(k);
      of(p[0], p[2]).dug.push(p);
    }
    for (const [k, v] of this.canvas.blocks) {
      const p = posOf(k);
      of(p[0], p[2]).blocks.push([p, v]);
    }
    return index;
  }

  /** The canvas's dug cells (air) then its blocks, in a plot column. */
  private overlayCanvas(ids: Uint8Array, meta: Uint8Array, column: CanvasColumn | undefined) {
    if (!column) return;
    const base = this.base;
    for (const [x, y, z] of column.dug) {
      const i = cellIndex(x & 15, y + base, z & 15);
      ids[i] = 0;
      meta[i] = 0;
    }
    for (const [[x, y, z], v] of column.blocks) {
      const i = cellIndex(x & 15, y + base, z & 15);
      ids[i] = v >> 4;
      meta[i] = v & 15;
    }
  }
}

type CanvasColumn = { dug: V3[]; blocks: Array<[V3, number]> };
type ColumnIndex = Map<number, CanvasColumn>;
/** Plot columns only (0 to the last column), so a small multiplier keeps keys distinct. */
const columnKey = (cx: number, cz: number) => cx * 4096 + cz;

/** The grassland column: bedrock at 0, stone, three dirt layers, grass at y 63. */
function fillGrassland(ids: Uint8Array) {
  const ground = BASE_Y - 1;
  for (let lx = 0; lx < 16; lx++)
    for (let lz = 0; lz < 16; lz++) {
      ids[cellIndex(lx, 0, lz)] = BEDROCK;
      for (let y = 1; y < ground - 3; y++) ids[cellIndex(lx, y, lz)] = STONE;
      for (let y = ground - 3; y < ground; y++) ids[cellIndex(lx, y, lz)] = DIRT;
      ids[cellIndex(lx, ground, lz)] = GRASS;
    }
}

/** Per (x, z) (index x·16 + z): the highest block the sky light stops at (0 when none). */
function skyTops(ids: Uint8Array, height: number): Int16Array {
  const tops = new Int16Array(256);
  for (let lx = 0; lx < 16; lx++)
    for (let lz = 0; lz < 16; lz++) {
      let y = height - 1;
      while (y > 0 && CLEAR.has(ids[cellIndex(lx, y, lz)]!)) y--;
      tops[lx * 16 + lz] = y;
    }
  return tops;
}

/** Block light: glowstone and jack-o'-lanterns 15, torches 14. */
const blockLight = (id: number): number => (id === 89 || id === 91 ? 15 : id === 50 ? 14 : 0);
