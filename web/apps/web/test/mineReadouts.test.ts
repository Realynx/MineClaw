import { act, cleanup, render, renderHook, screen } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MinePanel, type MinePanelProps } from '@/features/studio/MinePanel';
import { Gesture, READOUT_IDLE_MS, useIdleReadout } from '@/features/studio/mine/mineReadouts';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function readout(gesture: Gesture | null = null) {
  let source = 0;
  const compute = vi.fn(() => source);
  const hook = renderHook((p: { rev: number }) => useIdleReadout(compute, [p.rev], gesture), {
    initialProps: { rev: 0 },
  });
  const change = (value: number, rev: number) => {
    source = value;
    hook.rerender({ rev });
  };
  const wait = (ms: number) =>
    act(() => {
      vi.advanceTimersByTime(ms);
    });
  return { ...hook, compute, change, wait };
}

describe('mine readouts wait for idle', () => {
  it('computes once when mounted, then only after the canvas has been still for the idle time', () => {
    const h = readout();
    expect(h.compute).toHaveBeenCalledOnce();
    expect(h.result.current.value).toBe(0);
    h.change(1, 1);
    h.wait(READOUT_IDLE_MS - 50);
    h.change(2, 2);
    h.wait(READOUT_IDLE_MS - 1);
    expect(h.compute).toHaveBeenCalledOnce();
    expect(h.result.current.value).toBe(0);
    h.wait(1);
    expect(h.compute).toHaveBeenCalledTimes(2);
    expect(h.result.current.value).toBe(2);
    h.wait(5_000);
    expect(h.compute).toHaveBeenCalledTimes(2);
  });

  it('never computes while a gesture is held, and computes once after it ends', () => {
    const gesture = new Gesture();
    const h = readout(gesture);
    gesture.set(true);
    h.change(1, 1);
    h.wait(1_000);
    h.change(2, 2);
    h.wait(1_000);
    expect(h.compute).toHaveBeenCalledOnce();
    act(() => {
      gesture.set(false);
    });
    h.wait(READOUT_IDLE_MS - 1);
    expect(h.compute).toHaveBeenCalledOnce();
    h.wait(1);
    expect(h.compute).toHaveBeenCalledTimes(2);
    expect(h.result.current.value).toBe(2);
    // A gesture that changed nothing leaves nothing to compute.
    act(() => {
      gesture.set(true);
      gesture.set(false);
    });
    h.wait(1_000);
    expect(h.compute).toHaveBeenCalledTimes(2);
  });

  it('brings a waiting readout up to date at once when asked (a saved picture), without computing twice', () => {
    const gesture = new Gesture();
    const h = readout(gesture);
    expect(h.result.current.fresh()).toBe(0);
    expect(h.compute).toHaveBeenCalledOnce();
    gesture.set(true);
    h.change(3, 1);
    let shown = -1;
    act(() => {
      shown = h.result.current.fresh();
    });
    expect(shown).toBe(3);
    expect(h.result.current.value).toBe(3);
    act(() => {
      gesture.set(false);
    });
    h.wait(1_000);
    expect(h.compute).toHaveBeenCalledTimes(2);
  });

  it("the mine panel's dig estimate follows the canvas once it is still", () => {
    let dug: Array<readonly [number, number, number]> = [];
    const props = (rev: number): MinePanelProps => ({
      terrain: MineTerrain.for('12345', '1.12.2'),
      version: '1.12.2',
      seed: '12345',
      serverSeed: null,
      preferredFor: null,
      dug: () => dug,
      rev,
      level: 11,
      onLevel: () => undefined,
      xray: false,
      onXray: () => undefined,
      at: null,
      canWrite: true,
      busy: false,
      onSeed: () => undefined,
      onInsert: () => undefined,
      onPreset: () => undefined,
    });
    const view = render(createElement(MinePanel, props(0)));
    expect(screen.getByTestId('mine-stats').textContent).toContain('0 blocks to mine');
    dug = [
      [30, 40, 30],
      [31, 40, 30],
      [32, 40, 30],
    ];
    view.rerender(createElement(MinePanel, props(1)));
    expect(screen.getByTestId('mine-stats').textContent).not.toContain('y 40');
    act(() => {
      vi.advanceTimersByTime(READOUT_IDLE_MS);
    });
    expect(screen.getByTestId('mine-stats').textContent).toContain('y 40–40');
  });
});
