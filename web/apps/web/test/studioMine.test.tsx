/**
 * The Design Studio's mine mode: the canvas over generated terrain (absolute y, ground = solid terrain, bedrock never dug,
 * fill back), the studio switching modes from the session, the ore guide and dig estimate, the mine panel, and a mine design
 * placed in Story Mode (absolute y, `mine` items, refused below y 1).
 */
import type { DesignerSession } from '@mineclaw/contracts';
import { expandBuildItem, MOCK_DESIGNER_PALETTE, StoryBuildItem, StoryGhostBlock, toSnake } from '@mineclaw/contracts';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Client from '@/lib/api/client';
import type { Story } from '@/features/story/mode/commands';
import { pickUpDesign } from '@/features/story/mode/designParam';
import { releaseTool } from '@/features/story/mode/release';
import type { DragResult, MapTool } from '@/features/story/StoryMap';
import { Canvas, EARTH, inside, keyOf, plotMode, setPlotMode } from '@/features/studio/canvas';
import { MinePanel } from '@/features/studio/MinePanel';
import { levelOres, mineStats, xrayCells } from '@/features/studio/mine/mineStats';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { spiral } from '@/features/studio/mine/mineTemplates';
import { defaultModule } from '@/features/studio/mine/modules';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { aimHit, toggleUnder, underCut, underRange } from '@/features/studio/tools';
import {
  designCells,
  MAX_MINE_PARTS,
  mineCheck,
  mineItems,
  placeDesign,
  type DesignBlock,
} from '@/features/story/tools';

// Story Mode's AI directives and the design pick-up call the client; the studio tests below pass their own StudioApi.
const client = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock('@/lib/api/client', async (original) => ({ ...(await original<typeof Client>()), api: client.api }));

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
  it('keeps authored excavation at absolute y, including air, without digging bedrock', () => {
    setPlotMode({ kind: 'mine', terrain });
    expect(inside(0, 0, 0)).toBe(true);
    expect(inside(0, 127, 0)).toBe(true);
    expect(inside(0, 128, 0)).toBe(false);
    expect(inside(0, -1, 0)).toBe(false);
    expect(inside(255, 127, 255)).toBe(true);
    expect(inside(256, 20, 0)).toBe(false);
    expect(posKey(5, 120, 5)).toBe(true);
    const c = new Canvas();
    const top = terrain.surface(10, 10);
    expect(c.get(10, top, 10)).toEqual([EARTH, 0]);
    expect(c.get(10, top + 1, 10)).toEqual([0, 0]);
    const dig = c.apply([
      [10, 20, 10, 0, 0],
      [10, top + 3, 10, 0, 0], // air over the surface: retained as intended excavation
      [10, 0, 10, 0, 0], // the bedrock floor: never
    ]);
    expect(dig.forward).toEqual([[10, 20, 10, 0, 0], [10, top + 3, 10, 0, 0]]);
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
    expect(plotMode().size).toBe(64);
    expect(inside(64, 0, 0)).toBe(false);
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
  it('offers six draggable layouts, separate placement heights, the ore chart and version presets', () => {
    const onPickModule = vi.fn(), onPlacementY = vi.fn(), onLevel = vi.fn(), onPreset = vi.fn();
    render(<MinePanel terrain={terrain} version="1.12.2" seed="12345" serverSeed={null} preferredFor={null}
      dug={() => []} rev={0} level={11} onLevel={onLevel} xray={false} onXray={() => undefined}
      at={null} canWrite busy={false} onSeed={() => undefined} onInsert={() => undefined} onPreset={onPreset}
      placementY={11} onPlacementY={onPlacementY} onPickModule={onPickModule} />);
    expect(screen.getByTestId('ore-ruler')).toBeTruthy();
    expect(screen.getByTestId('level-readout').textContent).toContain('Diamond ore ★');
    fireEvent.click(screen.getByTitle('Diamond ore: preferred feet Y 11'));
    expect(onPlacementY).toHaveBeenCalledWith(11);
    expect(onLevel).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Place Spiral shaft' }));
    expect(onPickModule).toHaveBeenCalledWith(expect.objectContaining({ kind: 'spiral', depth: 6 }));
    fireEvent.click(screen.getByRole('button', { name: 'Place Branch mine' }));
    expect(onPickModule).toHaveBeenCalledWith(expect.objectContaining({ kind: 'branches', spacing: 4 }));
    expect(screen.getAllByRole('button', { name: /^Place / })).toHaveLength(6);
    const setData = vi.fn();
    fireEvent.dragStart(screen.getByRole('button', { name: 'Place Tunnel' }), { dataTransfer: { setData, effectAllowed: '' } });
    expect(setData).toHaveBeenCalledWith('application/x-mineclaw-mine-module', expect.stringContaining('tunnel'));
    expect(onPickModule).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'tunnel' }), { drag: true });
    fireEvent.click(screen.getByText('Version presets · 1.12.2'));
    fireEvent.click(screen.getByTestId('preset-1.12.2-diamond-y11'));
    expect(onPreset).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('preset-b1.8.1-diamond-y11')).toBeNull();
    expect(screen.getByTestId('mine-stats').textContent).toContain('0 blocks to mine');
    expect(screen.getByTestId('mine-vein-follow')).toHaveTextContent('follow connected exposed ore beyond the plan');
    fireEvent.keyDown(screen.getByTestId('ore-ruler'), { key: 'ArrowUp' });
    expect(onLevel).toHaveBeenCalledWith(12);
  });

  it('starts an empty mine with three steps and the presets at the top, and shows the working level with its view buttons', () => {
    const onPreset = vi.fn(), onTopView = vi.fn(), onFrameAll = vi.fn();
    const panel = (over: Partial<Parameters<typeof MinePanel>[0]>) => <MinePanel terrain={terrain} version="1.12.2" seed="12345" serverSeed={null} preferredFor={null}
      dug={() => []} rev={0} level={null} onLevel={() => undefined} xray={false} onXray={() => undefined}
      at={null} canWrite busy={false} onSeed={() => undefined} onInsert={() => undefined} onPreset={onPreset}
      workLevel={16} onTopView={onTopView} onFrameAll={onFrameAll} {...over} />;
    const view = render(panel({ empty: true }));
    const start = screen.getByTestId('mine-start');
    expect(screen.getByTestId('mine-properties').firstElementChild).toBe(start);
    expect(start.querySelectorAll('ol > li')).toHaveLength(3);
    // The presets are open there, and not again in the collapsed section at the bottom.
    expect(screen.queryByText('Version presets · 1.12.2')).toBeNull();
    fireEvent.click(screen.getByTestId('preset-1.12.2-diamond-y11'));
    expect(onPreset).toHaveBeenCalledWith(expect.objectContaining({ id: '1.12.2-diamond-y11' }));
    expect(screen.getByTestId('work-level')).toHaveTextContent('Work at Y16');
    fireEvent.click(screen.getByRole('button', { name: 'Top view' }));
    fireEvent.click(screen.getByRole('button', { name: 'Frame all' }));
    expect(onTopView).toHaveBeenCalledOnce();
    expect(onFrameAll).toHaveBeenCalledOnce();
    // Once something is planned, the steps give way and the presets go back to the bottom.
    view.rerender(panel({ empty: false, workLevel: null }));
    expect(screen.queryByTestId('mine-start')).toBeNull();
    expect(screen.getByText('Version presets · 1.12.2')).toBeTruthy();
    expect(screen.getByTestId('work-level')).toHaveTextContent('No working level');
    view.rerender(panel({ empty: true, modules: [{ id: 'a', spec: { ...defaultModule('tunnel') }, at: [30, 20, 30] }] }));
    expect(screen.queryByTestId('mine-start')).toBeNull();
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

describe('a big mine queued from the Controller', () => {
  /** `n` dig cells in 2 000-cell layers going down from y 70, and a torch row on the lowest layer, as the studio gives them. */
  const mine = (n: number): DesignBlock[] => {
    const out: DesignBlock[] = [];
    for (let i = 0; i < n; i++) out.push([i % 40, 70 - Math.floor(i / 2000), Math.floor(i / 40) % 50, 0, 0]);
    const low = 70 - Math.floor((n - 1) / 2000);
    for (let k = 0; k < 20; k++) out.push([k * 2, low, 0, 50, 5]);
    return out;
  };
  /** Five-digit negative coordinates: the longest cells a real world gives (about 19 bytes a cell). */
  const FAR: [number, number, number] = [-12345, 64, -23456];
  const bytes = (body: unknown) => new TextEncoder().encode(JSON.stringify(toSnake(body))).length;

  const story = (write: (route: string, body: unknown) => Promise<unknown>, ai: string[] = []) => {
    const say = vi.fn();
    const tool: MapTool = {
      kind: 'design',
      id: 7,
      name: 'deep mine',
      blocks: mine(20_000),
      mine: { min: 61, max: 70 },
    };
    const c = {
      tool,
      build: { facing: 'n' },
      state: { version: '1.12.2' },
      canWrite: true,
      split: { pawns: [], ai },
      write: vi.fn(write),
      say,
      setTool: vi.fn(),
      run: vi.fn(),
    } as unknown as Story;
    return { c, say, write: c.write as unknown as ReturnType<typeof vi.fn> };
  };
  const click = {
    rect: [FAR[0], FAR[2], FAR[0], FAR[2]],
    a: FAR,
    b: FAR,
    cells: [],
    entities: [],
    count: 1,
    moved: false,
  } as unknown as DragResult;

  beforeEach(() => {
    client.api.mockReset();
    client.api.mockResolvedValue({});
  });

  it('no mine item exceeds 60 000 bytes at five-digit negative coordinates', () => {
    const items = mineItems(placeDesign(mine(20_000), FAR, 'n', true), 'deep mine', 'g1');
    expect(items.length).toBeGreaterThan(1);
    for (const it of items) {
      expect(JSON.stringify(toSnake(it)).length).toBeLessThan(60_000);
      expect((it.blocks?.length ?? 0) + (it.place?.length ?? 0)).toBeLessThanOrEqual(4096);
      expect(StoryBuildItem.parse(it).group).toBe('g1');
    }
    // Nothing is dropped: every dig cell and every torch is in exactly one item.
    expect(items.reduce((n, it) => n + (it.blocks?.length ?? 0), 0)).toBe(20_000);
    expect(items.reduce((n, it) => n + (it.place?.length ?? 0), 0)).toBe(20);
  });

  it('refuses a mine that needs too many parts instead of dropping cells', async () => {
    const huge = mine(MAX_MINE_PARTS * 4096 + 1).filter((b) => b[3] === 0);
    expect(() => mineItems(huge)).toThrow(`at most ${MAX_MINE_PARTS}`);
    const s = story(() => Promise.resolve({}));
    await releaseTool({ ...s.c, tool: { ...(s.c.tool as object), blocks: huge } as MapTool }, click, false);
    expect(s.write).not.toHaveBeenCalled();
    expect(s.say).toHaveBeenCalledWith(expect.stringContaining(`to queue (at most ${MAX_MINE_PARTS})`), 'warn');
  });

  it('a 20 000-cell mine is queued in several requests, in top-down order, each under 64 KiB', async () => {
    const sizes: number[] = [];
    const s = story(
      (route, body) => {
        expect(route).toBe('story.builds');
        sizes.push(bytes(body));
        return Promise.resolve({});
      },
      ['maid_chan'],
    );
    await releaseTool(s.c, click, false);
    const bodies = s.write.mock.calls.map((call) => call[1] as { op: string; items: StoryBuildItem[] });
    expect(bodies.length).toBeGreaterThan(5);
    expect(Math.max(...sizes)).toBeLessThan(64 * 1024);
    expect(bodies.every((b) => b.op === 'add' && b.items.length === 1)).toBe(true);
    const items = bodies.map((b) => b.items[0]!);
    expect(new Set(items.map((i) => i.group)).size).toBe(1);
    // In order: the dig top-down across the parts, then the torches.
    const ys = items.flatMap((i) => (i.blocks ?? []).map((c) => c[1]));
    expect(ys).toHaveLength(20_000);
    expect(ys.every((y, k) => k === 0 || y <= ys[k - 1]!)).toBe(true);
    expect(items.flatMap((i) => i.place ?? [])).toHaveLength(20);
    // The AI body gets one directive (one chat line, one brain turn), with the first part, under the cap too.
    const directives = client.api.mock.calls.filter((call) => call[0] === 'story.directives');
    expect(directives).toHaveLength(1);
    const body = (
      directives[0]![1] as { body: { bots: string[]; directive: { kind: string; build: StoryBuildItem[] } } }
    ).body;
    expect(body.bots).toEqual(['maid_chan']);
    expect(body.directive.build).toEqual([items[0]]);
    expect(bytes(body)).toBeLessThan(64 * 1024);
    expect(s.say).toHaveBeenCalledOnce();
    expect(s.say.mock.calls[0]![0]).toMatch(new RegExp(`^Queued deep mine: 20020 blocks in ${items.length} parts`));
    expect(s.say.mock.calls[0]![0]).toContain(`the AI got part 1 of ${items.length}`);
  });

  it('a refused AI directive is said on its own and never stops the colony’s parts', async () => {
    client.api.mockImplementation((route: string) =>
      route === 'story.directives' ? Promise.reject(new Error('no such bot')) : Promise.resolve({}),
    );
    const s = story(() => Promise.resolve({}), ['old_body']);
    await releaseTool(s.c, click, false);
    const total = mineItems(placeDesign(mine(20_000), FAR, 'n', true)).length;
    expect(s.write).toHaveBeenCalledTimes(total);
    expect(s.say).toHaveBeenCalledOnce();
    const [line, tone] = s.say.mock.calls[0]! as [string, string | undefined];
    expect(line).toMatch(new RegExp(`^Queued deep mine: 20020 blocks in ${total} parts`));
    expect(line).toContain('old_body: the AI directive failed · no such bot');
    expect(line).not.toContain('the AI got part 1');
    expect(tone).toBe('warn');
  });

  it('a failure in part 3 reports 2 of N queued and stops', async () => {
    let n = 0;
    const s = story(() => (++n === 3 ? Promise.reject(new Error('body over 65536 bytes')) : Promise.resolve({})));
    await releaseTool(s.c, click, false);
    expect(s.write).toHaveBeenCalledTimes(3);
    const total = mineItems(placeDesign(mine(20_000), FAR, 'n', true)).length;
    expect(s.say).toHaveBeenCalledOnce();
    expect(s.say).toHaveBeenCalledWith(
      `deep mine: queued 2 of ${total} parts, then stopped · body over 65536 bytes`,
      'warn',
    );
  });

  it('designCells handles 300 000 cells (past what Math.min(...cells) takes)', () => {
    const dug = Array.from({ length: 300_000 }, (_, i) => [i % 400, 20 + Math.floor(i / 40_000), Math.floor(i / 400)]);
    const cells = designCells({ kind: 'mine', dug, blocks: [[5, 19, 5, 50, 5]] });
    expect(cells).toHaveLength(300_001);
    expect(cells[0]).toEqual([5, 19, 5, 50, 5]);
    expect(cells[1]).toEqual([0, 20, 0, 0, 0]);
  });

  it('a saved mine with torches is picked up as a mine, torches included', async () => {
    // Real-shaped replies: the blueprint bill has no block list (BlueprintService.BillJson) and a studio design has no build plan.
    const replies: Record<string, unknown> = {
      'designs.get': { design: { name: 'deep mine' } },
      'blueprints.get': {
        id: 7,
        name: 'deep mine',
        site: [0, 64, 0],
        origin: null,
        dsl: 'mine',
        builds: [],
        bill: [{ item: 'torch', need: 2, have: 0, stored: 0, missing: 2, source: 'craft' }],
      },
      'designer.models': { version: '1.12.2' },
      'designer.create': {
        kind: 'mine',
        blocks: [
          [30, 12, 30, 50, 5],
          [31, 12, 30, 50, 5],
        ],
        dug: [
          [30, 13, 30],
          [30, 12, 30],
          [31, 12, 30],
        ],
      },
    };
    client.api.mockImplementation((id: string) =>
      id in replies ? Promise.resolve(replies[id]) : Promise.reject(new Error('no build plan for that design')),
    );
    const setTool = vi.fn();
    await pickUpDesign({ say: vi.fn(), can: () => true, setTool }, 7);
    expect(client.api).toHaveBeenCalledWith('designer.create', expect.anything());
    const tool = setTool.mock.calls[0]![0] as Extract<MapTool, { kind: 'design' }>;
    expect(tool.mine).toEqual({ min: 12, max: 13 });
    expect(tool.blocks.filter((b) => b[3] === 50)).toHaveLength(2);
    // Queued as a mine: the torches ride in `place`, put in once their cells are dug.
    const items = mineItems(placeDesign(tool.blocks, [0, 64, 0], 'n', true), tool.name, 'g');
    expect(items.map((i) => i.kind)).toEqual(['mine']);
    expect(items[0]!.place).toHaveLength(2);
  });
});
