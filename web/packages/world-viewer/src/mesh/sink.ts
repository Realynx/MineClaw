// Growable vertex buffers for one section pass. Vertex layout (per vertex):
//   pos   f32×3  section-local position
//   uv    f32×2  cell-local texture coordinate (0..1; wraps/spans per tile flags)
//   tile  u16×4  atlas cell column, row, texture flags (1 textured, 2 wrap), span ((w << 8) | h cells of a tile-entity
//                sheet the UVs address; 0 = one cell)
//   color u8×4   rgb = tint × face shade (or the flat colour), a = opacity
//   light u8×4   sky×17, block×17, ambient occlusion factor×255, light flags (LF_*)
//   cell  u8×4   the block's section-local cell (x, y, z) for the x-ray cone, 0
// Quads become two triangles (0,1,2)+(0,2,3), or (1,2,3)+(1,3,0) when `flip` (AO-aware diagonal).

export const TEX_TEXTURED = 1;
export const TEX_WRAP = 2;
/** The end portal / gateway starfield (render/starfield.ts) instead of a texel. */
export const TEX_STARFIELD = 4;
/** Ignore the texel's alpha (fast leaves). */
export const TEX_OPAQUE = 8;
/** With TEX_STARFIELD: the end gateway (one more starfield layer than the portal). */
export const TEX_GATEWAY = 16;

export const LF_EMISSIVE = 1;
export const LF_UNKNOWN = 2;
export const LF_REMEMBERED = 4;
export const LF_CUT = 8;

export interface MeshArrays {
  pos: Float32Array;
  uv: Float32Array;
  tile: Uint16Array;
  color: Uint8Array;
  light: Uint8Array;
  cell: Uint8Array;
  index: Uint16Array | Uint32Array;
  quads: number;
}

export class VertexSink {
  private cap = 1024;
  n = 0;
  pos = new Float32Array(this.cap * 3);
  uv = new Float32Array(this.cap * 2);
  tile = new Uint16Array(this.cap * 4);
  color = new Uint8Array(this.cap * 4);
  light = new Uint8Array(this.cap * 4);
  cell = new Uint8Array(this.cap * 4);
  private flips: number[] = [];

  reset(): void {
    this.n = 0;
    this.flips.length = 0;
  }

  get quads(): number {
    return this.n >> 2;
  }

  private grow(): void {
    const cap = this.cap * 2;
    const g = <T extends Float32Array | Uint16Array | Uint8Array>(a: T, k: number): T => {
      const b = new (a.constructor as { new (n: number): T })(cap * k);
      b.set(a);
      return b;
    };
    this.pos = g(this.pos, 3);
    this.uv = g(this.uv, 2);
    this.tile = g(this.tile, 4);
    this.color = g(this.color, 4);
    this.light = g(this.light, 4);
    this.cell = g(this.cell, 4);
    this.cap = cap;
  }

  /** Starts a quad; returns the first vertex index. */
  begin(flip = false): number {
    if (this.n + 4 > this.cap) this.grow();
    this.flips.push(flip ? 1 : 0);
    const v = this.n;
    this.n += 4;
    return v;
  }

  vertex(v: number, x: number, y: number, z: number, u: number, w: number): void {
    this.pos[v * 3] = x;
    this.pos[v * 3 + 1] = y;
    this.pos[v * 3 + 2] = z;
    this.uv[v * 2] = u;
    this.uv[v * 2 + 1] = w;
  }

  /** Same tile, colour and cell on all four vertices of the quad starting at v. */
  quadAttrs(v: number, col: number, row: number, tex: number, rgb: number, alpha: number, cx: number, cy: number, cz: number, span = 0): void {
    const r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255, a = Math.max(0, Math.min(255, Math.round(alpha * 255)));
    for (let i = v; i < v + 4; i++) {
      this.tile[i * 4] = col;
      this.tile[i * 4 + 1] = row;
      this.tile[i * 4 + 2] = tex;
      this.tile[i * 4 + 3] = span;
      this.color[i * 4] = r;
      this.color[i * 4 + 1] = g;
      this.color[i * 4 + 2] = b;
      this.color[i * 4 + 3] = a;
      this.cell[i * 4] = cx;
      this.cell[i * 4 + 1] = cy;
      this.cell[i * 4 + 2] = cz;
      this.cell[i * 4 + 3] = 0;
    }
  }

  lightAt(v: number, sky: number, block: number, ao: number, flags: number): void {
    this.light[v * 4] = Math.max(0, Math.min(255, Math.round(sky * 17)));
    this.light[v * 4 + 1] = Math.max(0, Math.min(255, Math.round(block * 17)));
    this.light[v * 4 + 2] = Math.max(0, Math.min(255, Math.round(ao * 255)));
    this.light[v * 4 + 3] = flags;
  }

  /** Copies the quad starting at v with the opposite winding (two-sided geometry under back-face culling). */
  mirror(v: number): void {
    const m = this.begin(this.flips[v >> 2] === 1);
    const order = [0, 3, 2, 1];
    for (let k = 0; k < 4; k++) {
      const s = v + order[k], d = m + k;
      this.pos.copyWithin(d * 3, s * 3, s * 3 + 3);
      this.uv.copyWithin(d * 2, s * 2, s * 2 + 2);
      this.tile.copyWithin(d * 4, s * 4, s * 4 + 4);
      this.color.copyWithin(d * 4, s * 4, s * 4 + 4);
      this.light.copyWithin(d * 4, s * 4, s * 4 + 4);
      this.cell.copyWithin(d * 4, s * 4, s * 4 + 4);
    }
  }

  /** Trimmed copies (transferable), or null when empty. */
  finish(): MeshArrays | null {
    if (this.n === 0) return null;
    const n = this.n;
    const q = n >> 2;
    const index = n > 65535 ? new Uint32Array(q * 6) : new Uint16Array(q * 6);
    for (let i = 0; i < q; i++) {
      const b = i * 4, o = i * 6;
      if (this.flips[i]) {
        index[o] = b + 1; index[o + 1] = b + 2; index[o + 2] = b + 3;
        index[o + 3] = b + 1; index[o + 4] = b + 3; index[o + 5] = b;
      } else {
        index[o] = b; index[o + 1] = b + 1; index[o + 2] = b + 2;
        index[o + 3] = b; index[o + 4] = b + 2; index[o + 5] = b + 3;
      }
    }
    return {
      pos: this.pos.slice(0, n * 3),
      uv: this.uv.slice(0, n * 2),
      tile: this.tile.slice(0, n * 4),
      color: this.color.slice(0, n * 4),
      light: this.light.slice(0, n * 4),
      cell: this.cell.slice(0, n * 4),
      index,
      quads: q,
    };
  }
}

/** The transferable buffers of a mesh. */
export function meshBuffers(m: MeshArrays | null): ArrayBuffer[] {
  if (!m) return [];
  return [m.pos.buffer, m.uv.buffer, m.tile.buffer, m.color.buffer, m.light.buffer, m.cell.buffer, m.index.buffer] as ArrayBuffer[];
}
