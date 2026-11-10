import { cellKey, rowsOf, zoneCellSet, type StoryZone } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { quarryModeNow } from '@/features/story/zones/quarry';
import { outlineOf } from '@/features/story/zones/zoneDraw';
import {
  ERASE_ANY,
  previewLabel,
  zoneBody,
  zoneInto,
  zonePreview,
  type ZoneTool,
} from '@/features/story/zones/zonePaint';

// Zones as cell sets, painted RimWorld-style (docs/web/story-mode.md "Zones"): the map's live preview of a paint or an erase.
const zone = (
  id: string,
  kind: string,
  rect: [number, number, number, number],
  extra: Partial<StoryZone> = {},
): StoryZone => ({
  id,
  kind,
  area: [rect[0], 63, rect[1], rect[2], 63, rect[3]],
  ...extra,
});
const add = (id: string): ZoneTool => ({ kind: 'zone', id, op: 'add' });

describe('zone paint preview', () => {
  it('a growing zone over one of the same plant merges into it; over two it merges both into the oldest', () => {
    const zs = [
      zone('z1', 'farm', [0, 0, 4, 4], { plant: 'wheat' }),
      zone('z2', 'farm', [8, 0, 10, 4], { plant: 'wheat' }),
    ];
    const out = zonePreview(zs, add('farm'), [3, 2, 9, 2], 63, 'wheat', undefined)!;
    expect(out.result.merged).toEqual([{ into: 'z1', from: ['z2'] }]);
    expect(out.zones).toHaveLength(1);
    expect(zoneCellSet(out.zones[0]!).size).toBe(25 + 15 + 3);
    expect(previewLabel(add('farm'), out, 7)).toContain('merged 2 zones');
  });

  it('another plant or another work kind overwrites; overlays neither overwrite nor are overwritten', () => {
    const zs = [zone('z1', 'farm', [0, 0, 9, 0], { plant: 'wheat' }), zone('h', 'home', [0, 0, 9, 9])];
    const carrots = zonePreview(zs, add('farm'), [4, 0, 5, 0], 63, 'carrots', undefined)!;
    expect(carrots.result.created).toHaveLength(1);
    expect(carrots.result.split).toEqual([{ from: 'z1', into: [expect.any(String)] }]);
    expect(carrots.removedCells).toEqual(new Set([cellKey(4, 0), cellKey(5, 0)]));
    expect(zoneCellSet(carrots.zones.find((z) => z.id === 'h')!).size).toBe(100); // the home area is a layer apart
    const quarry = zonePreview(zs, add('mine'), [0, 0, 9, 0], 63, null, undefined)!;
    expect(quarry.result.deleted).toEqual(['z1']);
    const home = zonePreview(zs, add('forbidden'), [0, 0, 9, 9], 63, null, undefined)!;
    expect(home.removedCells.size).toBe(0);
    expect(home.result.created).toHaveLength(1);
  });

  it('erase: any kind by default, of one kind when picked; Shift extends the zone the drag starts in', () => {
    const zs = [zone('z1', 'chop', [0, 0, 4, 0]), zone('h', 'home', [0, 0, 4, 0])];
    const any = zonePreview(zs, { kind: 'zone', id: ERASE_ANY, op: 'remove' }, [2, 0, 2, 0], 63, null, undefined)!;
    expect(any.result.split.map((s) => s.from).sort()).toEqual(['h', 'z1']);
    expect(zoneBody({ kind: 'zone', id: ERASE_ANY, op: 'remove' }, [2, 0, 2, 0], 63, null, undefined)).toEqual({
      op: 'remove',
      area: [2, 63, 0, 2, 63, 0],
    });
    const chopOnly = zonePreview(zs, { kind: 'zone', id: 'chop', op: 'remove' }, [0, 0, 4, 0], 63, null, undefined)!;
    expect(chopOnly.result.deleted).toEqual(['z1']);
    const wheat = [zone('w', 'farm', [0, 0, 2, 2], { plant: 'wheat' })];
    expect(zoneInto(add('farm'), wheat, [1, 63, 1], true)).toBe('w');
    expect(zoneInto(add('farm'), wheat, [1, 63, 1], false)).toBeUndefined();
    expect(zoneBody(add('farm'), [3, 0, 4, 2], 63, 'carrots', 'w')).toEqual({
      op: 'add',
      area: [3, 63, 0, 4, 63, 2],
      into: 'w',
    });
    const ext = zonePreview(wheat, add('farm'), [3, 0, 4, 2], 63, 'carrots', 'w')!;
    expect(ext.result.grown).toEqual(['w']);
    expect(ext.zones[0]!.plant).toBe('wheat');
  });

  it('a paint or erase that would cost a quarry its stair is no preview: the label says why', () => {
    const zs = [
      zone('q', 'mine', [10, 10, 13, 13], { mode: 'quarry' }),
      zone('o', 'mine', [10, 18, 13, 19], { mode: 'ores' }),
    ];
    const ell = zonePreview(zs, add('mine'), [10, 14, 11, 15], 63, null, undefined);
    expect(ell).toBeNull();
    expect(previewLabel(add('mine'), ell, 4)).toMatch(
      /^Quarry q would lose its stair.*Nothing changed: paint the new part/,
    );
    const erase: ZoneTool = { kind: 'zone', id: ERASE_ANY, op: 'remove' };
    const notch = zonePreview(zs, erase, [11, 10, 12, 10], 63, null, undefined);
    expect(previewLabel(erase, notch, 2)).toContain('only whole rows or columns can come off its sides');
    const merge = zonePreview(zs, add('mine'), [10, 14, 13, 17], 63, null, undefined);
    expect(previewLabel(add('mine'), merge, 16)).toContain('Quarry q and ores-only zone o would merge');
    // A whole row off a side is fine, and the next refusal-free preview shows its own label again.
    const row = zonePreview(zs, erase, [10, 10, 13, 10], 63, null, undefined)!;
    expect(previewLabel(erase, row, 4)).toMatch(/^erase 4 cells/);
    expect(previewLabel(add('farm'), null, 1)).toBe('too large');
  });

  it('a zone outline goes around the whole merged shape', () => {
    const cells = new Set([cellKey(0, 0), cellKey(1, 0), cellKey(0, 1)]); // an L
    const seg = outlineOf(cells);
    let len = 0;
    for (let i = 0; i < seg.length; i += 4)
      len += Math.abs(seg[i + 2]! - seg[i]!) + Math.abs(seg[i + 3]! - seg[i + 1]!);
    expect(len).toBe(8);
    expect(rowsOf(cells)).toEqual([
      [0, 0, 1],
      [1, 0, 0],
    ]);
  });

  it('a Quarry paint is a quarry by default, Ores only when picked', () => {
    expect(quarryModeNow()).toBe('quarry');
    expect(zoneBody(add('mine'), [0, 0, 2, 2], 63, null, undefined, quarryModeNow())).toEqual({
      op: 'add',
      kind: 'mine',
      area: [0, 63, 0, 2, 63, 2],
      mode: 'quarry',
    });
    expect(zoneBody(add('mine'), [0, 0, 2, 2], 63, null, undefined, 'ores')).toMatchObject({ mode: 'ores' });
  });
});
