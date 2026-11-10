/**
 * The timeline's drawing, apart from React so it can be tested on a recording context: a logic analyser with one lane per
 * probe (the on/off trace, and the power 0..15 as a fainter step line), the game ticks along the top, the part of the
 * window that can no longer be stepped back to shaded, and a cursor on the current tick. The window ends at the current
 * tick; one game tick is `pxPerTick` pixels wide.
 *
 * It costs a canvas redraw and a history read per probe, so `TimelinePainter` draws only when what it shows can have
 * changed: the tick, the zoom, the size, the probes or the mode.
 */
import type { Sample } from '@mineclaw/redstone';

/** Pixels per game tick. */
export const ZOOMS: readonly number[] = [1, 2, 4, 8, 16];
export const DEFAULT_ZOOM = 4;
export const AXIS_HEIGHT = 14;
export const LANE_HEIGHT = 28;
/** Space between the lane's edge and the trace's high and low lines. */
const PAD = 5;
/** Tick numbers on the axis are at least this far apart. */
const MIN_LABEL_GAP = 40;
const TICK_STEPS = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000];
const GRID = 'rgba(128,128,128,0.35)';
const TEXT = 'rgba(128,128,128,1)';
const SHADE = 'rgba(128,128,128,0.12)';
const CURSOR = 'rgba(239,68,68,0.9)';
const POWER_ALPHA = 0.4;

export type TimelineCtx = Pick<
  CanvasRenderingContext2D,
  | 'clearRect'
  | 'fillRect'
  | 'beginPath'
  | 'moveTo'
  | 'lineTo'
  | 'stroke'
  | 'fillText'
  | 'save'
  | 'restore'
  | 'fillStyle'
  | 'strokeStyle'
  | 'lineWidth'
  | 'globalAlpha'
  | 'font'
  | 'textAlign'
  | 'textBaseline'
>;

export interface TimelineLane {
  label: string;
  /** 0xRRGGBB. */
  color: number;
  /** Oldest first; ticks the history no longer holds are missing. */
  samples: readonly Sample[];
}

export interface TimelineFrame {
  width: number;
  height: number;
  pxPerTick: number;
  /** The current tick: the window's last one and where the cursor is. */
  tick: number;
  /** The oldest tick that can still be stepped back to. */
  historyFrom: number;
  lanes: readonly TimelineLane[];
}

export const timelineHeight = (lanes: number): number => AXIS_HEIGHT + Math.max(1, lanes) * LANE_HEIGHT;

/** The first tick the window shows (it may be before tick 0 when the window is wider than the run). */
export const firstTick = (tick: number, width: number, pxPerTick: number): number =>
  tick - Math.max(1, Math.floor(width / pxPerTick)) + 1;

export const xOfTick = (t: number, from: number, pxPerTick: number): number => (t - from) * pxPerTick;

export const tickAtX = (x: number, from: number, pxPerTick: number): number => from + Math.floor(x / pxPerTick);

/** The tick a click at `x` steps back to: an earlier tick still in history, else null. */
export function stepBackTarget(x: number, f: Pick<TimelineFrame, 'width' | 'pxPerTick' | 'tick' | 'historyFrom'>) {
  const t = tickAtX(x, firstTick(f.tick, f.width, f.pxPerTick), f.pxPerTick);
  return t < f.tick && t >= Math.max(0, f.historyFrom) ? t : null;
}

export const cssColor = (rgb: number): string => `#${(rgb & 0xffffff).toString(16).padStart(6, '0')}`;

/** Ticks between two numbers on the axis. */
export function labelStep(pxPerTick: number): number {
  return TICK_STEPS.find((s) => s * pxPerTick >= MIN_LABEL_GAP) ?? TICK_STEPS[TICK_STEPS.length - 1]!;
}

/** A step line through the samples: flat over each tick, a jump between neighbours, a break where a tick is missing. */
function stepLine(
  ctx: TimelineCtx,
  samples: readonly Sample[],
  from: number,
  to: number,
  px: number,
  y: (s: Sample) => number,
): void {
  ctx.beginPath();
  let prev: number | null = null;
  for (const s of samples) {
    if (s.tick < from || s.tick > to) continue;
    const x0 = xOfTick(s.tick, from, px);
    const yy = y(s);
    if (prev === s.tick - 1) ctx.lineTo(x0, yy);
    else ctx.moveTo(x0, yy);
    ctx.lineTo(x0 + px, yy);
    prev = s.tick;
  }
  ctx.stroke();
}

export function drawTimeline(ctx: TimelineCtx, f: TimelineFrame): void {
  const { width, height, pxPerTick: px, tick } = f;
  const from = firstTick(tick, width, px);
  ctx.clearRect(0, 0, width, height);
  ctx.save();

  // Before the history's start nothing can be stepped back to.
  const shadeTo = Math.min(width, xOfTick(Math.max(0, f.historyFrom), from, px));
  if (shadeTo > 0) {
    ctx.fillStyle = SHADE;
    ctx.fillRect(0, 0, shadeTo, height);
  }

  ctx.font = '9px ui-monospace, monospace';
  ctx.textBaseline = 'top';
  ctx.textAlign = 'left';
  ctx.fillStyle = TEXT;
  ctx.strokeStyle = GRID;
  ctx.lineWidth = 1;
  const step = labelStep(px);
  ctx.beginPath();
  for (let t = Math.ceil(Math.max(0, from) / step) * step; t <= tick; t += step) {
    const x = xOfTick(t, from, px);
    ctx.moveTo(x, AXIS_HEIGHT - 4);
    ctx.lineTo(x, height);
    ctx.fillText(String(t), x + 2, 1);
  }
  ctx.stroke();

  f.lanes.forEach((lane, i) => {
    const top = AXIS_HEIGHT + i * LANE_HEIGHT;
    const hi = top + PAD;
    const lo = top + LANE_HEIGHT - PAD;
    const color = cssColor(lane.color);
    ctx.strokeStyle = GRID;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, top + LANE_HEIGHT);
    ctx.lineTo(width, top + LANE_HEIGHT);
    ctx.stroke();

    ctx.strokeStyle = color;
    ctx.globalAlpha = POWER_ALPHA;
    stepLine(ctx, lane.samples, from, tick, px, (s) => lo - (s.power / 15) * (lo - hi));
    ctx.globalAlpha = 1;
    ctx.lineWidth = 1.5;
    stepLine(ctx, lane.samples, from, tick, px, (s) => (s.powered ? hi : lo));

    ctx.fillStyle = color;
    ctx.fillText(lane.label, 2, top + 1);
  });

  // The cursor sits in the middle of the current tick's column, with its number beside it.
  const cx = xOfTick(tick, from, px) + px / 2;
  ctx.strokeStyle = CURSOR;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(cx, 0);
  ctx.lineTo(cx, height);
  ctx.stroke();
  ctx.fillStyle = CURSOR;
  ctx.textAlign = 'right';
  ctx.fillText(`${tick} gt`, cx - 2, 1);
  ctx.restore();
}

/** What decides whether the timeline has to be drawn again. */
export interface TimelineInput {
  width: number;
  pxPerTick: number;
  tick: number;
  historyFrom: number;
  /** The simulation's mode: a new simulation starts over at the same tick. */
  mode: string;
  probes: ReadonlyArray<{ id: string; label: string; color: number }>;
}

const keyOf = (i: TimelineInput): string =>
  [i.width, i.pxPerTick, i.tick, i.historyFrom, i.mode, ...i.probes.map((p) => `${p.id}:${p.color}:${p.label}`)].join(
    '|',
  );

/** Draws the timeline only when its input changed since the last drawing. */
export class TimelinePainter {
  private last: string | null = null;

  /** Draws, reading the lanes' samples only then. True when it drew. */
  paint(
    ctx: TimelineCtx,
    input: TimelineInput,
    samples: (probeId: string, from: number, to: number) => readonly Sample[],
  ) {
    const key = keyOf(input);
    if (key === this.last) return false;
    this.last = key;
    const from = Math.max(0, firstTick(input.tick, input.width, input.pxPerTick));
    drawTimeline(ctx, {
      width: input.width,
      height: timelineHeight(input.probes.length),
      pxPerTick: input.pxPerTick,
      tick: input.tick,
      historyFrom: input.historyFrom,
      lanes: input.probes.map((p) => ({ label: p.label, color: p.color, samples: samples(p.id, from, input.tick) })),
    });
    return true;
  }

  /** The next `paint` draws whatever it is given (a new canvas). */
  invalidate(): void {
    this.last = null;
  }
}
