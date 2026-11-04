/**
 * The browser's ports of the bot's mine terrain, templates and presets (src/MineClaw.Bot/Blueprints/Mine/*.cs): the golden
 * hashes are the same constants tests/MineClaw.Bot.Tests/MineDesignerTests.cs asserts, so both sides generate the same world.
 */
import { describe, expect, it } from 'vitest';
import { BEDROCK, GRASS, MineTerrain, seedBits, TERRAIN_HEIGHT } from '@/features/studio/mine/mineTerrain';
import { cellsHash, fromStep, spiral, strip, TORCH } from '@/features/studio/mine/mineTemplates';
import { MINE_PRESETS, presetCells, presetsFor } from '@/features/studio/mine/presets';
import { bandWeight, oreOf, oreTable, ORE_SOURCES } from '@/features/studio/mine/oreTable';

const GOLDEN_PLOT_1122 = 1021586989,
  GOLDEN_PLOT_BETA = 1802239221,
  GOLDEN_SPIRAL = 2749961926,
  GOLDEN_STRIP = 2072293685;

describe('the ore table', () => {
  it('has every band per version with its sources', () => {
    const r = oreTable('1.12.2');
    expect(r.ores.map((o) => o.id)).toEqual(['coal', 'iron', 'gold', 'redstone', 'diamond', 'lapis', 'emerald']);
    const d = r.ores.find((o) => o.id === 'diamond')!;
    expect([d.min, d.max, d.veins, d.size, d.best]).toEqual([0, 15, 1, 8, 11]);
    const lapis = r.ores.find((o) => o.id === 'lapis')!;
    expect(bandWeight(lapis, 16)).toBe(1);
    expect(bandWeight(lapis, 8)).toBe(0.5);
    expect(bandWeight(lapis, 40)).toBe(0);
    expect(oreTable('b1.8.1').pockets.map((p) => p.id)).toEqual(['dirt', 'gravel']);
    expect(oreOf('1.12.2', 56, 0)?.id).toBe('diamond');
    expect(ORE_SOURCES.length).toBeGreaterThan(0);
  });
});

describe('the mine terrain (port of MineTerrain.cs)', () => {
  it('reads seeds like the bot', () => {
    expect(seedBits('5')).toEqual([5, 0]);
    expect(seedBits('-1')).toEqual([0xffffffff, 0xffffffff]);
    expect(seedBits('81985529216486895')).toEqual([0x89abcdef, 0x01234567]);
    expect(seedBits('hello')).toEqual([99162322, 0]);
  });

  it('matches the bot bit for bit (golden hashes)', () => {
    expect(new MineTerrain('12345', '1.12.2').plotHash()).toBe(GOLDEN_PLOT_1122);
    expect(new MineTerrain('hello', 'b1.8.1').plotHash()).toBe(GOLDEN_PLOT_BETA);
  });

  it('rolls, with a bedrock floor and grass on top', () => {
    const t = MineTerrain.for('12345', '1.12.2');
    expect(MineTerrain.for(' 12345', '1.12.2')).toBe(t);
    const { min, max } = t.surfaceRange();
    expect(min).toBeGreaterThanOrEqual(58);
    expect(max).toBeLessThanOrEqual(81);
    expect(max - min).toBeGreaterThanOrEqual(4);
    for (let x = 0; x < 64; x += 7)
      for (let z = 0; z < 64; z += 5) {
        expect(t.block(x, 0, z)[0]).toBe(BEDROCK);
        expect(t.block(x, 5, z)[0]).not.toBe(BEDROCK);
        const top = t.surface(x, z);
        expect(t.block(x, top, z)[0]).toBe(GRASS);
        expect(t.block(x, top + 1, z)[0]).toBe(0);
      }
  });

  it.each(['1.12.2', 'b1.8.1'])('puts %s ores in their bands in the expected amounts', (version) => {
    const t = new MineTerrain('777', version);
    const table = oreTable(version);
    const counts = new Map(table.ores.map((o) => [o.id, 0]));
    for (let y = 0; y < TERRAIN_HEIGHT; y++)
      for (let z = 0; z < 64; z++)
        for (let x = 0; x < 64; x++) {
          const [id, meta] = t.block(x, y, z);
          const ore = oreOf(version, id, meta);
          if (!ore) continue;
          counts.set(ore.id, counts.get(ore.id)! + 1);
          expect(y).toBeGreaterThanOrEqual(ore.min - ore.size);
          expect(y).toBeLessThanOrEqual(ore.max + ore.size);
        }
    for (const o of table.ores.filter((x) => x.veins > 0)) {
      const expected = o.veins * 16 * o.size * 0.5;
      expect(counts.get(o.id)!).toBeGreaterThanOrEqual(Math.trunc(expected * 0.3));
      expect(counts.get(o.id)!).toBeLessThanOrEqual(Math.trunc(expected * 2.2));
    }
    expect(counts.get('gold')!).toBeGreaterThan(counts.get('diamond')!);
  });
});

describe('the mine templates (port of MineTemplates.cs)', () => {
  it('walks a spiral shaft step by step', () => {
    for (const [size, core, cw] of [
      [4, 2, true],
      [3, 1, false],
      [5, 1, true],
      [6, 2, false],
      [4, 1, true],
    ] as const) {
      const m = spiral({ x: 10, z: 10, top: 70, bottom: 11, size, core, cw, torchEvery: 6, landings: [40, 11] });
      const dug = new Set(m.cells.filter((c) => c[3] === 0).map((c) => `${c[0]},${c[1]},${c[2]}`));
      const has = (x: number, y: number, z: number) => dug.has(`${x},${y},${z}`);
      expect(m.path.length).toBe(60);
      expect(m.entry).toEqual(m.path[0]);
      m.path.forEach((p, k) => {
        expect(has(p[0], p[1], p[2])).toBe(true);
        expect(has(p[0], p[1] + 1, p[2])).toBe(true);
        if (![40, 41, 11, 12].includes(p[1] - 1)) expect(has(p[0], p[1] - 1, p[2])).toBe(false);
        if (k === 0) return;
        const q = m.path[k - 1]!;
        expect(Math.abs(p[0] - q[0]) + Math.abs(p[2] - q[2])).toBe(1);
        expect(Math.abs(p[1] - q[1])).toBeLessThanOrEqual(1);
        expect(has(p[0], q[1] + 1, p[2])).toBe(true);
      });
      expect(m.cells.some((c) => c[3] === TORCH)).toBe(true);
    }
    expect(cellsHash(spiral({ x: 30, z: 30, top: 72, bottom: 11, size: 4, core: 2, cw: true, torchEvery: 6, landings: [11, 40] }).cells)).toBe(
      GOLDEN_SPIRAL,
    );
  });

  it('spaces a strip mine’s branches', () => {
    const m = strip({ x: 10, y: 11, z: 20, axis: 'x', length: 30, width: 2, height: 3, spacing: 3, branchLength: 10, sides: 'both', torchEvery: 8 });
    const cols = [...new Set(m.cells.filter((c) => c[3] === 0 && c[2] < 20).map((c) => c[0] - 10))].sort((a, b) => a - b);
    expect(cols).toEqual([3, 7, 11, 15, 19, 23, 27]);
    expect(m.entry).toEqual([10, 11, 20]);
    for (const t of m.cells.filter((c) => c[3] === TORCH)) expect(cols).not.toContain(t[0] - 10);
    expect(
      cellsHash(
        strip({ x: 34, y: 11, z: 31, axis: 'x', length: 30, width: 2, height: 3, spacing: 3, branchLength: 14, sides: 'both', torchEvery: 8 }).cells,
      ),
    ).toBe(GOLDEN_STRIP);
    const s = fromStep({ template: 'strip', x: 1, y: 12, z: 2, axis: 'z', length: 5, branch_length: 2, spacing: 1 }, null);
    expect(s.cells.some((c) => c[0] === 1 && c[2] === 6 && c[1] === 12)).toBe(true);
  });

  it('loads the version-preferred presets onto a terrain', () => {
    expect(MINE_PRESETS.some((p) => p.id === '1.12.2-diamond-y11' && p.y === 11)).toBe(true);
    expect(presetsFor('b1.8.1').every((p) => p.version === 'b1.8.1')).toBe(true);
    const t = new MineTerrain('42', '1.12.2');
    const c = presetCells(MINE_PRESETS.find((p) => p.id === '1.12.2-diamond-y11')!, t);
    expect(c.entry[1]).toBe(t.surfaceOver(30, 30, 33, 33));
    expect(c.cells.some((x) => x[3] === 0 && x[1] === 11 && x[0] === 60)).toBe(true);
    expect(c.cells.every((x) => x[1] >= 11)).toBe(true);
  });
});
