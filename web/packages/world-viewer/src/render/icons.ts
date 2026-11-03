// Inventory-style block icons from the bundled textures (the Design Studio's hotbar and palette), drawn on a 2D canvas (no
// WebGL): the block's own baked geometry (the mesher's quads: 1.12.2 blockstates/models, b1.8.1 render types; stairs, slabs,
// fences, torches, plants, chests and beds as they are) projected the way the inventory shows a block (top, south face left,
// east face right, shaded like vanilla's item renderer), or a flat sprite: an item model's sprite for blocks the game shows as
// an item (doors, beds, signs, cane, wire, repeaters: the palette's `icon` hint, 1.12.2 "oak_door" / b1.8.1 "item:43"), the
// b1.8.1 flat render types' terrain tile. `blockIconAtlas` draws a whole palette into one sheet (one image, CSS positions): no
// per-entry WebGL, no per-entry image decode, fast on software rendering.

import { meshAssets } from '../atlas/assets';
import type { AssetBundle, Pack1122 } from '../assets/bundle';
import { buildStateTable, type StateTable } from '../mesh/states';
import { TINT_FIXED, TINT_FOLIAGE, TINT_GRASS, TINT_NONE, type Quad } from '../mesh/quad';
import { tileFor } from '../mesh/beta/textures';
import { flatColour, FOLIAGE_TINT, GRASS_TINT } from '../world/blocks';
import { look1122 } from './entities/items';

/** b1.8.1 blocks the inventory draws flat (cross / flat render types): their front tile is the icon. */
const FLAT_BETA = new Set([6, 27, 28, 30, 31, 32, 37, 38, 39, 40, 50, 55, 59, 64, 65, 66, 69, 71, 75, 76, 77, 83, 101, 102, 104, 105, 106]);

const tables = new Map<string, StateTable>();

function tableOf(bundle: AssetBundle): StateTable {
  let t = tables.get(bundle.version);
  if (!t) {
    t = buildStateTable(bundle.version, meshAssets(bundle.pack), true);
    tables.set(bundle.version, t);
  }
  return t;
}

const hex = (rgb: number) => `#${(rgb & 0xffffff).toString(16).padStart(6, '0')}`;

function tintColour(kind: number): number {
  if (kind >= TINT_FIXED) return kind & 0xffffff;
  if (kind === TINT_GRASS) return GRASS_TINT;
  if (kind === TINT_FOLIAGE) return FOLIAGE_TINT;
  return 0;
}

/** A tinted copy of one atlas cell (grass, leaves, vines…), cached: tinting on the shared icon canvas would darken what lies under. */
const tinted = new Map<string, HTMLCanvasElement>();
function tintedCell(img: CanvasImageSource, sx: number, sy: number, ts: number, rgb: number, key: string): HTMLCanvasElement {
  let c = tinted.get(key);
  if (c) return c;
  c = document.createElement('canvas');
  c.width = c.height = ts;
  const g = c.getContext('2d')!;
  g.imageSmoothingEnabled = false;
  g.drawImage(img, sx, sy, ts, ts, 0, 0, ts, ts);
  g.globalCompositeOperation = 'multiply';
  g.fillStyle = hex(rgb);
  g.fillRect(0, 0, ts, ts);
  g.globalCompositeOperation = 'destination-in';
  g.drawImage(img, sx, sy, ts, ts, 0, 0, ts, ts);
  tinted.set(key, c);
  return c;
}

/** Block-local point → icon pixels (32 px frame, scaled by s): top, south face left, east face right (vanilla's view). */
function project(x: number, y: number, z: number, s: number, ox: number, oy: number): [number, number] {
  return [ox + s * (16 + 14 * (x - z)), oy + s * (16 + 7 * (x + z - 1) - 14 * (y - 0.5))];
}

/** Draws one baked quad: its texture (or flat colour) mapped onto its projected parallelogram, shaded and tinted. */
function drawQuad(g: CanvasRenderingContext2D, bundle: AssetBundle, cols: number, q: Quad, s: number, ox: number, oy: number): void {
  const P = [0, 1, 2, 3].map((i) => project(q.p[i * 3], q.p[i * 3 + 1], q.p[i * 3 + 2], s, ox, oy));
  g.save();
  g.beginPath();
  g.moveTo(P[0][0], P[0][1]);
  for (let i = 1; i < 4; i++) g.lineTo(P[i][0], P[i][1]);
  g.closePath();
  g.clip();
  const shade = q.shade > 0 ? q.shade : 1;
  if (!q.textured) {
    let rgb = q.flat;
    if (q.tint !== TINT_NONE) {
      const t = tintColour(q.tint);
      rgb = (((((rgb >> 16) & 255) * ((t >> 16) & 255)) / 255) << 16) | (((((rgb >> 8) & 255) * ((t >> 8) & 255)) / 255) << 8) | (((rgb & 255) * (t & 255)) / 255);
    }
    g.fillStyle = hex(rgb);
    g.globalAlpha = 1;
    g.filter = shade < 1 ? `brightness(${shade})` : 'none';
    g.fill();
    g.restore();
    return;
  }
  const ts = bundle.pack.tileSize;
  void cols;
  // Texture pixels of three corners (uv in cell units from the quad's cell) → their screen points: one affine map.
  const tx = (i: number) => (q.c + q.uv[i * 2]) * ts;
  const ty = (i: number) => (q.r + q.uv[i * 2 + 1]) * ts;
  const [u0, v0, u1, v1, u3, v3] = [tx(0), ty(0), tx(1), ty(1), tx(3), ty(3)];
  const du1 = u1 - u0, dv1 = v1 - v0, du3 = u3 - u0, dv3 = v3 - v0;
  const det = du1 * dv3 - du3 * dv1;
  if (Math.abs(det) < 1e-9) {
    g.restore();
    return;
  }
  const [x0, y0] = P[0], [x1, y1] = P[1], [x3, y3] = P[3];
  const a = ((x1 - x0) * dv3 - (x3 - x0) * dv1) / det;
  const c = ((x3 - x0) * du1 - (x1 - x0) * du3) / det;
  const b = ((y1 - y0) * dv3 - (y3 - y0) * dv1) / det;
  const d = ((y3 - y0) * du1 - (y1 - y0) * du3) / det;
  g.setTransform(a, b, c, d, x0 - a * u0 - c * v0, y0 - b * u0 - d * v0);
  g.imageSmoothingEnabled = false;
  g.filter = shade < 0.999 ? `brightness(${shade.toFixed(2)})` : 'none';
  const sx = Math.floor(Math.min(u0, u1, u3, u0 + du1 + du3)), sy = Math.floor(Math.min(v0, v1, v3, v0 + dv1 + dv3));
  const sw = Math.ceil(Math.max(u0, u1, u3, u0 + du1 + du3)) - sx, sh = Math.ceil(Math.max(v0, v1, v3, v0 + dv1 + dv3)) - sy;
  if (sw > 0 && sh > 0) {
    if (q.tint !== TINT_NONE && !q.span) {
      const cell = tintedCell(bundle.blocks, q.c * ts, q.r * ts, ts, tintColour(q.tint), `${bundle.version}:${q.c}:${q.r}:${q.tint}`);
      g.drawImage(cell, sx - q.c * ts, sy - q.r * ts, sw, sh, sx, sy, sw, sh);
    } else g.drawImage(bundle.blocks, sx, sy, sw, sh, sx, sy, sw, sh);
    // The grass side's fringe (an overlay cell over the same face).
    if (q.ovC >= 0) {
      const ovt = q.ovTint !== TINT_NONE ? tintColour(q.ovTint) : 0;
      const src = ovt ? tintedCell(bundle.blocks, q.ovC * ts, q.ovR * ts, ts, ovt, `${bundle.version}:${q.ovC}:${q.ovR}:${q.ovTint}`) : null;
      const dx = (q.ovC - q.c) * ts, dy = (q.ovR - q.r) * ts;
      if (src) g.drawImage(src, sx - q.c * ts, sy - q.r * ts, sw, sh, sx, sy, sw, sh);
      else g.drawImage(bundle.blocks, sx + dx, sy + dy, sw, sh, sx, sy, sw, sh);
    }
  }
  g.restore();
}

/** Visible from the icon's camera (+x, +y, +z), or drawn from both sides. */
function facesCamera(q: Quad): boolean {
  if (q.twoSided) return true;
  const p = q.p;
  const ax = p[3] - p[0], ay = p[4] - p[1], az = p[5] - p[2];
  const bx = p[9] - p[0], by = p[10] - p[1], bz = p[11] - p[2];
  const nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
  return nx + ny + nz > 1e-6;
}

/** Draws a flat sprite (an atlas cell) filling the icon frame, tinted when asked. */
function drawSprite(g: CanvasRenderingContext2D, img: CanvasImageSource, sx: number, sy: number, ts: number, size: number, ox: number, oy: number, tint: number, key: string): void {
  const src = tint ? tintedCell(img, sx, sy, ts, tint, key) : null;
  g.imageSmoothingEnabled = false;
  const pad = Math.round(size * 0.06);
  if (src) g.drawImage(src, 0, 0, ts, ts, ox + pad, oy + pad, size - 2 * pad, size - 2 * pad);
  else g.drawImage(img, sx, sy, ts, ts, ox + pad, oy + pad, size - 2 * pad, size - 2 * pad);
}

/**
 * One icon into `g` at (ox, oy), `size` px. `icon` = the palette's hint ("oak_door" for a 1.12.2 item model, "item:43" for a
 * b1.8.1 items.png cell); without one (or when it doesn't resolve) the block's own geometry.
 */
export function drawBlockIcon(g: CanvasRenderingContext2D, bundle: AssetBundle, id: number, meta: number, size: number, ox = 0, oy = 0, icon?: string | null): void {
  const s = size / 32;
  // b1.8.1 item sprites.
  if (icon && icon.startsWith('item:') && bundle.version === 'b1.8.1') {
    const n = Number(icon.slice(5));
    const ts = bundle.pack.tileSize;
    return drawSprite(g, bundle.items, (n % 16) * ts, Math.floor(n / 16) * ts, ts, size, ox, oy, 0, `bi:${n}`);
  }
  // 1.12.2 item models that are sprites (doors, beds, signs, plants…).
  if (icon && bundle.version === '1.12.2' && !icon.startsWith('item:')) {
    const pack = bundle.pack as Pack1122;
    const look = look1122(pack, icon);
    if (look && look.kind === 'sprite') {
      const cols = look.atlas === 'items' ? pack.items.columns : pack.blocks.columns;
      const ts = pack.tileSize;
      const img = look.atlas === 'items' ? bundle.items : bundle.blocks;
      return drawSprite(g, img, (look.cell % cols) * ts, Math.floor(look.cell / cols) * ts, ts, size, ox, oy, look.tint, `${look.atlas}:${look.cell}:${look.tint}`);
    }
  }
  // b1.8.1 flat render types: the front tile.
  if (bundle.version === 'b1.8.1' && FLAT_BETA.has(id)) {
    const t = tileFor(id, meta, 3);
    const ts = bundle.pack.tileSize;
    if (t.tile >= 0) {
      const tint = t.tint ? (id === 106 || id === 18 ? FOLIAGE_TINT : GRASS_TINT) : 0;
      return drawSprite(g, bundle.blocks, (t.tile % 16) * ts, Math.floor(t.tile / 16) * ts, ts, size, ox, oy, tint, `bt:${t.tile}:${tint}`);
    }
  }
  const table = tableOf(bundle);
  const quads = table.source.quads((id << 4) | (meta & 15)).filter(facesCamera);
  if (quads.length === 0) {
    g.fillStyle = hex(flatColour(bundle.version, id, meta, true));
    g.fillRect(ox + 6 * s, oy + 6 * s, 20 * s, 20 * s);
    return;
  }
  // Painter's order: farthest from the camera (+x +y +z) first.
  const depth = (q: Quad) => {
    let d = 0;
    for (let i = 0; i < 4; i++) d += q.p[i * 3] + q.p[i * 3 + 1] + q.p[i * 3 + 2];
    return d;
  };
  quads.sort((a, b) => depth(a) - depth(b));
  for (const q of quads) drawQuad(g, bundle, table.cols, q, s, ox, oy);
}

const cache = new Map<string, string>();

/**
 * A data URL of the block's icon (`size` px square), cached per version, state and hint. Needs a DOM canvas (call it in the
 * browser).
 */
export function blockIconUrl(bundle: AssetBundle, id: number, meta: number, size = 32, icon?: string | null): string {
  const k = `${bundle.version}:${id}:${meta}:${size}:${icon ?? ''}`;
  const hit = cache.get(k);
  if (hit) return hit;
  const c = document.createElement('canvas');
  c.width = c.height = size;
  const g = c.getContext('2d');
  if (!g) return '';
  try {
    drawBlockIcon(g, bundle, id, meta, size, 0, 0, icon);
  } catch {
    /* a state the geometry can't draw: an empty icon */
  }
  const url = c.toDataURL('image/png');
  cache.set(k, url);
  return url;
}

/** A palette drawn into one sheet: `index` = "id:meta" → slot (row-major, `cols` per row, `size` px each). */
export interface IconAtlas {
  url: string;
  size: number;
  cols: number;
  rows: number;
  index: Map<string, number>;
}

const atlases = new Map<string, IconAtlas>();

/**
 * Every entry's icon in one image (cached per version, size and entry list): the palette shows them as CSS background
 * positions of a single sheet. Needs a DOM canvas.
 */
export function blockIconAtlas(bundle: AssetBundle, entries: ReadonlyArray<{ id: number; meta: number; icon?: string | null }>, size = 32): IconAtlas {
  const key = `${bundle.version}:${size}:${entries.length}:${entries.map((e) => `${e.id}.${e.meta}`).join(',')}`;
  const hit = atlases.get(key);
  if (hit) return hit;
  const cols = 16, rows = Math.max(1, Math.ceil(entries.length / cols));
  const c = document.createElement('canvas');
  c.width = cols * size;
  c.height = rows * size;
  const g = c.getContext('2d')!;
  const index = new Map<string, number>();
  entries.forEach((e, i) => {
    index.set(`${e.id}:${e.meta}`, i);
    try {
      drawBlockIcon(g, bundle, e.id, e.meta, size, (i % cols) * size, Math.floor(i / cols) * size, e.icon);
    } catch {
      /* one state that can't be drawn leaves its slot empty */
    }
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.filter = 'none';
  });
  const atlas = { url: c.toDataURL('image/png'), size, cols, rows, index };
  atlases.set(key, atlas);
  return atlas;
}
