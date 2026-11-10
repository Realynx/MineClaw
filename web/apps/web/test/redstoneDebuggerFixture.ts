/**
 * A stand-in for the redstone simulation controller, for the debugger's tests: every `SimControllerApi` method is a spy,
 * the snapshot is whatever the test sets (listeners hear of each change), and the timeline and the inspector answer from
 * data the test gives.
 */
import type { Sample } from '@mineclaw/redstone';
import { vi } from 'vitest';
import {
  DEFAULT_DISPLAY_HZ,
  DEFAULT_SIM_SPEED,
  type CellInspection,
  type SimControllerApi,
  type SimSnapshot,
} from '@/features/studio/redstone/simController.types';

export const snapshotOf = (over: Partial<SimSnapshot> = {}): SimSnapshot => ({
  mode: 'paused',
  tick: 0,
  idle: false,
  speed: DEFAULT_SIM_SPEED,
  displayHz: DEFAULT_DISPLAY_HZ,
  displayHzNow: DEFAULT_DISPLAY_HZ,
  canStepBack: 0,
  probes: [],
  breakpoints: [],
  levels: [],
  lastHits: [],
  events: [],
  diagnostics: [],
  version: '1.12.2',
  ...over,
});

/** A sample as the engine gives it: `powered` follows the power. */
export const sampleOf = (tick: number, power: number, state = 55 * 16 + power): Sample => ({
  tick,
  power,
  powered: power > 0,
  state,
});

export function fakeController(over: Partial<SimSnapshot> = {}) {
  let snap = snapshotOf(over);
  const listeners = new Set<() => void>();
  const samples = new Map<string, Sample[]>();
  const view = { inspection: null as CellInspection | null };
  const api = {
    subscribe: vi.fn((fn: () => void) => {
      listeners.add(fn);
      return () => void listeners.delete(fn);
    }),
    snapshot: vi.fn(() => snap),
    start: vi.fn(),
    pause: vi.fn(),
    stop: vi.fn(),
    stepGameTick: vi.fn(),
    stepRedstoneTick: vi.fn(),
    stepBack: vi.fn(),
    stepBackTo: vi.fn(),
    runUntilChange: vi.fn(),
    reset: vi.fn(),
    setSpeed: vi.fn(),
    setDisplayHz: vi.fn(),
    click: vi.fn(() => false),
    setLevel: vi.fn(),
    addProbe: vi.fn(),
    removeProbe: vi.fn(),
    renameProbe: vi.fn(),
    setBreakpoint: vi.fn(),
    removeBreakpoint: vi.fn(),
    timeline: vi.fn((id: string, from: number, to: number) =>
      (samples.get(id) ?? []).filter((s) => s.tick >= from && s.tick <= to),
    ),
    inspect: vi.fn(() => view.inspection),
  } satisfies SimControllerApi;
  return {
    api,
    samples,
    view,
    /** Changes the snapshot and tells the listeners, as the controller does when it publishes. */
    set(patch: Partial<SimSnapshot>) {
      snap = { ...snap, ...patch };
      for (const l of listeners) l();
    },
  };
}
