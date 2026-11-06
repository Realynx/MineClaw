import { describe, expect, it, vi } from 'vitest';
import type { Story } from '@/features/story/mode/commands';
import { releaseTool } from '@/features/story/mode/release';
import type { DragResult } from '@/features/story/StoryMap';

describe('map cancellation', () => {
  it('cancels the exact previewed marks at all heights, including underground cleanup', async () => {
    const run = vi.fn().mockResolvedValue(undefined);
    const c = {
      tool: { kind: 'designate', id: 'cancel' },
      lv: 64,
      cutY: 65,
      canWrite: true,
      state: {
        designations: [
          { id: 'deep', kind: 'deconstruct', target: [5, 30, 5], label: 'removing a temporary block' },
          { id: 'high', kind: 'mine', target: [5, 110, 5] },
          { id: 'surface', kind: 'mine', target: [6, 64, 6] },
          { id: 'outside', kind: 'mine', target: [20, 30, 20] },
        ],
      },
      run,
      setTool: vi.fn(),
      say: vi.fn(),
    } as unknown as Story;
    const drag = { rect: [4, 4, 7, 7], moved: true, count: 3 } as DragResult;
    await releaseTool(c, drag, false);
    expect(run).toHaveBeenCalledWith(
      { route: 'story.designations', body: { op: 'cancel', ids: ['deep', 'high', 'surface'] } },
      null,
      'cancelled',
      expect.any(Function),
    );
  });
});
