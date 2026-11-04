import { StoryColonyOptions, StoryOptionsRequest, type StoryPawn } from '@mineclaw/contracts';
import { StorySim } from '@mineclaw/contracts/testing';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { slowestSpeed } from '@/features/story/model';
import { SPEED_BUTTONS, SpeedControl } from '@/features/story/StoryHud';
import { keyAction } from '@/features/story/tools';

afterEach(cleanup);

const pawn = (name: string, extra: Partial<StoryPawn> = {}): StoryPawn => ({
  name,
  display: name,
  kind: 'pawn',
  online: true,
  state: 'idle',
  queue: [],
  skills: {},
  traits: [],
  priorities: {},
  schedule: [],
  inventory: [],
  ...extra,
});

describe('the speed control (▶ / ▶▶ / ▶▶▶)', () => {
  it('shows the three levels with the current one checked and tooltips that say it is a pawn movement boost', () => {
    const onSpeed = vi.fn();
    render(<SpeedControl speed="fast" canWrite onSpeed={onSpeed} />);
    const radios = screen.getAllByRole('radio');
    expect(radios.map((r) => r.textContent)).toEqual(['▶', '▶▶', '▶▶▶']);
    expect(radios.map((r) => r.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false']);
    expect(screen.getByRole('radio', { name: 'Faster' }).title).toMatch(/movement-speed boost for the colony's pawns/);
    expect(screen.getByRole('radio', { name: 'Fast' }).title).toMatch(/\(2\)/);
    fireEvent.click(screen.getByRole('radio', { name: 'Faster' }));
    expect(onSpeed).toHaveBeenCalledWith('faster');
  });

  it('is read-only without write access and marks a level the server stepped pawns down to', () => {
    const onSpeed = vi.fn();
    render(<SpeedControl speed="faster" effective="fast" canWrite={false} onSpeed={onSpeed} />);
    const fast = screen.getByRole('radio', { name: 'Fast' });
    expect(fast.className).toMatch(/ring-warn/);
    expect(fast.title).toMatch(/server rejected faster movement/);
    fireEvent.click(fast);
    expect(onSpeed).not.toHaveBeenCalled();
    expect(SPEED_BUTTONS.map((b) => b.key)).toEqual(['1', '2', '3']);
  });

  it('finds the slowest level a pawn was stepped down to', () => {
    const at = (level: 'normal' | 'fast' | 'faster', multiplier: number, requested: number) => ({
      level,
      multiplier,
      requested,
      serverFactor: 1,
      rollbacks: 0,
    });
    expect(slowestSpeed([pawn('a', { speed: at('faster', 3, 3) })])).toBeNull();
    expect(slowestSpeed([pawn('a', { speed: at('fast', 2, 3) }), pawn('b', { speed: at('faster', 3, 3) })])).toBe(
      'fast',
    );
    expect(slowestSpeed([pawn('a', { speed: at('fast', 2, 3) }), pawn('b', { speed: at('normal', 1, 3) })])).toBe(
      'normal',
    );
    expect(slowestSpeed([pawn('a', { online: false, speed: at('normal', 1, 3) })])).toBeNull();
  });
});

describe('speed keys', () => {
  it('1 / 2 / 3 set the speed with no tool in hand', () => {
    expect(keyAction({ code: 'Digit1', key: '1' }, false, false, false, true)).toBe('speed:normal');
    expect(keyAction({ code: 'Digit2', key: '2' }, false, false, false, true)).toBe('speed:fast');
    expect(keyAction({ code: 'Numpad3', key: '3' }, false, false, false, true)).toBe('speed:faster');
    expect(keyAction({ code: 'Digit4', key: '4' }, false, false, false, true)).toBeNull();
    expect(keyAction({ code: 'Digit2', key: '@', shiftKey: true }, false, false, false, true)).toBeNull();
  });

  it("stay the tool's keys while a tool is in hand", () => {
    // A designate tool (no number keys of its own): nothing.
    expect(keyAction({ code: 'Digit2', key: '2' }, false, false, false, false)).toBeNull();
    // Mine / Deconstruct: the height modes.
    expect(keyAction({ code: 'Digit2', key: '2' }, false, true, false, true)).toBe('height:level');
    // A structure tool: the recent materials.
    expect(keyAction({ code: 'Digit3', key: '3' }, true, false, true, true)).toBe('recent:3');
    // Space still pauses.
    expect(keyAction({ code: 'Space', key: ' ' }, false, false, false, true)).toBe('pause');
  });
});

describe('the speed option', () => {
  it('defaults to normal with the Speed effect on, and the request takes only the three levels', () => {
    const o = StoryColonyOptions.parse({});
    expect(o.speed).toBe('normal');
    expect(o.speedEffect).toBe(true);
    expect(StoryOptionsRequest.safeParse({ speed: 'faster' }).success).toBe(true);
    expect(StoryOptionsRequest.safeParse({ speed: 'warp' }).success).toBe(false);
  });

  it('the mock sets every online pawn to the multiplier', () => {
    const sim = new StorySim({ world: { blockAt: () => [0, 0], ground: () => 63, entities: () => [] }, timeRateMs: 0 });
    const { id } = sim.create({ name: 'Speed', pawns: 3 });
    expect(sim.options(id, { speed: 'faster' }).speed).toBe('faster');
    const state = sim.state(id);
    expect(state.options?.speed).toBe('faster');
    expect(state.pawns.filter((p) => p.online !== false).every((p) => p.speed?.multiplier === 3)).toBe(true);
  });
});
