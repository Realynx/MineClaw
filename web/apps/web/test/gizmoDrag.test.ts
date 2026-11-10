/**
 * The page's gizmo drag over the kinds of component it has (mine modules, then redstone components): the first kind that
 * takes the press owns the drag to its end, across the page's renders, and the others never see its steps.
 */
import { describe, expect, it, vi } from 'vitest';
import type { V3 } from '@/features/studio/canvas';
import { gizmoDrags, type GizmoDrag } from '@/features/studio/components/gizmoDrag';

const kind = (takes: boolean) => ({
  dragStart: vi.fn(() => takes),
  dragPreview: vi.fn((_delta: V3) => true),
  dragCommit: vi.fn((_delta: V3) => undefined),
  dragCancel: vi.fn(() => undefined),
});

describe('the gizmo drag over several kinds', () => {
  it('routes the drag to the first kind that takes the press, even when the page rebuilds its actions', () => {
    const mine = kind(false);
    const redstone = kind(true);
    const owner = { current: null as number | null };
    const drags: GizmoDrag = gizmoDrags([mine, redstone], owner);
    expect(drags.dragStart()).toBe(true);
    // A render in the middle of the drag makes the actions again: the owner is kept in the page's holder.
    const again = gizmoDrags([mine, redstone], owner);
    expect(again.dragPreview([2, 0, 0])).toBe(true);
    again.dragCommit([2, 0, 0]);
    expect(redstone.dragPreview).toHaveBeenCalledExactlyOnceWith([2, 0, 0]);
    expect(redstone.dragCommit).toHaveBeenCalledExactlyOnceWith([2, 0, 0]);
    for (const fn of [mine.dragPreview, mine.dragCommit, mine.dragCancel]) expect(fn).not.toHaveBeenCalled();
    // The drag is over: a late step reaches no one.
    expect(again.dragPreview([3, 0, 0])).toBe(false);
    again.dragCancel();
    expect(redstone.dragCancel).not.toHaveBeenCalled();
  });

  it('asks the kinds in order and stops at the first, and a press no kind takes previews nothing', () => {
    const mine = kind(true);
    const redstone = kind(true);
    const drags = gizmoDrags([mine, redstone], { current: null });
    expect(drags.dragStart()).toBe(true);
    expect(redstone.dragStart).not.toHaveBeenCalled();
    drags.dragCancel();
    expect(mine.dragCancel).toHaveBeenCalledOnce();

    const none = gizmoDrags([kind(false), kind(false)], { current: null });
    expect(none.dragStart()).toBe(false);
    expect(none.dragPreview([1, 0, 0])).toBe(false);
  });
});
