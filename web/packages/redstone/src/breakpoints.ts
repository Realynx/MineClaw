/**
 * The breakpoint rule in one place. The engine and the scripted test double both decide hits with it, so code that was
 * tested against the double stops on the same ticks with the engine.
 */
import type { Breakpoint, BreakpointHit } from './api.js';

/** The two values of a cell a breakpoint can look at. */
export interface CellValue {
  state: number;
  power: number;
}

/**
 * The hit of `b` for a cell that went from `before` (the end of the previous game tick) to `after` (the end of game tick
 * `tick`), or null. A disabled breakpoint never hits, and neither does one that needs a `value` it does not have.
 */
export function breakpointHit(b: Breakpoint, before: CellValue, after: CellValue, tick: number): BreakpointHit | null {
  if (!b.enabled) return null;
  const power = (hit: boolean): BreakpointHit | null =>
    hit ? { id: b.id, tick, at: b.at, before: before.power, after: after.power } : null;
  const state = (hit: boolean): BreakpointHit | null =>
    hit ? { id: b.id, tick, at: b.at, before: before.state, after: after.state } : null;
  switch (b.on) {
    case 'change':
      return state(before.state !== after.state || before.power !== after.power);
    case 'rising':
      return power(before.power === 0 && after.power > 0);
    case 'falling':
      return power(before.power > 0 && after.power === 0);
    case 'atLeast':
      return power(b.value !== undefined && before.power < b.value && after.power >= b.value);
    case 'state':
      return state(b.value !== undefined && before.state !== b.value && after.state === b.value);
  }
}
