import type { StoryArchitectEntry } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ArchitectPalette, MaterialRow, stockLine } from '@/features/story/StoryPanels';

afterEach(cleanup);

const entry = (extra: Partial<StoryArchitectEntry>): StoryArchitectEntry => ({
  id: 'fence',
  label: 'Oak Fence',
  tab: 'structure',
  tier: 'workbench',
  unlocked: true,
  reason: null,
  item: [85, 0],
  material: [85, 0],
  kind: 'fence',
  kinds: ['fence'],
  chain: ['craft 4 Oak Planks', 'craft 3 Oak Fence (1x, table)'],
  have: 0,
  craftable: 0,
  from: [],
  short: [],
  ...extra,
});

describe('build materials and the Architect palette', () => {
  it('says what the colony has, can make, or is short of', () => {
    expect(stockLine(entry({ have: 12 }))).toBe('have 12');
    expect(stockLine(entry({ craftable: 9, from: ['1 Wood'] }))).toBe('craftable 9 from 1 wood');
    expect(stockLine(entry({ short: ['2 Wood'] }))).toBe('short: 2 wood');
    expect(stockLine(entry({ tier: 'gathered' }))).toBe('none stored');
  });

  it('greys locked entries with the reason; an unlocked one arms its tool', () => {
    const onPick = vi.fn();
    render(
      <ArchitectPalette
        tab="structure"
        onPick={onPick}
        entries={[
          entry({}),
          entry({
            id: 'glass',
            label: 'Glass',
            tier: 'furnace',
            unlocked: false,
            reason: 'Needs a furnace — place one or designate an existing one',
          }),
          entry({ id: 'torch', label: 'Torch', tab: 'lighting' }),
        ]}
      />,
    );
    const glass = document.querySelector<HTMLButtonElement>('[data-entry="glass"]')!;
    expect(glass.disabled).toBe(true);
    expect(glass.textContent).toContain('Needs a furnace');
    expect(screen.getByLabelText('locked')).toBeTruthy();
    expect(document.querySelector('[data-entry="torch"]')).toBeNull(); // another tab
    fireEvent.click(document.querySelector('[data-entry="fence"]')!);
    expect(onPick).toHaveBeenCalledWith(expect.objectContaining({ id: 'fence' }));
    fireEvent.click(glass);
    expect(onPick).toHaveBeenCalledTimes(1);
  });

  it('a build material row shows have/need, crafting and short, with the chain on hover', () => {
    render(
      <MaterialRow
        m={{
          item: 'fence',
          label: 'oak fence',
          need: 6,
          have: 1,
          crafting: 3,
          short: 2,
          via: 'craft',
          chain: ['craft planks', 'craft fence'],
          raw: ['1 Wood'],
        }}
      />,
    );
    const row = document.querySelector<HTMLElement>('[data-material="fence"]')!;
    expect(row.textContent).toContain('1/6');
    expect(row.textContent).toContain('crafting 3');
    expect(row.textContent).toContain('short 2');
    expect(row.title).toContain('craft planks → craft fence');
    expect(row.title).toContain('Short of: 1 Wood');
  });
});
