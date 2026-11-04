import {
  expandBuildItem,
  irrigationPlan,
  liquidFlow,
  pourOrder,
  StoryBuildRequest,
  StoryColonyState,
  waterItems,
  waterToolCells,
} from '@mineclaw/contracts';
import { StorySim } from '@mineclaw/contracts/testing';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { dryText, needsWater } from '@/features/story/GrowingZones';
import { buildItem, buildItems, waterDig, type BuildOpts } from '@/features/story/tools';
import { pendingHolo, pendingLine, WaterBar, WaterConfirm, type PendingWater } from '@/features/story/WaterTools';

afterEach(cleanup);

type V3 = [number, number, number];
const OPTS: BuildOpts = { height: 3, roof: 'flat', overhang: false, facing: 'n', material: 'planks' };
/** Flat stone up to y 63, air above; `extra` overrides blocks. */
const world =
  (extra: Record<string, readonly [number, number]> = {}) =>
  (x: number, y: number, z: number): readonly [number, number] =>
    extra[`${x},${y},${z}`] ?? (y <= 63 ? [1, 0] : [0, 0]);

describe('water tools', () => {
  it('a source takes the clicked cell, the infinite source a 2×2, the channel the dragged area; dig puts them in the ground', () => {
    expect(waterToolCells('water', [4, 64, 4], [9, 64, 9], false)).toEqual([[4, 64, 4]]);
    expect(waterToolCells('infinite_water', [4, 64, 4], [4, 64, 4], true)).toEqual([
      [4, 63, 4],
      [5, 63, 4],
      [4, 63, 5],
      [5, 63, 5],
    ]);
    expect(waterToolCells('water_channel', [6, 64, 2], [2, 64, 2], true)).toEqual([2, 3, 4, 5, 6].map((x) => [x, 63, 2]));
    expect(waterToolCells('water_channel', [0, 64, 0], [2, 64, 1], false)).toHaveLength(6);
  });

  it('the channel and infinite source dig by default, a single source does not; the option wins', () => {
    expect(waterDig('water_channel', {})).toBe(true);
    expect(waterDig('infinite_water', {})).toBe(true);
    expect(waterDig('water', {})).toBe(false);
    expect(waterDig('water_channel', { dig: false })).toBe(false);
  });

  it('a placement queues the dig first and the water after it in one group; the preview item is the water alone', () => {
    const items = buildItems('water_channel', [0, 64, 0], [3, 64, 0], OPTS);
    expect(items.map((i) => i.kind)).toEqual(['remove', 'water']);
    expect(items[0]!.group).toBeTruthy();
    expect(items[1]!.group).toBe(items[0]!.group);
    expect(items[1]!.blocks).toEqual(items[0]!.blocks);
    for (const it of items) expect(StoryBuildRequest.safeParse({ op: 'add', items: [it] }).success).toBe(true);
    const one = buildItem('water_channel', [0, 64, 0], [3, 64, 0], OPTS);
    expect(one.kind).toBe('water');
    expect(expandBuildItem(one).every((b) => b[3] === 9 && b[1] === 63)).toBe(true);
    // A single source is the explicit click (indoors allowed); lava pours lava; the pool says it is infinite.
    expect(buildItems('water', [1, 64, 1], [1, 64, 1], OPTS)).toEqual([{ kind: 'water', blocks: [[1, 64, 1]], indoor: true }]);
    expect(expandBuildItem(buildItem('lava', [1, 64, 1], [1, 64, 1], OPTS))[0]![3]).toBe(11);
    expect(buildItems('infinite_water', [1, 64, 1], [1, 64, 1], OPTS)[1]!.infinite).toBe(true);
  });

  it('water on open ground runs off; in a dug trench it stays; water reaching lava is said', () => {
    const line: V3[] = [
      [4, 64, 4],
      [5, 64, 4],
    ];
    const open = liquidFlow(world(), line);
    expect(open.spread.length).toBeGreaterThan(20);
    const trench: V3[] = [
      [4, 63, 4],
      [5, 63, 4],
    ];
    expect(liquidFlow(world(), trench, false, trench).spread).toEqual([]);
    const lava = liquidFlow(world({ '7,64,4': [11, 0] }), line);
    expect(lava.touches).toContain('lava at 7,64,4');
    expect(lava.touches).toContain('obsidian');
    // Over a drop it falls and spreads again at the bottom.
    const ledge = liquidFlow(world({ '6,63,4': [0, 0], '6,62,4': [0, 0] }), [[5, 64, 4]]);
    expect(ledge.spread).toContainEqual([6, 62, 4]);
  });

  it('pours every other cell of a channel first, two opposite corners of a pool', () => {
    const line: V3[] = [0, 1, 2, 3, 4].map((x) => [x, 63, 0]);
    expect(pourOrder(line).map((c) => c[0])).toEqual([0, 2, 4, 1, 3]);
    const pool: V3[] = [
      [0, 63, 0],
      [1, 63, 0],
      [0, 63, 1],
      [1, 63, 1],
    ];
    expect(pourOrder(pool).slice(0, 2)).toEqual([
      [0, 63, 0],
      [1, 63, 1],
    ]);
  });
});

describe('irrigation', () => {
  const zone = { id: 'z1', kind: 'farm', area: [0, 64, 40, 19, 64, 51] as [number, number, number, number, number, number] };

  it('a dry 20×12 wheat field gets a channel every 9th row at soil level, dug first', () => {
    const plan = irrigationPlan(zone, false, () => 63, () => false, () => 2);
    expect(plan.axis).toBe('x');
    expect([...new Set(plan.cells.map((c) => c[2]))]).toEqual([44, 51]);
    expect(plan.cells).toHaveLength(40);
    expect(plan.dig).toHaveLength(40);
    expect(plan.note).toContain('2 channels');
  });

  it('a watered field wants nothing; sugar cane gets water beside every cane row', () => {
    expect(irrigationPlan(zone, false, () => 63, () => true, () => 2).cells).toEqual([]);
    const cane = { ...zone, area: [0, 64, 60, 9, 64, 65] as typeof zone.area };
    expect([...new Set(irrigationPlan(cane, true, () => 63, () => false, () => 12).cells.map((c) => c[2]))]).toEqual([61, 64]);
  });

  it('the growing zone window offers it where cells are dry', () => {
    const z = { id: 'z1', kind: 'farm', area: zone.area, status: '0/40 sown, 0 ripe, 40 too far from water' } as never;
    expect(needsWater(z)).toBe(true);
    expect(dryText(z)).toBe('40 cells have no water within 4 blocks');
    expect(needsWater({ ...(z as object), status: '40/40 sown, 2 ripe' } as never)).toBe(false);
  });
});

describe('the mock director', () => {
  const sim = (extra: Record<string, readonly [number, number]> = {}) =>
    new StorySim({ world: { blockAt: world(extra), ground: () => 63, entities: () => [] }, timeRateMs: 0 });

  it('previews a channel (cells, flow), refuses a solid cell unless its group digs it, and queues pour ghosts', () => {
    const s = sim();
    const { id } = s.create({ name: 'Water', pawns: 1 });
    const cells: V3[] = [
      [4, 63, 4],
      [5, 63, 4],
    ];
    const bad = s.builds(id, { op: 'preview', items: [{ kind: 'water', blocks: cells }] });
    expect(bad.error).toContain('dig it first');
    const items = waterItems('water_channel', cells, { dig: true, group: 'g1' });
    const ok = s.builds(id, { op: 'preview', items });
    expect(ok.error).toBeNull();
    expect(ok.flow).toEqual({ spread: 0, cells: [] });
    const open = s.builds(id, { op: 'preview', items: [{ kind: 'water', blocks: [[4, 64, 4]] }] });
    expect(open.flow!.spread).toBeGreaterThan(0);
    expect(open.warnings![0]).toContain('will spread to');
    s.builds(id, { op: 'add', items });
    const st = StoryColonyState.parse(s.state(id));
    const water = (st.builds ?? []).find((b) => b.kind === 'water')!;
    expect(water.label).toBe('water channel (2 sources)');
    expect(water.liquid).toBe('water');
    expect(water.materials[0]).toMatchObject({ item: 'bucket', need: 1 });
    const pour = st.ghosts!.blocks.filter((g) => g[7] === 'pour');
    expect(pour.map((g) => g[3])).toEqual([9, 9]);
  });

  it('lava needs allow_lava; the Water tab lists the tools (and lava once allowed)', () => {
    const s = sim();
    const { id } = s.create({ name: 'Lava', pawns: 1 });
    expect(() => s.builds(id, { op: 'add', items: [{ kind: 'water', material: 'lava', blocks: [[4, 64, 4]] }] })).toThrow(/allow_lava/);
    const tab = () => s.architect(id).entries.filter((e) => e.tab === 'water').map((e) => e.kind);
    expect(tab()).toEqual(['water', 'water_channel', 'infinite_water']);
    expect(s.architect(id).tabs).toContain('water');
    s.options(id, { allowLava: true });
    expect(tab()).toContain('lava');
  });

  it('answers irrigation for a growing zone', () => {
    const s = sim();
    const { id } = s.create({ name: 'Farm', pawns: 1 });
    s.zones(id, { op: 'add', kind: 'farm', area: [0, 64, 40, 19, 64, 51], plant: 'wheat' });
    const z = StoryColonyState.parse(s.state(id)).zones.find((x) => x.kind === 'farm')!;
    const out = s.zones(id, { op: 'irrigation', id: z.id }) as { cells: V3[]; dig: V3[]; note: string };
    expect(out.cells.length).toBe(40);
    expect(out.dig.length).toBe(40);
  });
});

describe('water UI', () => {
  const pending: PendingWater = {
    label: 'Water channel',
    items: waterItems('water_channel', [[4, 64, 4]], { dig: false }),
    cells: 1,
    flow: { spread: 2, cells: [[5, 64, 4], [3, 64, 4]] },
    warnings: ['the water will spread to 2 cells beyond what was drawn'],
  };

  it('the confirm bar says how far it runs, and shows the sources and the flow as holograms', () => {
    expect(pendingLine(pending)).toBe('Water channel: 1 source · spreads to 2 more cells');
    expect(pendingHolo(pending)).toEqual([
      [4, 64, 4, 9, 0],
      [5, 64, 4, 8, 4],
      [3, 64, 4, 8, 4],
    ]);
    // A trench (dig first, the irrigation helper's shape too): its cells show as dig cells (id 0) besides the water.
    const trench = waterItems('water_channel', [[4, 63, 4], [5, 63, 4]], { dig: true, group: 'g' });
    expect(pendingHolo({ ...pending, items: trench, cells: 2, flow: null })).toEqual([
      [4, 63, 4, 0, 0],
      [5, 63, 4, 0, 0],
      [4, 63, 4, 9, 0],
      [5, 63, 4, 9, 0],
    ]);
    const onQueue = vi.fn(),
      onCancel = vi.fn();
    render(<WaterConfirm pending={pending} onQueue={onQueue} onCancel={onCancel} />);
    expect(screen.getByTestId('water-confirm').textContent).toContain('spread to 2 cells');
    fireEvent.click(screen.getByTestId('water-queue'));
    expect(onQueue).toHaveBeenCalled();
  });

  it('the water bar toggles digging the trench', () => {
    const onBuild = vi.fn();
    render(<WaterBar toolId="water_channel" build={OPTS} onBuild={onBuild} onClose={() => {}} />);
    const dig = screen.getByTestId<HTMLInputElement>('water-dig');
    expect(dig.checked).toBe(true);
    fireEvent.click(dig);
    expect(onBuild).toHaveBeenCalledWith({ dig: false });
  });
});
