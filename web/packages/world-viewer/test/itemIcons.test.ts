import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { Pack1122 } from '../src/assets/bundle';
import { itemLookById, itemLookByName, itemName, POTION_TINT } from '../src/render/entities/items';

const PACK = JSON.parse(readFileSync(join(__dirname, '..', 'generated', '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
const cell = (name: string) => {
  const t = PACK.items.tiles[name];
  return t[1] * PACK.items.columns + t[0];
};

// Items whose 1.12.2 model is not a plain item/<name> sprite: they must still resolve to a real icon (the inventory panel
// otherwise shows a placeholder square).
describe('1.12.2 item icons without a plain model', () => {
  it('potions: the bottle model, the bottle over its tinted liquid', () => {
    for (const [id, name, bottle] of [
      [373, 'potion', 'items/potion_bottle_drinkable'],
      [438, 'splash_potion', 'items/potion_bottle_splash'],
      [441, 'lingering_potion', 'items/potion_bottle_lingering'],
    ] as const) {
      expect(itemName(id)).toBe(name);
      const l = itemLookById('1.12.2', PACK, id, 0);
      expect(l).toMatchObject({ kind: 'sprite', atlas: 'items', cell: cell(bottle), under: { atlas: 'items', cell: cell('items/potion_overlay'), tint: POTION_TINT } });
      expect(itemLookByName('1.12.2', PACK, name.replace(/_/g, ' '))).toEqual(l);
    }
  });

  it('the shield: its front face from the entity sheet, the GUI shield outline as the 3D stand-in', () => {
    expect(itemName(442)).toBe('shield');
    const l = itemLookById('1.12.2', PACK, 442, 0);
    expect(l).toMatchObject({ kind: 'sprite', atlas: 'items', cell: cell('items/empty_armor_slot_shield') });
    const r = PACK.entity.rects['entity/shield_base_nopattern'];
    const k = r[2] / 64;
    expect(l?.kind === 'sprite' && l.entityRect).toEqual([r[0] + k, r[1] + k, 12 * k, 22 * k]);
    // Inside the sheet.
    const [x, y, w, h] = (l as { entityRect: number[] }).entityRect;
    expect(x + w).toBeLessThanOrEqual(PACK.entity.width);
    expect(y + h).toBeLessThanOrEqual(PACK.entity.height);
  });

  it('plain items keep a single layer', () => {
    const l = itemLookById('1.12.2', PACK, 374, 0); // glass bottle
    expect(l).toMatchObject({ kind: 'sprite', cell: cell('items/potion_bottle_empty') });
    expect(l && 'under' in l).toBe(false);
  });
});
