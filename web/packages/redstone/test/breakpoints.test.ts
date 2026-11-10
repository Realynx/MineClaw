import { describe, expect, it } from 'vitest';
import type { BreakOn, Breakpoint, Pos } from '../src/index.js';
import { breakpointHit, type CellValue } from '../src/breakpoints.js';

const AT: Pos = [4, 1, 2];
const DUST_OFF = (55 << 4) | 0;
const DUST_ON = (55 << 4) | 15;
const bp = (on: BreakOn, value?: number): Breakpoint => ({
  id: 'b',
  at: AT,
  on,
  enabled: true,
  ...(value === undefined ? {} : { value }),
});
const cell = (state: number, power: number): CellValue => ({ state, power });

describe('the breakpoint rule', () => {
  it('rising and falling look at power crossing zero and report the power', () => {
    expect(breakpointHit(bp('rising'), cell(DUST_OFF, 0), cell(DUST_ON, 9), 7)).toEqual({
      id: 'b',
      tick: 7,
      at: AT,
      before: 0,
      after: 9,
    });
    expect(breakpointHit(bp('rising'), cell(DUST_ON, 3), cell(DUST_ON, 9), 7)).toBeNull();
    expect(breakpointHit(bp('rising'), cell(DUST_ON, 9), cell(DUST_OFF, 0), 7)).toBeNull();
    expect(breakpointHit(bp('falling'), cell(DUST_ON, 9), cell(DUST_OFF, 0), 8)).toEqual({
      id: 'b',
      tick: 8,
      at: AT,
      before: 9,
      after: 0,
    });
    expect(breakpointHit(bp('falling'), cell(DUST_ON, 9), cell(DUST_ON, 3), 8)).toBeNull();
  });

  it('atLeast hits when the power crosses its level from below, once', () => {
    expect(breakpointHit(bp('atLeast', 8), cell(DUST_ON, 7), cell(DUST_ON, 8), 3)).toEqual({
      id: 'b',
      tick: 3,
      at: AT,
      before: 7,
      after: 8,
    });
    expect(breakpointHit(bp('atLeast', 8), cell(DUST_ON, 8), cell(DUST_ON, 12), 3)).toBeNull();
    expect(breakpointHit(bp('atLeast', 8), cell(DUST_OFF, 0), cell(DUST_ON, 7), 3)).toBeNull();
    expect(breakpointHit(bp('atLeast'), cell(DUST_OFF, 0), cell(DUST_ON, 15), 3)).toBeNull();
  });

  it('change hits when the shown state or the power differs and reports the shown state', () => {
    expect(breakpointHit(bp('change'), cell(DUST_OFF, 0), cell(DUST_ON, 15), 2)).toEqual({
      id: 'b',
      tick: 2,
      at: AT,
      before: DUST_OFF,
      after: DUST_ON,
    });
    const stone = 1 << 4;
    expect(breakpointHit(bp('change'), cell(stone, 0), cell(stone, 15), 2)).toEqual({
      id: 'b',
      tick: 2,
      at: AT,
      before: stone,
      after: stone,
    });
    expect(breakpointHit(bp('change'), cell(stone, 4), cell(stone, 4), 2)).toBeNull();
  });

  it('state hits when the shown state becomes the given one', () => {
    const lit = (76 << 4) | 5;
    const unlit = (75 << 4) | 5;
    expect(breakpointHit(bp('state', unlit), cell(lit, 15), cell(unlit, 0), 6)).toEqual({
      id: 'b',
      tick: 6,
      at: AT,
      before: lit,
      after: unlit,
    });
    expect(breakpointHit(bp('state', unlit), cell(unlit, 0), cell(unlit, 0), 6)).toBeNull();
    expect(breakpointHit(bp('state', unlit), cell(unlit, 0), cell(lit, 15), 6)).toBeNull();
    expect(breakpointHit(bp('state'), cell(lit, 15), cell(unlit, 0), 6)).toBeNull();
    expect(breakpointHit(bp('state', 0), cell(lit, 15), cell(0, 0), 6)).toEqual({
      id: 'b',
      tick: 6,
      at: AT,
      before: lit,
      after: 0,
    });
  });

  it('a disabled breakpoint never hits', () => {
    for (const on of ['change', 'rising', 'falling', 'atLeast', 'state'] as const)
      expect(breakpointHit({ ...bp(on, 1), enabled: false }, cell(DUST_OFF, 0), cell(DUST_ON, 15), 1)).toBeNull();
    expect(breakpointHit({ ...bp('falling'), enabled: false }, cell(DUST_ON, 15), cell(DUST_OFF, 0), 1)).toBeNull();
  });
});
