// The b1.8.1 procedural block textures, regenerated every game tick like the client's TextureFX subclasses (our own code,
// data-free, from the beta algorithms; the C# FluidTextures renders one warmed-up frame of the same recurrences):
// TextureWaterFX / TextureWaterFlowFX (a blurred heat field with random drops; the flow one blurs along v and scrolls a row
// per tick), TextureLavaFX / TextureLavaFlowFX (a sine-warped 3×3 blur with slow random hot spots; the flow one scrolls a row
// every 3 ticks), TextureFlamesFX (a 16×20 field rising from a random bottom row, two independent tiles) and TexturePortalFX
// (32 precomputed swirl frames). Each produces 16×16 RGBA for its terrain.png cell.

import { TILE_FIRE_A, TILE_FIRE_B, TILE_LAVA_FLOW, TILE_LAVA_STILL, TILE_PORTAL, TILE_WATER_FLOW, TILE_WATER_STILL } from '../mesh/beta/textures';

/** A small seeded PRNG (mulberry32): deterministic pictures, no Math.random. */
export function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export interface TextureFX {
  /** terrain.png tile index the texture fills. */
  readonly tile: number;
  readonly pixels: Uint8Array;
  tick(): void;
}

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

class WaterFX implements TextureFX {
  readonly pixels = new Uint8Array(1024);
  private a = new Float32Array(256);
  private b = new Float32Array(256);
  private readonly heat = new Float32Array(256);
  private readonly vel = new Float32Array(256);
  private ticks = 0;
  private readonly rand: () => number;
  constructor(
    readonly tile: number,
    private readonly flow: boolean,
  ) {
    this.rand = rng(flow ? 81 : 80);
  }
  tick(): void {
    this.ticks++;
    const { a, b, heat, flow } = this;
    for (let i = 0; i < 16; i++) for (let j = 0; j < 16; j++) b[i + j * 16] = this.spread(a, i, j) / (flow ? 3.2 : 3.3) + heat[i + j * 16] * 0.8;
    this.stir();
    this.a = b;
    this.b = a;
    this.paint();
  }

  /** The cell's neighbourhood sum: the three cells above it (flowing, the texture scrolls down) or beside it (still). */
  private spread(a: Float32Array, i: number, j: number): number {
    let f = 0;
    if (this.flow) for (let k = j - 2; k <= j; k++) f += a[(i & 15) + (k & 15) * 16];
    else for (let k = i - 1; k <= i + 1; k++) f += a[(k & 15) + (j & 15) * 16];
    return f;
  }

  /** Heat rises with each cell's velocity, which decays and now and then kicks back up. */
  private stir(): void {
    const { heat, vel, flow } = this;
    for (let i = 0; i < 256; i++) {
      heat[i] += vel[i] * 0.05;
      if (heat[i] < 0) heat[i] = 0;
      vel[i] -= flow ? 0.3 : 0.1;
      if (this.rand() < (flow ? 0.2 : 0.05)) vel[i] = 0.5;
    }
  }

  private paint(): void {
    const src = this.a;
    for (let k = 0; k < 256; k++) {
      const f1 = clamp01(src[this.flow ? (k - this.ticks * 16) & 255 : k]);
      const f2 = f1 * f1;
      const o = k * 4;
      this.pixels[o] = 32 + f2 * 32;
      this.pixels[o + 1] = 50 + f2 * 64;
      this.pixels[o + 2] = 255;
      this.pixels[o + 3] = 146 + f2 * 50;
    }
  }
}

class LavaFX implements TextureFX {
  readonly pixels = new Uint8Array(1024);
  private a = new Float32Array(256);
  private b = new Float32Array(256);
  private readonly heat = new Float32Array(256);
  private readonly vel = new Float32Array(256);
  private ticks = 0;
  private readonly rand: () => number;
  constructor(
    readonly tile: number,
    private readonly flow: boolean,
  ) {
    this.rand = rng(flow ? 91 : 90);
  }
  tick(): void {
    this.ticks++;
    const { a, b, heat, vel } = this;
    for (let i = 0; i < 16; i++)
      for (let j = 0; j < 16; j++) {
        let f = 0;
        const l = (Math.sin((j * Math.PI * 2) / 16) * 1.2) | 0, i1 = (Math.sin((i * Math.PI * 2) / 16) * 1.2) | 0;
        for (let k = i - 1; k <= i + 1; k++) for (let m = j - 1; m <= j + 1; m++) f += a[((k + l) & 15) + ((m + i1) & 15) * 16];
        const idx = i + j * 16;
        b[idx] = f / 10 + ((heat[(i & 15) + (j & 15) * 16] + heat[((i + 1) & 15) + (j & 15) * 16] + heat[((i + 1) & 15) + ((j + 1) & 15) * 16] + heat[(i & 15) + ((j + 1) & 15) * 16]) / 4) * 0.8;
        heat[idx] += vel[idx] * 0.01;
        if (heat[idx] < 0) heat[idx] = 0;
        vel[idx] -= 0.06;
        if (this.rand() < 0.005) vel[idx] = 1.5;
      }
    this.a = b;
    this.b = a;
    const src = this.a;
    const shift = this.flow ? ((this.ticks / 3) | 0) * 16 : 0;
    for (let k = 0; k < 256; k++) {
      const f1 = clamp01(src[(k - shift) & 255] * 2);
      const o = k * 4;
      this.pixels[o] = f1 * 100 + 155;
      this.pixels[o + 1] = f1 * f1 * 255;
      this.pixels[o + 2] = f1 * f1 * f1 * f1 * 128;
      this.pixels[o + 3] = 255;
    }
  }
}

/** A flame cell rises from the one below it (weighted 18) and the six around and above it that lie on the sheet. */
function flameCell(a: Float32Array, i: number, j: number): number {
  let l = 18;
  let f = a[i + ((j + 1) % 20) * 16] * l;
  for (let k = i - 1; k <= i + 1; k++)
    for (let m = j; m <= j + 1; m++) {
      if (k >= 0 && m >= 0 && k < 16 && m < 20) f += a[k + m * 16];
      l++;
    }
  return f / (l * 1.06);
}

class FlamesFX implements TextureFX {
  readonly pixels = new Uint8Array(1024);
  private a = new Float32Array(320);
  private b = new Float32Array(320);
  private readonly rand: () => number;
  constructor(readonly tile: number) {
    this.rand = rng(tile);
  }
  tick(): void {
    const { a, b } = this;
    for (let i = 0; i < 16; i++)
      for (let j = 0; j < 20; j++) {
        b[i + j * 16] = flameCell(a, i, j);
        if (j >= 19) b[i + j * 16] = this.rand() * this.rand() * this.rand() * 4 + this.rand() * 0.1 + 0.2;
      }
    this.a = b;
    this.b = a;
    for (let k = 0; k < 256; k++) {
      const f1 = clamp01(this.a[k] * 1.8);
      const o = k * 4;
      this.pixels[o] = f1 * 155 + 100;
      this.pixels[o + 1] = f1 * f1 * 255;
      this.pixels[o + 2] = f1 ** 10 * 255;
      this.pixels[o + 3] = f1 < 0.5 ? 0 : 255;
    }
  }
}

class PortalFX implements TextureFX {
  readonly tile = TILE_PORTAL;
  readonly pixels = new Uint8Array(1024);
  private readonly frames: Uint8Array[] = [];
  private ticks = 0;
  constructor() {
    const rand = rng(100);
    for (let i = 0; i < 32; i++) {
      const frame = new Uint8Array(1024);
      for (let j = 0; j < 16; j++)
        for (let k = 0; k < 16; k++) {
          let f = 0;
          for (let l = 0; l < 2; l++) {
            const off = l * 8;
            let f3 = ((j - off) / 16) * 2, f4 = ((k - off) / 16) * 2;
            if (f3 < -1) f3 += 2;
            if (f3 >= 1) f3 -= 2;
            if (f4 < -1) f4 += 2;
            if (f4 >= 1) f4 -= 2;
            const f5 = f3 * f3 + f4 * f4;
            let f6 = Math.atan2(f4, f3) + ((i / 32) * Math.PI * 2 - f5 * 10 + l * 2) * (l * 2 - 1);
            f6 = (Math.sin(f6) + 1) / 2;
            f6 /= f5 + 1;
            f += f6 * 0.5;
          }
          f += rand() * 0.1;
          const o = (k * 16 + j) * 4;
          // Purple: r = f²·200 + 55, g = f⁴·255, b = f·100 + 155; alpha f·100 + 155.
          frame[o] = Math.min(255, f * f * 200 + 55);
          frame[o + 1] = Math.min(255, f * f * f * f * 255);
          frame[o + 2] = Math.min(255, f * 100 + 155);
          frame[o + 3] = Math.min(255, f * 100 + 155);
        }
      this.frames.push(frame);
    }
  }
  tick(): void {
    this.ticks++;
    this.pixels.set(this.frames[this.ticks & 31]);
  }
}

/** Every b1.8.1 TextureFX, warmed up so the first frame is a settled one. */
export function betaEffects(warmup = 120): TextureFX[] {
  const fx: TextureFX[] = [
    new WaterFX(TILE_WATER_STILL, false),
    new WaterFX(TILE_WATER_FLOW, true),
    new LavaFX(TILE_LAVA_STILL, false),
    new LavaFX(TILE_LAVA_FLOW, true),
    new FlamesFX(TILE_FIRE_A),
    new FlamesFX(TILE_FIRE_B),
    new PortalFX(),
  ];
  for (const f of fx) for (let i = 0; i < warmup; i++) f.tick();
  return fx;
}
