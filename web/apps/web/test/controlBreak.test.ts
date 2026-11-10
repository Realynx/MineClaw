import { describe, expect, it } from 'vitest';
import type { PawnControlBroke, PawnControlDig } from '@mineclaw/contracts';
import { BREAK_HOLD_MS, ControlBreak, placedCell } from '@/features/control/controlBreak';

const dig = (progress: number, at = { x: 3, y: 64, z: -2 }): PawnControlDig => ({
  ...at,
  face: 1,
  progress,
  id: 1,
  meta: 0,
});
const broke = (count: number, at = { x: 3, y: 64, z: -2 }): PawnControlBroke => ({ count, ...at, id: 1, meta: 0 });
/** A copy of the frame, since the tracker reuses its result. */
const frame = (b: ControlBreak, now: number) => {
  const f = b.frame(now);
  return { crack: f.crack && { ...f.crack }, hit: f.hit, bursts: [...f.bursts] };
};

describe("the crack of the pawn's own dig", () => {
  it('follows the dig and clears at once when the dig ends without a break', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.05), tick: 10 }, 0);
    expect(frame(b, 0).crack).toEqual({ x: 3, y: 64, z: -2, progress: 0.05 });
    b.state({ dim: 0, dig: dig(0.42), tick: 14 }, 200);
    expect(frame(b, 210).crack).toEqual({ x: 3, y: 64, z: -2, progress: 0.42 });
    b.state({ dim: 0, dig: null, tick: 15 }, 250); // looked away
    expect(frame(b, 251)).toEqual({ crack: null, hit: null, bursts: [] });
  });

  it('holds the full crack after a break until the view shows the cell changed, then bursts once', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.9), tick: 20 }, 0);
    b.state({ dim: 0, dig: dig(1), tick: 21 }, 50);
    b.broke(broke(1), 100);
    b.state({ dim: 0, dig: null, tick: 22 }, 100); // the dig is over on the server
    expect(frame(b, 110)).toMatchObject({ crack: { x: 3, y: 64, z: -2, progress: 1 }, bursts: [] });
    b.blocksChanged(0, [[9, 9, 9]], 300); // another cell
    b.blocksChanged(-1, [[3, 64, -2]], 300); // another dimension
    expect(frame(b, 310)).toMatchObject({ crack: { progress: 1 }, bursts: [] });
    b.blocksChanged(0, [[3, 64, -2]], 400);
    expect(frame(b, 410)).toEqual({ crack: null, hit: null, bursts: [broke(1)] });
    expect(frame(b, 420).bursts).toEqual([]);
  });

  it('gives up waiting for the change after 600 ms', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.95), tick: 20 }, 0);
    b.broke(broke(1), 100);
    b.state({ dim: 0, dig: null, tick: 21 }, 100);
    expect(frame(b, 100 + BREAK_HOLD_MS - 1)).toMatchObject({ crack: { progress: 1 }, bursts: [] });
    expect(frame(b, 100 + BREAK_HOLD_MS)).toMatchObject({ crack: null, bursts: [broke(1)] });
  });

  it('bursts at once when the change came before the break', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.95), tick: 20 }, 0);
    b.blocksChanged(0, [[3, 64, -2]], 40);
    b.broke(broke(1), 90);
    // The server may still report the dig for a frame: the block is gone, so it has no crack.
    expect(frame(b, 95)).toMatchObject({ crack: null, bursts: [broke(1)] });
    expect(frame(b, 100).crack).toBeNull();
    // A change older than the hold says nothing about this break.
    b.state({ dim: 0, dig: dig(0.5, { x: 4, y: 64, z: -2 }), tick: 30 }, 1000);
    b.blocksChanged(0, [[4, 64, -2]], 1000);
    b.broke(broke(2, { x: 4, y: 64, z: -2 }), 1000 + BREAK_HOLD_MS);
    expect(frame(b, 1000 + BREAK_HOLD_MS).bursts).toEqual([]);
  });

  it('draws no crack for a block that breaks at once, and still bursts it with the change', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, aim: { x: 5, y: 65, z: 1 }, tick: 3 }, 0);
    b.broke(broke(1, { x: 5, y: 65, z: 1 }), 50);
    expect(frame(b, 60)).toEqual({ crack: null, hit: null, bursts: [] });
    b.blocksChanged(0, [[5, 65, 1]], 200);
    expect(frame(b, 210).bursts).toEqual([broke(1, { x: 5, y: 65, z: 1 })]);
  });

  it('bursts a held block at once when the next one breaks', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.95), tick: 20 }, 0);
    b.broke(broke(1), 50);
    b.broke(broke(2, { x: 3, y: 63, z: -2 }), 60);
    expect(frame(b, 70).bursts).toEqual([broke(1)]);
    b.blocksChanged(0, [[3, 63, -2]], 80);
    expect(frame(b, 90).bursts).toEqual([broke(2, { x: 3, y: 63, z: -2 })]);
  });

  it('forgets everything when the pawn changes dimension or the tracker is reset', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.95), tick: 20 }, 0);
    b.broke(broke(1), 50);
    b.state({ dim: -1, tick: 21 }, 60);
    expect(frame(b, 70)).toEqual({ crack: null, hit: null, bursts: [] });
    b.state({ dim: -1, dig: dig(0.5), tick: 22 }, 80);
    b.reset();
    expect(frame(b, 90)).toEqual({ crack: null, hit: null, bursts: [] });
  });
});

describe('chips while digging', () => {
  const hitsAt = (b: ControlBreak, ticks: number[], progress = 0.3) =>
    ticks.filter((tick) => {
      b.state({ dim: 0, dig: dig(progress), tick }, tick * 50);
      return frame(b, tick * 50).hit !== null;
    });

  it('chips on the first tick of a dig and then every four ticks, keeping the rhythm when ticks are skipped', () => {
    expect(hitsAt(new ControlBreak(), [10, 11, 12, 13, 14, 15, 16, 17, 18])).toEqual([10, 14, 18]);
    expect(hitsAt(new ControlBreak(), [10, 13, 15, 17, 19, 23])).toEqual([10, 15, 19, 23]);
  });

  it('chips once per frame of the tick, not once per drawn frame', () => {
    const b = new ControlBreak();
    b.state({ dim: 0, dig: dig(0.3), tick: 10 }, 0);
    expect(frame(b, 0).hit).not.toBeNull();
    expect(frame(b, 16).hit).toBeNull();
    expect(frame(b, 33).hit).toBeNull();
  });

  it('stops chipping once the block is finished, and starts again on a new dig', () => {
    const b = new ControlBreak();
    expect(hitsAt(b, [10, 14])).toEqual([10, 14]);
    expect(hitsAt(b, [18, 22], 1)).toEqual([]);
    b.state({ dim: 0, dig: null, tick: 23 }, 23 * 50);
    expect(hitsAt(b, [24, 25])).toEqual([24]);
  });

  it('counts on the local clock for a server that does not number its ticks', () => {
    const b = new ControlBreak();
    const hits = [0, 50, 100, 150, 200, 250, 300, 350, 400].filter((t) => {
      b.state({ dim: 0, dig: dig(0.3) }, t);
      return frame(b, t).hit !== null;
    });
    expect(hits).toEqual([0, 200, 400]);
  });
});

describe('where a right click places the held block', () => {
  const stone = { x: 0, y: 63, z: 0, id: 1, face: 1, name: 'stone' };
  const body = { x: 5.5, y: 64, z: 5.5 };
  const planks = { id: 5, meta: 2 };

  it('fills the cell across the aimed face with the held block', () => {
    expect(placedCell(stone, planks, false, body)).toEqual({ x: 0, y: 64, z: 0, id: 5, meta: 2 });
    expect(placedCell({ ...stone, face: 4 }, planks, false, body)).toMatchObject({ x: -1, y: 63, z: 0 });
    expect(placedCell({ ...stone, face: 3 }, planks, false, body)).toMatchObject({ x: 0, y: 63, z: 1 });
    // Tall grass and snow layers are replaced where they stand.
    expect(placedCell({ ...stone, id: 31, name: 'tallgrass' }, planks, false, body)).toMatchObject({
      x: 0,
      y: 63,
      z: 0,
    });
  });

  it('places nothing with an item that is not a block, without a block face, or into the pawn', () => {
    expect(placedCell(stone, { id: 280, meta: 0 }, false, body)).toBeNull(); // a stick
    expect(placedCell(stone, undefined, false, body)).toBeNull();
    expect(placedCell(null, planks, false, body)).toBeNull();
    expect(placedCell({ ...stone, face: -1 }, planks, false, body)).toBeNull();
    expect(placedCell(stone, planks, false, { x: 0.5, y: 64, z: 0.5 })).toBeNull();
    // Pillaring: at the top of a jump the pawn's feet are clear of the cell under it.
    expect(placedCell(stone, planks, false, { x: 0.5, y: 65.01, z: 0.5 })).toMatchObject({ x: 0, y: 64, z: 0 });
  });

  it('uses doors and chests instead of placing against them, unless sneaking', () => {
    for (const name of ['chest', 'oak_door', 'wooden_door', 'spruce_fence_gate', 'crafting_table', 'lever']) {
      expect(placedCell({ ...stone, name }, planks, false, body)).toBeNull();
      expect(placedCell({ ...stone, name }, planks, true, body)).toMatchObject({ x: 0, y: 64, z: 0 });
    }
  });
});
