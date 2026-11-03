/**
 * The Design Studio's world: an in-memory editable `WorldSource` for @mineclaw/world-viewer. A flat grassland (8×8 columns,
 * grass at y 63, three dirt layers, stone) whose middle 64×64 is the canvas (canvas y 0 = world y 64, canvas y -1 the grass):
 * the canvas's dug cells are air in the ground, its blocks below y 0 replace the ground, noon, no weather, no bots. Edits are pushed as
 * block deltas through `subscribe`, so the viewer patches its store and re-meshes only the touched sections; the column
 * hashes never change (a refetch, if the viewer evicts a column, encodes the current canvas).
 */
import type { BlockDeltas, EntityDelta, Overlays, WorldIndex, WorldSource } from '@mineclaw/world-viewer';
import { cellIndex } from '@mineclaw/world-viewer';
import { encodeColumn, encodeMcwc, lightSections, type EncodeEntry } from '@mineclaw/world-viewer/testing';
import { EARTH, posOf, type Canvas, type Cell } from './canvas';
import type { Version } from './facing';

/** World y of canvas y 0 (the grass is one below). */
export const BASE_Y = 64;
/** Columns of grassland around the plot (the plot is columns 0..3). */
const RING = 2;
const C0 = -RING,
  C1 = 3 + RING;

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
  private rev = 1;
  private listeners = new Set<(d: BlockDeltas) => void>();
  readonly source: WorldSource;

  constructor(
    readonly version: Version,
    private readonly canvas: Canvas,
    private readonly now: () => number = () => Date.now(),
  ) {
    this.height = version === '1.12.2' ? 256 : 128;
    const hashOf = (cx: number, cz: number) => BigInt(((cx + 16) * 64 + (cz + 16)) * 7919 + 1);
    this.source = {
      index: (dim: number): Promise<WorldIndex> => {
        const columns: WorldIndex['columns'] = [];
        if (dim === 0)
          for (let cx = C0; cx <= C1; cx++)
            for (let cz = C0; cz <= C1; cz++)
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
        const entries: EncodeEntry[] = cols.map(([cx, cz]) => {
          if (dim !== 0 || cx < C0 || cx > C1 || cz < C0 || cz > C1)
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
          const e = this.encode(cx, cz);
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
          id === EARTH ? [x, y + BASE_Y, z, groundAt(y + BASE_Y), 0] : [x, y + BASE_Y, z, id, meta],
        ),
      },
      columns: [],
    };
    for (const l of this.listeners) l(d);
  }

  /** One column: bedrock, stone, dirt and grass up to y 63, plus the canvas blocks over it. */
  private encode(cx: number, cz: number) {
    const n = 16 * this.height * 16;
    const ids = new Uint8Array(n),
      meta = new Uint8Array(n);
    const ground = BASE_Y - 1;
    for (let lx = 0; lx < 16; lx++)
      for (let lz = 0; lz < 16; lz++) {
        ids[cellIndex(lx, 0, lz)] = BEDROCK;
        for (let y = 1; y < ground - 3; y++) ids[cellIndex(lx, y, lz)] = STONE;
        for (let y = ground - 3; y < ground; y++) ids[cellIndex(lx, y, lz)] = DIRT;
        ids[cellIndex(lx, ground, lz)] = GRASS;
      }
    if (cx >= 0 && cx <= 3 && cz >= 0 && cz <= 3) {
      for (const k of this.canvas.dug) {
        const [x, y, z] = posOf(k);
        if (x >> 4 !== cx || z >> 4 !== cz) continue;
        ids[cellIndex(x & 15, y + BASE_Y, z & 15)] = 0;
      }
      for (const [k, v] of this.canvas.blocks) {
        const [x, y, z] = posOf(k);
        if (x >> 4 !== cx || z >> 4 !== cz) continue;
        const i = cellIndex(x & 15, y + BASE_Y, z & 15);
        ids[i] = v >> 4;
        meta[i] = v & 15;
      }
    }
    const tops = new Int16Array(256);
    for (let lx = 0; lx < 16; lx++)
      for (let lz = 0; lz < 16; lz++) {
        let y = this.height - 1;
        while (y > 0 && CLEAR.has(ids[cellIndex(lx, y, lz)]!)) y--;
        tops[lx * 16 + lz] = y;
      }
    const light = lightSections(
      this.height,
      (x, y, z) => (y > tops[x * 16 + z]! ? 15 : 0),
      (x, y, z) =>
        ids[cellIndex(x, y, z)] === 89 || ids[cellIndex(x, y, z)] === 91 ? 15 : ids[cellIndex(x, y, z)] === 50 ? 14 : 0,
    );
    const biomes = this.version === '1.12.2' ? new Uint8Array(256).fill(1) : null;
    return encodeColumn(ids, meta, light, biomes);
  }
}
