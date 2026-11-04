import type { ContainerSummary, ContainerView, StoryStorage } from '@mineclaw/contracts';
import { mockContainer } from '@mineclaw/contracts/testing';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ContainerGrid } from '@/features/live/inventory/ContainerGrid';
import {
  agoShort,
  containerLayout,
  freshnessText,
  isContainerBlock,
  containerTotals,
} from '@/features/live/inventory/containerLayout';
import { chestBadge } from '@/features/story/chestBadges';

afterEach(cleanup);

const NOW = Date.UTC(2026, 10, 3, 12, 0, 0);

const view = (over: Partial<ContainerView> = {}): ContainerView => ({
  ...mockContainer(0, 2, 74, -4, 0, NOW),
  ...over,
});

describe('container layouts', () => {
  it('a chest is three rows of nine, a double chest six, slots in place', () => {
    const single = containerLayout(view({ kind: 'chest', size: 27, slots: view().slots.filter((s) => s.slot < 27) }));
    expect(single.shape).toBe('rows');
    if (single.shape !== 'rows') return;
    expect(single.rows.map((r) => r.length)).toEqual([9, 9, 9]);
    expect(single.rows[0]![3]!.item?.name).toBe('planks');
    expect(single.rows[1]![0]!.item?.name).toBe('stick');
    const dbl = containerLayout(view());
    if (dbl.shape !== 'rows') throw new Error('rows');
    expect(dbl.rows).toHaveLength(6);
    expect(dbl.rows[5]![8]!.index).toBe(53);
    expect(dbl.rows[5]![8]!.item?.name).toBe('diamond');
    expect(dbl.rows[1]![4]!.item?.durability).toEqual({ max: 131, left: 100 });
  });

  it('a slot past the size is ignored; dispensers are 3×3 and a hopper one row of five', () => {
    const l = containerLayout(view({ kind: 'chest', size: 27 }));
    if (l.shape !== 'rows') throw new Error('rows');
    expect(l.rows.flat().some((c) => c.item?.name === 'diamond')).toBe(false); // slot 53
    const disp = containerLayout({ kind: 'dispenser', size: 9, slots: [] });
    if (disp.shape !== 'rows') throw new Error('rows');
    expect(disp.rows.map((r) => r.length)).toEqual([3, 3, 3]);
    const hop = containerLayout({ kind: 'hopper', size: 5, slots: [] });
    if (hop.shape !== 'rows') throw new Error('rows');
    expect(hop.rows.map((r) => r.length)).toEqual([5]);
  });

  it('a furnace is input, fuel and output; a brewing stand three bottles, the ingredient and fuel', () => {
    const f = containerLayout(mockContainer(0, 0, 74, -6));
    expect(f.shape).toBe('furnace');
    if (f.shape !== 'furnace') return;
    expect([f.input.item?.name, f.fuel.item?.name, f.output.item?.name]).toEqual(['iron_ore', 'coal', 'iron_ingot']);
    const b = containerLayout({
      kind: 'brewing_stand',
      size: 5,
      slots: [{ slot: 3, id: 372, meta: 0, count: 2, name: 'nether_wart', display: 'Nether Wart', enchanted: false }],
    });
    if (b.shape !== 'brewing') throw new Error('brewing');
    expect(b.ingredient.item?.name).toBe('nether_wart');
    expect(b.bottles.map((c) => c.index)).toEqual([0, 1, 2]);
    expect(b.fuel.index).toBe(4);
  });

  it('knows the container blocks of both versions', () => {
    for (const id of [54, 146, 61, 62, 23, 158, 154, 130, 117, 219, 234]) expect(isContainerBlock(id)).toBe(true);
    for (const id of [58, 1, 26, 145, 116, 218, 235]) expect(isContainerBlock(id)).toBe(false);
    expect(isContainerBlock(null)).toBe(false);
  });
});

describe('freshness', () => {
  it('says who looked and when, or that nobody did', () => {
    expect(freshnessText(view(), NOW)).toBe('Seen 3 min ago by maid_chan_102');
    expect(freshnessText(mockContainer(0, 17, 70, -32, 0, NOW), NOW)).toBe('Open right now by maid_chan_70');
    expect(freshnessText(mockContainer(0, 20, 70, -32, 0, NOW), NOW)).toBe('Not opened yet by any bot');
    expect(freshnessText(mockContainer(0, 6, 74, -4, 0, NOW), NOW)).toBe('Story storage ledger · 40 s ago');
    expect(freshnessText(view({ partial: true, seenAt: NOW - 2 * 3600_000 }), NOW)).toBe(
      'Seen 2 h ago by maid_chan_102 · one half only',
    );
    expect(agoShort(NOW - 2000, NOW)).toBe('just now');
    expect(agoShort(NOW - 3 * 86400_000, NOW)).toBe('3 d ago');
    expect(containerTotals(view())).toEqual({
      used: 12,
      items: 64 + 64 + 37 + 48 + 32 + 12 + 1 + 64 + 64 + 21 + 9 + 2,
    });
  });
});

describe('the container grid', () => {
  it('renders every slot of a large chest with the items in place', () => {
    render(<ContainerGrid view={view()} />);
    const grid = screen.getByTestId('container-grid');
    expect(grid.dataset.kind).toBe('double_chest');
    expect(grid.querySelectorAll('[data-slot]')).toHaveLength(54);
    expect(screen.getByTestId('ctr-slot-0').dataset.item).toBe('log');
    expect(screen.getByTestId('ctr-slot-53').dataset.count).toBe('2');
    expect(screen.getByTestId('ctr-slot-52').dataset.item).toBeUndefined();
  });

  it('renders a furnace as its three slots', () => {
    render(<ContainerGrid view={mockContainer(0, 0, 74, -6)} />);
    expect(screen.getByRole('group', { name: 'Furnace' })).toBeTruthy();
    expect(screen.getByTestId('container-grid').querySelectorAll('[data-slot]')).toHaveLength(3);
    expect(screen.getByLabelText(/^Fuel: Coal ×3/)).toBeTruthy();
  });
});

describe('Story chest badges', () => {
  const storage = (over: Partial<StoryStorage> = {}): StoryStorage => ({
    chest: [2, 74, -4],
    doubleWith: [3, 74, -4],
    label: null,
    priority: 'preferred',
    filter: { allow: ['wood'], deny: [] },
    contents: [],
    freeSlots: 27,
    knownAt: new Date(NOW - 60_000).toISOString(),
    ...over,
  });
  const summary = (over: Partial<ContainerSummary> = {}): ContainerSummary => ({
    pos: [2, 74, -4],
    dim: 0,
    kind: 'double_chest',
    size: 54,
    used: 45,
    total: 900,
    top: [],
    seenAt: NOW,
    seenBy: 'maid_chan_102',
    source: 'memory',
    cells: [
      [2, 74, -4],
      [3, 74, -4],
    ],
    ...over,
  });

  it('shows the category, and the fill from the fresher of the hive and the ledger', () => {
    const fresh = chestBadge(storage(), summary(), null)!;
    expect(fresh.text).toBe('Wood');
    expect(fresh.fill).toBeCloseTo(45 / 54);
    const stale = chestBadge(storage(), summary({ seenAt: NOW - 3600_000 }), null)!;
    expect(stale.fill).toBeCloseTo(27 / 54); // the ledger: 27 of 54 free
    expect(chestBadge(storage({ label: 'Logs' }), undefined, null)!.text).toBe('Logs');
  });

  it('a chest that is not storage shows its fill alone, or nothing when nobody looked', () => {
    expect(chestBadge(undefined, summary(), null)).toEqual({ text: null, fill: 45 / 54 });
    expect(chestBadge(undefined, summary({ source: 'unknown' }), null)).toBeNull();
    expect(chestBadge(undefined, undefined, null)).toBeNull();
  });
});
