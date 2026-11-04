// Geometry for the 1.12.2 blocks a tile-entity renderer draws (no block model): chests (single, large, trapped, ender), beds,
// standing/wall signs, standing/wall banners, skulls and shulker boxes, plus the end portal/gateway. Each is our own port of
// the vanilla entity model (ModelChest, ModelLargeChest, ModelBed, ModelSign, ModelBanner, ModelSkeletonHead, ModelShulker)
// through the same transform chain its TileEntity*Renderer applies, so the boxes, their texture unwrap (ModelBox) and their
// placement match the game. Textures come from the entity sheets packed into the block atlas (pack.blocks.sheets).
// What the chunk data cannot tell (bed colour, banner colour/patterns, skull type and floor rotation, chest lid angle) uses
// the defaults: red bed, white banner, skeleton skull facing south, closed lid.

import { Layer } from '../../world/blocks';
import { chain, clipAxis, cloneQuad, makeQuad, offsetQuad, orient, rotate, scale, translate, apply, type M4, type Quad, TINT_NONE } from '../quad';
import type { Nb, StateRef } from './states';
import { TEX_GATEWAY, TEX_STARFIELD } from '../sink';

/** An entity texture placed in the block atlas: origin cell, size in cells (the texture is wc·16 × hc·16 px). */
export interface Sheet {
  c: number;
  r: number;
  wc: number;
  hc: number;
}

export type SheetLookup = (name: string) => Sheet | null;

export interface Part {
  /** Texture offset (px). */
  u: number;
  v: number;
  /** addBox origin and size (model px). */
  box: [number, number, number, number, number, number];
  /** rotationPoint (model px). */
  rp?: [number, number, number];
  /** rotateAngle X/Y/Z in degrees (applied Z·Y·X as ModelRenderer.render). */
  rot?: [number, number, number];
  inflate?: number;
}

const UV_EPS = 0.02; // px: keeps nearest sampling off the neighbouring face's texels at the box edges

/** One ModelRenderer box as block-local quads under `m` (the renderer's transform chain, in block units). */
export function partQuads(sheet: Sheet, part: Part, m: M4, out: Quad[], opts: { layer?: Layer; emissive?: boolean } = {}): void {
  const [x0, y0, z0, dx, dy, dz] = part.box;
  const g = part.inflate ?? 0;
  const x = x0 - g, y = y0 - g, z = z0 - g, x2 = x0 + dx + g, y2 = y0 + dy + g, z2 = z0 + dz + g;
  const { u, v } = part;
  const V = {
    v7: [x, y, z], v0: [x2, y, z], v1: [x2, y2, z], v2: [x, y2, z],
    v3: [x, y, z2], v4: [x2, y, z2], v5: [x2, y2, z2], v6: [x, y2, z2],
  };
  // ModelBox's six TexturedQuads: vertices and the (u1, v1, u2, v2) rectangle; vertex k gets (u2,v1) (u1,v1) (u1,v2) (u2,v2).
  const faces: Array<[number[][], number, number, number, number]> = [
    [[V.v4, V.v0, V.v1, V.v5], u + dz + dx, v + dz, u + dz + dx + dz, v + dz + dy],
    [[V.v7, V.v3, V.v6, V.v2], u, v + dz, u + dz, v + dz + dy],
    [[V.v4, V.v3, V.v7, V.v0], u + dz, v, u + dz + dx, v + dz],
    [[V.v1, V.v2, V.v6, V.v5], u + dz + dx, v + dz, u + dz + dx + dx, v],
    [[V.v0, V.v7, V.v2, V.v1], u + dz, v + dz, u + dz + dx, v + dz + dy],
    [[V.v3, V.v4, V.v5, V.v6], u + dz + dx + dz, v + dz, u + dz + dx + dz + dx, v + dz + dy],
  ];
  const rp = part.rp ?? [0, 0, 0];
  const [rx, ry, rz] = part.rot ?? [0, 0, 0];
  const pm = chain(m, scale(1 / 16, 1 / 16, 1 / 16), translate(rp[0], rp[1], rp[2]), rotate(rz, 'z'), rotate(ry, 'y'), rotate(rx, 'x'));
  const centre = apply(pm, (x + x2) / 2, (y + y2) / 2, (z + z2) / 2);
  for (const [verts, u1, v1, u2, v2] of faces) {
    const p: number[] = [];
    for (const vt of verts) p.push(...apply(pm, vt[0], vt[1], vt[2]));
    const cu = (u1 + u2) / 2, cv = (v1 + v2) / 2;
    const ins = (a: number, c: number) => (a + (a < c ? UV_EPS : a > c ? -UV_EPS : 0)) / 16;
    const uv = [ins(u2, cu), ins(v1, cv), ins(u1, cu), ins(v1, cv), ins(u1, cu), ins(v2, cv), ins(u2, cu), ins(v2, cv)];
    // Degenerate faces (zero-size axes) are skipped.
    const area = Math.hypot(p[3] - p[0], p[4] - p[1], p[5] - p[2]) * Math.hypot(p[9] - p[0], p[10] - p[1], p[11] - p[2]);
    if (area < 1e-9) continue;
    const q = makeQuad(p, uv, { c: sheet.c, r: sheet.r, span: (sheet.wc << 8) | sheet.hc, layer: opts.layer ?? Layer.Cutout, emissive: opts.emissive, tint: TINT_NONE });
    const fc = [(p[0] + p[6]) / 2, (p[1] + p[7]) / 2, (p[2] + p[8]) / 2];
    orient(q, [fc[0] - centre[0], fc[1] - centre[1], fc[2] - centre[2]]);
    out.push(q);
  }
}

const CHEST = [
  { u: 0, v: 0, box: [0, -5, -14, 14, 5, 14], rp: [1, 7, 15] },
  { u: 0, v: 19, box: [0, 0, 0, 14, 10, 14], rp: [1, 6, 1] },
  { u: 0, v: 0, box: [-1, -2, -15, 2, 4, 1], rp: [8, 7, 15] },
] as Part[];
const LARGE_CHEST = [
  { u: 0, v: 0, box: [0, -5, -14, 30, 5, 14], rp: [1, 7, 15] },
  { u: 0, v: 19, box: [0, 0, 0, 30, 10, 14], rp: [1, 6, 1] },
  { u: 0, v: 0, box: [-1, -2, -15, 2, 4, 1], rp: [16, 7, 15] },
] as Part[];

const facingIndex = (f: string) => ({ down: 0, up: 1, north: 2, south: 3, west: 4, east: 5 })[f] ?? 3;

/** The yaw a wall-mounted model turns by for its facing index (2 north 180°, 4 west 90°, 5 east −90°, else 0). */
function wallYaw(f: number): number {
  return f === 2 ? 180 : f === 4 ? 90 : f === 5 ? -90 : 0;
}

/** TileEntityChestRenderer: translate(0, 1, 1) · scale(1, −1, −1) · centre-rotate by the facing (2 → 180, 4 → 90, 5 → −90). */
function chestQuads(s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const kind = s.props.kind ?? 'normal';
  const f = facingIndex(s.props.facing);
  const pair = s.props.pair ? (s.props.pair.split(',').map(Number) as [number, number]) : null;
  if (pair) doubleChestQuads(kind, f, pair, sheets, out);
  else singleChestQuads(kind, f, sheets, out);
}

function singleChestQuads(kind: string, f: number, sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets(`entity/chest/${kind}`);
  if (!sheet) return;
  const m = chain(translate(0, 1, 1), scale(1, -1, -1), translate(0.5, 0.5, 0.5), rotate(wallYaw(f), 'y'), translate(-0.5, -0.5, -0.5));
  for (const part of CHEST) partQuads(sheet, part, m, out);
}

/** The master (west/north cell) draws the 30-wide model; every cell keeps the half inside its own cell. */
function doubleChestQuads(kind: string, f: number, pair: [number, number], sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets(`entity/chest/${kind}_double`);
  if (!sheet) return;
  const masterHere = pair[0] > 0 || pair[1] > 0;
  const mx = masterHere ? 0 : pair[0], mz = masterHere ? 0 : pair[1];
  const alongX = pair[0] !== 0;
  const m = chain(translate(0, 1, 1), scale(1, -1, -1), translate(0.5, 0.5, 0.5), doubleChestShift(f, alongX), rotate(wallYaw(f), 'y'), translate(-0.5, -0.5, -0.5));
  const all: Quad[] = [];
  for (const part of LARGE_CHEST) partQuads(sheet, part, m, all);
  for (const q of all) {
    offsetQuad(q, mx, 0, mz);
    const c = clipAxis(q, alongX ? 0 : 2, 0, 1);
    if (c) out.push(c);
  }
}

function doubleChestShift(f: number, alongX: boolean): M4 {
  if (f === 2 && alongX) return translate(1, 0, 0);
  return f === 5 && !alongX ? translate(0, 0, -1) : translate(0, 0, 0);
}

/** A bed piece's turn about z and the shift that keeps it in its cell, by facing ([angle, x, z]; north none). */
const BED_TURN: Record<string, [number, number, number]> = { south: [180, 1, 1], west: [-90, 0, 1], east: [90, 1, 0] };
/** The facing as a horizontal step ([x, z]; east when unknown). */
const BED_STEP: Record<string, [number, number]> = { north: [0, -1], south: [0, 1], west: [-1, 0] };

/** TileEntityBedRenderer + ModelBed: the head or foot piece (16×16×6 turned flat) and its two legs. */
function bedQuads(s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets(`entity/bed/${s.props.color ?? 'red'}`) ?? sheets('entity/bed/red');
  if (!sheet) return;
  const head = s.props.part === 'head';
  const f = s.props.facing;
  const [a, f1, f2] = BED_TURN[f] ?? [0, 0, 0];
  const m = chain(translate(f1, 0.5625, f2), rotate(90, 'x'), rotate(a, 'z'));
  partQuads(sheet, head ? { u: 0, v: 0, box: [0, 0, 0, 16, 16, 6] } : { u: 0, v: 22, box: [0, 0, 0, 16, 16, 6] }, m, out);
  // The two 3×3×3 legs (texture rows 50,0 / 50,6 / 50,12 / 50,18) at the corners of this piece's outer end: toward the
  // facing for the head, away from it for the foot.
  const [fx, fz] = BED_STEP[f] ?? [1, 0];
  const corners = legCorners(head ? fx : -fx, head ? fz : -fz);
  corners.forEach(([x, z], i) => partQuads(sheet, { u: 50, v: (head ? 6 : 0) + i * 12, box: [x, 0, z, 3, 3, 3] }, scale(1, 1, 1), out));
}

/** The two leg corners of a bed piece's end toward (sx, sz). */
function legCorners(sx: number, sz: number): Array<[number, number]> {
  if (sx !== 0) {
    const x = sx < 0 ? 0 : 13;
    return [[x, 0], [x, 13]];
  }
  const z = sz < 0 ? 0 : 13;
  return [[0, z], [13, z]];
}

/** TileEntitySignRenderer + ModelSign: board 24×12×2 (and the 2×14 stick when standing) at 2/3 scale. */
function signQuads(id: number, s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets('entity/sign');
  if (!sheet) return;
  const k = 0.6666667;
  let m: M4;
  const standing = id === 63;
  if (standing) m = chain(translate(0.5, 0.5, 0.5), rotate((-(+s.props.rotation || 0) * 360) / 16, 'y'), scale(k, -k, -k));
  else {
    const f = facingIndex(s.props.facing);
    const a = wallYaw(f);
    m = chain(translate(0.5, 0.5, 0.5), rotate(-a, 'y'), translate(0, -0.3125, -0.4375), scale(k, -k, -k));
  }
  partQuads(sheet, { u: 0, v: 0, box: [-12, -14, -1, 24, 12, 2] }, m, out, { layer: Layer.Opaque });
  if (standing) partQuads(sheet, { u: 0, v: 14, box: [-1, -2, -1, 2, 14, 2] }, m, out, { layer: Layer.Opaque });
}

/** TileEntityBannerRenderer + ModelBanner: slate 20×40×1, the bar on top, the pole when standing (no sway). */
function bannerQuads(id: number, s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets('mineclaw/banner_white');
  if (!sheet || s.props.layer === 'true') return; // a banner with its block entity: the main-thread layer draws it
  const k = 0.6666667;
  const standing = id === 176;
  let m: M4;
  if (standing) m = chain(translate(0.5, 0.5, 0.5), rotate((-(+s.props.rotation || 0) * 360) / 16, 'y'), scale(k, -k, -k));
  else {
    const f = facingIndex(s.props.facing);
    const a = wallYaw(f);
    m = chain(translate(0.5, -0.16666667, 0.5), rotate(-a, 'y'), translate(0, -0.3125, -0.4375), scale(k, -k, -k));
  }
  partQuads(sheet, { u: 0, v: 0, box: [-10, 0, -2, 20, 40, 1], rp: [0, -32, 0] }, m, out);
  partQuads(sheet, { u: 0, v: 42, box: [-10, -32, -1, 20, 2, 2] }, m, out);
  if (standing) partQuads(sheet, { u: 44, v: 0, box: [-1, -30, -1, 2, 42, 2] }, m, out);
}

/** TileEntitySkullRenderer + ModelSkeletonHead (skeleton texture: the skull type lives in the tile entity). */
const SKULL_TEXTURE: Record<string, string> = {
  skeleton: 'entity/skeleton/skeleton',
  wither: 'entity/skeleton/wither_skeleton',
  zombie: 'entity/zombie/zombie',
  player: 'entity/steve',
  creeper: 'entity/creeper/creeper',
  dragon: 'entity/enderdragon/dragon',
};

/** ModelDragonHead (1.12.2): upper lip, upper head, scales and nostrils, the jaw; texture offsets from ModelDragon. */
const DRAGON_HEAD: Part[] = [
  { u: 176, v: 44, box: [-6, -1, -24, 12, 5, 16] },
  { u: 112, v: 30, box: [-8, -8, -10, 16, 16, 16] },
  { u: 0, v: 0, box: [-5, -12, -4, 2, 4, 6] },
  { u: 112, v: 0, box: [-5, -3, -22, 2, 2, 4] },
  { u: 0, v: 0, box: [3, -12, -4, 2, 4, 6] },
  { u: 112, v: 0, box: [3, -3, -22, 2, 2, 4] },
  { u: 176, v: 65, box: [-6, 0, -16, 12, 4, 16], rp: [0, 4, -8] },
];

/** A wall skull's offset from its wall and its fixed yaw, by facing ([x, z, yaw]; north keeps the 16-step rotation). */
const WALL_SKULL: Record<string, [number, number, number | null]> = { north: [0.5, 0.74, null], south: [0.5, 0.26, 180], west: [0.74, 0.5, 270], east: [0.26, 0.5, 90] };

/** TileEntitySkullRenderer: the type's head (ModelSkeletonHead; ModelHumanoidHead adds the hat layer for zombie and player
 * heads; ModelDragonHead for the dragon), on the floor turned by the 16-step rotation, on a wall by its facing. */
function skullQuads(s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const type = s.props.type ?? 'skeleton';
  const sheet = sheets(SKULL_TEXTURE[type] ?? SKULL_TEXTURE.skeleton) ?? sheets(SKULL_TEXTURE.skeleton);
  if (!sheet) return;
  const wall = WALL_SKULL[s.props.facing];
  const pos = wall ? translate(wall[0], 0.25, wall[1]) : translate(0.5, 0, 0.5);
  const yaw = wall?.[2] ?? ((+s.props.rotation || 0) * 360) / 16;
  const m = chain(pos, scale(-1, -1, 1));
  if (type === 'dragon' && sheet.wc >= 16) {
    // The head is a parent with the jaw as its child: the yaw turns both about the head's origin.
    const dm = chain(m, translate(0, -0.374375, 0), scale(0.75, 0.75, 0.75), scale(1 / 16, 1 / 16, 1 / 16), rotate(yaw, 'y'), scale(16, 16, 16));
    for (const part of DRAGON_HEAD) partQuads(sheet, part, dm, out, { layer: Layer.Opaque });
    return;
  }
  partQuads(sheet, { u: 0, v: 0, box: [-4, -8, -4, 8, 8, 8], rot: [0, yaw, 0] }, m, out);
  if (type === 'zombie' || type === 'player') partQuads(sheet, { u: 32, v: 0, box: [-4, -8, -4, 8, 8, 8], rot: [0, yaw, 0], inflate: 0.25 }, m, out);
}

/** TileEntityShulkerBoxRenderer + ModelShulker: base and closed lid, turned to the box's facing. */
function shulkerQuads(s: StateRef, sheets: SheetLookup, out: Quad[]): void {
  const sheet = sheets(`entity/shulker/shulker_${s.props.color ?? 'purple'}`);
  if (!sheet) return;
  let turn: M4;
  switch (s.props.facing) {
    case 'down': turn = chain(translate(0, 2, 0), rotate(180, 'x')); break;
    case 'north': turn = chain(translate(0, 1, 1), rotate(90, 'x'), rotate(180, 'z')); break;
    case 'south': turn = chain(translate(0, 1, -1), rotate(90, 'x')); break;
    case 'west': turn = chain(translate(-1, 1, 0), rotate(90, 'x'), rotate(-90, 'z')); break;
    case 'east': turn = chain(translate(1, 1, 0), rotate(90, 'x'), rotate(90, 'z')); break;
    default: turn = translate(0, 0, 0);
  }
  const base = chain(translate(0.5, 1.5, 0.5), scale(1, -1, -1), turn);
  partQuads(sheet, { u: 0, v: 0, box: [-8, -16, -8, 16, 12, 16], rp: [0, 24, 0] }, base, out, { layer: Layer.Opaque });
  // The base sits a hair inside the lid where they overlap (vanilla scales both by 0.9995; the lid's sides win).
  partQuads(sheet, { u: 0, v: 28, box: [-8, -8, -8, 16, 8, 16], rp: [0, 24, 0], inflate: -0.01 }, base, out, { layer: Layer.Opaque });
}

/** The end portal (top surface at 12/16) and gateway (a cube): the starfield shader flag (dark flat when untextured). */
function voidQuads(id: number, out: Quad[]): void {
  const flat = 0x0c0e1a;
  const opt = { textured: true, fx: id === 209 ? TEX_STARFIELD | TEX_GATEWAY : TEX_STARFIELD, flat, emissive: true, layer: Layer.Opaque, ao: false };
  if (id === 119) {
    out.push(makeQuad([0, 0.75, 0, 0, 0.75, 1, 1, 0.75, 1, 1, 0.75, 0], [0, 0, 0, 1, 1, 1, 1, 0], opt));
    return;
  }
  const C = [
    [[0, 0, 1], [0, 0, 0], [1, 0, 0], [1, 0, 1]], [[0, 1, 0], [0, 1, 1], [1, 1, 1], [1, 1, 0]], [[1, 1, 0], [1, 0, 0], [0, 0, 0], [0, 1, 0]],
    [[0, 1, 1], [0, 0, 1], [1, 0, 1], [1, 1, 1]], [[0, 1, 0], [0, 0, 0], [0, 0, 1], [0, 1, 1]], [[1, 1, 1], [1, 0, 1], [1, 0, 0], [1, 1, 0]],
  ];
  C.forEach((c, f) => out.push(makeQuad(c.flat(), [0, 0, 0, 1, 1, 1, 1, 0], { ...opt, cull: f })));
}

type BuiltinDraw = (id: number, s: StateRef, sheets: SheetLookup, out: Quad[]) => void;

/** The built-in geometry of each id (moving pistons, end gateways' frames and structure voids draw nothing). */
const BUILTINS: Array<BuiltinDraw | undefined> = (() => {
  const t = new Array<BuiltinDraw | undefined>(256);
  const set = (ids: number[], f: BuiltinDraw) => ids.forEach((id) => (t[id] = f));
  set([54, 130, 146], (_id, s, sheets, out) => chestQuads(s, sheets, out));
  set([26], (_id, s, sheets, out) => bedQuads(s, sheets, out));
  set([63, 68], signQuads);
  set([176, 177], bannerQuads);
  set([144], (_id, s, sheets, out) => skullQuads(s, sheets, out));
  set([119, 209], (id, _s, _sheets, out) => voidQuads(id, out));
  set([36, 166, 217], () => undefined);
  for (let id = 219; id <= 234; id++) t[id] = (_id, s, sheets, out) => shulkerQuads(s, sheets, out);
  return t;
})();

/** Appends the quads of a built-in state; true when handled (fluids are drawn by the fluid mesher, not here). */
export function builtinQuads(id: number, s: StateRef, _nb: Nb | null, sheets: SheetLookup, out: Quad[]): boolean {
  const draw = BUILTINS[id];
  if (!draw) return false;
  draw(id, s, sheets, out);
  return true;
}

export { cloneQuad };
