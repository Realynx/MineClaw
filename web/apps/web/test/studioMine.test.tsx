/**
 * The Design Studio's mine mode: the canvas over generated terrain (absolute y, ground = solid terrain, bedrock never dug,
 * fill back), the studio switching modes from the session, the ore guide and dig estimate, the mine panel, and a mine design
 * placed in Story Mode (absolute y, `mine` items, refused below y 1).
 */
import type { DesignerSession } from '@mineclaw/contracts';
import { expandBuildItem, MOCK_DESIGNER_PALETTE, StoryBuildItem, StoryGhostBlock } from '@mineclaw/contracts';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Canvas, EARTH, inside, keyOf, plotMode, setPlotMode } from '@/features/studio/canvas';
import { MinePanel } from '@/features/studio/MinePanel';
import { levelOres, mineStats, xrayCells } from '@/features/studio/mine/mineStats';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { spiral } from '@/features/studio/mine/mineTemplates';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { aimHit, toggleUnder, underCut, underRange } from '@/features/studio/tools';
import { designCells, mineCheck, mineItems, placeDesign } from '@/features/story/tools';

afterEach(() => setPlotMode({ kind: 'build' }));

const terrain = MineTerrain.for('12345', '1.12.2');

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 9,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'new design',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 128, depth: 0 },
  revisions: [],
  kind: 'mine',
  seed: '12345',
  ...over,
});

const api: StudioApi = {
  message: () => Promise.reject(new Error('n/a')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('n/a')),
};

describe('a mine canvas', () => {
  it('is generated terrain at absolute y: solid cells dig, air does nothing, bedrock never digs, ground fills back', () => {
    setPlotMode({ kind: 'mine', terrain });
    expect(inside(0, 0, 0)).toBe(true);
    expect(inside(0, 127, 0)).toBe(true);
    expect(inside(0, 128, 0)).toBe(false);
    expect(inside(0, -1, 0)).toBe(false);
    expect(posKey(5, 120, 5)).toBe(true);
    const c = new Canvas();
    const top = terrain.surface(10, 10);
    expect(c.get(10, top, 10)).toEqual([EARTH, 0]);
    expect(c.get(10, top + 1, 10)).toEqual([0, 0]);
    const dig = c.apply([
      [10, 20, 10, 0, 0],
      [10, top + 3, 10, 0, 0], // air over the surface: nothing to dig
      [10, 0, 10, 0, 0], // the bedrock floor: never
    ]);
    expect(dig.forward).toEqual([[10, 20, 10, 0, 0]]);
    expect(c.dug.has(keyOf(10, 20, 10))).toBe(true);
    // A torch in the tunnel, then removed: dug again; on bedrock a removed block is ground again.
    c.apply([[10, 20, 10, 50, 5]]);
    c.apply([[10, 20, 10, 0, 0]]);
    expect(c.dug.has(keyOf(10, 20, 10))).toBe(true);
    c.apply([[10, 0, 10, 4, 0]]);
    c.apply([[10, 0, 10, 0, 0]]);
    expect(c.get(10, 0, 10)).toEqual([EARTH, 0]);
    expect(c.dug.has(keyOf(10, 0, 10))).toBe(false);
    // Fill back.
    c.apply([[10, 20, 10, EARTH, 0]]);
    expect(c.earth(10, 20, 10)).toBe(true);
    // Loading keeps every dug cell (a design keeps its digs whatever the terrain).
    const d = new Canvas();
    d.load([], [[3, top + 5, 3], [3, 30, 3]]);
    expect(d.dug.size).toBe(2);
  });

  it('switches with the session, digs with the primary tool and cuts the view down to y 1', () => {
    const s = new Studio(session(), api);
    expect(s.mine).toBe(true);
    expect(plotMode()).toMatchObject({ kind: 'mine', minY: 0, maxY: 127, baseY: 0 });
    expect(underRange()).toEqual({ min: 1, max: 127 });
    expect(toggleUnder(null, 70)).toBe(70);
    expect(underCut(11)).toEqual({ y: 12 });
    expect(underCut(0)).toEqual({ y: 2 });
    // The aim needs no underground mode: the ground is always editable, y absolute.
    const top = terrain.surface(4, 4);
    const h = aimHit({ kind: 'block', x: 4, y: top, z: 4, face: 1 } as never, s.canvas, false);
    expect(h?.hit).toEqual([4, top, 4]);
    s.edit([[4, top, 4, 0, 0]], 'remove', 'dug');
    expect(s.snapshot().dug).toBe(1);
    s.dispose();
    const b = new Studio(session({ kind: 'build', seed: null, plot: { size: 64, height: 48, depth: 16 } }), api);
    expect(b.mine).toBe(false);
    expect(plotMode().kind).toBe('build');
    expect(underRange()).toEqual({ min: -16, max: 0 });
    b.dispose();
  });
});

const posKey = (x: number, y: number, z: number) => inside(x, y, z);

describe('the ore guide and the dig estimate', () => {
  it('reads a level: bands, best levels and the slice’s generated ores', () => {
    const at11 = levelOres(terrain, 11);
    const diamond = at11.find((r) => r.ore.id === 'diamond')!;
    expect(diamond).toMatchObject({ inBand: true, weight: 1, best: true });
    expect(at11.find((r) => r.ore.id === 'emerald')).toBeUndefined();
    const at40 = levelOres(terrain, 40);
    expect(at40.find((r) => r.ore.id === 'diamond')!.inBand).toBe(false);
    expect(at40.find((r) => r.ore.id === 'lapis')!.inBand).toBe(false);
    const sum = Array.from({ length: 70 }, (_, y) => levelOres(terrain, y).reduce((n, r) => n + r.count, 0)).reduce((a, b) => a + b, 0);
    expect(sum).toBeGreaterThan(500);
  });

  it('counts the blocks to mine by material and the ores mined and exposed', () => {
    const shaft = spiral({ x: 30, z: 30, top: terrain.surfaceOver(30, 30, 33, 33), bottom: 11, landings: [11] });
    const dug = shaft.cells.filter((c) => c[3] === 0).map((c) => [c[0], c[1], c[2]] as const);
    const s = mineStats(terrain, dug);
    expect(s.toMine).toBeGreaterThan(150);
    expect(s.materials[0]!.name).toBe('Stone');
    expect(s.materials.some((m) => m.name === 'Dirt')).toBe(true);
    expect(s.yRange?.min).toBe(11);
    expect(s.ores.map((o) => o.ore.id)).toContain('diamond');
    expect(s.ores.reduce((n, o) => n + o.mined + o.exposed, 0)).toBeGreaterThan(0);
    // X-ray: a window shows every ore in it, the whole plot every ore but coal; capped, rarest first.
    const win = xrayCells(terrain, { lo: 0, hi: 15 });
    expect(win.every((c) => c[1] <= 15)).toBe(true);
    expect(xrayCells(terrain, null).length).toBeGreaterThan(0);
    expect(xrayCells(terrain, null, 10)).toHaveLength(10);
  });
});

describe('the mine panel', () => {
  it('shows the ruler and the level, sets a level from an ore band and inserts templates and presets', () => {
    const onInsert = vi.fn(),
      onLevel = vi.fn(),
      onPreset = vi.fn(),
      onSeed = vi.fn();
    render(
      <MinePanel
        terrain={terrain}
        version="1.12.2"
        seed="12345"
        serverSeed={null}
        preferredFor={null}
        dug={() => []}
        rev={0}
        level={11}
        onLevel={onLevel}
        xray={false}
        onXray={() => undefined}
        at={null}
        canWrite
        busy={false}
        onSeed={onSeed}
        onInsert={onInsert}
        onPreset={onPreset}
      />,
    );
    expect(screen.getByTestId('ore-ruler')).toBeTruthy();
    expect(screen.getByTestId('level-readout').textContent).toContain('Diamond ore ★');
    expect(screen.queryByLabelText("Use the server's seed")).toBeNull();
    fireEvent.click(screen.getAllByTitle('Diamond ore: best at y 11')[0]!);
    expect(onLevel).toHaveBeenCalledWith(11);
    fireEvent.click(screen.getByTestId('insert-spiral'));
    expect(onInsert).toHaveBeenCalledTimes(1);
    const shaft = onInsert.mock.calls[0]![0] as { label: string; entry: number[] };
    expect(shaft.label).toMatch(/^spiral shaft 4×4/);
    expect(shaft.entry[1]).toBe(terrain.surfaceOver(30, 30, 33, 33));
    fireEvent.click(screen.getByTestId('insert-strip'));
    expect((onInsert.mock.calls[1]![0] as { label: string }).label).toContain('strip mine at y11');
    fireEvent.click(screen.getByTestId('preset-1.12.2-diamond-y11'));
    expect(onPreset).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('preset-b1.8.1-diamond-y11')).toBeNull();
    expect(screen.getByTestId('mine-stats').textContent).toContain('0 blocks to mine');
  });
});

describe('a mine design in Story Mode', () => {
  const mineSession = {
    kind: 'mine',
    blocks: [[31, 12, 31, 50, 2]],
    dug: [
      [30, 70, 30],
      [30, 69, 30],
      [31, 11, 31],
      [31, 12, 32],
    ],
  };

  it('keeps its absolute y, refuses digging under y 1 and queues mine items top-down', () => {
    const cells = designCells(mineSession);
    expect(cells.map((c) => c[1]).sort((a, b) => a - b)).toEqual([11, 12, 12, 69, 70]);
    const check = mineCheck(cells);
    expect(check).toEqual({ min: 11, max: 70, refusal: null });
    expect(mineCheck([[0, 0, 0, 0, 0]]).refusal).toMatch(/y 1/);
    const placed = placeDesign(cells, [100, 64, 200], 'n', true);
    expect(placed.every((b, i) => b[1] === cells[i]![1])).toBe(true);
    expect(placed[0]![0]).toBe(100 + cells[0]![0]);
    const items = mineItems(placed, 'deep mine', 'g1');
    expect(items).toHaveLength(1);
    const it0 = items[0]!;
    expect(it0.kind).toBe('mine');
    expect(it0.group).toBe('g1');
    expect(it0.blocks!.map((b) => b[1])).toEqual([70, 69, 12, 11]);
    expect(it0.place).toEqual([[101, 12, 201, 50, 2]]);
    expect(StoryBuildItem.parse(it0).kind).toBe('mine');
    // The reference expansion: dig cells as air, the torch as itself.
    expect(expandBuildItem(it0).filter((c) => c[3] === 50)).toHaveLength(1);
    expect(StoryGhostBlock.parse([1, 2, 3, 1, 0, 'b1', 'todo', 'dig'])[7]).toBe('dig');
    expect(StoryGhostBlock.parse([1, 2, 3, 1, 0, 'b1', 'todo'])).toHaveLength(7);
  });

  it('splits a big mine into items of at most 4096 cells', () => {
    const big = Array.from({ length: 5000 }, (_, i): [number, number, number, number, number] => [i % 64, 10 + Math.floor(i / 4096), Math.floor(i / 64) % 64, 0, 0]);
    big.push([1, 50, 1, 50, 5]);
    const items = mineItems(big);
    expect(items).toHaveLength(2);
    expect(items[0]!.blocks).toHaveLength(4096);
    expect(items[0]!.blocks![0]![1]).toBe(11); // top-down
    expect((items[1]!.blocks?.length ?? 0) + (items[1]!.place?.length ?? 0)).toBe(905);
    expect(new Set(items.map((i) => i.group)).size).toBe(1);
  });
});
