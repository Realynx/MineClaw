// 1.12.2 block geometry for the section mesher (our port of the C# BlockModels1122): (id, meta) → blockstate (neighbours
// resolved from the padded section) → blockstate file → baked model quads, cached per state; tile-entity blocks and fluids
// from our own geometry. Tints follow BlockColors (grass/tall grass/double grass/reeds: biome grass; leaves: spruce and birch
// fixed, others biome foliage; vines: foliage; lily pads; stems by age; redstone wire by power; water by biome), resolved per
// position by the mesher. Classification per state (ClassifyNow): six full boundary faces = a cube (opaque when every face
// texture is fully covered and the block isn't on the translucent layer, else cut-out/blended); anything else is quads.

import { BLOCK_NAMES_1122, F_EMISSIVE, F_LAVA, F_LEAVES, F_OPAQUE, F_SELF_CULL, F_WATER, flatColour, Layer } from '../../world/blocks';
import { fluidQuads, isFluid } from '../fluids';
import { fixedTint, type Quad, TINT_FOLIAGE, TINT_GRASS, TINT_NONE, TINT_WATER } from '../quad';
import { STATES, type Assets1122, type BlockSource, NO_NB } from '../source';
import { ModelBaker, type BakedFace } from './baker';
import { BlockStateFile } from './blockstate';
import { builtinQuads, type Sheet } from './builtin';
import { TEX_OPAQUE } from '../sink';
import { cacheKey, resolveState, worldDependent, type Nb, type StateRef } from './states';

/** Block.getBlockLayer() == TRANSLUCENT: ice, stained glass (+ panes), slime, frosted ice, portals. */
const translucentLayer = (id: number) => id === 79 || id === 95 || id === 160 || id === 165 || id === 212 || id === 90;
const EMISSIVE = new Set([50, 76, 89, 91, 124, 138, 169, 198, 213, 10, 11, 51, 62, 74, 90, 119, 209]);
const SNOWY_CUBES = new Set([2, 3, 110]);
const SPRUCE = 0x619961, BIRCH = 0x80a755, LILY_PAD = 0x208030;

/** BlockRedstoneWire.colorMultiplier(power). */
export function wireColour(power: number): number {
  const f = power / 15;
  const r = f * 0.6 + (power === 0 ? 0.3 : 0.4);
  const g = Math.min(1, Math.max(0, f * f * 0.7 - 0.5)), b = Math.min(1, Math.max(0, f * f * 0.6 - 0.7));
  return ((Math.min(1, Math.max(0, r)) * 255) << 16) | ((g * 255) << 8) | (b * 255);
}

/** Faces against the same block are skipped: glass, stained glass, ice, slime, frosted ice. */
const SELF_CULL = new Set([20, 95, 79, 165, 212]);

/** A biome's grass, foliage and water colours (−1 = the plains default; swamps tint water). */
function biomeTints(id: number, b: { grass?: number | null; foliage?: number | null }): [number, number, number] {
  return [b.grass ?? -1, b.foliage ?? -1, id === 6 || id === 134 ? 0xe0ffae : 0xffffff];
}

/** The colours unknown biomes use: plains', with vanilla's own as the fallback. */
function plainsTints(pl: [number, number, number] | undefined): [number, number, number] {
  if (!pl) return [0x91bd59, 0x77ab2f, 0xffffff];
  return [pl[0] >= 0 ? pl[0] : 0x91bd59, pl[1] >= 0 ? pl[1] : 0x77ab2f, 0xffffff];
}

/** BlockColors.colorMultiplier for tinted faces, by id (biome kinds resolved per position); absent = untinted. */
const TINTS: Record<number, (s: StateRef) => number> = (() => {
  const t: Record<number, (s: StateRef) => number> = {};
  const set = (ids: number[], f: (s: StateRef) => number) => ids.forEach((id) => (t[id] = f));
  set([2, 31, 83], () => TINT_GRASS);
  set([175], (s) => (s.file === 'double_grass' || s.file === 'double_fern' ? TINT_GRASS : TINT_NONE));
  set([18], (s) => (s.file === 'spruce_leaves' ? fixedTint(SPRUCE) : s.file === 'birch_leaves' ? fixedTint(BIRCH) : TINT_FOLIAGE));
  set([161, 106], () => TINT_FOLIAGE);
  set([111], () => fixedTint(LILY_PAD));
  set([104, 105], (s) => {
    const age = s.props.age != null ? +s.props.age : 7;
    return fixedTint(((age * 32) << 16) | ((255 - age * 8) << 8) | (age * 4));
  });
  set([55], (s) => fixedTint(wireColour(+(s.props.power ?? 0))));
  return t;
})();

/** What every baked face of one block shares. */
interface FaceStyle {
  id: number;
  tint: number;
  translucent: boolean;
  emissive: boolean;
}

function samePositions(a: Quad, b: Quad): boolean {
  for (let i = 0; i < 12; i++) if (Math.abs(a.p[i] - b.p[i]) > 1e-4) return false;
  return true;
}

export class Models1122 implements BlockSource {
  readonly version = '1.12.2' as const;
  readonly flags = new Uint8Array(STATES);
  readonly dynamic = new Uint8Array(STATES);
  readonly animated = new Set<number>();
  readonly columns: number;
  private readonly baker: ModelBaker;
  private readonly files = new Map<string, BlockStateFile>();
  private readonly baked = new Map<string, Quad[]>();
  private readonly fixed: Array<Quad[] | null> = new Array(STATES).fill(null);
  private readonly biomes = new Map<number, [number, number, number]>();
  private readonly plains: [number, number, number];

  constructor(
    private readonly a: Assets1122,
    readonly textured: boolean,
    /** Opaque leaves that hide each other (vanilla "fast" graphics). */
    private readonly fastLeaves = false,
  ) {
    this.columns = a.columns;
    this.baker = new ModelBaker(a.models, {
      cell: (n) => a.tiles[n] ?? null,
      info: (n) => a.info[n] ?? null,
    });
    for (const [n, json] of Object.entries(a.blockstates)) this.files.set(n, BlockStateFile.parse(json));
    for (const [c, r] of a.anims) this.animated.add(r * a.columns + c);
    for (const [k, b] of Object.entries(a.biomes)) this.biomes.set(+k, biomeTints(+k, b));
    this.plains = plainsTints(this.biomes.get(1));
    this.classify();
  }

  private sheet = (name: string): Sheet | null => {
    const s = this.a.sheets[name];
    return s ? { c: s[0], r: s[1], wc: s[2], hc: s[3] } : null;
  };

  private solidFull = (id: number, meta: number) => (this.flags[(id << 4) | (meta & 15)] & F_OPAQUE) !== 0;

  /** Every state's flags and whether its geometry depends on the world (in id, meta order: a state's resolution may read
   * the flags of the states before it). */
  private classify(): void {
    for (let id = 1; id < 256; id++) {
      const dyn = worldDependent(id) || isFluid(id) || id === 54 || id === 146;
      const base = this.baseFlags(id);
      for (let meta = 0; meta < 16; meta++) {
        const s = (id << 4) | meta;
        this.dynamic[s] = dyn ? 1 : 0;
        this.flags[s] = this.stateFlags(id, meta, base, dyn);
      }
    }
  }

  /** The flags every state of a block shares. */
  private baseFlags(id: number): number {
    let base = 0;
    if (EMISSIVE.has(id)) base |= F_EMISSIVE;
    if (id === 8 || id === 9) base |= F_WATER;
    if (id === 10 || id === 11) base |= F_LAVA;
    if (id === 18 || id === 161) base |= F_LEAVES | (this.fastLeaves ? F_OPAQUE : 0);
    if (SELF_CULL.has(id)) base |= F_SELF_CULL;
    return base;
  }

  /** A state is an opaque cube when its fixed (or snowy-cube) model is one. */
  private stateFlags(id: number, meta: number, base: number, dyn: boolean): number {
    if (!BLOCK_NAMES_1122[id]) return base;
    if (dyn && !SNOWY_CUBES.has(id)) return base;
    const ref = resolveState(id, meta, null, this.solidFull);
    return !ref.builtin && this.isOpaqueCube(id, this.bake(id, ref)) ? base | F_OPAQUE : base;
  }

  /** Six flush faces on distinct sides, each the whole face, all fully covered and not on the translucent layer. */
  private isOpaqueCube(id: number, quads: Quad[]): boolean {
    if (quads.length !== 6 || translucentLayer(id)) return false;
    const seen = new Set<number>();
    for (const q of quads) {
      if (q.dir < 0 || !q.flush || seen.has(q.dir) || q.layer !== Layer.Opaque) return false;
      const ax = q.p[3] - q.p[0], ay = q.p[4] - q.p[1], az = q.p[5] - q.p[2], bx = q.p[9] - q.p[0], by = q.p[10] - q.p[1], bz = q.p[11] - q.p[2];
      if (Math.abs(Math.hypot(ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx) - 1) > 1e-3) return false;
      seen.add(q.dir);
    }
    return true;
  }

  quads(s: number): Quad[] {
    let q = this.fixed[s];
    if (!q) {
      const id = s >> 4;
      q = id === 0 || !BLOCK_NAMES_1122[id] ? [] : this.bake(id, resolveState(id, s & 15, null, this.solidFull));
      this.fixed[s] = q;
    }
    return q;
  }

  build(id: number, meta: number, nb: Nb, out: Quad[]): void {
    if (isFluid(id)) {
      this.buildFluid(id, nb, out);
      return;
    }
    const ref = resolveState(id, meta, nb, this.solidFull);
    for (const q of this.bake(id, ref, nb)) out.push(q);
  }

  /** Water and lava: the still and flowing textures (the flowing one at its native 32 px when the pack has the 2×2-cell
   * sheet, else its 16 px cell). */
  private buildFluid(id: number, nb: Nb, out: Quad[]): void {
    const water = id === 8 || id === 9;
    const kind = water ? 'water' : 'lava';
    const { still, big, flow } = this.fluidTiles(kind);
    const info = this.a.info[`blocks/${kind}_still`];
    const flat = info?.[2] ?? (water ? 0x2f5cff : 0xf55a10);
    fluidQuads(
      nb,
      id,
      { stillC: still[0], stillR: still[1], flowC: flow[0], flowR: flow[1], flowSpan: big ? (big[2] << 8) | big[3] : 0, beta: false, tint: water ? TINT_WATER : TINT_NONE, flat, textured: this.textured },
      (dx, dy, dz) => this.solidFull(nb.id(dx, dy, dz), nb.meta(dx, dy, dz)),
      out,
    );
  }

  private fluidTiles(kind: 'water' | 'lava'): { still: number[]; big: number[] | undefined; flow: number[] } {
    const still = this.a.tiles[`blocks/${kind}_still`] ?? [0, 0];
    const big = Object.entries(this.a.sheets).find(([n]) => n.startsWith(`blocks/${kind}_flow@`))?.[1];
    const flow = big ?? this.a.tiles[`blocks/${kind}_flow`] ?? still;
    return { still, big, flow };
  }

  /** Fast graphics: leaves drawn opaque (and hiding each other). */
  private opaqueLeaves(id: number): boolean {
    return this.fastLeaves && (id === 18 || id === 161);
  }

  /** The baked (cached) quads of a resolved state. */
  bake(id: number, ref: StateRef, nb: Nb | null = null): Quad[] {
    const key = id + '|' + cacheKey(ref);
    let quads = this.baked.get(key);
    if (quads) return quads;
    quads = this.bakeNow(id, ref, nb ?? NO_NB);
    this.baked.set(key, quads);
    return quads;
  }

  /** Model references a state draws with (tests, diagnostics). */
  modelsOf(ref: StateRef) {
    return this.files.get(ref.file)?.select(ref.props, ref.key) ?? [];
  }

  private bakeNow(id: number, ref: StateRef, nb: Nb): Quad[] {
    if (ref.builtin) return this.bakeBuiltin(id, ref, nb);
    const list: Quad[] = [];
    const file = this.files.get(ref.file);
    if (!file) return list;
    const raw: BakedFace[] = [];
    for (const v of file.select(ref.props, ref.key)) this.baker.bake(v.model, v.x, v.y, v.uvlock, raw);
    const style: FaceStyle = { id, tint: this.tintOf(id, ref), translucent: translucentLayer(id), emissive: EMISSIVE.has(id) };
    for (const m of raw) {
      const q = this.styleFace(m, style);
      // A second element over the same face (the grass side fringe) becomes the first one's overlay.
      const host = list.find((o) => o.ovC < 0 && samePositions(o, q));
      if (host) {
        host.ovC = q.c;
        host.ovR = q.r;
        host.ovTint = q.tint;
      } else list.push(q);
    }
    return list;
  }

  /** Tile-entity blocks and the like: our own geometry (untextured, a span takes the block's flat colour). */
  private bakeBuiltin(id: number, ref: StateRef, nb: Nb): Quad[] {
    const list: Quad[] = [];
    builtinQuads(id, ref, nb, this.sheet, list);
    const emissive = EMISSIVE.has(id);
    const flat = flatColour('1.12.2', id, 0, true);
    for (const q of list) {
      if (emissive) q.emissive = true;
      if (this.textured) continue;
      q.textured = false;
      if (q.span) q.flat = flat;
    }
    return list;
  }

  /** A baked face's tint, render layer (translucent blocks, cut-out where its texture has holes), light and texture mode. */
  private styleFace(m: BakedFace, st: FaceStyle): Quad {
    const q: Quad = m;
    q.tint = m.tintindex >= 0 ? st.tint : TINT_NONE;
    const cov = this.a.info[m.texture]?.[0] ?? 1;
    q.layer = st.translucent ? Layer.Translucent : cov < 0.999 ? Layer.Cutout : Layer.Opaque;
    q.emissive = st.emissive;
    if (st.emissive) q.ao = false;
    if (this.opaqueLeaves(st.id)) {
      q.layer = Layer.Opaque;
      q.fx = TEX_OPAQUE;
    }
    if (!this.textured) q.textured = false;
    return q;
  }

  /** BlockColors.colorMultiplier for tinted faces (biome kinds resolved per position). */
  private tintOf(id: number, s: StateRef): number {
    const f = TINTS[id];
    return f ? f(s) : TINT_NONE;
  }

  biomeColour(kind: number, biome: number): number {
    const b = this.biomes.get(biome);
    const k = kind - 1;
    if (b && b[k] >= 0) return b[k];
    return this.plains[k] ?? 0xffffff;
  }
}
