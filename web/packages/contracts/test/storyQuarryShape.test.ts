import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { StorySim, type StoryWorld } from '../src/testing.js';

// A quarry keeps its stair ring (docs/web/story-mode.md "Quarries"): the mock director refuses what the bot refuses
// (StoryColony.Quarry.cs QuarryShapeRefusal), and the preview's rule is the same function.
const flat = (): StoryWorld => ({
  blockAt: (_x, y) => (y < 63 ? [1, 0] : y === 63 ? [2, 0] : [0, 0]),
  ground: () => 63,
  entities: () => [],
  setBlock: () => undefined,
  ai: () => [],
});

describe('quarry shape changes', () => {
  it('a paint or erase that would leave a quarry without a ring, or merge it with an ores-only zone, is refused', () => {
    const sim = new StorySim({ world: flat(), defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 1 });
    const zones = () => sim.state(id).zones.filter((z) => z.kind === 'mine' || z.kind === 'chop');
    const q = sim.zones(id, { op: 'add', kind: 'mine', area: [10, 63, 10, 13, 63, 13] }) as C.StoryAck;
    // An L: its ring walk ends at the inner corner.
    expect(() => sim.zones(id, { op: 'add', kind: 'mine', area: [10, 63, 14, 11, 63, 15] })).toThrow(
      `Quarry ${q.id} would lose its stair`,
    );
    // A notch, by erasing or by another work zone painted into its side.
    expect(() => sim.zones(id, { op: 'remove', kind: 'mine', area: [11, 63, 10, 12, 63, 10] })).toThrow(
      /only whole rows or columns can come off its sides/,
    );
    expect(() => sim.zones(id, { op: 'add', kind: 'chop', area: [11, 63, 10, 12, 63, 10] })).toThrow(/lose its stair/);
    expect(zones()).toHaveLength(1);
    expect(zones()[0]!.cells ?? C.zoneCellSet(zones()[0]!).size).toBe(16);
    // A whole row off a side keeps a rectangle and its ring.
    sim.zones(id, { op: 'remove', kind: 'mine', area: [10, 63, 10, 13, 63, 10] });
    expect(C.quarryRing(C.zoneCellSet(zones()[0]!))).not.toBeNull();
    // A quarry and an ores-only zone never merge.
    const o = sim.zones(id, { op: 'add', kind: 'mine', area: [10, 63, 16, 13, 63, 17], mode: 'ores' }) as C.StoryAck;
    expect(() => sim.zones(id, { op: 'add', kind: 'mine', area: [10, 63, 14, 13, 63, 15] })).toThrow(
      `Quarry ${q.id} and ores-only zone ${o.id} would merge`,
    );
    expect(zones()).toHaveLength(2);
  });

  it('the rule itself: an unchanged or ring-keeping quarry is fine, one that had no ring loses nothing', () => {
    const quarry = { id: 'q', kind: 'mine', mode: 'quarry', area: [0, 63, 0, 3, 63, 3] } as C.ZoneLike & {
      mode: string;
    };
    const grown = C.paintZones(
      [quarry],
      { kind: 'mine', rect: [0, 4, 3, 5], y: [63, 63] },
      () => quarry,
      () => 'n',
    );
    expect(C.quarryPaintRefusal([quarry], grown)).toBeNull(); // still a rectangle
    const ell = C.paintZones(
      [quarry],
      { kind: 'mine', rect: [0, 4, 1, 5], y: [63, 63] },
      () => quarry,
      () => 'n',
    );
    expect(C.quarryPaintRefusal([quarry], ell)).toMatch(
      /^Quarry q would lose its stair.*paint the new part as a quarry of its own/,
    );
    const ores = { ...quarry, mode: 'ores' };
    expect(
      C.quarryPaintRefusal(
        [ores],
        C.paintZones(
          [ores],
          { kind: 'mine', rect: [0, 4, 1, 5], y: [63, 63] },
          () => ores,
          () => 'n',
        ),
      ),
    ).toBeNull();
  });
});
