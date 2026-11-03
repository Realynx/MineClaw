import type { BodyInventory, InventorySlot } from '@mineclaw/contracts';
import { mockInventory } from '@mineclaw/contracts/testing';
import { describe, expect, it } from 'vitest';
import { changedSlots, durabilityFraction, glints, inventoryGrid, tooltipLines } from '@/features/live/inventory/grid';

const item = (slot: number, id: number, count = 1, extra: Partial<InventorySlot> = {}): InventorySlot => ({
  slot,
  id,
  meta: 0,
  count,
  name: `item_${id}`,
  display: `Item ${id}`,
  durability: null,
  enchanted: false,
  ...extra,
});

const inv = (over: Partial<BodyInventory> = {}): BodyInventory => ({
  name: 'maid_chan',
  version: 'b1.8.1',
  heldSlot: 0,
  slots: [],
  armor: [null, null, null, null],
  offhand: null,
  at: 1,
  online: true,
  ...over,
});

describe('inventoryGrid', () => {
  it('maps InventoryPlayer indices to the screen: hotbar 0–8, main rows 9–17 / 18–26 / 27–35', () => {
    const g = inventoryGrid(
      inv({ slots: [item(0, 278), item(8, 4, 64), item(9, 3, 12), item(17, 5), item(18, 6), item(35, 264, 2)] }),
    );
    expect(g.hotbar.map((c) => c.index)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8]);
    expect(g.hotbar[0]!.item?.id).toBe(278);
    expect(g.hotbar[8]!.item?.count).toBe(64);
    expect(g.main).toHaveLength(3);
    expect(g.main.map((r) => r.map((c) => c.index))).toEqual([
      [9, 10, 11, 12, 13, 14, 15, 16, 17],
      [18, 19, 20, 21, 22, 23, 24, 25, 26],
      [27, 28, 29, 30, 31, 32, 33, 34, 35],
    ]);
    expect(g.main[0]![0]!.item?.id).toBe(3); // slot 9: top-left of the main inventory
    expect(g.main[0]![8]!.item?.id).toBe(5); // slot 17: top-right
    expect(g.main[1]![0]!.item?.id).toBe(6); // slot 18: second row
    expect(g.main[2]![8]!.item?.id).toBe(264); // slot 35: bottom-right
    expect(g.main.flat().filter((c) => c.item)).toHaveLength(4);
  });

  it('keeps the held hotbar slot (clamped to 0–8)', () => {
    expect(inventoryGrid(inv({ heldSlot: 5 })).held).toBe(5);
    expect(inventoryGrid(inv({ heldSlot: 12 })).held).toBe(8);
    expect(inventoryGrid(inv({ heldSlot: -1 })).held).toBe(0);
  });

  it('armour is head, chest, legs, feet (indices 39..36); the off hand is 1.12.2 only', () => {
    const helmet = item(39, 306),
      boots = item(36, 301);
    const g = inventoryGrid(inv({ armor: [helmet, null, null, boots] }));
    expect(g.armor.map((c) => c.index)).toEqual([39, 38, 37, 36]);
    expect(g.armor[0].item?.id).toBe(306);
    expect(g.armor[1].item).toBeNull();
    expect(g.armor[3].item?.id).toBe(301);
    expect(g.offhand).toBeNull();
    const modern = inventoryGrid(inv({ version: '1.12.2', offhand: item(40, 442) }));
    expect(modern.offhand?.index).toBe(40);
    expect(modern.offhand?.item?.id).toBe(442);
    expect(inventoryGrid(inv({ version: '1.12.2' })).offhand).toEqual({ index: 40, item: null });
  });

  it('ignores slots outside 0–35 in the main list', () => {
    const g = inventoryGrid(inv({ slots: [item(36, 301), item(-1, 1), item(4, 50, 3)] }));
    expect([...g.hotbar, ...g.main.flat()].filter((c) => c.item).map((c) => c.index)).toEqual([4]);
  });

  it('lays out the shared mock bodies of both versions', () => {
    const miner = inventoryGrid(mockInventory('maid_chan'));
    expect(miner.held).toBe(0);
    expect(miner.hotbar[0]!.item?.name).toBe('diamond_pickaxe');
    expect(miner.armor[0].item?.name).toBe('iron_helmet');
    const scout = inventoryGrid(mockInventory('maid_chan_67'));
    expect(scout.offhand?.item?.name).toBe('shield');
    expect(scout.hotbar[scout.held]!.item?.name).toBe('diamond_pickaxe');
  });
});

describe('changes, glint, durability, tooltip', () => {
  it('reports the slots whose item, count or damage changed, armour and off hand included', () => {
    const a = inv({ slots: [item(0, 4, 10), item(1, 3, 5)], armor: [item(39, 306), null, null, null] });
    const b = inv({
      at: 2,
      slots: [item(0, 4, 11), item(2, 50, 1)],
      armor: [item(39, 306, 1, { meta: 3 }), null, null, null],
    });
    expect([...changedSlots(a, b)].sort((x, y) => x - y)).toEqual([0, 1, 2, 39]);
    expect(changedSlots(null, b).size).toBe(0);
    expect(changedSlots(inv({ name: 'other' }), b).size).toBe(0);
    expect(changedSlots(b, b).size).toBe(0);
  });

  it('glints enchanted 1.12.2 items and the always-glinting ones, never on b1.8.1', () => {
    expect(glints('1.12.2', item(0, 276, 1, { enchanted: true }))).toBe(true);
    expect(glints('1.12.2', item(0, 403))).toBe(true);
    expect(glints('1.12.2', item(0, 322, 1, { meta: 1 }))).toBe(true);
    expect(glints('1.12.2', item(0, 322))).toBe(false);
    expect(glints('b1.8.1', item(0, 276, 1, { enchanted: true }))).toBe(false);
  });

  it('durability fraction and tooltip lines', () => {
    const pick = item(0, 278, 1, {
      name: 'diamond_pickaxe',
      display: 'Diamond Pickaxe',
      meta: 340,
      durability: { max: 1561, left: 1221 },
    });
    expect(durabilityFraction(pick)).toBeCloseTo(1221 / 1561);
    expect(durabilityFraction(item(1, 4))).toBeNull();
    expect(tooltipLines(pick)).toEqual(['Diamond Pickaxe', 'diamond_pickaxe · 278:340', 'Durability 1221 / 1561']);
    expect(tooltipLines(item(2, 4, 64))[0]).toBe('Item 4 ×64');
  });
});
