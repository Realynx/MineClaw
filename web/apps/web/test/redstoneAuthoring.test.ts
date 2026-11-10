/**
 * A redstone design's saved metadata (`redstoneAuthoring`): the envelope is read entry by entry and written back with
 * every key it does not know kept; the debugger's probes, breakpoints, start levels and settings go into it through
 * `Studio.setRedstoneAuthoring`, so a change of a probe alone is unsaved work that the next save sends; opening a design
 * puts them back into the simulation controller and is no change at all.
 */
import {
  MOCK_DESIGNER_PALETTE,
  type DesignerCell,
  type DesignerSaveRequest,
  type DesignerSession,
} from '@mineclaw/contracts';
import type { Pos } from '@mineclaw/redstone';
import { fakeSimulations } from '@mineclaw/redstone/testing';
import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { act, renderHook } from '@testing-library/react';
import { Scene } from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  authoringStoreOf,
  bindRedstoneAuthoring,
  blocksMin,
  debuggerPart,
  parseAuthoring,
  RedstoneAuthoringStore,
  serializeAuthoring,
  withDebugger,
  type DebuggerAuthoring,
} from '@/features/studio/redstone/authoring';
import { PowerOverlay } from '@/features/studio/redstone/powerOverlay';
import { RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import { REBUILD_MS, RedstoneSimController } from '@/features/studio/redstone/RedstoneSimController';
import { useRedstoneSim } from '@/features/studio/redstone/useRedstoneSim';
import { Studio, type StudioApi } from '@/features/studio/studio';
import type { StudioWorld } from '@/features/studio/studioWorld';

const OUT: Pos = [8, 0, 7];
const CHEST: Pos = [8, 0, 8];

const saved: DebuggerAuthoring = {
  probes: [{ id: 'p3', at: OUT, label: 'sum', color: 0xfacc15 }],
  breakpoints: [{ id: 'b1', at: OUT, on: 'atLeast', value: 8, enabled: false }],
  levels: [{ at: CHEST, level: 6 }],
  speed: 'max',
  displayHz: 5,
};

/** What a saved revision holds: a component placed by the library, someone else's key, and the debugger's state. */
const savedText = JSON.stringify({
  redstone: 1,
  components: [{ id: 'c1', def: 'gate.xor', at: [10, 0, 12], turns: 1, mirror: null, label: 'sum' }],
  customDefs: [],
  future: { kept: true },
  probes: saved.probes,
  breakpoints: saved.breakpoints,
  levels: saved.levels,
  sim: { speed: 'max', displayHz: 5, later: 'kept too' },
  // The min corner of the session's blocks (one chest at 8,0,8): the canvas these cells were saved on.
  origin: [8, 0, 8],
});

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: 5,
  name: 'xor',
  rev: 1,
  blocks: [[8, 0, 8, 54, 2]],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
  ...over,
});

const studios: Studio[] = [];
afterEach(() => {
  for (const s of studios.splice(0)) s.dispose();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function studioOf(over: Partial<DesignerSession> = {}) {
  const save = vi.fn((_id: number, _body: DesignerSaveRequest) =>
    Promise.resolve({ designId: 5, rev: 2, name: 'xor', blocks: 1, created: false, text: 'saved' }),
  );
  const api: StudioApi = { canvas: () => Promise.resolve({ rev: 1, applied: 1 }), message: vi.fn(), save };
  const studio = new Studio(session(over), api, 60_000);
  studios.push(studio);
  return { studio, save };
}

function controllerOf(studio: Studio) {
  const fake = fakeSimulations({}, { inputs: [[CHEST, 'level']] });
  const clock = { t: 0 };
  const c = new RedstoneSimController({
    studio,
    version: '1.12.2',
    create: fake.create,
    display: { cells: () => undefined },
    now: () => clock.t,
  });
  return { c, fake, clock };
}

describe('the envelope', () => {
  it('is empty for no text, text that is not JSON, and JSON not marked as redstone authoring', () => {
    const empty = {
      redstone: 1,
      probes: [],
      breakpoints: [],
      levels: [],
      sim: { speed: 10, displayHz: 10 },
    };
    for (const text of [undefined, null, '', 'not json', '[1]', '{"redstone":2,"probes":[]}', '{"mine":1}'])
      expect(parseAuthoring(text)).toEqual(empty);
  });

  it('keeps every key it does not know, the components and the sim settings it does not use included', () => {
    const a = parseAuthoring(savedText);
    expect(a.components).toEqual([
      { id: 'c1', def: 'gate.xor', at: [10, 0, 12], turns: 1, mirror: null, label: 'sum' },
    ]);
    expect(a.future).toEqual({ kept: true });
    expect(a.sim).toEqual({ speed: 'max', displayHz: 5, later: 'kept too' });
    expect(debuggerPart(a)).toEqual(saved);
    // Written back unchanged, in the same key order.
    expect(serializeAuthoring(withDebugger(a, debuggerPart(a)))).toBe(savedText);
  });

  it('drops entries that do not fit, and the second of an id or a cell', () => {
    const a = parseAuthoring(
      JSON.stringify({
        redstone: 1,
        probes: [
          { id: 'p1', at: [1, 2, 3], label: '  long name that is more than forty characters long, really  ', color: 1 },
          { id: 'p1', at: [4, 4, 4], label: 'twice', color: 2 },
          { id: 'p2', at: [1, 2], label: 'short pos', color: 2 },
          { id: 'p3', at: [1, 2, 3.5], label: 'not whole', color: 2 },
          { id: 'p4', at: [0, 0, 0], label: '', color: 2 },
          { id: 'p5', at: [0, 0, 0], label: 'colour', color: 0x1000000 },
          'p6',
        ],
        breakpoints: [
          { id: 'b1', at: [1, 0, 1], on: 'rising', enabled: true, value: 3 },
          { id: 'b2', at: [1, 0, 1], on: 'sometimes', enabled: true },
          { id: 'b3', at: [1, 0, 1], on: 'change' },
          { id: 'b4', at: [1, 0, 1], on: 'state', enabled: false, value: 1.5 },
        ],
        levels: [
          { at: [1, 0, 1], level: 20 },
          { at: [1, 0, 1], level: 3 },
          { at: [2, 0, 2], level: 0 },
          { at: [3, 0, 3], level: 4.4 },
          { at: [4, 0, 4], level: 'full' },
        ],
        sim: { speed: -1, displayHz: 7 },
      }),
    );
    expect(a.probes).toEqual([
      { id: 'p1', at: [1, 2, 3], label: 'long name that is more than forty charac', color: 1 },
    ]);
    expect(a.breakpoints).toEqual([
      { id: 'b1', at: [1, 0, 1], on: 'rising', enabled: true, value: 3 },
      { id: 'b4', at: [1, 0, 1], on: 'state', enabled: false },
    ]);
    expect(a.levels).toEqual([
      { at: [1, 0, 1], level: 15 },
      { at: [3, 0, 3], level: 4 },
    ]);
    expect(a.sim).toEqual({ speed: 10, displayHz: 10 });
  });
});

describe('the store', () => {
  it('opening a design is no change: the text is taken as saved in the form the store writes it', () => {
    const loose = savedText.replace('"redstone":1,', '"redstone":1, ');
    const { studio } = studioOf({ redstoneAuthoring: loose });
    const set = vi.spyOn(studio, 'setRedstoneAuthoring');
    const store = new RedstoneAuthoringStore(studio);
    expect(set).toHaveBeenCalledWith(savedText, true);
    expect(studio.dirty).toBe(false);
    // The same state again writes nothing.
    store.setDebugger(saved);
    expect(set).toHaveBeenCalledTimes(1);
  });

  it('a new design starts with an empty envelope, and that is not a change either', () => {
    const { studio } = studioOf({ redstoneAuthoring: null });
    const store = authoringStoreOf(studio);
    expect(store.current).toMatchObject({ redstone: 1, probes: [], sim: { speed: 10, displayHz: 10 } });
    expect(studio.dirty).toBe(false);
    expect(authoringStoreOf(studio)).toBe(store);
  });

  it('the debugger and the components write their own keys and keep each other’s', () => {
    const { studio } = studioOf({ redstoneAuthoring: savedText });
    const store = authoringStoreOf(studio);
    store.setDebugger({ ...saved, probes: [] });
    expect(studio.dirty).toBe(true);
    store.update({ components: [] });
    expect(store.current).toMatchObject({ components: [], future: { kept: true }, probes: [], levels: saved.levels });
    store.setDebugger(saved);
    expect(store.current.components).toEqual([]);
  });
});

describe('the debugger state in the design', () => {
  it('goes back into the controller when the design opens, without making it unsaved', () => {
    const { studio } = studioOf({ redstoneAuthoring: savedText });
    const { c, fake, clock } = controllerOf(studio);
    const off = bindRedstoneAuthoring(authoringStoreOf(studio), c);
    expect(c.snapshot()).toMatchObject({
      probes: saved.probes,
      breakpoints: saved.breakpoints,
      levels: saved.levels,
      speed: 'max',
      displayHz: 5,
      displayHzNow: 5,
    });
    expect(studio.dirty).toBe(false);
    // The start levels and the breakpoints reach the engine; the probe is watched.
    c.connect();
    clock.t += REBUILD_MS;
    c.frame();
    const sim = fake.made[fake.made.length - 1]!;
    expect(sim.circuit.extras).toEqual([{ at: CHEST, level: 6 }]);
    expect(c.timeline('p3', 0, 0)).toHaveLength(1);
    off();
  });

  it('a probe added and nothing else is unsaved work, and the save sends it with the rest kept', async () => {
    const { studio, save } = studioOf({ redstoneAuthoring: savedText });
    const { c } = controllerOf(studio);
    bindRedstoneAuthoring(authoringStoreOf(studio), c);
    c.addProbe([1, 0, 1]);
    expect(studio.dirty).toBe(true);

    await studio.save('xor');
    const sent = JSON.parse(save.mock.calls[0]![1].redstoneAuthoring!) as Record<string, unknown>;
    expect(sent).toMatchObject({
      redstone: 1,
      components: [{ id: 'c1' }],
      future: { kept: true },
      probes: [saved.probes[0], { id: 'p4', at: [1, 0, 1], label: 'P4' }],
      sim: { speed: 'max', displayHz: 5, later: 'kept too' },
    });
    expect(studio.dirty).toBe(false);
  });

  it('a placed component goes out with the next save beside the debugger’s keys, and the save leaves nothing unsaved', async () => {
    const { studio, save } = studioOf({ redstoneAuthoring: null, blocks: [] });
    const { c } = controllerOf(studio);
    bindRedstoneAuthoring(authoringStoreOf(studio), c);
    c.addProbe([1, 0, 1]);
    const components = new RedstoneComponentController(studio, null);
    components.place('gate.not', [20, 0, 20], { label: 'inverter' });
    const def = components.insertDesign('one block', [[0, 0, 0, 1, 0]], '1.12.2')!;

    await studio.save('gates');
    const sent = JSON.parse(save.mock.calls[0]![1].redstoneAuthoring!) as Record<string, unknown>;
    expect(sent).toMatchObject({
      redstone: 1,
      probes: [{ id: 'p1', at: [1, 0, 1] }],
      components: [{ id: 'c1', def: 'gate.not', at: [20, 0, 20], turns: 0, mirror: null, label: 'inverter' }],
      customDefs: [{ id: def.id, name: 'one block', layout: { size: [1, 1, 1], cells: [[0, 0, 0, 1, 0]], ports: [] } }],
    });
    expect(studio.dirty).toBe(false);
    components.dispose();
  });

  it('every part of it is written: a name, a breakpoint, a start level, the speed and the redraw rate', () => {
    const { studio } = studioOf({ redstoneAuthoring: null });
    const { c, clock } = controllerOf(studio);
    const store = authoringStoreOf(studio);
    bindRedstoneAuthoring(store, c);
    c.connect();
    clock.t += REBUILD_MS;
    c.frame();

    c.addProbe(OUT);
    c.renameProbe('p1', 'out');
    c.setBreakpoint({ id: 'b1', at: OUT, on: 'rising', enabled: true });
    c.setLevel(CHEST, 9);
    c.setSpeed(20);
    c.setDisplayHz(20);
    expect(store.debugger).toEqual({
      probes: [{ id: 'p1', at: OUT, label: 'out', color: 0x38bdf8 }],
      breakpoints: [{ id: 'b1', at: OUT, on: 'rising', enabled: true }],
      levels: [{ at: CHEST, level: 9 }],
      speed: 20,
      displayHz: 20,
    });

    // Taken back step by step, it is the opened design again.
    c.removeProbe('p1');
    c.removeBreakpoint('b1');
    c.setLevel(CHEST, 0);
    c.setSpeed(10);
    c.setDisplayHz(10);
    expect(studio.dirty).toBe(false);
  });

  it('what runs is no part of it: stepping, a lever and a level poked while live write nothing', () => {
    const { studio } = studioOf({ redstoneAuthoring: savedText });
    const { c } = controllerOf(studio);
    const set = vi.spyOn(studio, 'setRedstoneAuthoring');
    bindRedstoneAuthoring(authoringStoreOf(studio), c);
    set.mockClear();
    c.stepGameTick();
    c.setLevel(CHEST, 2);
    c.stepRedstoneTick();
    c.stepBack();
    c.stop();
    expect(set).not.toHaveBeenCalled();
    expect(studio.dirty).toBe(false);
  });
});

describe('a design reopened somewhere else on the plot', () => {
  it('moves the components, probes, breakpoints and start levels with its cells', async () => {
    // Drawn on a new canvas: a NOT with its corner at (2,0,2), a chest with a start level, a probe and a breakpoint.
    const { studio, save } = studioOf({ designId: null, redstoneAuthoring: null });
    const { c, clock } = controllerOf(studio);
    bindRedstoneAuthoring(authoringStoreOf(studio), c);
    c.connect();
    clock.t += REBUILD_MS;
    c.frame();
    const components = new RedstoneComponentController(studio, null);
    components.place('gate.not', [2, 0, 2]);
    const torch = studio.canvas.cells().find((x) => x[3] === 76)!;
    const at: Pos = [torch[0], torch[1], torch[2]];
    c.addProbe(at);
    c.setBreakpoint({ id: 'b1', at, on: 'rising', enabled: true });
    c.setLevel(CHEST, 4);
    await studio.save('not');
    const text = save.mock.calls[0]![1].redstoneAuthoring!;
    const corner = blocksMin(studio.canvas)!;
    expect(JSON.parse(text)).toMatchObject({ origin: corner, levels: [{ at: CHEST, level: 4 }] });
    components.dispose();

    // The bot opens the saved design centred on the plot: every block moved alike, here by (+28, 0, +3).
    const d: Pos = [28, 0, 3];
    const moved = studio.canvas.cells().map((x): DesignerCell => [x[0] + d[0], x[1], x[2] + d[2], x[3], x[4]]);
    const again = studioOf({ id: 8, blocks: moved, redstoneAuthoring: text }).studio;
    const reopened = new RedstoneComponentController(again, null);
    expect(reopened.snapshot().components).toEqual([
      { id: 'c1', def: 'gate.not', at: [30, 0, 5], turns: 0, mirror: null },
    ]);
    const store = authoringStoreOf(again);
    const there: Pos = [at[0] + 28, at[1], at[2] + 3];
    expect(store.debugger.probes.map((p) => p.at)).toEqual([there]);
    expect(store.debugger.breakpoints.map((b) => b.at)).toEqual([there]);
    expect(store.debugger.levels).toEqual([{ at: [36, 0, 11], level: 4 }]);
    expect(store.current.origin).toEqual([corner[0] + 28, corner[1], corner[2] + 3]);
    // Opening it is no change, and the controller that binds next gets the moved state.
    expect(again.dirty).toBe(false);
    const sim = controllerOf(again).c;
    bindRedstoneAuthoring(store, sim);
    expect(sim.snapshot().probes.map((p) => p.at)).toEqual([there]);
    expect(again.dirty).toBe(false);
    reopened.dispose();
  });

  it('follows the corner when an edit moves it, and names none when it names no cell', () => {
    const { studio } = studioOf({ redstoneAuthoring: null, blocks: [[8, 0, 8, 54, 2]] });
    const store = authoringStoreOf(studio);
    expect(store.current.origin).toBeUndefined();
    store.setDebugger({ ...store.debugger, probes: [{ id: 'p1', at: [8, 0, 8], label: 'P1', color: 1 }] });
    expect(store.current.origin).toEqual([8, 0, 8]);
    studio.edit([[3, 0, 9, 1, 0]], 'place', 'stone');
    expect(store.current.origin).toEqual([3, 0, 8]);
    studio.undo();
    expect(store.current.origin).toEqual([8, 0, 8]);
    store.setDebugger({ ...store.debugger, probes: [] });
    expect(store.current.origin).toBeUndefined();
    expect(studio.dirty).toBe(false);
  });
});

describe('in the studio page', () => {
  it('the page’s simulation opens with the saved state, and keeps unsaved changes across a new controller', () => {
    const { studio } = studioOf({ redstoneAuthoring: savedText });
    vi.stubGlobal('requestAnimationFrame', () => 0);
    vi.stubGlobal('cancelAnimationFrame', () => undefined);
    const fake = fakeSimulations({});
    const props = {
      enabled: true,
      studio,
      version: '1.12.2' as '1.12.2' | 'b1.8.1',
      world: { setDisplay: vi.fn() } as unknown as StudioWorld,
      mod: { PowerOverlay, createSimulation: fake.create },
      viewer: { current: { engine: () => ({ runtime: { scene: new Scene() } }) } as unknown as WorldViewerHandle },
      stage: { current: document.createElement('div') },
      ready: true,
      fps: 60,
      aim: null,
      look: 'north' as const,
    };
    const hook = renderHook((p: typeof props) => useRedstoneSim(p), { initialProps: props });
    const api = () => hook.result.current.api!;
    expect(api().snapshot().probes).toEqual(saved.probes);
    expect(studio.dirty).toBe(false);

    act(() => api().addProbe([2, 0, 2]));
    expect(studio.dirty).toBe(true);
    const first = api();
    hook.rerender({ ...props, version: 'b1.8.1' });
    expect(api()).not.toBe(first);
    expect(
      api()
        .snapshot()
        .probes.map((p) => p.id),
    ).toEqual(['p3', 'p4']);
    hook.unmount();
  });
});
