// b1.8.1 block geometry on terrain.png, ported in our own words from the C# BlockGeometry (RenderBlocks per render type:
// 0 cube, 1 crossed squares, 2 torch, 3 fire, 4 fluids, 5 redstone wire, 6 crops, 7 door, 8 ladder, 9 rail, 10 stairs,
// 11 fence, 12 lever, 13 cactus, 14 bed, 15 repeater, 16/17 piston, 18 pane, 19 stem, 20 vine, 21 fence gate, signs via
// their tile-entity model) and BlockTextures (tiles, chest halves via ChestTile, the grass side overlay). Beyond the C#:
// repeaters carry their two torches, redstone wire picks its line tile and trims the dot to its connections, and portals
// use the animated portal tile.

import { flatColour, Layer } from '../../world/blocks';
import { chain, fixedTint, makeQuad, rotate, scale, transformQuad, translate, type M4, type Quad, type QuadInit, TINT_NONE } from '../quad';
import { fluidQuads, isFluid } from '../fluids';
import type { Nb } from '../models/states';
import { emissiveBeta, NO_TILE, tileFor, FOLIAGE_TINT, TILE_FIRE_A, TILE_FIRE_B, TILE_LAVA_FLOW, TILE_LAVA_STILL, TILE_PORTAL, TILE_WATER_FLOW, TILE_WATER_STILL, type TileRef } from './textures';

/** RenderBlocks render types by b1.8.1 id (unlisted ids: 0, the standard cube). */
const RENDER_TYPE_IDS: ReadonlyArray<readonly [number, readonly number[]]> = [
  [-1, [0, 63, 68, 36]],
  [1, [6, 30, 31, 32, 37, 38, 39, 40, 83]],
  [2, [50, 75, 76]],
  [3, [51]],
  [4, [8, 9, 10, 11]],
  [5, [55]],
  [6, [59]],
  [7, [64, 71]],
  [8, [65]],
  [9, [66, 27, 28]],
  [10, [53, 67, 108, 109]],
  [11, [85]],
  [12, [69]],
  [13, [81]],
  [14, [26]],
  [15, [93, 94]],
  [16, [29, 33]],
  [17, [34]],
  [18, [101, 102]],
  [19, [104, 105]],
  [20, [106]],
  [21, [107]],
];
const RENDER_TYPES = new Int8Array(256);
for (const [type, ids] of RENDER_TYPE_IDS) for (const id of ids) RENDER_TYPES[id] = type;

/** RenderBlocks render type of a b1.8.1 id (0 = standard cube; -1 = tile-entity model or invisible). */
export function renderType(id: number): number {
  return RENDER_TYPES[id] ?? 0;
}

const PARTIAL_CUBE = new Set([44, 78, 70, 72, 60, 96, 92, 77, 90]);

export const enum BetaShape {
  Air = 0,
  Cube = 1,
  CutoutCube = 2,
  TranslucentCube = 3,
  Custom = 4,
}

export function classifyBeta(id: number): BetaShape {
  if (id === 0) return BetaShape.Air;
  if (id === 18 || id === 20 || id === 52) return BetaShape.CutoutCube;
  if (id === 79) return BetaShape.TranslucentCube;
  if (renderType(id) !== 0 || PARTIAL_CUBE.has(id) || id === 63 || id === 68) return BetaShape.Custom;
  return BetaShape.Cube;
}

/** Opaque full cube (Block.opaqueCubeLookup). */
export const opaqueBeta = (id: number) => id > 0 && id < 256 && classifyBeta(id) === BetaShape.Cube;

/** Per-tile untextured colours (pack terrain.info averages); null → the palette. */
export type TileAverage = (tile: number) => number | null;

const E = 1 / 16;
/** Faces tagged in a cull mask are culled by the mesher against an opaque neighbour on that side (the C# `skip`). */
const SKIP = 0b111111;
/** A piston head's facing (0 down, 1 up, 2 north, 3 south, 4 west, 5 east) as a unit step. */
const PISTON_N = [[0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0]];

type BlockBuild = (b: BetaBuilder, id: number, meta: number, nb: Nb) => void;

export class BetaBuilder {
  private out: Quad[] = [];
  private id = 0;
  private meta = 0;
  constructor(
    private readonly textured: boolean,
    private readonly avg: TileAverage,
    /** Fancy leaves (alpha-cut tiles 52/132) or fast ones (the opaque tiles 53/133). */
    private readonly fancy = true,
  ) {}

  private flatOf(tile: number, f: number): number {
    if (tile >= 0) {
      const a = this.avg(tile);
      if (a != null && tile !== TILE_WATER_STILL && tile !== TILE_WATER_FLOW && tile !== TILE_LAVA_STILL && tile !== TILE_LAVA_FLOW && tile !== TILE_FIRE_A && tile !== TILE_FIRE_B && tile !== TILE_PORTAL) return a;
    }
    if (this.id === 51) return 0xff9a20;
    if (this.id === 90) return 0x7a2bd9;
    return flatColour('b1.8.1', this.id, this.meta, f === 1);
  }

  private tinted(t: number): number {
    return t ? fixedTint(t) : TINT_NONE;
  }

  /** A box with vanilla per-face UVs; faces whose bit is in `cull` are culled against an opaque neighbour. */
  box(x0: number, y0: number, z0: number, x1: number, y1: number, z1: number, tile: (f: number) => TileRef, layer = Layer.Opaque, cull = 0, xf: M4 | null = null, shade = true): Quad[] {
    const made: Quad[] = [];
    for (let f = 0; f < 6; f++) {
      let p: number[];
      switch (f) {
        case 1: p = [x0, y1, z0, x0, y1, z1, x1, y1, z1, x1, y1, z0]; break;
        case 0: p = [x0, y0, z1, x0, y0, z0, x1, y0, z0, x1, y0, z1]; break;
        case 2: p = [x1, y1, z0, x1, y0, z0, x0, y0, z0, x0, y1, z0]; break;
        case 3: p = [x0, y1, z1, x0, y0, z1, x1, y0, z1, x1, y1, z1]; break;
        case 4: p = [x0, y1, z0, x0, y0, z0, x0, y0, z1, x0, y1, z1]; break;
        default: p = [x1, y1, z1, x1, y0, z1, x1, y0, z0, x1, y1, z0];
      }
      const uv: number[] = [];
      for (let k = 0; k < 4; k++) uv.push(...uvOn(f, p[k * 3], p[k * 3 + 1], p[k * 3 + 2]));
      const t = tile(f);
      const q = makeQuad(p, uv, {
        c: t.tile >= 0 ? t.tile & 15 : 0,
        r: t.tile >= 0 ? t.tile >> 4 : 0,
        tint: this.tinted(t.tint),
        ovC: t.overlay >= 0 ? t.overlay & 15 : -1,
        ovR: t.overlay >= 0 ? t.overlay >> 4 : -1,
        ovTint: this.tinted(t.overlayTint),
        layer,
        cull: (cull >> f) & 1 ? f : -1,
        emissive: emissiveBeta(this.id),
        flat: this.flatOf(t.tile, f),
        textured: this.textured && t.tile >= 0,
        shade: shade ? undefined : 1,
      });
      if (xf) transformQuad(q, xf, !shade);
      made.push(q);
      this.out.push(q);
    }
    return made;
  }

  /** A two-sided plane a, b, c, d with tile UV (0,0) at a, (1,0) at d (uvA..uvC the rectangle). */
  plane(p: number[], tile: number, tint: number, layer = Layer.Cutout, shade = 1, uvA: [number, number] = [0, 0], uvC: [number, number] = [1, 1]): Quad {
    const uv = [uvA[0], uvA[1], uvA[0], uvC[1], uvC[0], uvC[1], uvC[0], uvA[1]];
    const q = makeQuad(p, uv, {
      c: tile & 15,
      r: tile >> 4,
      tint: this.tinted(tint),
      layer,
      shade,
      twoSided: true,
      emissive: emissiveBeta(this.id),
      flat: this.flatOf(tile, 2),
      textured: this.textured,
      dir: -1,
    });
    this.out.push(q);
    return q;
  }

  /** The geometry of each render type (0, the standard cube, and −1, tile-entity models, are not here). */
  private static readonly BY_RENDER_TYPE: ReadonlyArray<BlockBuild | undefined> = [
    undefined,
    (b, id, meta, nb) => b.crossed(nb, id, meta),
    (b, id, meta) => b.torch(id, meta),
    (b) => b.fire(),
    (b, id, _meta, nb) => b.fluid(nb, id),
    (b, _id, meta, nb) => b.wire(nb, meta),
    (b, _id, meta) => b.crops(meta),
    (b, id, meta) => b.door(id, meta),
    (b, _id, meta) => b.ladder(meta),
    (b, id, meta) => b.rail(id, meta),
    (b, id, meta) => b.stairs(id, meta, SKIP),
    (b, _id, _meta, nb) => b.fence(nb),
    (b, _id, meta) => b.lever(meta),
    (b, id, meta) => b.cactus(id, meta),
    (b, _id, meta) => b.bed(meta, SKIP),
    (b, id, meta) => b.repeater(id, meta, SKIP),
    (b, id, meta) => b.piston(id, meta),
    (b, _id, meta) => b.pistonHead(meta),
    (b, id, _meta, nb) => b.pane(nb, id),
    (b, _id, meta) => b.stem(meta),
    (b, _id, meta) => b.vine(meta),
    (b, _id, meta) => b.gate(meta),
  ];

  /** Standard-cube render types that are not full cubes (or not drawn: 36, the moving piston). */
  private static readonly PARTIAL_BLOCKS = new Map<number, BlockBuild>([
    [36, () => {}],
    [44, (b, id, meta) => b.slab(id, meta)],
    [78, (b, id, meta) => b.snow(id, meta)],
    [70, (b, id, meta) => b.pressurePlate(id, meta)],
    [72, (b, id, meta) => b.pressurePlate(id, meta)],
    [60, (b, id, meta) => b.farmland(id, meta)],
    [96, (b, id, meta) => b.trapdoor(id, meta)],
    [92, (b, _id, meta) => b.cake(meta)],
    [77, (b, _id, meta) => b.button(meta)],
    [90, (b, _id, _meta, nb) => b.portal(nb)],
  ]);

  build(id: number, meta: number, nb: Nb, out: Quad[]): void {
    this.out = out;
    this.id = id;
    this.meta = meta;
    const byType = BetaBuilder.BY_RENDER_TYPE[renderType(id)];
    if (byType) return byType(this, id, meta, nb);
    if (id === 63 || id === 68) return this.sign(id, meta);
    const partial = BetaBuilder.PARTIAL_BLOCKS.get(id);
    if (partial) return partial(this, id, meta, nb);
    this.cube(id, meta, nb);
  }

  /** The block's own tiles (fancy or fast leaves). */
  private std(id: number, meta: number): (f: number) => TileRef {
    return (f: number) => tileFor(id, meta, f, this.fancy);
  }

  /** A full cube; glass/ice/spawners are culled against themselves by the mesher's self-cull flag. */
  private cube(id: number, meta: number, nb: Nb): void {
    const shape = classifyBeta(id);
    const layer = shape === BetaShape.CutoutCube && !(id === 18 && !this.fancy) ? Layer.Cutout : shape === BetaShape.TranslucentCube ? Layer.Translucent : Layer.Opaque;
    const tf = id === 54 ? (f: number) => ref1(chestTile(nb, f)) : this.std(id, meta);
    const qs = this.box(0, 0, 0, 1, 1, 1, tf, layer, SKIP);
    if (id === 79 && !this.textured) for (const q of qs) q.alpha = 0.62;
  }

  private ladder(meta: number): void {
    const o = 0.05;
    const t = 83;
    switch (meta) {
      case 5: this.plane([o, 1, 1, o, 0, 1, o, 0, 0, o, 1, 0], t, 0); break;
      case 4: this.plane([1 - o, 1, 0, 1 - o, 0, 0, 1 - o, 0, 1, 1 - o, 1, 1], t, 0); break;
      case 3: this.plane([0, 1, o, 0, 0, o, 1, 0, o, 1, 1, o], t, 0); break;
      default: this.plane([1, 1, 1 - o, 1, 0, 1 - o, 0, 0, 1 - o, 0, 1, 1 - o], t, 0);
    }
  }

  /** Top and bottom from the box; the sides are planes 1/16 inside the cell (the spines are transparent texels). */
  private cactus(id: number, meta: number): void {
    this.box(0, 0, 0, 1, 1, 1, this.std(id, meta), Layer.Opaque, SKIP & 0b000011);
    this.out.splice(this.out.length - 4, 4);
    this.plane([1, 1, E, 1, 0, E, 0, 0, E, 0, 1, E], 70, 0, Layer.Cutout, 0.8);
    this.plane([0, 1, 1 - E, 0, 0, 1 - E, 1, 0, 1 - E, 1, 1, 1 - E], 70, 0, Layer.Cutout, 0.8);
    this.plane([E, 1, 0, E, 0, 0, E, 0, 1, E, 1, 1], 70, 0, Layer.Cutout, 0.6);
    this.plane([1 - E, 1, 1, 1 - E, 0, 1, 1 - E, 0, 0, 1 - E, 1, 0], 70, 0, Layer.Cutout, 0.6);
  }

  /** A piston base: its face (extended, sticky or plain), back and sides. */
  private piston(id: number, meta: number): void {
    const facing = meta & 7, ext = (meta & 8) !== 0;
    const front = ext ? 110 : id === 29 ? 106 : 107;
    this.box(0, 0, 0, 1, 1, 1, (f) => ref1(f === facing ? front : f === (facing ^ 1) ? 109 : 108), Layer.Opaque, SKIP);
  }

  /** A piston head: the quarter-block plate on its facing side. */
  private pistonHead(meta: number): void {
    const [nx, ny, nz] = PISTON_N[meta & 7];
    const x0 = nx > 0 ? 0.75 : 0, x1 = nx < 0 ? 0.25 : 1, y0 = ny > 0 ? 0.75 : 0, y1 = ny < 0 ? 0.25 : 1, z0 = nz > 0 ? 0.75 : 0, z1 = nz < 0 ? 0.25 : 1;
    this.box(x0, y0, z0, x1, y1, z1, () => ref1(107));
  }

  /** A pumpkin/melon stem: crossed planes growing with its age, green to yellow. */
  private stem(meta: number): void {
    const h = Math.min(1, (meta * 2 + 2) / 16);
    const tint = ((meta * 32) << 16) | ((255 - meta * 8) << 8) | (meta * 4);
    this.crossAt(0.5, 0, 0.5, h, 111, tint, [0, 1 - h], [1, 1]);
  }

  private slab(id: number, meta: number): void {
    if ((meta & 8) !== 0) this.box(0, 0.5, 0, 1, 1, 1, this.std(id, meta), Layer.Opaque, SKIP & ~1);
    else this.box(0, 0, 0, 1, 0.5, 1, this.std(id, meta), Layer.Opaque, SKIP & ~2);
  }

  private snow(id: number, meta: number): void {
    this.box(0, 0, 0, 1, ((meta & 7) + 1) / 8, 1, this.std(id, meta), Layer.Opaque, SKIP & ~2);
  }

  private pressurePlate(id: number, meta: number): void {
    const h = meta === 1 ? 1 / 32 : 1 / 16;
    this.box(E, 0, E, 15 * E, h, 15 * E, this.std(id, meta), Layer.Opaque, SKIP & 1);
  }

  private farmland(id: number, meta: number): void {
    this.box(0, 0, 0, 1, 15 * E, 1, this.std(id, meta), Layer.Opaque, SKIP & ~2);
  }

  /** A trapdoor: flat on the floor when shut, against its wall when open. */
  private trapdoor(id: number, meta: number): void {
    const t = 3 * E, std = this.std(id, meta);
    if ((meta & 4) === 0) {
      this.box(0, 0, 0, 1, t, 1, std, Layer.Cutout, SKIP & 1);
      return;
    }
    switch (meta & 3) {
      case 0: this.box(0, 0, 1 - t, 1, 1, 1, std, Layer.Cutout); break;
      case 1: this.box(0, 0, 0, 1, 1, t, std, Layer.Cutout); break;
      case 2: this.box(1 - t, 0, 0, 1, 1, 1, std, Layer.Cutout); break;
      default: this.box(0, 0, 0, t, 1, 1, std, Layer.Cutout);
    }
  }

  /** A cake, eaten from the west by its bites. */
  private cake(meta: number): void {
    const x0 = (1 + meta * 2) / 16;
    this.box(x0, 0, E, 15 * E, 0.5, 15 * E, (f) => ref1(f === 1 ? 121 : f === 0 ? 124 : f === 4 && meta > 0 ? 123 : 122), Layer.Cutout, SKIP & 1);
  }

  /** A stone button on its wall, pressed in when powered. */
  private button(meta: number): void {
    const i1 = meta & 7;
    const f3 = (meta & 8) !== 0 ? E : 0.125;
    const a = 0.375, b = 0.625, c = 0.1875;
    const bx: Record<number, number[]> = { 1: [0, a, 0.5 - c, f3, b, 0.5 + c], 2: [1 - f3, a, 0.5 - c, 1, b, 0.5 + c], 3: [0.5 - c, a, 0, 0.5 + c, b, f3] };
    const v = bx[i1] ?? [0.5 - c, a, 1 - f3, 0.5 + c, b, 1];
    this.box(v[0], v[1], v[2], v[3], v[4], v[5], () => ref1(1));
  }

  /** A portal pane along the frame it stands in (obsidian or more portal on its x sides: along x). */
  private portal(nb: Nb): void {
    const alongX = nb.id(-1, 0, 0) === 90 || nb.id(-1, 0, 0) === 49 || nb.id(1, 0, 0) === 90 || nb.id(1, 0, 0) === 49;
    const t = () => ref1(TILE_PORTAL);
    if (alongX) this.box(0, 0, 0.375, 1, 1, 0.625, t, Layer.Translucent, SKIP);
    else this.box(0.375, 0, 0, 0.625, 1, 1, t, Layer.Translucent, SKIP);
  }

  // ------------------------------------------------------------------------------------------------------------ shapes

  private crossAt(cx: number, y0: number, cz: number, h: number, tile: number, tint: number, uvA?: [number, number], uvC?: [number, number]): Quad[] {
    const a = cx - 0.45, b = cx + 0.45, c = cz - 0.45, d = cz + 0.45;
    return [
      this.plane([a, y0 + h, c, a, y0, c, b, y0, d, b, y0 + h, d], tile, tint, Layer.Cutout, 1, uvA, uvC),
      this.plane([a, y0 + h, d, a, y0, d, b, y0, c, b, y0 + h, c], tile, tint, Layer.Cutout, 1, uvA, uvC),
    ];
  }

  private crossed(nb: Nb, id: number, meta: number): void {
    const t = tileFor(id, meta, 2);
    let cx = 0.5, cy = 0, cz = 0.5;
    if (id === 31) {
      // renderBlockReed: tall grass jittered by a position hash (64-bit, like the client's long arithmetic).
      let l = BigInt.asIntN(64, (BigInt(Math.imul(nb.wx, 0x2fc20f)) ^ (BigInt(nb.wz) * 0x6ebfff5n)) ^ BigInt(nb.wy));
      l = BigInt.asIntN(64, l * l * 0x285b825n + l * 11n);
      const nib = (s: bigint) => Number((l >> s) & 15n);
      cx += (nib(16n) / 15 - 0.5) * 0.5;
      cy += (nib(20n) / 15 - 1) * 0.2;
      cz += (nib(24n) / 15 - 0.5) * 0.5;
    }
    this.crossAt(cx, cy, cz, 1, t.tile < 0 ? 15 : t.tile, t.tint);
  }

  private fire(): void {
    const qs = this.crossAt(0.5, 0, 0.5, 1.4, TILE_FIRE_A, 0, [0, 0], [1, 1]);
    qs[1].c = TILE_FIRE_B & 15;
    qs[1].r = TILE_FIRE_B >> 4;
    for (const q of qs) q.emissive = true;
  }

  private fluid(nb: Nb, id: number): void {
    const water = id === 8 || id === 9;
    fluidQuads(
      nb,
      id,
      {
        stillC: (water ? TILE_WATER_STILL : TILE_LAVA_STILL) & 15,
        stillR: (water ? TILE_WATER_STILL : TILE_LAVA_STILL) >> 4,
        flowC: (water ? TILE_WATER_FLOW : TILE_LAVA_FLOW) & 15,
        flowR: (water ? TILE_WATER_FLOW : TILE_LAVA_FLOW) >> 4,
        beta: true,
        tint: TINT_NONE,
        flat: water ? 0x2f5cff : 0xf55a10,
        textured: this.textured,
      },
      (dx, dy, dz) => opaqueBeta(nb.id(dx, dy, dz)),
      this.out,
    );
  }

  private torch(id: number, meta: number): void {
    const d = 0.4, d1 = 0.1, d2 = 0.2;
    let bx = 0, by = 0, bz = 0, tx = 0, tz = 0;
    switch (meta) {
      case 1: bx = -d1; by = d2; tx = -d; break;
      case 2: bx = d1; by = d2; tx = d; break;
      case 3: bz = -d1; by = d2; tz = -d; break;
      case 4: bz = d1; by = d2; tz = d; break;
    }
    this.torchAt(id === 75 ? 115 : id === 76 ? 99 : 80, bx + 0.5, by, bz + 0.5, tx, tz, id !== 75);
  }

  /** renderTorchAtAngle: four 1/16-off planes of the full tile leaning by (dx, dz) at the foot, and the 2×2 px cap. */
  private torchAt(tile: number, x: number, y: number, z: number, dx: number, dz: number, lit: boolean): void {
    const e = E, cap = 0.625;
    const cx = x + dx * (1 - cap), cz = z + dz * (1 - cap);
    const top = makeQuad([cx - e, y + cap, cz - e, cx - e, y + cap, cz + e, cx + e, y + cap, cz + e, cx + e, y + cap, cz - e], [7 / 16, 6 / 16, 7 / 16, 8 / 16, 9 / 16, 8 / 16, 9 / 16, 6 / 16], {
      c: tile & 15, r: tile >> 4, layer: Layer.Cutout, twoSided: true, emissive: lit, flat: 0xffd84a, textured: this.textured, shade: 1,
    });
    this.out.push(top);
    const yt = y + 1;
    const side = (ax: number, az: number, bx: number, bz: number) => {
      const q = makeQuad([ax, yt, az, ax + dx, yt - 1, az + dz, bx + dx, yt - 1, bz + dz, bx, yt, bz], [0, 0, 0, 1, 1, 1, 1, 0], {
        c: tile & 15, r: tile >> 4, layer: Layer.Cutout, twoSided: true, emissive: lit, flat: this.flatOf(tile, 2), textured: this.textured, shade: 1, dir: -1,
      });
      this.out.push(q);
    };
    side(x - e, z - 0.5, x - e, z + 0.5);
    side(x + e, z + 0.5, x + e, z - 0.5);
    side(x + 0.5, z - e, x - 0.5, z - e);
    side(x - 0.5, z + e, x + 0.5, z + e);
  }

  /** renderBlockRedstoneWire: the dot trimmed to its connections, or the line tile along one axis; power-coloured. */
  private wire(nb: Nb, meta: number): void {
    const w = wireLinks(nb, -1, 0), e = wireLinks(nb, 1, 0), n = wireLinks(nb, 0, -1), s = wireLinks(nb, 0, 1);
    const opts = { tint: fixedTint(wireTint(meta)), layer: Layer.Cutout, flat: 0x8a0a0a, textured: this.textured, shade: 1, twoSided: true };
    const h = 1 / 64;
    if ((w || e) && !n && !s) this.out.push(makeQuad([0, h, 0, 0, h, 1, 1, h, 1, 1, h, 0], [0, 0, 0, 1, 1, 1, 1, 0], { ...opts, c: 165 & 15, r: 165 >> 4 }));
    else if ((n || s) && !e && !w) this.out.push(makeQuad([0, h, 0, 0, h, 1, 1, h, 1, 1, h, 0], [0, 1, 1, 1, 1, 0, 0, 0], { ...opts, c: 165 & 15, r: 165 >> 4 }));
    else this.wireDot(w, e, n, s, opts);
    // Climbing up the faces of opaque neighbours.
    if (opaqueBeta(nb.id(0, 1, 0))) return;
    const o = 1 / 64;
    this.wireClimb(nb, -1, 0, [o, 1, 1, o, 0, 1, o, 0, 0, o, 1, 0], opts);
    this.wireClimb(nb, 1, 0, [1 - o, 1, 0, 1 - o, 0, 0, 1 - o, 0, 1, 1 - o, 1, 1], opts);
    this.wireClimb(nb, 0, -1, [0, 1, o, 0, 0, o, 1, 0, o, 1, 1, o], opts);
    this.wireClimb(nb, 0, 1, [1, 1, 1 - o, 1, 0, 1 - o, 0, 0, 1 - o, 0, 1, 1 - o], opts);
  }

  /** The dot (the cross tile), trimmed to the sides it links to. */
  private wireDot(w: boolean, e: boolean, n: boolean, s: boolean, opts: QuadInit): void {
    const h = 1 / 64;
    let x0 = 0, x1 = 1, z0 = 0, z1 = 1;
    if (w || e || n || s) {
      if (!w) x0 += 0.3125;
      if (!e) x1 -= 0.3125;
      if (!n) z0 += 0.3125;
      if (!s) z1 -= 0.3125;
    }
    this.out.push(makeQuad([x0, h, z0, x0, h, z1, x1, h, z1, x1, h, z0], [x0, z0, x0, z1, x1, z1, x1, z0], { ...opts, c: 164 & 15, r: 164 >> 4 }));
  }

  private wireClimb(nb: Nb, dx: number, dz: number, p: number[], opts: QuadInit): void {
    if (opaqueBeta(nb.id(dx, 0, dz)) && nb.id(dx, 1, dz) === 55) this.out.push(makeQuad(p, [1, 0, 1, 1, 0, 1, 0, 0], { ...opts, c: 165 & 15, r: 165 >> 4, dir: -1 }));
  }

  private crops(meta: number): void {
    const tile = 88 + Math.max(0, Math.min(7, meta));
    const y0 = -E, y1 = 1 - E;
    for (const p of [0.25, 0.75]) {
      this.plane([p, y1, 0, p, y0, 0, p, y0, 1, p, y1, 1], tile, 0);
      this.plane([0, y1, p, 0, y0, p, 1, y0, p, 1, y1, p], tile, 0);
    }
  }

  /** BlockDoor: state = (meta & 4) == 0 ? (meta − 1) & 3 : meta & 3, 3/16 thick on that side; mirrored faces flip u. */
  private door(id: number, meta: number): void {
    const st = (meta & 4) === 0 ? (meta - 1) & 3 : meta & 3;
    const f = 0.1875;
    const [x0, z0, x1, z1] = st === 0 ? [0, 0, 1, f] : st === 1 ? [1 - f, 0, 1, 1] : st === 2 ? [0, 1 - f, 1, 1] : [0, 0, f, 1];
    const baseTile = id === 71 ? 98 : 97;
    const qs = this.box(x0, 0, z0, x1, 1, z1, (side) => ref1(Math.abs(doorTex(baseTile, side, meta))), Layer.Cutout);
    qs.forEach((q, side) => {
      if (doorTex(baseTile, side, meta) < 0) for (let k = 0; k < 4; k++) q.uv[k * 2] = 1 - q.uv[k * 2];
    });
  }

  private rail(id: number, meta: number): void {
    let m = meta;
    if (id !== 66) m &= 7;
    const tile = id === 27 ? ((meta & 8) !== 0 ? 179 : 163) : id === 28 ? 195 : m >= 6 ? 112 : 128;
    let vx = [1, 1, 0, 0], vz = [0, 1, 1, 0];
    if (m === 1 || m === 2 || m === 3 || m === 7) {
      vx = [1, 0, 0, 1];
      vz = [1, 1, 0, 0];
    } else if (m === 8) {
      vx = [0, 0, 1, 1];
      vz = [1, 0, 0, 1];
    } else if (m === 9) {
      vx = [0, 1, 1, 0];
      vz = [0, 0, 1, 1];
    }
    const h = E;
    const vy = [h, h, h, h];
    if (m === 2 || m === 4) {
      vy[0] += 1;
      vy[3] += 1;
    }
    if (m === 3 || m === 5) {
      vy[1] += 1;
      vy[2] += 1;
    }
    const q = makeQuad([vx[0], vy[0], vz[0], vx[1], vy[1], vz[1], vx[2], vy[2], vz[2], vx[3], vy[3], vz[3]], [1, 0, 1, 1, 0, 1, 0, 0], {
      c: tile & 15, r: tile >> 4, layer: Layer.Cutout, twoSided: true, flat: this.flatOf(tile, 1), textured: this.textured, shade: 1, dir: -1,
    });
    this.out.push(q);
  }

  /** b1.8.1 stairs (meta 0 high side east, 1 west, 2 south, 3 north): two standard boxes. */
  private stairs(id: number, meta: number, skip: number): void {
    const t = (f: number) => tileFor(id, meta, f);
    switch (meta & 3) {
      case 0: this.box(0, 0, 0, 0.5, 0.5, 1, t, Layer.Opaque, skip & 0b011101); this.box(0.5, 0, 0, 1, 1, 1, t, Layer.Opaque, skip & 0b101111); break;
      case 1: this.box(0, 0, 0, 0.5, 1, 1, t, Layer.Opaque, skip & 0b011111); this.box(0.5, 0, 0, 1, 0.5, 1, t, Layer.Opaque, skip & 0b101101); break;
      case 2: this.box(0, 0, 0, 1, 0.5, 0.5, t, Layer.Opaque, skip & 0b110101); this.box(0, 0, 0.5, 1, 1, 1, t, Layer.Opaque, skip & 0b111011); break;
      default: this.box(0, 0, 0, 1, 1, 0.5, t, Layer.Opaque, skip & 0b110111); this.box(0, 0, 0.5, 1, 0.5, 1, t, Layer.Opaque, skip & 0b111001);
    }
  }

  private fence(nb: Nb): void {
    const t = () => ref1(4);
    this.box(0.375, 0, 0.375, 0.625, 1, 0.625, t);
    const like = (id: number) => id === 85 || id === 107;
    const wv = like(nb.id(-1, 0, 0)), ev = like(nb.id(1, 0, 0)), nv = like(nb.id(0, 0, -1)), sv = like(nb.id(0, 0, 1));
    const a = 0.4375, b = 0.5625;
    for (const [y0, y1] of [[0.75, 0.9375], [0.375, 0.5625]]) {
      if (wv || ev) this.box(wv ? 0 : a, y0, a, ev ? 1 : b, y1, b, t);
      if (nv || sv) this.box(a, y0, nv ? 0 : a, b, y1, sv ? 1 : b, t);
    }
  }

  private gate(meta: number): void {
    const t = () => ref1(4);
    const alongX = (meta & 3) === 0 || (meta & 3) === 2;
    const open = (meta & 4) !== 0;
    const p0 = 7 / 16, p1 = 9 / 16;
    if (alongX) {
      this.box(0, 5 / 16, p0, 2 / 16, 1, p1, t);
      this.box(14 / 16, 5 / 16, p0, 1, 1, p1, t);
      if (!open) for (const [y0, y1] of [[6 / 16, 9 / 16], [12 / 16, 15 / 16]]) this.box(2 / 16, y0, p0, 14 / 16, y1, p1, t);
    } else {
      this.box(p0, 5 / 16, 0, p1, 1, 2 / 16, t);
      this.box(p0, 5 / 16, 14 / 16, p1, 1, 1, t);
      if (!open) for (const [y0, y1] of [[6 / 16, 9 / 16], [12 / 16, 15 / 16]]) this.box(p0, y0, 2 / 16, p1, y1, 14 / 16, t);
    }
  }

  private lever(meta: number): void {
    const m = meta & 7, on = (meta & 8) !== 0;
    const cob = () => ref1(16);
    const rad = 0.7 * (180 / Math.PI);
    let stick: M4;
    switch (m) {
      case 1: this.box(0, 0.3125, 0.25, 0.1875, 0.6875, 0.75, cob); stick = chain(translate(0.19, 0.5, 0.5), rotate(on ? -rad : rad, 'z')); break;
      case 2: this.box(0.8125, 0.3125, 0.25, 1, 0.6875, 0.75, cob); stick = chain(translate(0.81, 0.5, 0.5), rotate(on ? rad : -rad, 'z')); break;
      case 3: this.box(0.25, 0.3125, 0, 0.75, 0.6875, 0.1875, cob); stick = chain(translate(0.5, 0.5, 0.19), rotate(on ? rad : -rad, 'x')); break;
      case 4: this.box(0.25, 0.3125, 0.8125, 0.75, 0.6875, 1, cob); stick = chain(translate(0.5, 0.5, 0.81), rotate(on ? -rad : rad, 'x')); break;
      default: this.box(0.3125, 0, 0.25, 0.6875, 0.1875, 0.75, cob); stick = chain(translate(0.5, 0.1, 0.5), rotate(on ? rad : -rad, 'x'));
    }
    // The handle points out of the base (local +y): turn it onto the base's outward axis first.
    const align = m === 1 ? rotate(-90, 'z') : m === 2 ? rotate(90, 'z') : m === 3 ? rotate(90, 'x') : m === 4 ? rotate(-90, 'x') : translate(0, 0, 0);
    this.box(-E, 0, -E, E, 10 * E, E, () => ref1(96), Layer.Cutout, 0, chain(stick, align));
  }

  private bed(meta: number, skip: number): void {
    const head = (meta & 8) !== 0;
    const qs = this.box(0, 3 / 16, 0, 1, 9 / 16, 1, (f) => ref1(f === 1 ? (head ? 135 : 134) : f === 0 ? 4 : head ? 151 : 150), Layer.Cutout, skip & 1);
    rotateTopUv(qs[1], [2, 3, 0, 1][meta & 3]);
  }

  private repeater(id: number, meta: number, skip: number): void {
    const tile = id === 94 ? 147 : 131;
    const qs = this.box(0, 0, 0, 1, 2 / 16, 1, (f) => ref1(f === 1 ? tile : 1), Layer.Opaque, skip & 1);
    rotateTopUv(qs[1], meta & 3);
    // renderBlockRepeater's two torches: the fixed one at the back, the delay one moving with meta >> 2.
    const offs = [-0.0625, 0.0625, 0.1875, 0.3125];
    const dir = meta & 3, delay = (meta >> 2) & 3;
    let d1 = 0, d2 = 0, d3 = 0, d4 = 0;
    switch (dir) {
      case 0: d4 = -0.3125; d2 = offs[delay]; break;
      case 2: d4 = 0.3125; d2 = -offs[delay]; break;
      case 3: d3 = -0.3125; d1 = offs[delay]; break;
      default: d3 = 0.3125; d1 = -offs[delay];
    }
    const t = id === 94 ? 99 : 115;
    this.torchAt(t, 0.5 + d1, -0.1875, 0.5 + d2, 0, 0, id === 94);
    this.torchAt(t, 0.5 + d3, -0.1875, 0.5 + d4, 0, 0, id === 94);
  }

  /** Panes / iron bars: arms toward panes, glass and opaque cubes; no neighbour = both full arms (vanilla). */
  private pane(nb: Nb, id: number): void {
    const tile = id === 101 ? 85 : 49;
    const c = (n: number) => n === id || n === 20 || opaqueBeta(n);
    let wv = c(nb.id(-1, 0, 0)), ev = c(nb.id(1, 0, 0)), nv = c(nb.id(0, 0, -1)), sv = c(nb.id(0, 0, 1));
    if (!wv && !ev && !nv && !sv) wv = ev = nv = sv = true;
    const a = 7 / 16, b = 9 / 16;
    const t = () => ref1(tile);
    if (wv || ev) this.box(wv ? 0 : a, 0, a, ev ? 1 : b, 1, b, t, Layer.Cutout);
    if (nv || sv) this.box(a, 0, nv ? 0 : a, b, 1, sv ? 1 : b, t, Layer.Cutout);
  }

  private vine(meta: number): void {
    const o = 0.05, t = 143, tint = FOLIAGE_TINT;
    if (meta & 1) this.plane([0, 1, 1 - o, 0, 0, 1 - o, 1, 0, 1 - o, 1, 1, 1 - o], t, tint);
    if (meta & 2) this.plane([o, 1, 0, o, 0, 0, o, 0, 1, o, 1, 1], t, tint);
    if (meta & 4) this.plane([1, 1, o, 1, 0, o, 0, 0, o, 0, 1, o], t, tint);
    if (meta & 8) this.plane([1 - o, 1, 1, 1 - o, 0, 1, 1 - o, 0, 0, 1 - o, 1, 0], t, tint);
    if (meta === 0) this.plane([0, 1 - o, 0, 0, 1 - o, 1, 1, 1 - o, 1, 1, 1 - o, 0], t, tint);
  }

  /** TileEntitySignRenderer + ModelSign: a 24×12×2 px board (+ 2×14 stick standing) at 2/3 scale, planks and log tiles. */
  private sign(id: number, meta: number): void {
    const s = 0.6666667 / 16;
    let m: M4;
    if (id === 63) m = chain(translate(0.5, 0.5, 0.5), rotate((-meta * 360) / 16, 'y'), scale(s, -s, -s));
    else {
      const ang = meta === 2 ? 180 : meta === 4 ? 90 : meta === 5 ? -90 : 0;
      m = chain(translate(0.5, 0.5, 0.5), rotate(-ang, 'y'), translate(0, -0.3125, -0.4375), scale(s, -s, -s));
    }
    const full = (qs: Quad[]) => {
      for (const q of qs) {
        q.uv = [0, 0, 0, 1, 1, 1, 1, 0];
        q.cull = -1;
      }
    };
    full(this.box(-12, -14, -1, 12, -2, 1, () => ref1(4), Layer.Opaque, 0, m));
    if (id === 63) full(this.box(-1, -2, -1, 1, 12, 1, () => ref1(20), Layer.Opaque, 0, m));
  }
}

const ref1 = (tile: number): TileRef => (tile < 0 ? NO_TILE : { tile, tint: 0, overlay: -1, overlayTint: 0 });

/** Redstone wire's power colour: dark red unpowered, brighter and yellower with power. */
function wireTint(meta: number): number {
  const p = meta / 15;
  const r = meta === 0 ? 0.3 : p * 0.6 + 0.4, g = Math.max(0, p * p * 0.7 - 0.5), b = Math.max(0, p * p * 0.6 - 0.7);
  return ((r * 255) << 16) | ((g * 255) << 8) | (b * 255);
}

/** Blocks redstone wire links to on its own level. */
const WIRE_ATTRACTS = new Set([55, 75, 76, 69, 70, 72, 77, 93, 94]);

/** Wire links toward (dx, dz): to a power component, down over a non-opaque side, or up an opaque one (nothing above). */
function wireLinks(nb: Nb, dx: number, dz: number): boolean {
  if (WIRE_ATTRACTS.has(nb.id(dx, 0, dz))) return true;
  if (!opaqueBeta(nb.id(dx, 0, dz)) && nb.id(dx, -1, dz) === 55) return true;
  return !opaqueBeta(nb.id(0, 1, 0)) && opaqueBeta(nb.id(dx, 0, dz)) && nb.id(dx, 1, dz) === 55;
}

function uvOn(f: number, x: number, y: number, z: number): [number, number] {
  switch (f) {
    case 0: case 1: return [x, z];
    case 2: return [1 - x, 1 - y];
    case 3: return [x, 1 - y];
    case 4: return [z, 1 - y];
    default: return [1 - z, 1 - y];
  }
}

/** Turns a top quad's UVs by quarter turns (repeater/bed tops). */
function rotateTopUv(q: Quad | undefined, quarter: number): void {
  if (!q) return;
  for (let r = 0; r < quarter; r++) q.uv = [q.uv[2], q.uv[3], q.uv[4], q.uv[5], q.uv[6], q.uv[7], q.uv[0], q.uv[1]];
}

function doorTex(idx: number, side: number, meta: number): number {
  if (side === 0 || side === 1) return idx;
  const k = (meta & 4) === 0 ? (meta - 1) & 3 : meta & 3;
  const tile = idx - (meta & 8) * 2;
  if ((k === 0 || k === 2) !== side <= 3) return tile;
  let l = ((k / 2) | 0) + ((side & 1) ^ k);
  l += (meta & 4) / 4;
  return (l & 1) !== 0 ? -tile : tile;
}

/** BlockChest.getBlockTexture(world, x, y, z, side): singles face away from an opaque neighbour, pairs show the wide
 * front (41/42) and back (57/58) halves. */
export function chestTile(nb: Nb, l: number): number {
  if (l === 0 || l === 1) return CHEST_TILE - 1;
  const i1 = nb.id(0, 0, -1), j1 = nb.id(0, 0, 1), k1 = nb.id(-1, 0, 0), l1 = nb.id(1, 0, 0);
  if (i1 === CHEST || j1 === CHEST) return chestPairZ(nb, l, i1, k1, l1);
  if (k1 === CHEST || l1 === CHEST) return chestPairX(nb, l, i1, j1, k1);
  return chestSingle(l, i1, j1, k1, l1);
}

const CHEST = 54, CHEST_TILE = 26;

/** A pair along z (its partner north or south): the wide halves on the west/east faces, the front where nothing opaque is. */
function chestPairZ(nb: Nb, l: number, i1: number, k1: number, l1: number): number {
  if (l === 2 || l === 3) return CHEST_TILE;
  let i2 = i1 === CHEST ? -1 : 0;
  const dz = i1 !== CHEST ? 1 : -1;
  const k2 = nb.id(-1, 0, dz), i3 = nb.id(1, 0, dz);
  if (l === 4) i2 = -1 - i2;
  const front = blockedOnlyOn(l1, i3, k1, k2) ? 4 : 5;
  return (l !== front ? CHEST_TILE + 32 : CHEST_TILE + 16) + i2;
}

/** A pair along x (its partner west or east): the wide halves on the north/south faces. */
function chestPairX(nb: Nb, l: number, i1: number, j1: number, k1: number): number {
  if (l === 4 || l === 5) return CHEST_TILE;
  let j2 = k1 === CHEST ? -1 : 0;
  const dx = k1 !== CHEST ? 1 : -1;
  const l2 = nb.id(dx, 0, -1), j3 = nb.id(dx, 0, 1);
  if (l === 3) j2 = -1 - j2;
  const front = blockedOnlyOn(j1, j3, i1, l2) ? 2 : 3;
  return (l !== front ? CHEST_TILE + 32 : CHEST_TILE + 16) + j2;
}

/** One of `a`/`a2` is opaque and neither of `b`/`b2` is: the pair's front turns away from the `a` side. */
function blockedOnlyOn(a: number, a2: number, b: number, b2: number): boolean {
  return (opaqueBeta(a) || opaqueBeta(a2)) && !opaqueBeta(b) && !opaqueBeta(b2);
}

/** A single chest faces away from an opaque neighbour (the last rule that applies: south, north, east, west). */
function chestSingle(l: number, i1: number, j1: number, k1: number, l1: number): number {
  let b0 = 3;
  if (opaqueBeta(j1) && !opaqueBeta(i1)) b0 = 2;
  if (opaqueBeta(k1) && !opaqueBeta(l1)) b0 = 5;
  if (opaqueBeta(l1) && !opaqueBeta(k1)) b0 = 4;
  return l !== b0 ? CHEST_TILE : CHEST_TILE + 1;
}

export { isFluid };
