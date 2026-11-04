// 1.12.2 texture animations (.mcmeta: frames, frametime, interpolate), as a pure function of the game tick, and the CPU
// side of the atlas animation pass: which animated cells need new pixels this tick (within a budget and, optionally, only
// the visible ones) and where they come from (a frame of anim.png, or an interpolated blend).
//
// TextureAtlasSprite.updateAnimation: each tick the tick counter advances; when it reaches the current frame's time the
// frame counter steps (wrapping) and the counter resets. So frame k shows for time[k] ticks, and with `interpolate` the
// texel is d·current + (1 − d)·next with d = 1 − tickInFrame / time[k] (alpha from the current frame).

export interface AnimFrame {
  index: number;
  time: number;
}

export interface AnimSpec {
  cell: [number, number];
  frames: AnimFrame[];
  interpolate: boolean;
  /** Row of its strip in anim.png in 16 px units (frame i at x = size·i). */
  row: number;
  /** Frame size in px (16; 32 for the native flowing water/lava sheets). */
  size?: number;
}

export interface FrameState {
  /** Position in the frame list. */
  k: number;
  /** Strip frame shown. */
  frame: number;
  /** The next strip frame (interpolation target). */
  next: number;
  /** Weight of `frame` (1 = exactly it). */
  weight: number;
  tickInFrame: number;
}

/** The frame (and blend) of an animation at absolute game tick `tick`. */
export function frameAt(frames: AnimFrame[], tick: number, interpolate = false): FrameState {
  const n = frames.length;
  if (n === 0) return { k: 0, frame: 0, next: 0, weight: 1, tickInFrame: 0 };
  let period = 0;
  for (const f of frames) period += Math.max(1, f.time);
  let t = ((Math.floor(tick) % period) + period) % period;
  let k = 0;
  while (t >= Math.max(1, frames[k].time)) {
    t -= Math.max(1, frames[k].time);
    k++;
  }
  const time = Math.max(1, frames[k].time);
  const next = frames[(k + 1) % n].index;
  const weight = interpolate && next !== frames[k].index ? 1 - t / time : 1;
  return { k, frame: frames[k].index, next, weight, tickInFrame: t };
}

/** size×size RGBA blend of two frames: rgb = w·a + (1 − w)·b (truncated), alpha from a. */
export function blendFrames(a: Uint8Array | Uint8ClampedArray, ao: number, b: Uint8Array | Uint8ClampedArray, bo: number, stride: number, w: number, out: Uint8Array, size = 16): void {
  for (let y = 0; y < size; y++)
    for (let x = 0; x < size; x++) {
      const ia = ao + y * stride + x * 4, ib = bo + y * stride + x * 4, o = (y * size + x) * 4;
      out[o] = (w * a[ia] + (1 - w) * b[ib]) | 0;
      out[o + 1] = (w * a[ia + 1] + (1 - w) * b[ib + 1]) | 0;
      out[o + 2] = (w * a[ia + 2] + (1 - w) * b[ib + 2]) | 0;
      out[o + 3] = a[ia + 3];
    }
}

/** Where a cell's new pixels come from. */
export interface CellWriter {
  /** Copy the size×size frame at (sx, sy) px of the animation sheet into the cells from (c, r). */
  fromSheet(c: number, r: number, sx: number, sy: number, size: number): void;
  /** Upload size×size RGBA pixels into the cells from (c, r). */
  fromPixels(c: number, r: number, px: Uint8Array, size: number): void;
}

export interface AnimBudget {
  /** Most cells updated per tick (0 = all). */
  maxCells?: number;
  /** Only these cells (row * columns + column); null = all. */
  visible?: ReadonlySet<number> | null;
}

/** Drives every animated cell of a block atlas from its anim.png strip. */
export class SpriteAnimator {
  private readonly shown: Float64Array;
  private cursor = 0;
  private readonly scratch = new Map<number, Uint8Array>();

  constructor(
    readonly anims: AnimSpec[],
    /** anim.png RGBA pixels (unpremultiplied) and its width, for interpolated frames. */
    private readonly sheet: { data: Uint8Array | Uint8ClampedArray; width: number } | null,
    readonly columns: number,
  ) {
    this.shown = new Float64Array(anims.length).fill(-1);
  }

  /** Brings due cells to tick `tick`; returns how many were written. */
  update(tick: number, w: CellWriter, budget: AnimBudget = {}): number {
    const n = this.anims.length;
    const max = budget.maxCells && budget.maxCells > 0 ? budget.maxCells : n;
    let done = 0, last = -1;
    for (let i = 0; i < n && done < max; i++) {
      const j = (this.cursor + i) % n;
      if (!this.refresh(j, tick, w, budget.visible)) continue;
      done++;
      last = j;
    }
    // Round-robin: under a budget the next tick starts after the last cell written.
    if (done && done >= max) this.cursor = (last + 1) % n;
    return done;
  }

  /** Writes animation j's frame for `tick` when its cell is visible and shows another frame; true when written. */
  private refresh(j: number, tick: number, w: CellWriter, visible: AnimBudget['visible']): boolean {
    const a = this.anims[j];
    if (visible && !visible.has(a.cell[1] * this.columns + a.cell[0])) return false;
    const f = frameAt(a.frames, tick, a.interpolate && !!this.sheet);
    const key = f.weight >= 1 ? f.frame : f.frame * 1e6 + f.next * 1e3 + Math.round(f.weight * 255);
    if (this.shown[j] === key) return false;
    this.shown[j] = key;
    this.writeFrame(a, f, w);
    return true;
  }

  /** A whole frame straight from the sheet, or (interpolated) two frames blended. */
  private writeFrame(a: AnimSpec, f: ReturnType<typeof frameAt>, w: CellWriter): void {
    const size = a.size ?? 16;
    if (f.weight >= 1 || !this.sheet) {
      w.fromSheet(a.cell[0], a.cell[1], f.frame * size, a.row * 16, size);
      return;
    }
    const stride = this.sheet.width * 4;
    let px = this.scratch.get(size);
    if (!px) this.scratch.set(size, (px = new Uint8Array(size * size * 4)));
    blendFrames(this.sheet.data, a.row * 16 * stride + f.frame * size * 4, this.sheet.data, a.row * 16 * stride + f.next * size * 4, stride, f.weight, px, size);
    w.fromPixels(a.cell[0], a.cell[1], px, size);
  }

  /** Forget what was uploaded (the texture was recreated). */
  reset(): void {
    this.shown.fill(-1);
  }
}
