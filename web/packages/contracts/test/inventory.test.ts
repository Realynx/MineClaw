import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { mockInventory } from '../src/testing.js';

describe('BodyInventory', () => {
  it('every mock body parses, both versions, armour as [head, chest, legs, feet]', () => {
    const names = [
      'maid_chan',
      'maid_chan_67',
      'maid_chan_68',
      'maid_chan_70',
      'maid_chan_109',
      'maid_chan_102',
      'maid_chan_114',
    ];
    for (const [i, n] of names.entries()) {
      const inv = C.BodyInventory.parse(mockInventory(n, i, 7));
      expect(inv.name).toBe(n);
      expect(inv.slots.map((s) => s.slot)).toEqual([...inv.slots.map((s) => s.slot)].sort((a, b) => a - b));
      expect(inv.slots.every((s) => s.slot >= 0 && s.slot <= 35)).toBe(true);
      inv.armor.forEach((a, k) => (a ? expect(a.slot).toBe(39 - k) : undefined));
      if (inv.version === 'b1.8.1') expect(inv.offhand).toBeNull();
    }
    const scout = mockInventory('maid_chan_67');
    expect(scout.version).toBe('1.12.2');
    expect(scout.offhand?.slot).toBe(40);
    expect(scout.slots.some((s) => s.enchanted)).toBe(true);
  });

  it('the wire form converts (snake_case → camelCase) and rejects a bad armour row', () => {
    const wire = C.toSnake<Record<string, unknown>>(mockInventory('maid_chan'));
    expect(wire.held_slot).toBe(0);
    expect(C.BodyInventory.safeParse(C.toCamel(wire)).success).toBe(true);
    expect(C.BodyInventory.safeParse({ ...mockInventory('maid_chan'), armor: [null, null, null] }).success).toBe(false);
  });

  it('the route reaches the bot through the viewer pass-through', () => {
    expect(C.bridgePath('bots.inventory', { bot: 'maid_chan_67' })).toBe(
      '/v1/sup/viewer/bodies/maid_chan_67/inventory',
    );
    expect(C.SUPERVISOR_PASSTHROUGH_ALLOW.get).toContain('viewer/bodies/');
    expect(C.ROUTES['bots.inventory'].access).toBe('viewer');
  });
});
