/**
 * The debugger's breakpoints against a stand-in controller: the list with its on/off toggles, the condition and the value
 * it needs, removing, adding on the picked cell, and the hits that stopped the last step, each a way to its cell.
 */
import type { Breakpoint, Pos } from '@mineclaw/redstone';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Breakpoints, parsePower, parseState, stateText } from '@/features/studio/redstone/debugger/Breakpoints';
import { reloadKeyMap } from '@/features/studio/redstone/debugger/debuggerKeys';
import type { SimSnapshot } from '@/features/studio/redstone/simController.types';
import { fakeController } from './redstoneDebuggerFixture';

const OUT: Pos = [8, 0, 7];
const TORCH: Pos = [4, 0, 2];
const rising: Breakpoint = { id: 'b1', at: OUT, on: 'rising', enabled: true };
const atLeast: Breakpoint = { id: 'b2', at: TORCH, on: 'atLeast', value: 8, enabled: false };

beforeEach(() => {
  localStorage.clear();
  reloadKeyMap();
});

function mount(over: Partial<SimSnapshot> = {}, selected: Pos | null = null) {
  const fake = fakeController({ breakpoints: [rising, atLeast], ...over });
  const onJump = vi.fn();
  const view = (sel: Pos | null) => (
    <Breakpoints api={fake.api} snap={fake.api.snapshot()} selected={sel} onJump={onJump} />
  );
  const r = render(view(selected));
  return { ...fake, onJump, rerender: (sel: Pos | null = selected) => r.rerender(view(sel)) };
}

describe('the breakpoint list', () => {
  it('shows each breakpoint with its cell, condition and whether it is on', () => {
    mount();
    expect(screen.getByRole('checkbox', { name: 'Breakpoint b1 on' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Breakpoint b2 on' })).not.toBeChecked();
    expect(screen.getByRole('combobox', { name: 'Breakpoint b1 condition' })).toHaveValue('rising');
    expect(screen.getByRole('combobox', { name: 'Breakpoint b2 condition' })).toHaveValue('atLeast');
    expect(screen.queryByRole('textbox', { name: 'Breakpoint b1 value' })).toBeNull();
    expect(screen.getByRole('textbox', { name: 'Breakpoint b2 value' })).toHaveValue('8');
  });

  it('a toggle switches a breakpoint on or off and keeps the rest of it', () => {
    const h = mount();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Breakpoint b1 on' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Breakpoint b2 on' }));
    expect(h.api.setBreakpoint.mock.calls).toEqual([[{ ...rising, enabled: false }], [{ ...atLeast, enabled: true }]]);
  });

  it('a new condition drops the old value, which meant something else', () => {
    const h = mount();
    fireEvent.change(screen.getByRole('combobox', { name: 'Breakpoint b2 condition' }), { target: { value: 'state' } });
    expect(h.api.setBreakpoint).toHaveBeenLastCalledWith({ id: 'b2', at: TORCH, on: 'state', enabled: false });
    fireEvent.change(screen.getByRole('combobox', { name: 'Breakpoint b1 condition' }), {
      target: { value: 'falling' },
    });
    expect(h.api.setBreakpoint).toHaveBeenLastCalledWith({ ...rising, on: 'falling' });
  });

  it('takes a power level for "at least" and an id:meta for "state", and refuses anything else', () => {
    const h = mount({
      breakpoints: [{ ...atLeast }, { id: 'b3', at: OUT, on: 'state', enabled: true }],
    });
    const power = screen.getByRole('textbox', { name: 'Breakpoint b2 value' });
    fireEvent.change(power, { target: { value: '16' } });
    fireEvent.blur(power);
    fireEvent.change(power, { target: { value: '12' } });
    fireEvent.keyDown(power, { key: 'Enter' });
    expect(h.api.setBreakpoint.mock.calls).toEqual([[{ ...atLeast, value: 12 }]]);

    expect(screen.getByTestId('breakpoint-b3')).toHaveTextContent('needs a value to hit');
    const state = screen.getByRole('textbox', { name: 'Breakpoint b3 value' });
    fireEvent.change(state, { target: { value: '76:5' } });
    fireEvent.blur(state);
    expect(h.api.setBreakpoint).toHaveBeenLastCalledWith({
      id: 'b3',
      at: OUT,
      on: 'state',
      enabled: true,
      value: 76 * 16 + 5,
    });
  });

  it('removes a breakpoint and jumps to its cell', () => {
    const h = mount();
    fireEvent.click(screen.getByRole('button', { name: 'Remove breakpoint b2' }));
    expect(h.api.removeBreakpoint).toHaveBeenCalledWith('b2');
    fireEvent.click(within(screen.getByTestId('breakpoint-b1')).getByRole('button', { name: /8, 0, 7/ }));
    expect(h.onJump).toHaveBeenCalledWith(OUT);
  });

  it('adds "any change" on the picked cell with the next free id', () => {
    const h = mount({}, null);
    const add = screen.getByRole('button', { name: /on the picked cell/ });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Pick a cell first (or aim at one and press F9)');
    h.rerender([1, 2, 3]);
    fireEvent.click(add);
    expect(h.api.setBreakpoint).toHaveBeenCalledWith({ id: 'b3', at: [1, 2, 3], on: 'change', enabled: true });
  });

  it('says how to add one when there is none', () => {
    mount({ breakpoints: [] });
    expect(screen.getByTestId('breakpoints')).toHaveTextContent('None yet. Aim at a cell and press F9');
  });
});

describe('hits', () => {
  it('lists the hits of the last step with their values, marks their breakpoints and jumps to the cell', () => {
    const h = mount({
      breakpoints: [rising, { id: 'b4', at: TORCH, on: 'change', enabled: true }],
      lastHits: [
        { id: 'b1', tick: 12, at: OUT, before: 0, after: 15 },
        { id: 'b4', tick: 12, at: TORCH, before: 76 * 16 + 1, after: 75 * 16 + 1 },
      ],
    });
    const hits = screen.getByTestId('breakpoint-hits');
    expect(hits).toHaveTextContent('Stopped at tick 12');
    const rows = within(hits).getAllByRole('button');
    expect(rows.map((b) => b.textContent)).toEqual(['b1 8, 0, 7 · power 0 → 15', 'b4 4, 0, 2 · 76:1 → 75:1']);
    expect(screen.getByTestId('breakpoint-b1').className).toContain('border-danger');
    fireEvent.click(rows[0]!);
    expect(h.onJump).toHaveBeenCalledWith(OUT);
  });

  it('are gone once a step stops on none', () => {
    const h = mount({ lastHits: [{ id: 'b1', tick: 3, at: OUT, before: 0, after: 1 }] });
    expect(screen.getByTestId('breakpoint-hits')).toBeInTheDocument();
    act(() => h.set({ lastHits: [] }));
    h.rerender();
    expect(screen.queryByTestId('breakpoint-hits')).toBeNull();
  });
});

describe('values', () => {
  it('reads and writes shown states and power levels', () => {
    expect(stateText(76 * 16 + 5)).toBe('76:5');
    expect(parseState('76:5')).toBe(76 * 16 + 5);
    expect(parseState(' 55 ')).toBe(55 * 16);
    expect(parseState('-1')).toBe(-16);
    expect([parseState('76:16'), parseState('x'), parseState('1:2:3')]).toEqual([null, null, null]);
    expect([parsePower('1'), parsePower('15'), parsePower('0'), parsePower('16'), parsePower('2.5')]).toEqual([
      1,
      15,
      null,
      null,
      null,
    ]);
  });
});
