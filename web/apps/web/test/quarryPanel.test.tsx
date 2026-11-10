import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { StoryZone as StoryZoneSchema, type StoryColonyState, type StoryZone } from '@mineclaw/contracts';
import { QuarryZonesPanel } from '@/features/story/zones/quarry';

/** A done quarry as the bot sends it (snake_case keys camelized), with what its last full look left standing. */
function doneQuarry(extra: Record<string, unknown> = {}): StoryZone {
  return StoryZoneSchema.parse({
    id: 'z1',
    kind: 'mine',
    area: [10, 63, 10, 15, 63, 15],
    mode: 'quarry',
    quarry: {
      top: 63,
      bottom: 3,
      depth: 61,
      layer: 61,
      y: 2,
      cellsLeft: 0,
      stairFixes: 0,
      sealed: 9,
      done: true,
      stair: [[10, 63, 10]],
      status: 'done: down to bedrock; left standing: 6 water, 1 cave, 1 lava, 1 pool (breached a cave at y 52)',
      left: { cave: 1, lava: 1, pool: 1, step: 61, water: 6 },
      leftovers: [
        { at: [15, 52, 12], kind: 'cave' },
        { at: [12, 46, 12], kind: 'water' },
        { at: [12, 45, 12], kind: 'pool' },
      ],
      ...extra,
    },
  });
}

const panel = (zones: StoryZone[]) =>
  render(<QuarryZonesPanel state={{ zones } as StoryColonyState} canWrite onMode={vi.fn()} onShow={vi.fn()} />);

it('lists what a quarry leaves standing by kind, apart from its stair, and the cells when opened', () => {
  panel([doneQuarry()]);
  const left = screen.getByTestId('quarry-left');
  expect(left.querySelector('summary')!.textContent).toBe(
    '9 cell(s) left standing: 6 beside water, 1 walling off a cave, 1 beside lava, 1 liquid closed in by rock',
  );
  expect(left.textContent).not.toContain('step');
  expect(screen.getByTestId('quarry-zone').textContent).not.toContain('sealed by lava or water');
  fireEvent.click(left.querySelector('summary')!);
  const cells = Array.from(left.querySelectorAll('li')).map((li) => li.textContent);
  expect(cells).toEqual([
    '15, 52, 12 · walling off a cave',
    '12, 46, 12 · beside water',
    '12, 45, 12 · liquid closed in by rock',
  ]);
});

it('shows nothing left when only the stair stands, and an older bot’s count without kinds', () => {
  const status = 'done: down to bedrock';
  panel([doneQuarry({ sealed: 0, left: { step: 61 }, leftovers: [], status })]);
  expect(screen.queryByTestId('quarry-left')).toBeNull();
  expect(screen.getByTestId('quarry-zone').textContent).not.toContain('left standing');
  panel([doneQuarry({ sealed: 3, left: undefined, leftovers: undefined, status })]);
  expect(screen.getAllByTestId('quarry-zone')[1]!.textContent).toContain('3 cell(s) left standing');
});
