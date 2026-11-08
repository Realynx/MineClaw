import { describe, expect, it, vi } from 'vitest';
import { contextMenu, type Target } from '@/features/story/tools';
import { pickMenu } from '@/features/story/mode/menu';
import type { Story } from '@/features/story/mode/commands';

describe('stop rebuilding map action', () => {
  const targets: Target[] = [
    { kind: 'block', id: 4, name: 'Cobblestone', x: 223, y: 63, z: 374 },
    { kind: 'ghost', build: 'repair-zone', label: 'Farm repairs', repair: true, x: 223, y: 63, z: 374 },
  ];
  it.each(targets)('releases only the clicked $kind cell, with no selected pawn required', async (target) => {
    const item = contextMenu(target, false, false).find((i) => i.action === 'unprotect')!;
    expect(item.label).toBe('Stop rebuilding here');
    const write = vi.fn().mockResolvedValue({ cells: 1 }),
      say = vi.fn();
    await pickMenu({ menu: { target }, setMenu: vi.fn(), canWrite: true, write, say } as unknown as Story, item, false);
    expect(write).toHaveBeenCalledExactlyOnceWith('story.builds', { op: 'unprotect', cells: [[223, 63, 374]] });
    expect(say).toHaveBeenCalledWith('Stopped rebuilding at 223, 63, 374');
  });
  it('does not submit for read-only accounts or announce success on failure', async () => {
    const target = targets[0]!,
      item = contextMenu(target, false, false).find((i) => i.action === 'unprotect')!;
    const write = vi.fn().mockRejectedValue(new Error('unavailable')),
      say = vi.fn();
    const c = { menu: { target }, setMenu: vi.fn(), canWrite: false, write, say } as unknown as Story;
    await pickMenu(c, item, false);
    expect(write).not.toHaveBeenCalled();
    c.canWrite = true;
    await pickMenu(c, item, false);
    expect(say).toHaveBeenLastCalledWith('unavailable', 'warn');
  });
});
