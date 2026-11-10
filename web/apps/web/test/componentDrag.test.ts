import { afterEach, describe, expect, it, vi } from 'vitest';
import type { V3 } from '@/features/studio/canvas';
import {
  ComponentDrag,
  DRAG_THRESHOLD_PX,
  type ComponentDragHandlers,
  type ComponentPress,
} from '@/features/studio/components/dragSession';

const press: ComponentPress = { pointerId: 7, x: 100, y: 100, id: 'gate', origin: [10, 0, 10], grab: [12, 0, 11] };

/** A drag bound to its own event target, with frames run by hand and a corner one cell per 10 px. */
function setup(over: Partial<ComponentDragHandlers> = {}) {
  const target = new EventTarget();
  const frames: (() => void)[] = [];
  const handlers = {
    start: vi.fn(() => true),
    // Past x 500 the pointer is off the plane.
    target: vi.fn((x: number, y: number): V3 | null =>
      x > 500 ? null : [10 + Math.round((x - 100) / 10), 0, 10 + Math.round((y - 100) / 10)],
    ),
    preview: vi.fn(),
    commit: vi.fn(),
    cancel: vi.fn(),
    ...over,
  };
  const drag = new ComponentDrag(handlers, {
    schedule: (run) => {
      frames.push(run);
    },
  });
  drag.bind(target as unknown as Window);
  const pointer = (type: string, x: number, y: number, pointerId = press.pointerId) =>
    target.dispatchEvent(new PointerEvent(type, { clientX: x, clientY: y, pointerId, cancelable: true }));
  const frame = () => {
    for (const run of frames.splice(0)) run();
  };
  return { drag, handlers, target, frames, pointer, frame };
}

afterEach(() => vi.useRealTimers());

describe('component drag session', () => {
  it('stays a click under the threshold and starts a drag past it', () => {
    const h = setup();
    h.drag.press(press);
    expect(h.drag.active).toBe(true);
    h.pointer('pointermove', 100 + DRAG_THRESHOLD_PX - 1, 100);
    expect(h.handlers.start).not.toHaveBeenCalled();
    expect(h.drag.moved).toBe(false);
    h.pointer('pointerup', 103, 100);
    expect(h.drag.active).toBe(false);
    expect(h.frames).toHaveLength(0);
    for (const fn of [h.handlers.start, h.handlers.preview, h.handlers.commit, h.handlers.cancel])
      expect(fn).not.toHaveBeenCalled();

    h.drag.press(press);
    h.pointer('pointermove', 100 + DRAG_THRESHOLD_PX, 100);
    expect(h.handlers.start).toHaveBeenCalledExactlyOnceWith(press);
    expect(h.drag.moved).toBe(true);
  });

  it('previews at most once per frame, at the latest pointer position, and only when the corner changes', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 120, 100);
    h.pointer('pointermove', 130, 100);
    h.pointer('pointermove', 140, 110);
    expect(h.frames).toHaveLength(1);
    expect(h.handlers.preview).not.toHaveBeenCalled();
    h.frame();
    expect(h.handlers.target).toHaveBeenCalledExactlyOnceWith(140, 110, press);
    expect(h.handlers.preview).toHaveBeenCalledExactlyOnceWith([14, 0, 11], press);
    // The same corner again is not drawn again; a new one is, in the next frame.
    h.pointer('pointermove', 141, 111);
    h.frame();
    expect(h.handlers.preview).toHaveBeenCalledOnce();
    h.pointer('pointermove', 160, 110);
    h.frame();
    expect(h.handlers.preview).toHaveBeenLastCalledWith([16, 0, 11], press);
    expect(h.handlers.preview).toHaveBeenCalledTimes(2);
  });

  it('commits once on release, where the pointer is even before its frame came', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    h.frame();
    h.pointer('pointermove', 150, 120);
    h.pointer('pointerup', 150, 120);
    expect(h.handlers.commit).toHaveBeenCalledExactlyOnceWith([15, 0, 12], press);
    expect(h.handlers.cancel).not.toHaveBeenCalled();
    expect(h.drag.active).toBe(false);
    // The frame still queued draws nothing once the press has ended.
    h.frame();
    expect(h.handlers.preview).toHaveBeenCalledTimes(2);
    h.pointer('pointerup', 150, 120);
    expect(h.handlers.commit).toHaveBeenCalledOnce();
  });

  it('puts the piece back instead of committing when it is released where it began', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    h.frame();
    h.pointer('pointermove', 101, 101);
    h.pointer('pointerup', 101, 101);
    expect(h.handlers.commit).not.toHaveBeenCalled();
    expect(h.handlers.cancel).toHaveBeenCalledExactlyOnceWith(press);
  });

  it('keeps the corner shown while the pointer is off the plane', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    h.frame();
    h.pointer('pointermove', 900, 900);
    h.frame();
    h.pointer('pointerup', 900, 900);
    expect(h.handlers.preview).toHaveBeenCalledOnce();
    expect(h.handlers.commit).toHaveBeenCalledExactlyOnceWith([13, 0, 10], press);
  });

  it('drops the press when the drag may not start', () => {
    const h = setup({ start: vi.fn(() => false) });
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    expect(h.drag.active).toBe(false);
    expect(h.frames).toHaveLength(0);
    h.pointer('pointerup', 130, 100);
    expect(h.handlers.commit).not.toHaveBeenCalled();
    expect(h.handlers.cancel).not.toHaveBeenCalled();
  });

  it('ignores other pointers', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100, 8);
    h.pointer('pointercancel', 130, 100, 8);
    h.pointer('pointerup', 130, 100, 8);
    expect(h.handlers.start).not.toHaveBeenCalled();
    expect(h.drag.active).toBe(true);
  });

  const cancels: [string, (h: ReturnType<typeof setup>) => void][] = [
    [
      'Escape',
      (h) => {
        const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
        h.target.dispatchEvent(escape);
        expect(escape.defaultPrevented).toBe(true);
      },
    ],
    [
      'pointercancel',
      (h) => {
        h.pointer('pointercancel', 130, 100);
      },
    ],
    [
      'lostpointercapture',
      (h) => {
        h.pointer('lostpointercapture', 130, 100);
      },
    ],
    [
      'window blur',
      (h) => {
        h.target.dispatchEvent(new Event('blur'));
      },
    ],
    [
      'unbind',
      (h) => {
        h.drag.unbind();
      },
    ],
  ];
  it.each(cancels)('%s puts a dragged piece back, and the release then commits nothing', (_name, cancel) => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    h.pointer('pointermove', 140, 100);
    cancel(h);
    expect(h.handlers.cancel).toHaveBeenCalledExactlyOnceWith(press);
    expect(h.drag.active).toBe(false);
    h.frame();
    h.pointer('pointerup', 140, 100);
    expect(h.handlers.preview).not.toHaveBeenCalled();
    expect(h.handlers.commit).not.toHaveBeenCalled();
    expect(h.handlers.cancel).toHaveBeenCalledOnce();
  });

  it('leaves Escape alone before the press has become a drag', () => {
    const h = setup();
    h.drag.press(press);
    const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    h.target.dispatchEvent(escape);
    expect(escape.defaultPrevented).toBe(false);
    expect(h.drag.active).toBe(true);
  });

  it('a new press cancels a drag still held', () => {
    const h = setup();
    h.drag.press(press);
    h.pointer('pointermove', 130, 100);
    h.drag.press({ ...press, id: 'other' });
    expect(h.handlers.cancel).toHaveBeenCalledExactlyOnceWith(press);
    expect(h.drag.moved).toBe(false);
  });

  it('waits for the next animation frame by default', () => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame', 'performance'] });
    const target = new EventTarget();
    const preview = vi.fn();
    const drag = new ComponentDrag({
      start: () => true,
      target: (x) => [Math.round(x / 10), 0, 0],
      preview,
      commit: vi.fn(),
      cancel: vi.fn(),
    });
    drag.bind(target as unknown as Window);
    drag.press(press);
    for (const x of [120, 130, 140])
      target.dispatchEvent(new PointerEvent('pointermove', { clientX: x, clientY: 100, pointerId: press.pointerId }));
    expect(preview).not.toHaveBeenCalled();
    vi.advanceTimersByTime(20);
    expect(preview).toHaveBeenCalledExactlyOnceWith([14, 0, 0], press);
    drag.unbind();
  });
});
