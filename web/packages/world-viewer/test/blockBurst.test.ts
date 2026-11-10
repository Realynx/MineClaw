import { describe, expect, it } from 'vitest';
import { BURSTS, Particles } from '../src/render/entities/effects';
import { EntityLayer } from '../src/render/entityLayer';

describe('block particles', () => {
  it("block_break emits its configured count in the block's colour", () => {
    const p = new Particles();
    p.burst('block_break', 0, 1.5, 64.5, 2.5, 1000, [0.5, 0.25, 0]);
    expect(p.list).toHaveLength(BURSTS.block_break.count);
    expect(BURSTS.block_break).toMatchObject({ count: 24, speed: 2.2, spread: 0.9, life: 700, gravity: 9 });
    const j = BURSTS.block_break.jitter ?? 0;
    for (const q of p.list) {
      expect(q.r).toBeGreaterThanOrEqual(0.5 * (1 - j));
      expect(q.r).toBeLessThanOrEqual(0.5 * (1 + j));
      expect(q.g / q.r).toBeCloseTo(0.5);
      expect(q.b).toBe(0);
      expect(Math.abs(q.x - 1.5)).toBeLessThanOrEqual(0.45);
    }
  });

  it('block_hit throws one chip; a scale thins a burst but never below one', () => {
    const p = new Particles();
    p.burst('block_hit', 0, 0, 0, 0, 0, [1, 1, 1]);
    expect(p.list).toHaveLength(1);
    p.burst('block_break', 0, 0, 0, 0, 0, [1, 1, 1], 0.5);
    expect(p.list).toHaveLength(1 + 12);
    p.burst('block_hit', 0, 0, 0, 0, 0, [1, 1, 1], 0.5);
    expect(p.list).toHaveLength(14);
  });

  it('respects the 1024 particle cap', () => {
    const p = new Particles();
    for (let i = 0; i < 50; i++) p.burst('block_break', 0, 0, 64, 0, 0, [1, 1, 1]);
    expect(p.list).toHaveLength(1024);
  });

  it("keeps the style's own colour without one, as before", () => {
    const a = new Particles(),
      b = new Particles();
    a.burst('crit', 0, 0, 0, 0, 0);
    b.burst('crit', 0, 0, 0, 0, 0, undefined, 1);
    expect(a.list).toEqual(b.list);
    expect(a.list[0].b).toBeGreaterThan(0.5);
  });

  it('EntityLayer.blockBurst turns 0xRRGGBB into the chip colour, halved on request', () => {
    const layer = new EntityLayer();
    layer.blockBurst(0, 0.5, 64.5, 0.5, 0xff0000, 1000);
    expect(layer.effects.particles).toBe(24);
    layer.blockBurst(0, 0.5, 64.5, 0.5, 0x00ff00, 1000, 'block_break', 0.5);
    expect(layer.effects.particles).toBe(36);
    layer.blockBurst(0, 0.5, 64.5, 0.5, 0x0000ff, 1000, 'block_hit');
    expect(layer.effects.particles).toBe(37);
    const list = (layer as unknown as { particles: Particles }).particles.list;
    expect(list[0]).toMatchObject({ g: 0, b: 0 });
    expect(list[0].r).toBeGreaterThan(0.8);
    expect(list[30]).toMatchObject({ r: 0, b: 0 });
    expect(list[36]).toMatchObject({ r: 0, g: 0 });
    layer.dispose();
  });
});
