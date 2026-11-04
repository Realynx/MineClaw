// Water and lava, both versions (RenderBlocks.renderBlockFluids / 1.12.2 BlockFluidRenderer, via the C# BlockGeometry port):
// per-corner surface heights averaged over the four cells sharing each corner (sources and falling fluid weigh 10 + 1), the
// flow vector toward lower neighbours, the flowing texture turned to the flow angle on sloped/flowing tops and the still
// texture on flat ones, sides up to the corner heights where the neighbour is neither this fluid nor opaque, and the bottom
// over anything that isn't. Water is translucent (its top drawn from below too), lava opaque and full bright.

import { Layer } from '../world/blocks';
import { FACE_SHADE, makeQuad, type Quad, type QuadInit, TINT_NONE } from './quad';
import type { Nb } from './models/states';

export const isWater = (id: number) => id === 8 || id === 9;
export const isLava = (id: number) => id === 10 || id === 11;
export const isFluid = (id: number) => id >= 8 && id <= 11;
export const sameFluid = (a: number, b: number) => (isWater(a) ? isWater(b) : isLava(a) && isLava(b));

/** Materials that are not solid (Material.isSolid false): plants, fluids, fire, circuits, snow layers, portals, vines. */
const NON_SOLID = new Set([0, 6, 8, 9, 10, 11, 27, 28, 30, 31, 32, 37, 38, 39, 40, 50, 51, 55, 59, 65, 66, 69, 75, 76, 77, 78, 83, 90, 93, 94, 104, 105, 106, 111, 115, 131, 132, 141, 142, 143, 149, 150, 157, 171, 175, 207]);
export const solidMaterial = (id: number) => !NON_SOLID.has(id);

/** BlockFluids.getPercentAir: the fraction of the cell above the fluid; falling (≥ 8) counts as a source. */
export const percentAir = (meta: number) => ((meta >= 8 ? 0 : meta) + 1) / 9;

/** The top height of a corner (cx, cz ∈ {0, 1} of the cell at nb's origin), averaged over the four cells sharing it. */
export function cornerHeight(nb: Nb, cx: number, cz: number, fluid: number): number {
  let l = 0, f = 0;
  for (let n = 0; n < 4; n++) {
    const x = cx - (n & 1), z = cz - ((n >> 1) & 1);
    if (sameFluid(fluid, nb.id(x, 1, z))) return 1;
    const id = nb.id(x, 0, z);
    if (sameFluid(fluid, id)) {
      const m = nb.meta(x, 0, z);
      if (m >= 8 || m === 0) {
        f += percentAir(m) * 10;
        l += 10;
      }
      f += percentAir(m);
      l++;
      continue;
    }
    if (!solidMaterial(id)) {
      f++;
      l++;
    }
  }
  return l === 0 ? 1 - percentAir(0) : 1 - f / l;
}

/** The four top-corner heights in vanilla order: (x0 z0), (x0 z1), (x1 z1), (x1 z0). */
export function fluidCorners(nb: Nb, fluid: number): [number, number, number, number] {
  return [cornerHeight(nb, 0, 0, fluid), cornerHeight(nb, 0, 1, fluid), cornerHeight(nb, 1, 1, fluid), cornerHeight(nb, 1, 0, fluid)];
}

function decay(nb: Nb, x: number, y: number, z: number, fluid: number): number {
  if (!sameFluid(fluid, nb.id(x, y, z))) return -1;
  const m = nb.meta(x, y, z);
  return m >= 8 ? 0 : m;
}

/** BlockFluids flow vector (x, y, z), normalised; zero when still. */
export function flowVector(nb: Nb, fluid: number): [number, number, number] {
  let vx = 0, vy = 0, vz = 0;
  const l = decay(nb, 0, 0, 0, fluid);
  for (const [x, z] of FLOW_SIDES) {
    const d = sideFlow(nb, x, z, l, fluid);
    if (d === null) continue;
    vx += x * d;
    vz += z * d;
  }
  // Falling fluid next to a solid side: mostly down.
  if (nb.meta(0, 0, 0) >= 8 && besideSolid(nb)) {
    const len0 = Math.hypot(vx, vz);
    if (len0 > 0) {
      vx /= len0;
      vz /= len0;
    }
    vy -= 6;
  }
  const len = Math.hypot(vx, vy, vz);
  return len < 1e-9 ? [0, 0, 0] : [vx / len, vy / len, vz / len];
}

const FLOW_SIDES: ReadonlyArray<readonly [number, number]> = [[-1, 0], [0, -1], [1, 0], [0, 1]];
/** The cells beside a falling fluid (its level and the one above) that turn its flow down when solid. */
const FALL_SIDES: ReadonlyArray<readonly [number, number, number]> = [[0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0], [0, 1, -1], [0, 1, 1], [-1, 1, 0], [1, 1, 0]];

/** The flow toward side (x, z) from a cell of decay `l`: the side's decay difference, or (an open side without fluid) the
 * difference to the fluid below it; null when nothing flows that way. */
function sideFlow(nb: Nb, x: number, z: number, l: number, fluid: number): number | null {
  const l1 = decay(nb, x, 0, z, fluid);
  if (l1 >= 0) return l1 - l;
  if (solidMaterial(nb.id(x, 0, z))) return null;
  const below = decay(nb, x, -1, z, fluid);
  return below >= 0 ? below - (l - 8) : null;
}

function besideSolid(nb: Nb): boolean {
  for (const [x, y, z] of FALL_SIDES) if (solidMaterial(nb.id(x, y, z)) && nb.id(x, y, z) !== 0) return true;
  return false;
}

/** atan2(flow.z, flow.x) − π/2, or −1000 when still (no horizontal flow). */
export function flowAngle(nb: Nb, fluid: number): number {
  const [fx, , fz] = flowVector(nb, fluid);
  if (fx === 0 && fz === 0) return -1000;
  return Math.atan2(fz, fx) - Math.PI / 2;
}

export interface FluidTex {
  stillC: number;
  stillR: number;
  flowC: number;
  flowR: number;
  /** The flowing texture as a w×h-cell sheet at native resolution (1.12.2's 32 px flow on 2×2 cells): (w << 8) | h, 0 = one
   * cell. UVs are scaled to the sheet. */
  flowSpan?: number;
  /** b1.8.1: tiling procedural textures (UVs wrap, the flow window is a whole tile); 1.12.2: a sprite (half windows). */
  beta: boolean;
  tint: number;
  /** Untextured colours. */
  flat: number;
  textured: boolean;
}

/** Appends the quads of the fluid cell at nb's origin. `opaque(dx, dy, dz)`: the neighbour is an opaque cube. */
export function fluidQuads(nb: Nb, id: number, tex: FluidTex, opaque: (dx: number, dy: number, dz: number) => boolean, out: Quad[]): void {
  const water = isWater(id);
  const [h00, h01, h11, h10] = fluidCorners(nb, id);
  const base = fluidBase(tex, water);
  const fspan = tex.flowSpan ?? 0, fk = fspan ? fspan >> 8 : 1;
  const flowUv = (uv: number[]) => (fk === 1 ? uv : uv.map((v) => v * fk));
  if (!sameFluid(id, nb.id(0, 1, 0))) {
    const t = topSurface(flowAngle(nb, id), tex, flowUv);
    out.push(makeQuad([0, h00, 0, 0, h01, 1, 1, h11, 1, 1, h10, 0], t.uv, { ...base, c: t.c, r: t.r, span: t.flowing ? fspan : 0, wrap: tex.beta, shade: 1, dir: 1, twoSided: water }));
  }
  const below = nb.id(0, -1, 0);
  if (!sameFluid(id, below) && !opaque(0, -1, 0))
    out.push(makeQuad([0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 1], [0, 1, 0, 0, 1, 0, 1, 1], { ...base, c: tex.stillC, r: tex.stillR, wrap: tex.beta, shade: 0.5 }));
  const su = tex.beta ? 1 : 0.5;
  const side = (f: number, nx: number, nz: number, ax: number, az: number, bx: number, bz: number, ha: number, hb: number) => {
    const n = nb.id(nx, 0, nz);
    if (sameFluid(id, n) || opaque(nx, 0, nz) || n === 79) return;
    out.push(
      makeQuad([ax, ha, az, bx, hb, bz, bx, 0, bz, ax, 0, az], flowUv([0, (1 - ha) * 0.5, su, (1 - hb) * 0.5, su, 0.5, 0, 0.5]), {
        ...base,
        c: tex.flowC,
        r: tex.flowR,
        span: fspan,
        wrap: tex.beta,
        shade: FACE_SHADE[f],
        dir: f,
      }),
    );
  };
  side(2, 0, -1, 0, 0, 1, 0, h00, h10);
  side(3, 0, 1, 1, 1, 0, 1, h11, h01);
  side(4, -1, 0, 0, 1, 0, 0, h01, h00);
  side(5, 1, 0, 1, 0, 1, 1, h10, h11);
}

/** What every quad of a fluid cell shares: water blends on the translucent layer, lava glows; untextured water is see-through. */
function fluidBase(tex: FluidTex, water: boolean): QuadInit {
  const layer = water ? Layer.Translucent : Layer.Opaque;
  const alpha = tex.textured || !water ? 1 : 0.62;
  return { layer, emissive: !water, ao: false, tint: water ? tex.tint : TINT_NONE, flat: tex.flat, textured: tex.textured, alpha };
}

/** The top surface's texture: still when nothing flows, else the flowing texture turned along the flow (a window of it). */
function topSurface(ang: number, tex: FluidTex, flowUv: (uv: number[]) => number[]): { uv: number[]; c: number; r: number; flowing: boolean } {
  if (ang < -999) return { uv: [0, 0, 0, 1, 1, 1, 1, 0], c: tex.stillC, r: tex.stillR, flowing: false };
  const win = tex.beta ? 0.5 : 0.25;
  const s = Math.sin(ang) * win, k = Math.cos(ang) * win;
  const uv = flowUv([0.5 - k - s, 0.5 - k + s, 0.5 - k + s, 0.5 + k + s, 0.5 + k + s, 0.5 + k - s, 0.5 + k - s, 0.5 - k - s]);
  return { uv, c: tex.flowC, r: tex.flowR, flowing: true };
}
