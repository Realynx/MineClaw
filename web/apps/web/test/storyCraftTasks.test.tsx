import type { StoryCraftTask } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { benchTaskLine, CraftNeeds, sortTasks, taskLine } from '@/features/story/CraftNeeds';

afterEach(cleanup);

const task = (extra: Partial<StoryCraftTask>): StoryCraftTask => ({
  id: 'bill1',
  item: 'craft:stick',
  label: 'Make Stick (from any planks)',
  count: 16,
  reason: 'for the fence of build b4',
  for: 'b4',
  forItem: 'fence',
  priority: 'normal',
  bench: [3, 64, -3],
  status: 'queued',
  blocked: null,
  ...extra,
});

const pick = task({
  id: 'bill2',
  item: 'craft:wooden_pickaxe',
  label: 'Make Wooden Pickaxe',
  count: 7,
  reason: 'for mining: Cleo Z., Dov U. and 4 more (+1 spare)',
  for: 'tools',
  forItem: 'wooden_pickaxe',
  priority: 'immediate',
});

describe("craft tasks (the colony's own crafts, never bills)", () => {
  it('says what, how many and why; immediate ones first', () => {
    expect(taskLine(pick)).toBe('7 wooden pickaxe for mining: Cleo Z., Dov U. and 4 more (+1 spare)');
    expect(
      taskLine(task({ reason: "growing zone 'East' needs a hoe to till", label: 'Make Wooden Hoe', count: 1 })),
    ).toBe("1 wooden hoe — growing zone 'East' needs a hoe to till");
    expect(sortTasks([task({}), pick]).map((t) => t.id)).toEqual(['bill2', 'bill1']);
    expect(sortTasks([task({ count: 0 })])).toEqual([]);
  });

  it('gives a bench inspector one line for the tasks at that bench only', () => {
    const other = task({ id: 'bill3', bench: [9, 64, 9] });
    expect(benchTaskLine([task({}), pick, other], [3, 64, -3])).toBe(
      'Also crafting: 7 wooden pickaxe for mining: Cleo Z., Dov U. and 4 more (+1 spare); 16 stick for the fence of build b4',
    );
    expect(benchTaskLine([other], [3, 64, -3])).toBeNull();
    expect(benchTaskLine(null, [3, 64, -3])).toBeNull();
  });

  it('lists the crafting needs in the Work tab, immediate first, with a cancel for bench tasks', () => {
    const onCancel = vi.fn();
    const hand = task({
      id: 'hand:b2:craft:planks',
      bench: null,
      label: 'Make Oak Wood Planks',
      count: 4,
      reason: 'for build b2',
    });
    render(
      <CraftNeeds
        tasks={[task({ status: 'waiting', blocked: 'no planks' }), pick, hand]}
        canWrite
        onCancel={onCancel}
      />,
    );
    const rows = screen.getByTestId('craft-needs').querySelectorAll('li');
    expect([...rows].map((r) => r.getAttribute('data-task'))).toEqual(['bill2', 'bill1', 'hand:b2:craft:planks']);
    expect(rows[0]!.getAttribute('data-priority')).toBe('immediate');
    expect(rows[1]!.getAttribute('title')).toBe('no planks');
    expect(screen.queryByLabelText('Cancel oak wood planks')).toBeNull(); // a hand craft has no bench task to cancel
    fireEvent.click(screen.getByLabelText('Cancel wooden pickaxe'));
    expect(onCancel).toHaveBeenCalledWith(pick);
  });

  it('shows nothing when the colony crafts nothing for itself', () => {
    const { container } = render(<CraftNeeds tasks={[]} canWrite={false} />);
    expect(container.textContent).toBe('');
  });
});
