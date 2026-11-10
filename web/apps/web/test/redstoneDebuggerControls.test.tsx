/**
 * The redstone debugger's tabs against a stand-in controller: the run controls (each button's call, the clock in game and
 * redstone ticks, the idle badge, the speed, the events, the keys changed by pressing them), the probes with their live
 * reading, the inspector's decoding, and the help that lists what the simulator leaves out.
 */
import type { Pos } from '@mineclaw/redstone';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DebuggerPanel } from '@/features/studio/redstone/debugger';
import { DEFAULT_KEYS, keyMap, reloadKeyMap } from '@/features/studio/redstone/debugger/debuggerKeys';
import { SimHelp } from '@/features/studio/redstone/SimHelp';
import { SIMPLIFICATIONS } from '@/features/studio/redstone/simplifications';
import type { SimSnapshot } from '@/features/studio/redstone/simController.types';
import { fakeController, sampleOf } from './redstoneDebuggerFixture';

beforeEach(() => {
  localStorage.clear();
  reloadKeyMap();
});
afterEach(() => vi.clearAllMocks());

function mount(tab: 'simulate' | 'probes' | 'inspector', over: Partial<SimSnapshot> = {}, selected: Pos | null = null) {
  const fake = fakeController(over);
  const onJump = vi.fn();
  const aimed = vi.fn((): Pos | null => null);
  const r = render(<DebuggerPanel api={fake.api} tab={tab} aimed={aimed} selected={selected} onJump={onJump} />);
  return {
    ...fake,
    onJump,
    rerender: (sel: Pos | null) =>
      r.rerender(<DebuggerPanel api={fake.api} tab={tab} aimed={aimed} selected={sel} onJump={onJump} />),
  };
}

describe('run controls', () => {
  it('each control calls the controller', () => {
    const h = mount('simulate', { canStepBack: 3 });
    fireEvent.click(screen.getByRole('button', { name: /^Run$/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Step a redstone tick' }));
    fireEvent.click(screen.getByRole('button', { name: 'Step a game tick' }));
    fireEvent.click(screen.getByRole('button', { name: 'Step back' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run until something changes' }));
    fireEvent.click(screen.getByRole('button', { name: 'Back to tick 0' }));
    expect(h.api.start).toHaveBeenCalledTimes(1);
    expect(h.api.stepRedstoneTick).toHaveBeenCalledTimes(1);
    expect(h.api.stepGameTick).toHaveBeenCalledTimes(1);
    expect(h.api.stepBack).toHaveBeenCalledTimes(1);
    expect(h.api.runUntilChange).toHaveBeenCalledTimes(1);
    expect(h.api.reset).toHaveBeenCalledTimes(1);
    expect(h.api.pause).not.toHaveBeenCalled();
  });

  it('run becomes pause while the circuit runs, and follows the snapshot', () => {
    const h = mount('simulate');
    act(() => h.set({ mode: 'running' }));
    fireEvent.click(screen.getByRole('button', { name: /^Pause$/ }));
    expect(h.api.pause).toHaveBeenCalledTimes(1);
    expect(h.api.start).not.toHaveBeenCalled();
    act(() => h.set({ mode: 'paused' }));
    expect(screen.getByRole('button', { name: /^Run$/ })).toBeInTheDocument();
  });

  it('step back needs history and reset a live simulation', () => {
    const h = mount('simulate', { mode: 'edit', canStepBack: 0 });
    expect(screen.getByRole('button', { name: 'Step back' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Back to tick 0' })).toBeDisabled();
    act(() => h.set({ mode: 'paused', canStepBack: 1 }));
    expect(screen.getByRole('button', { name: 'Step back' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Back to tick 0' })).toBeEnabled();
  });

  it('the clock reads in game ticks and redstone ticks, and the idle badge shows only while live', () => {
    const h = mount('simulate', { tick: 7, idle: true, mode: 'edit' });
    expect(screen.getByTestId('sim-clock')).toHaveTextContent('tick 7 gt · 3.5 rt');
    expect(screen.queryByTestId('sim-idle')).toBeNull();
    act(() => h.set({ mode: 'paused', tick: 8 }));
    expect(screen.getByTestId('sim-clock')).toHaveTextContent('tick 8 gt · 4 rt');
    expect(screen.getByTestId('sim-idle')).toHaveTextContent('idle');
    act(() => h.set({ idle: false }));
    expect(screen.queryByTestId('sim-idle')).toBeNull();
  });

  it('offers the speeds and Max, and keeps a saved speed that is not among them', () => {
    const h = mount('simulate');
    const select = screen.getByRole('combobox', { name: 'Speed' });
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['1 gt/s', '2 gt/s', '5 gt/s', '10 gt/s', '20 gt/s', 'Max']);
    fireEvent.change(select, { target: { value: 'max' } });
    fireEvent.change(select, { target: { value: '20' } });
    expect(h.api.setSpeed.mock.calls).toEqual([['max'], [20]]);
    act(() => h.set({ speed: 3 }));
    expect(screen.getByRole('combobox', { name: 'Speed' })).toHaveValue('3');
  });

  it('lists the newest events first, each a way to its cell', () => {
    const h = mount('simulate', {
      events: [
        { kind: 'fired', at: [1, 0, 1], tick: 4 },
        { kind: 'burnout', at: [2, 0, 2], tick: 9, note: 'eighth turn-off' },
      ],
    });
    const items = within(screen.getByTestId('sim-events')).getAllByRole('button');
    expect(items.map((b) => b.textContent)).toEqual(['9 burned out 2, 0, 2 · eighth turn-off', '4 fired 1, 0, 1']);
    fireEvent.click(items[0]!);
    expect(h.onJump).toHaveBeenCalledWith([2, 0, 2]);
  });

  it('a key is changed by pressing the new one, and Escape keeps the old one', () => {
    mount('simulate');
    const change = screen.getByRole('button', { name: 'Change the key for Run or pause' });
    expect(change).toHaveTextContent('F8');
    expect(screen.getByRole('button', { name: /^Run$/ })).toHaveAttribute('title', 'Run (F8)');

    fireEvent.click(change);
    expect(change).toHaveTextContent('press a key');
    const press = new KeyboardEvent('keydown', { key: 'p', bubbles: true, cancelable: true });
    act(() => void window.dispatchEvent(press));
    expect(press.defaultPrevented).toBe(true);
    expect(keyMap().runPause).toEqual({ key: 'P' });
    expect(change).toHaveTextContent(/^P$/);
    expect(screen.getByRole('button', { name: /^Run$/ })).toHaveAttribute('title', 'Run (P)');

    fireEvent.click(change);
    act(() => void window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })));
    expect(keyMap().runPause).toEqual({ key: 'P' });

    fireEvent.click(screen.getByRole('button', { name: 'Back to the usual keys' }));
    expect(keyMap()).toEqual(DEFAULT_KEYS);
  });
});

describe('probes', () => {
  const probes = [
    { id: 'p1', at: [3, 0, 2] as Pos, label: 'A', color: 0x38bdf8 },
    { id: 'p2', at: [8, 0, 7] as Pos, label: 'OUT', color: 0xfacc15 },
  ];

  it('show their power and state now and the last tick they changed, from the history in reach', () => {
    const h = mount('probes', { tick: 6, canStepBack: 4, probes });
    h.samples.set('p1', [sampleOf(2, 0), sampleOf(3, 15), sampleOf(4, 15), sampleOf(5, 14), sampleOf(6, 14)]);
    h.samples.set('p2', [sampleOf(2, 0), sampleOf(3, 0), sampleOf(4, 0), sampleOf(5, 0), sampleOf(6, 0)]);
    act(() => h.set({}));
    expect(screen.getByTestId('probe-p1-power')).toHaveTextContent('14');
    expect(screen.getByTestId('probe-p1')).toHaveTextContent('on');
    expect(screen.getByTestId('probe-p1-changed')).toHaveTextContent('Δ 5');
    expect(screen.getByTestId('probe-p2')).toHaveTextContent('off');
    expect(screen.getByTestId('probe-p2-changed')).toHaveTextContent('');
    // Only what history still holds was asked for.
    expect(h.api.timeline).toHaveBeenLastCalledWith('p2', 2, 6);
  });

  it('are renamed, removed, shown and added on the picked cell', () => {
    const h = mount('probes', { probes }, null);
    expect(screen.getAllByRole('button', { name: /on the picked cell/ })[0]).toBeDisabled();
    const name = screen.getByRole('textbox', { name: 'Probe p1 name' });
    fireEvent.change(name, { target: { value: ' sum ' } });
    fireEvent.keyDown(name, { key: 'Enter' });
    expect(h.api.renameProbe).toHaveBeenCalledWith('p1', ' sum ');
    fireEvent.click(screen.getByRole('button', { name: 'Remove probe OUT' }));
    expect(h.api.removeProbe).toHaveBeenCalledWith('p2');
    fireEvent.click(screen.getByRole('button', { name: 'Show probe A' }));
    expect(h.onJump).toHaveBeenCalledWith([3, 0, 2]);

    h.rerender([9, 0, 9]);
    fireEvent.click(within(screen.getByTestId('watch-list')).getByRole('button', { name: /on the picked cell/ }));
    expect(h.api.addProbe).toHaveBeenCalledWith([9, 0, 9]);
  });

  it('the probes tab also holds the timeline and the breakpoints', () => {
    mount('probes');
    expect(screen.getByTestId('watch-list')).toBeInTheDocument();
    expect(screen.getByTestId('timeline')).toBeInTheDocument();
    expect(screen.getByTestId('breakpoints')).toBeInTheDocument();
    expect(screen.queryByTestId('sim-controls')).toBeNull();
  });
});

describe('inspector', () => {
  const REPEATER: Pos = [4, 0, 2];

  it('asks for a cell, then says when there is no simulation to read yet', () => {
    const h = mount('inspector');
    expect(screen.getByTestId('inspector')).toHaveTextContent('Pick a cell');
    h.rerender(REPEATER);
    expect(screen.getByTestId('inspector')).toHaveTextContent('4, 0, 2: the simulation is still being built');
  });

  it('decodes a locked repeater with its delay, facing, power, pending ticks and the chain that powers it', () => {
    const h = mount('inspector', { tick: 10 });
    h.view.inspection = {
      sample: { tick: 10, power: 15, powered: true, state: 94 * 16 + (1 | (2 << 2)) },
      locked: true,
      pending: [{ dueTick: 14, priority: -2 }],
      explain: [
        { at: REPEATER, why: 'repeater on' },
        { at: [2, 0, 2], why: 'lever switched on' },
      ],
    };
    h.rerender(REPEATER);
    expect(h.api.inspect).toHaveBeenLastCalledWith(REPEATER);
    const panel = screen.getByTestId('inspector');
    expect(screen.getByTestId('inspector-block')).toHaveTextContent('repeater (on) 94:9');
    expect(panel).toHaveTextContent('delay 3 redstone ticks (6 gt)');
    expect(panel).toHaveTextContent('held by a repeater or comparator at its side');
    expect(screen.getByTestId('inspector-power')).toHaveTextContent('15 (on) at tick 10');
    expect(screen.getByTestId('inspector-pending')).toHaveTextContent('tick 14 (in 4 gt), priority -2');
    const chain = within(screen.getByTestId('inspector-explain')).getAllByRole('button');
    expect(chain.map((b) => b.textContent)).toEqual(['4, 0, 2 repeater on', '2, 0, 2 lever switched on']);
    fireEvent.click(chain[1]!);
    expect(h.onJump).toHaveBeenCalledWith([2, 0, 2]);
  });

  it('a b1.8.1 pending tick has no priority, and a comparator shows its mode', () => {
    const h = mount('inspector', { version: 'b1.8.1', tick: 0 });
    h.view.inspection = {
      sample: { tick: 0, power: 0, powered: false, state: 149 * 16 + 4 },
      locked: false,
      pending: [{ dueTick: 2, priority: 0 }],
      explain: [],
    };
    h.rerender([1, 0, 1]);
    expect(screen.getByTestId('inspector')).toHaveTextContent('subtract mode');
    expect(screen.getByTestId('inspector-pending').textContent).toBe('tick 2 (in 2 gt)');
    expect(screen.getByTestId('inspector')).toHaveTextContent('nothing');
  });

  it('a container takes its start level in edit mode', () => {
    const CHEST: Pos = [8, 0, 8];
    const h = mount('inspector', { mode: 'edit', levels: [{ at: CHEST, level: 6 }] });
    h.view.inspection = {
      sample: { tick: 0, power: 0, powered: false, state: 54 * 16 },
      locked: false,
      pending: [],
      explain: [],
    };
    h.rerender(CHEST);
    const input = screen.getByRole('spinbutton', { name: 'Start level' });
    expect(input).toHaveValue(6);
    fireEvent.change(input, { target: { value: '11' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(h.api.setLevel).toHaveBeenCalledWith(CHEST, 11);
    act(() => h.set({ mode: 'paused' }));
    expect(screen.getByRole('spinbutton', { name: 'Level now' })).toBeInTheDocument();
  });
});

describe('help', () => {
  it('lists the nine stated simplifications and says how faithful the simulator is', () => {
    render(<SimHelp />);
    const button = screen.getByRole('button', { name: 'What the simulator leaves out' });
    expect(button).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(button);
    const help = screen.getByRole('dialog', { name: 'What the simulator leaves out' });
    expect(within(help).getAllByRole('listitem')).toHaveLength(9);
    expect(SIMPLIFICATIONS).toHaveLength(9);
    expect(help).toHaveTextContent('exact to the game tick');
    expect(help).toHaveTextContent('flagged in the checks, not emulated');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
