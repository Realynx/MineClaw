// Behaviour pin for the complexity refactor of render/entities/items.ts (docs/dev/complexity.md, WP12): every item look by
// id × damage in both versions (with and without a pack), every packed 1.12.2 item model, and held names. The digests were
// taken from the code before it was reshaped.

import { describe, expect, it } from 'vitest';
import { isHandheld, itemLookById, itemLookByName, itemName, look1122, parseHeld } from '../src/render/entities/items';
import { Digest } from './digest';
import { noPacks, pack1122, packBeta } from './pack';

const DAMAGES = [...Array.from({ length: 16 }, (_, i) => i), 16, 17, 31, 255, -1];

describe.skipIf(noPacks)('item looks golden (behaviour pin)', () => {
  it('itemLookById over every id × damage, both versions', () => {
    const d = new Digest();
    for (const [version, pack] of [['1.12.2', pack1122()], ['b1.8.1', packBeta()], ['1.12.2', null], ['b1.8.1', pack1122()]] as const)
      for (let id = -1; id < 2300; id++) {
        if (id > 460 && id < 2250) continue;
        for (const dmg of DAMAGES) d.add(`${id}:${dmg} ${JSON.stringify(itemLookById(version, pack, id, dmg))}`);
      }
    expect(d.hex()).toBe('9c74a54ccab0d31da01ee312889491d6');
  });

  it('look1122 over every packed item model', () => {
    const d = new Digest();
    const pack = pack1122();
    for (const k of Object.keys(pack.itemModels).sort()) {
      const n = k.replace(/^item\//, '');
      d.add(`${n} ${JSON.stringify(look1122(pack, n))}`);
    }
    d.add(JSON.stringify(look1122(pack, 'no_such_item')));
    expect(d.hex()).toBe('7f764488edb7339714654e16a7caeb2f');
  });

  it('itemLookByName and the name helpers', () => {
    const d = new Digest();
    const names: string[] = [];
    for (let id = 1; id < 2270; id++) {
      const n = itemName(id);
      if (n) names.push(n, n.replace(/_/g, ' '), `${n} x3`, `${n.toUpperCase()} X12`);
    }
    names.push('wood_pickaxe', 'gold pickaxe', 'seeds', 'raw fish', 'steak', 'yellow_flower', 'red_flower', 'fence', 'log2', 'potion', 'splash_potion', 'lingering_potion', 'shield', 'chest', 'banner', '', '  ', 'nope x', 'x5');
    for (const n of names) {
      for (const [version, pack] of [['1.12.2', pack1122()], ['b1.8.1', packBeta()], ['1.12.2', null]] as const) d.add(`${n} ${version} ${JSON.stringify(itemLookByName(version, pack, n))}`);
      d.add(`${JSON.stringify(parseHeld(n))} ${isHandheld(n)}`);
    }
    expect(d.hex()).toBe('5660fdbda618b861f1522424872315ac');
  });
});
