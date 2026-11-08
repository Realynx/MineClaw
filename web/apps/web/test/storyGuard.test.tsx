import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import type { StoryColonyState, StoryZone } from '@mineclaw/contracts';
import { ZoneToolbar } from '@/features/story/zones/ZoneToolbar';
import { OtherZonesPanel } from '@/features/story/zones/OtherZones';
import { zoneBody, type ZoneTool } from '@/features/story/zones/zonePaint';
import { zoneLabel } from '@/features/story/zones/zoneDraw';

it('offers Guard as a paintable overlay with an explicit animal warning and sends the standard zone request', () => {
  const pick = vi.fn<(tool: ZoneTool) => void>();
  render(
    <ZoneToolbar
      tool={{ kind: 'select' }}
      zones={[]}
      onTool={pick}
      btn={(tool, _icon, label, hint) => (
        <button key={label} title={hint} onClick={() => pick(tool)}>
          {label}
        </button>
      )}
    />,
  );
  const guard = screen.getByRole('button', { name: 'Guard' });
  expect(guard.title).toContain('including animals');
  expect(guard.title).toContain('overlay layer');
  fireEvent.click(guard);
  const tool = pick.mock.calls[0]![0];
  expect(zoneBody(tool, [4, 5, 7, 9], 63, null, undefined)).toEqual({
    op: 'add',
    kind: 'guard',
    area: [4, 63, 5, 7, 63, 9],
  });
});

it('labels an unnamed guard zone, explains its scope and lets the owner remove it', () => {
  const zone: StoryZone = { id: 'g1', kind: 'guard', area: [4, 63, 5, 7, 63, 9] };
  const remove = vi.fn(),
    show = vi.fn();
  render(<OtherZonesPanel state={{ zones: [zone] } as StoryColonyState} canWrite onShow={show} onRemove={remove} />);
  expect(zoneLabel(zone, 1, [])).toBe('Guard');
  expect(screen.getByText(/Players are excluded/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Guard g1' }));
  expect(show).toHaveBeenCalledWith(zone);
  fireEvent.click(screen.getByRole('button', { name: 'Remove Guard g1' }));
  expect(remove).toHaveBeenCalledWith(zone);
});
