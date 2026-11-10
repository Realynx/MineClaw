/**
 * The debugger's timeline: the drawing on a recording context (the window ends at the current tick, a step line per
 * probe with breaks where history has no tick, the shade before the history, the cursor), the painter that draws only
 * when the tick, the zoom, the size or the probes change, and the component's click that steps back to a tick still in
 * history.
 */
import type { Sample } from '@mineclaw/redstone';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Timeline } from '@/features/studio/redstone/debugger/Timeline';
import {
  AXIS_HEIGHT,
  DEFAULT_ZOOM,
  drawTimeline,
  firstTick,
  labelStep,
  LANE_HEIGHT,
  stepBackTarget,
  tickAtX,
  timelineHeight,
  TimelinePainter,
  xOfTick,
  type TimelineCtx,
  type TimelineInput,
} from '@/features/studio/redstone/debugger/timelineDraw';
import { fakeController, sampleOf } from './redstoneDebuggerFixture';

type Op = [string, ...Array<number | string>];

/** A 2D context that writes down what is drawn, with the stroke colour and alpha each line was drawn in. */
class Recorder implements TimelineCtx {
  ops: Op[] = [];
  private path: Op[] = [];
  /** Only colours are drawn with here. */
  fillStyle = '';
  strokeStyle = '';
  lineWidth = 1;
  globalAlpha = 1;
  font = '';
  textAlign: CanvasTextAlign = 'left';
  textBaseline: CanvasTextBaseline = 'alphabetic';
  /** Each stroked path: its colour, alpha and the points. */
  strokes: Array<{ color: string; alpha: number; path: Op[] }> = [];

  clearRect(x: number, y: number, w: number, h: number) {
    this.ops.push(['clearRect', x, y, w, h]);
  }
  fillRect(x: number, y: number, w: number, h: number) {
    this.ops.push(['fillRect', x, y, w, h, this.fillStyle]);
  }
  beginPath() {
    this.path = [];
  }
  moveTo(x: number, y: number) {
    this.path.push(['M', x, y]);
  }
  lineTo(x: number, y: number) {
    this.path.push(['L', x, y]);
  }
  stroke() {
    this.strokes.push({ color: this.strokeStyle, alpha: this.globalAlpha, path: this.path });
  }
  fillText(text: string, x: number, y: number) {
    this.ops.push(['fillText', text, x, y, this.fillStyle]);
  }
  save() {}
  restore() {}
  setTransform() {}
  texts() {
    return this.ops.filter((o) => o[0] === 'fillText').map((o) => o[1]);
  }
}

const lane = (samples: Sample[], color = 0x38bdf8, label = 'A') => ({ label, color, samples });

describe('the window', () => {
  it('ends at the current tick and maps pixels to ticks', () => {
    // 100 px at 4 px a tick: 25 ticks, 76..100.
    expect(firstTick(100, 100, 4)).toBe(76);
    expect(xOfTick(100, 76, 4)).toBe(96);
    expect(tickAtX(0, 76, 4)).toBe(76);
    expect(tickAtX(99, 76, 4)).toBe(100);
    // Wider than the run: the window starts before tick 0.
    expect(firstTick(3, 100, 4)).toBe(-21);
    expect(timelineHeight(0)).toBe(AXIS_HEIGHT + LANE_HEIGHT);
    expect(timelineHeight(3)).toBe(AXIS_HEIGHT + 3 * LANE_HEIGHT);
  });

  it('a click steps back only to an earlier tick still in history', () => {
    const f = { width: 100, pxPerTick: 4, tick: 100, historyFrom: 90 };
    expect(stepBackTarget(4 * 20, f)).toBe(96);
    expect(stepBackTarget(4 * 14, f)).toBe(90);
    expect(stepBackTarget(4 * 13, f)).toBeNull();
    expect(stepBackTarget(4 * 24, f)).toBeNull();
    // Never before tick 0.
    expect(stepBackTarget(0, { width: 100, pxPerTick: 4, tick: 3, historyFrom: -5 })).toBeNull();
  });

  it('numbers the axis at least 40 px apart', () => {
    expect([1, 2, 4, 8, 16].map(labelStep)).toEqual([50, 20, 10, 5, 5]);
  });
});

describe('drawing', () => {
  it('draws a digital trace and a fainter power line per probe, breaking where a tick is missing', () => {
    const ctx = new Recorder();
    drawTimeline(ctx, {
      width: 40,
      height: timelineHeight(1),
      pxPerTick: 4,
      tick: 9,
      historyFrom: 0,
      lanes: [lane([sampleOf(0, 0), sampleOf(1, 15), sampleOf(2, 15), sampleOf(5, 0)])],
    });
    const traces = ctx.strokes.filter((s) => s.color === '#38bdf8');
    expect(traces.map((s) => s.alpha)).toEqual([0.4, 1]);
    const top = AXIS_HEIGHT;
    const hi = top + 5;
    const lo = top + LANE_HEIGHT - 5;
    // On/off: low over tick 0, a jump up at tick 1, high to the end of tick 2; tick 5 starts again after the gap.
    expect(traces[1]!.path).toEqual([
      ['M', 0, lo],
      ['L', 4, lo],
      ['L', 4, hi],
      ['L', 8, hi],
      ['L', 8, hi],
      ['L', 12, hi],
      ['M', 20, lo],
      ['L', 24, lo],
    ]);
    // Power 15 is the top of the lane, 0 the bottom.
    expect(traces[0]!.path.slice(0, 3)).toEqual([
      ['M', 0, lo],
      ['L', 4, lo],
      ['L', 4, hi],
    ]);
    expect(ctx.texts()).toContain('A');
  });

  it('leaves out what is outside the window, shades what can no longer be stepped back to, and marks the tick', () => {
    const ctx = new Recorder();
    // 40 px at 4 px a tick: ticks 11..20. History reaches back to 15.
    drawTimeline(ctx, {
      width: 40,
      height: 50,
      pxPerTick: 4,
      tick: 20,
      historyFrom: 15,
      lanes: [lane([sampleOf(9, 1), sampleOf(10, 1), sampleOf(11, 2), sampleOf(20, 3)])],
    });
    const digital = ctx.strokes.filter((s) => s.color === '#38bdf8' && s.alpha === 1)[0]!;
    expect(digital.path).toEqual([
      ['M', 0, AXIS_HEIGHT + 5],
      ['L', 4, AXIS_HEIGHT + 5],
      ['M', 36, AXIS_HEIGHT + 5],
      ['L', 40, AXIS_HEIGHT + 5],
    ]);
    expect(ctx.ops).toContainEqual(['fillRect', 0, 0, 16, 50, 'rgba(128,128,128,0.12)']);
    const cursor = ctx.strokes.find((s) => s.color.startsWith('rgba(239,68,68'))!;
    expect(cursor.path).toEqual([
      ['M', 38, 0],
      ['L', 38, 50],
    ]);
    expect(ctx.texts()).toContain('20 gt');
  });

  it('starts by clearing the whole canvas, and shades nothing while all of it can be stepped back to', () => {
    const ctx = new Recorder();
    drawTimeline(ctx, { width: 40, height: 50, pxPerTick: 4, tick: 20, historyFrom: 5, lanes: [] });
    expect(ctx.ops[0]).toEqual(['clearRect', 0, 0, 40, 50]);
    expect(ctx.ops.some((o) => o[0] === 'fillRect')).toBe(false);
  });
});

describe('the painter', () => {
  const input = (over: Partial<TimelineInput> = {}): TimelineInput => ({
    width: 100,
    pxPerTick: DEFAULT_ZOOM,
    tick: 30,
    historyFrom: 10,
    mode: 'paused',
    probes: [{ id: 'p1', label: 'A', color: 1 }],
    ...over,
  });

  it('draws again only when the tick, the zoom, the size, the probes or the mode change', () => {
    const p = new TimelinePainter();
    const ctx = new Recorder();
    const samples = vi.fn((): readonly Sample[] => []);
    const draws = () => ctx.ops.filter((o) => o[0] === 'clearRect').length;

    expect(p.paint(ctx, input(), samples)).toBe(true);
    // The same view, as when the panel re-renders for something else: no drawing and no history read.
    expect(p.paint(ctx, input(), samples)).toBe(false);
    expect([draws(), samples.mock.calls.length]).toEqual([1, 1]);
    // 100 px at 4 px a tick, ending at 30: the window starts at tick 6.
    expect(samples).toHaveBeenLastCalledWith('p1', 6, 30);

    const changes: Array<Partial<TimelineInput>> = [
      { tick: 31 },
      { tick: 31, pxPerTick: 8 },
      { tick: 31, pxPerTick: 8, width: 120 },
      { tick: 31, pxPerTick: 8, width: 120, probes: [{ id: 'p1', label: 'sum', color: 1 }] },
      { tick: 31, pxPerTick: 8, width: 120, probes: [{ id: 'p1', label: 'sum', color: 1 }], mode: 'running' },
    ];
    for (const over of changes) {
      expect(p.paint(ctx, input(over), samples)).toBe(true);
      expect(p.paint(ctx, input(over), samples)).toBe(false);
    }
    expect(draws()).toBe(6);
    p.invalidate();
    expect(p.paint(ctx, input(changes[4]), samples)).toBe(true);
  });
});

describe('the timeline component', () => {
  afterEach(() => vi.restoreAllMocks());

  function mount() {
    const ctx = new Recorder();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(ctx as unknown as RenderingContext);
    const fake = fakeController({
      tick: 100,
      canStepBack: 10,
      probes: [{ id: 'p1', at: [1, 0, 1], label: 'A', color: 0x38bdf8 }],
    });
    fake.samples.set(
      'p1',
      Array.from({ length: 11 }, (_, i) => sampleOf(90 + i, i % 2 ? 15 : 0)),
    );
    const view = () => createElement(Timeline, { api: fake.api, snap: fake.api.snapshot() });
    const r = render(view());
    const draws = () => ctx.ops.filter((o) => o[0] === 'clearRect').length;
    return { ...fake, ctx, draws, rerender: () => r.rerender(view()) };
  }

  it('draws when the tick or the zoom changes, not when the snapshot changes otherwise', () => {
    const h = mount();
    expect(h.draws()).toBe(1);
    act(() => h.set({ events: [{ kind: 'fired', at: [0, 0, 0], tick: 100 }], idle: true }));
    h.rerender();
    expect(h.draws()).toBe(1);
    act(() => h.set({ tick: 101 }));
    h.rerender();
    expect(h.draws()).toBe(2);
    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
    expect(screen.getByTestId('timeline-zoom')).toHaveTextContent('8 px/gt');
    expect(h.draws()).toBe(3);
  });

  it('a click on an earlier tick in history steps back to it, and nothing else does', () => {
    const h = mount();
    const canvas = screen.getByTestId('timeline-canvas');
    // 264 px at 4 px a tick, ending at 100: tick 35 is at x 0, tick 95 at x 240.
    fireEvent.click(canvas, { clientX: 241 });
    expect(h.api.stepBackTo).toHaveBeenCalledWith(95);
    fireEvent.click(canvas, { clientX: 4 * 50 });
    fireEvent.click(canvas, { clientX: 4 * 65 + 1 });
    expect(h.api.stepBackTo).toHaveBeenCalledTimes(1);
    fireEvent.mouseMove(canvas, { clientX: 241 });
    expect(screen.getByTestId('timeline-hover')).toHaveTextContent('tick 95');
  });
});
