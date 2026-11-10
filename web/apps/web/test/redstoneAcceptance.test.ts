/**
 * The redstone studio end to end on the real simulator (no stand-ins below the page): a NOT gate from the library is
 * placed, its lever clicked, and the torch's shown state flips two game ticks after its block is powered; stopping puts
 * the canvas view back exactly as it was. Then the debugger's acceptance: a rising breakpoint on the XOR's output pauses
 * on the very tick the output rises, and a step back returns to the tick before it, with the timeline's cursor there.
 * Both game versions; time is a number the test moves.
 */
import { inflateRawSync } from 'node:zlib';
import { MOCK_DESIGNER_PALETTE, type DesignerCell, type DesignerSession } from '@mineclaw/contracts';
import { createSimulation, type Pos, type Port, type RedstoneVersion } from '@mineclaw/redstone';
import { cellIndex, decodeMcwc, unpackSnapshot } from '@mineclaw/world-viewer';
import { act, render } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { Timeline } from '@/features/studio/redstone/debugger/Timeline';
import { beyond } from '@/features/studio/redstone/ports';
import { RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import { RedstoneSimController } from '@/features/studio/redstone/RedstoneSimController';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { BASE_Y, StudioWorld } from '@/features/studio/studioWorld';

const VERSIONS: readonly RedstoneVersion[] = ['b1.8.1', '1.12.2'];
const STONE = 1;
const DUST = 55;
const LEVER = 69;
const LIT_TORCH = 76;
const UNLIT_TORCH = 75;

const session = (version: RedstoneVersion): DesignerSession => ({
  id: 11,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'acceptance',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version,
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
});

const api: StudioApi = {
  message: () => Promise.reject(new Error('n/a')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('n/a')),
};

const disposers: Array<() => void> = [];
afterEach(() => {
  for (const d of disposers.splice(0)) d();
  setPlotMode({ kind: 'build' });
  vi.restoreAllMocks();
});

/** A studio on a redstone canvas, its world as the viewer reads it, and the two controllers on the real engine. */
function mount(version: RedstoneVersion) {
  const clock = { t: 0 };
  const studio = new Studio(session(version), api, 60_000);
  const world = new StudioWorld(version, studio.canvas, () => 1000);
  const components = new RedstoneComponentController(studio, null);
  const sim = new RedstoneSimController({
    studio,
    version,
    create: createSimulation,
    now: () => clock.t,
    display: { cells: (cells) => world.setDisplay(cells) },
  });
  const off = sim.connect();
  disposers.push(() => {
    off();
    components.dispose();
    studio.dispose();
  });
  sim.frame();
  /** Animation frames `step` ms apart for `ms` in all. */
  const run = (ms: number, step = 50) => {
    for (let passed = 0; passed < ms; passed += step) {
      clock.t += step;
      sim.frame();
    }
  };
  return { studio, world, components, sim, run };
}

/** The harness's feeder on an input port: dust on the port, a block beyond it and a lever on that block. */
function feeder(port: Port): { cells: DesignerCell[]; lever: Pos } {
  const block = beyond(port);
  return {
    cells: [
      [port.at[0], port.at[1], port.at[2], DUST, 0],
      [block[0], block[1], block[2], STONE, 0],
      [block[0], block[1] + 1, block[2], LEVER, 5],
    ],
    lever: [block[0], block[1] + 1, block[2]],
  };
}

/** The blocks of every column the plot's corner chunk holds, as the viewer would fetch them. */
async function columnBlocks(world: StudioWorld, cx = 0, cz = 0): Promise<{ ids: number[]; meta: number[] }> {
  const entry = decodeMcwc(await world.source.chunks(0, [[cx, cz]])).entries[0]!;
  const b = unpackSnapshot(inflateRawSync(entry.data));
  return { ids: Array.from(b.ids), meta: Array.from(b.meta) };
}

async function shownAt(world: StudioWorld, at: Pos): Promise<[number, number]> {
  const col = await columnBlocks(world, at[0] >> 4, at[2] >> 4);
  const i = cellIndex(at[0] & 15, at[1] + BASE_Y, at[2] & 15);
  return [col.ids[i]!, col.meta[i]!];
}

describe.each(VERSIONS)('a library NOT gate in %s', (version) => {
  it('flips its torch two game ticks after its block is powered, and stopping restores the view exactly', async () => {
    const h = mount(version);
    const gate = h.components.place('gate.not', [4, 0, 4], { turns: 1 })!;
    expect(gate).not.toBeNull();
    const placed = h.components.placedOf(gate.id)!;
    const input = placed.ports.find((p) => p.kind === 'in')!;
    const feed = feeder(input);
    h.studio.edit(feed.cells, 'place', 'feeder');
    const torch = placed.cells.find((c) => c[3] === LIT_TORCH)!;
    const torchAt: Pos = [torch[0], torch[1], torch[2]];
    // The block the torch hangs on (the NOT's only stone).
    const block = placed.cells.find((c) => c[3] === STONE)!;
    const blockAt: Pos = [block[0], block[1], block[2]];
    h.run(200);
    const before = await columnBlocks(h.world);
    expect(await shownAt(h.world, torchAt)).toEqual([LIT_TORCH, torch[4]]);

    expect(h.sim.click(feed.lever)).toBe(true);
    expect(h.sim.snapshot().mode).toBe('paused');
    // Step one game tick at a time and note when the block takes power and when the torch goes out.
    let powered = -1;
    let flipped = -1;
    for (let i = 0; i < 20 && flipped < 0; i++) {
      h.sim.stepGameTick();
      const tick = h.sim.snapshot().tick;
      if (powered < 0 && h.sim.inspect(blockAt)!.sample.power > 0) powered = tick;
      if (h.sim.inspect(torchAt)!.sample.state >> 4 === UNLIT_TORCH) flipped = tick;
    }
    // The click lands between ticks 0 and 1 and the input repeater's 2 gt count from tick 0, so its block is powered at
    // tick 2; the torch takes its own 2 gt after that.
    expect(powered).toBe(2);
    expect(flipped).toBe(powered + 2);
    h.run(200);
    expect(await shownAt(h.world, torchAt)).toEqual([UNLIT_TORCH, torch[4]]);
    // The design itself never changed.
    expect(h.studio.canvas.get(...(torchAt as [number, number, number]))).toEqual([LIT_TORCH, torch[4]]);

    h.sim.stop();
    expect(h.sim.snapshot().mode).toBe('edit');
    expect(await columnBlocks(h.world)).toEqual(before);
    expect(await shownAt(h.world, torchAt)).toEqual([LIT_TORCH, torch[4]]);
  });
});

/** A canvas context that only notes the text drawn on it (the timeline's cursor label among it). */
function textRecorder() {
  const texts: string[] = [];
  const ctx = new Proxy(
    { texts },
    {
      get: (target, key) => {
        if (key === 'fillText') return (t: string) => texts.push(t);
        if (key === 'texts') return target.texts;
        return () => undefined;
      },
      set: () => true,
    },
  );
  return { ctx, texts };
}

describe.each(VERSIONS)('the debugger on a library XOR in %s', (version) => {
  it('pauses on the exact tick its output rises, and a step back returns to the tick before', () => {
    const h = mount(version);
    const xor = h.components.place('gate.xor', [4, 0, 20])!;
    expect(xor).not.toBeNull();
    const ports = h.components.placedOf(xor.id)!.ports;
    const a = feeder(ports.find((p) => p.name === 'A')!);
    const b = feeder(ports.find((p) => p.name === 'B')!);
    const out = ports.find((p) => p.kind === 'out')!.at;
    h.studio.edit([...a.cells, ...b.cells, [out[0], out[1], out[2], DUST, 0]], 'place', 'wiring');

    // The tick the output rises, found on a simulation of its own.
    const plain = createSimulation({ version, cells: h.studio.canvas.cells(), ground: { topY: -1 } });
    expect(plain.powerAt(out)).toBe(0);
    plain.input({ kind: 'toggle', at: a.lever });
    let rises = -1;
    for (let i = 0; i < 40 && rises < 0; i++) {
      plain.stepGameTick();
      if (plain.powerAt(out) > 0) rises = plain.tick;
    }
    expect(rises).toBeGreaterThan(0);

    h.sim.setBreakpoint({ id: 'b1', at: out, on: 'rising', enabled: true });
    h.sim.addProbe(out);
    h.run(200);
    expect(h.sim.click(a.lever)).toBe(true);
    h.sim.start();
    for (let i = 0; i < 200 && h.sim.snapshot().mode === 'running'; i++) h.run(50);

    const hit = h.sim.snapshot();
    expect(hit.mode).toBe('paused');
    expect(hit.tick).toBe(rises);
    expect(hit.lastHits).toHaveLength(1);
    expect(hit.lastHits[0]).toMatchObject({ id: 'b1', tick: rises, at: out, before: 0 });
    expect(hit.lastHits[0]!.after).toBeGreaterThan(0);

    h.sim.stepBack();
    const back = h.sim.snapshot();
    expect(back.tick).toBe(rises - 1);
    expect(h.sim.inspect(out)!.sample.power).toBe(0);
    const trace = h.sim.timeline(back.probes[0]!.id, 0, back.tick);
    expect(trace[trace.length - 1]).toMatchObject({ tick: rises - 1, powered: false });

    // The timeline draws its cursor (the last text it draws) on the tick the simulation is at.
    const rec = textRecorder();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(rec.ctx as unknown as CanvasRenderingContext2D);
    const view = render(createElement(Timeline, { api: h.sim, snap: back }));
    expect(rec.texts.at(-1)).toBe(`${rises - 1} gt`);
    act(() => h.sim.stepGameTick());
    expect(h.sim.snapshot().tick).toBe(rises);
    view.rerender(createElement(Timeline, { api: h.sim, snap: h.sim.snapshot() }));
    expect(rec.texts.at(-1)).toBe(`${rises} gt`);
    view.unmount();
  });
});
