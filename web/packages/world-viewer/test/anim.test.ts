import { describe, expect, it } from 'vitest';
import { blendFrames, frameAt, SpriteAnimator, type AnimSpec, type CellWriter } from '../src/atlas/anim';
import { betaEffects } from '../src/atlas/betaFx';
import { noPacks, pack1122 } from './pack';

describe('mcmeta animation timing', () => {
  const frames = [{ index: 0, time: 2 }, { index: 3, time: 3 }];
  it('shows frame k for its time in ticks, then wraps', () => {
    expect([0, 1, 2, 3, 4, 5, 6].map((t) => frameAt(frames, t).frame)).toEqual([0, 0, 3, 3, 3, 0, 0]);
    expect(frameAt(frames, 7).tickInFrame).toBe(0);
    expect(frameAt(frames, -1).frame).toBe(3); // negative ticks wrap too
  });

  it('interpolation weights: 1 − tickInFrame / time toward the next frame', () => {
    expect(frameAt(frames, 0, true)).toMatchObject({ frame: 0, next: 3, weight: 1 });
    expect(frameAt(frames, 1, true).weight).toBeCloseTo(0.5);
    expect(frameAt(frames, 3, true)).toMatchObject({ frame: 3, next: 0 });
    expect(frameAt(frames, 4, true).weight).toBeCloseTo(1 / 3);
    expect(frameAt(frames, 1, false).weight).toBe(1);
  });

  it.skipIf(noPacks)('the pack carries the vanilla lava/water timings', () => {
    const anims = pack1122().anims;
    const lava = anims.find((a) => a.name === 'blocks/lava_still')!;
    expect(lava.frames.length).toBeGreaterThan(lava.count); // a custom frame list (ping-pong)
    const water = anims.find((a) => a.name === 'blocks/water_still')!;
    expect(water.frames.every((f) => f.time === 2)).toBe(true);
    expect(frameAt(water.frames, 2).frame).toBe(water.frames[1].index);
  });

  it('blends rgb by weight and keeps the current alpha', () => {
    const a = new Uint8Array(16 * 16 * 4).fill(200), b = new Uint8Array(16 * 16 * 4).fill(100);
    b[3] = 10;
    const out = new Uint8Array(1024);
    blendFrames(a, 0, b, 0, 64, 0.25, out);
    expect([out[0], out[3]]).toEqual([125, 200]);
  });
});

describe('atlas animation pass', () => {
  const specs: AnimSpec[] = [
    { cell: [0, 0], frames: [{ index: 0, time: 1 }, { index: 1, time: 1 }], interpolate: false, row: 0 },
    { cell: [1, 0], frames: [{ index: 0, time: 4 }, { index: 1, time: 4 }], interpolate: true, row: 1 },
    { cell: [2, 0], frames: [{ index: 0, time: 10 }], interpolate: false, row: 2 },
  ];
  const sheet = { data: new Uint8Array(32 * 48 * 4), width: 32 };
  const writer = () => {
    const log: string[] = [];
    const w: CellWriter = { fromSheet: (c, r, sx, sy, size) => log.push(`s${c},${r}@${sx},${sy}${size !== 16 ? `x${size}` : ''}`), fromPixels: (c, r) => log.push(`p${c},${r}`) };
    return { log, w };
  };

  it('writes only cells whose frame changed; interpolated cells every tick', () => {
    const a = new SpriteAnimator(specs, sheet, 32);
    const { log, w } = writer();
    expect(a.update(0, w)).toBe(3);
    expect(log).toEqual(['s0,0@0,0', 's1,0@0,16', 's2,0@0,32']);
    log.length = 0;
    a.update(1, w);
    expect(log).toEqual(['s0,0@16,0', 'p1,0']); // the single-frame cell is never rewritten
    log.length = 0;
    a.update(1, w);
    expect(log).toEqual([]); // same tick: nothing
  });

  it('honours a per-tick budget (round-robin) and the visible-only set', () => {
    const a = new SpriteAnimator(specs, sheet, 32);
    const { log, w } = writer();
    expect(a.update(0, w, { maxCells: 1 })).toBe(1);
    expect(a.update(0, w, { maxCells: 1 })).toBe(1);
    expect(a.update(0, w, { maxCells: 1 })).toBe(1);
    expect(new Set(log).size).toBe(3);
    const b = new SpriteAnimator(specs, sheet, 32);
    const v = writer();
    b.update(0, v.w, { visible: new Set([1]) });
    expect(v.log).toEqual(['s1,0@0,16']);
  });

  it('b1.8.1 TextureFX: water alpha in the client range, flames cut out, portal cycles 32 frames', () => {
    const fx = betaEffects(60);
    const water = fx.find((f) => f.tile === 205)!;
    for (let i = 3; i < 1024; i += 4) {
      expect(water.pixels[i]).toBeGreaterThanOrEqual(146);
      expect(water.pixels[i]).toBeLessThanOrEqual(196);
    }
    const fire = fx.find((f) => f.tile === 31)!;
    const alphas = new Set<number>();
    for (let i = 3; i < 1024; i += 4) alphas.add(fire.pixels[i]);
    expect([...alphas].every((a) => a === 0 || a === 255)).toBe(true);
    expect(alphas.has(255)).toBe(true);
    const portal = fx.find((f) => f.tile === 14)!;
    const before = portal.pixels.slice();
    for (let i = 0; i < 32; i++) portal.tick();
    expect(portal.pixels).toEqual(before);
    const lava = fx.find((f) => f.tile === 237)!;
    const l0 = lava.pixels.slice();
    lava.tick();
    expect(lava.pixels).not.toEqual(l0);
  });
});
