// [holograms] Meshes queued build cells (and a design being placed) as the blocks they will become, in the worker that holds
// the world: each request's blocks go into a private, throw-away column store on their own (no world around them, so a wall's
// faces between its own cells are culled but nothing else hides them) and are meshed by the ordinary section mesher (real
// block models: stairs, doors, fences and torches in their variant and facing), then merged into ONE vertex set per request,
// positioned relative to an origin, so the main thread draws a request with a single draw call.
// The world decides what still shows ("match by cell"):
// - build: a cell whose world block already has the target id is built and left out; a liquid to pour (water, lava) is also
//   left out while a solid block still fills its cell (it waits for its dig: the trench's dig outline shows there instead);
// - dig: a cell whose world block is already air is dug and left out (dig cells come back as cells, drawn as outlines);
// - place: nothing is left out, but the cells are split into placeable and blocked (a solid block is in the way, unless the
//   placement digs that cell first: `clear`).
// Cells in columns the worker does not hold always show.

import { cellIndex } from '../wire/chunk';
import { ColumnStore, type StoredColumn } from './columns';
import { meshSection } from './mesher';
import type { MeshArrays } from './sink';
import type { StateTable } from './states';

export type HoloMode = 'build' | 'dig' | 'place';

export interface HoloRequest {
  /** The caller's group key (a state, a build); answered under the same key ('place' answers `key:ok` and `key:blocked`). */
  key: string;
  /** Flat [x, y, z, id, meta, …]. */
  blocks: number[];
  mode: HoloMode;
  /** 'place': cells the placement digs out first, flat [x, y, z, …] (a solid block there is not in the way). */
  clear?: number[];
}

/** One request's merged geometry: the section mesher's attributes, positions relative to `origin`, plus each vertex's block cell
 * (relative to `origin` too) for the shader's cut test. */
export interface HoloMesh {
  origin: [number, number, number];
  pos: Float32Array;
  uv: Float32Array;
  tile: Uint16Array;
  color: Uint8Array;
  cell: Float32Array;
  index: Uint32Array;
  quads: number;
}

export interface HoloResult {
  key: string;
  mesh: HoloMesh | null;
  /** The cells shown, flat [x, y, z, id, meta, …] (dig cells: the ones still to dig). */
  cells: number[];
}

/** Water and lava, flowing and still. */
const LIQUIDS = new Set([8, 9, 10, 11]);
/** Blocks a placement may replace (air, plants, snow, vines, water). */
const REPLACEABLE = new Set([0, 8, 9, 31, 32, 37, 38, 78, 106, 175]);

/** Answers every request; `world` is the worker's own store (read only). */
export function meshHolograms(world: ColumnStore, dim: number, table: StateTable, reqs: readonly HoloRequest[]): HoloResult[] {
  const out: HoloResult[] = [];
  for (const r of reqs) {
    if (r.mode === 'place') out.push(...placeResults(world, dim, table, r));
    else out.push(pendingResult(world, dim, table, r));
  }
  return out;
}

/** A placement preview: the cells that fit (`key:ok`) and those a block is in the way of (`key:blocked`). */
function placeResults(world: ColumnStore, dim: number, table: StateTable, r: HoloRequest): [HoloResult, HoloResult] {
  const ok: number[] = [], blocked: number[] = [];
  const clear = clearedCells(r.clear);
  const n = Math.floor(r.blocks.length / 5);
  for (let i = 0; i < n; i++) {
    const b = r.blocks.slice(i * 5, i * 5 + 5);
    const w = world.blockAt(dim, b[0], b[1], b[2]);
    (w && !REPLACEABLE.has(w.id) && b[3] !== 0 && !clear.has(`${b[0]},${b[1]},${b[2]}`) ? blocked : ok).push(...b);
  }
  return [result(`${r.key}:ok`, ok, table), result(`${r.key}:blocked`, blocked, table)];
}

function clearedCells(flat: number[] | undefined): Set<string> {
  const clear = new Set<string>();
  for (let i = 0; i + 2 < (flat?.length ?? 0); i += 3) clear.add(`${flat![i]},${flat![i + 1]},${flat![i + 2]}`);
  return clear;
}

/** A build or dig: the cells still to do (a built cell holds its block, a dug one air; a liquid can't go where a solid is). */
function pendingResult(world: ColumnStore, dim: number, table: StateTable, r: HoloRequest): HoloResult {
  const keep: number[] = [];
  const n = Math.floor(r.blocks.length / 5);
  for (let i = 0; i < n; i++) {
    const o = i * 5;
    const x = r.blocks[o], y = r.blocks[o + 1], z = r.blocks[o + 2], id = r.blocks[o + 3];
    const w = world.blockAt(dim, x, y, z);
    if (alreadyDone(r.mode, w?.id, id)) continue;
    if (r.mode === 'build' && liquidBlocked(w?.id, id)) continue;
    keep.push(x, y, z, id, r.blocks[o + 4]);
  }
  return r.mode === 'dig' ? { key: r.key, mesh: null, cells: keep } : result(r.key, keep, table);
}

/** The world already holds what the cell asks for (air for a dig); unknown cells never are. */
function alreadyDone(mode: HoloMode, have: number | undefined, id: number): boolean {
  if (have === undefined) return false;
  return mode === 'dig' ? have === 0 : have === (id & 255);
}

/** A liquid queued where a solid block stands (not replaceable, not a liquid itself). */
function liquidBlocked(have: number | undefined, id: number): boolean {
  return LIQUIDS.has(id & 255) && have !== undefined && !REPLACEABLE.has(have) && !LIQUIDS.has(have);
}

function result(key: string, cells: number[], table: StateTable): HoloResult {
  return { key, mesh: cells.length ? meshCells(cells, table) : null, cells };
}

/** Meshes flat [x, y, z, id, meta, …] cells (air ones skipped) as one merged vertex set, or null when nothing is drawn. */
export function meshCells(cells: readonly number[], table: StateTable): HoloMesh | null {
  const g = groupCells(cells);
  if (!g.byCol.size) return null;
  const store = new ColumnStore();
  store.version = table.version;
  const height = ((g.maxY >> 4) + 1) * 16;
  const sections = new Set<string>();
  for (const [k, list] of g.byCol) {
    const [cx, cz] = k.split(',').map(Number);
    const col = holoColumn(cx, cz, height, list, cells, sections);
    if (table.version === 'b1.8.1') fixBetaDoors(col, list, cells);
    store.cols.set(`${HOLO_DIM},${cx},${cz}`, col);
  }
  const origin: [number, number, number] = [Math.floor(g.minX / 16) * 16, Math.floor(g.minY / 16) * 16, Math.floor(g.minZ / 16) * 16];
  return merge(meshParts(store, sections, table, origin), origin);
}

/** The drawable cells (not air, inside the world's height) by column, and their bounds. */
function groupCells(cells: readonly number[]): { byCol: Map<string, number[]>; minX: number; minY: number; minZ: number; maxY: number } {
  const n = Math.floor(cells.length / 5);
  let minX = Infinity, minY = Infinity, minZ = Infinity, maxY = 0;
  const byCol = new Map<string, number[]>();
  for (let i = 0; i < n; i++) {
    const o = i * 5;
    const x = cells[o], y = cells[o + 1], z = cells[o + 2], id = cells[o + 3];
    if (id === 0 || y < 0 || y > 255) continue;
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    minZ = Math.min(minZ, z);
    maxY = Math.max(maxY, y);
    const k = `${x >> 4},${z >> 4}`;
    let l = byCol.get(k);
    if (!l) byCol.set(k, (l = []));
    l.push(o);
  }
  return { byCol, minX, minY, minZ, maxY };
}

/** A private column holding the listed cells (offsets into `cells`); adds the sections they lie in. */
function holoColumn(cx: number, cz: number, height: number, list: number[], cells: readonly number[], sections: Set<string>): StoredColumn {
  const size = Math.ceil(height / 128) << 15;
  const col: StoredColumn = {
    dim: HOLO_DIM, cx, cz, hash: 0n, seen: 0, by: null, live: true,
    ids: new Uint8Array(size), meta: new Uint8Array(size), height, light: null, top: new Int16Array(256).fill(-1),
  };
  for (const o of list) {
    const x = cells[o], y = cells[o + 1], z = cells[o + 2];
    const i = cellIndex(x & 15, y, z & 15);
    col.ids[i] = cells[o + 3] & 255;
    col.meta[i] = cells[o + 4] & 15;
    sections.add(`${cx},${y >> 4},${cz}`);
  }
  return col;
}

/** b1.8.1 doors: the upper half carries the lower's facing and open bit (ItemDoor sets meta + 8), while a queued door's
 * upper half comes as plain 8 (the 1.12.2 form: facing lives in the lower half). Take them from the half below, or the
 * two halves face different ways. */
function fixBetaDoors(col: StoredColumn, list: number[], cells: readonly number[]): void {
  for (const o of list) {
    const x = cells[o], y = cells[o + 1], z = cells[o + 2];
    const i = cellIndex(x & 15, y, z & 15);
    if (!BETA_DOORS.has(col.ids[i]) || !(col.meta[i] & 8) || y < 1) continue;
    const b = cellIndex(x & 15, y - 1, z & 15);
    if (col.ids[b] === col.ids[i] && !(col.meta[b] & 8)) col.meta[i] = 8 | (col.meta[b] & 7);
  }
}

/** Every touched section's passes, offset from the merged mesh's origin. */
function meshParts(store: ColumnStore, sections: Set<string>, table: StateTable, origin: [number, number, number]): Array<{ m: MeshArrays; ox: number; oy: number; oz: number }> {
  const parts: Array<{ m: MeshArrays; ox: number; oy: number; oz: number }> = [];
  for (const s of sections) {
    const [cx, sy, cz] = s.split(',').map(Number);
    const m = meshSection(store, HOLO_DIM, cx, sy, cz, table, null);
    const ox = cx * 16 - origin[0], oy = sy * 16 - origin[1], oz = cz * 16 - origin[2];
    if (m.solid) parts.push({ m: m.solid, ox, oy, oz });
    if (m.translucent) parts.push({ m: m.translucent, ox, oy, oz });
  }
  return parts;
}

/** Wooden and iron doors (b1.8.1). */
const BETA_DOORS = new Set([64, 71]);

/** The private store's dimension (any number: the store holds nothing else). */
const HOLO_DIM = 0;

function merge(parts: Array<{ m: MeshArrays; ox: number; oy: number; oz: number }>, origin: [number, number, number]): HoloMesh | null {
  let verts = 0, idx = 0, quads = 0;
  for (const p of parts) {
    verts += p.m.pos.length / 3;
    idx += p.m.index.length;
    quads += p.m.quads;
  }
  if (!verts) return null;
  const pos = new Float32Array(verts * 3), uv = new Float32Array(verts * 2), tile = new Uint16Array(verts * 4);
  const color = new Uint8Array(verts * 4), cell = new Float32Array(verts * 3), index = new Uint32Array(idx);
  let v = 0, ii = 0;
  for (const { m, ox, oy, oz } of parts) {
    const nv = m.pos.length / 3;
    for (let k = 0; k < nv; k++) {
      pos[(v + k) * 3] = m.pos[k * 3] + ox;
      pos[(v + k) * 3 + 1] = m.pos[k * 3 + 1] + oy;
      pos[(v + k) * 3 + 2] = m.pos[k * 3 + 2] + oz;
      cell[(v + k) * 3] = m.cell[k * 4] + ox;
      cell[(v + k) * 3 + 1] = m.cell[k * 4 + 1] + oy;
      cell[(v + k) * 3 + 2] = m.cell[k * 4 + 2] + oz;
    }
    uv.set(m.uv, v * 2);
    tile.set(m.tile, v * 4);
    color.set(m.color, v * 4);
    for (let k = 0; k < m.index.length; k++) index[ii + k] = m.index[k] + v;
    v += nv;
    ii += m.index.length;
  }
  return { origin, pos, uv, tile, color, cell, index, quads };
}

/** The transferable buffers of the results. */
export function holoBuffers(rs: readonly HoloResult[]): ArrayBuffer[] {
  return rs.flatMap((r) => (r.mesh ? [r.mesh.pos.buffer, r.mesh.uv.buffer, r.mesh.tile.buffer, r.mesh.color.buffer, r.mesh.cell.buffer, r.mesh.index.buffer] : [])) as ArrayBuffer[];
}
