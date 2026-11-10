/**
 * Stepping back (report 4.4): a ring of keyframes, each a full copy of the engine every `interval` game ticks, plus the
 * log of the inputs each game tick began with. A rewind loads the newest keyframe at or before the target tick and runs
 * the same engine forward again with the logged inputs, so the state it reaches is exactly the one the clock passed
 * through: no reverse rule has to be right for that. Breakpoints, samples and events belong to the caller and are
 * left out of a replay.
 */
import type { InputEvent } from './api.js';
import type { Engine, Keyframe } from './engine.js';

/** What the keyframes of one simulation may hold, roughly: the interval grows with the engine's box to stay under it. */
const BUDGET_BYTES = 4 * 1024 * 1024;
/** A keyframe's stores (pending ticks, logs) on top of its cells, as a rough allowance. */
const STORE_BYTES = 4096;

export class History {
  /** Game ticks between two keyframes. */
  readonly interval: number;
  /** Oldest first; the first one is the oldest tick a rewind can reach. */
  private frames: Keyframe[] = [];
  /** The inputs waiting at the start of each game tick, by the tick they began (only ticks that had any). */
  private readonly inputs = new Map<number, readonly InputEvent[]>();

  constructor(
    /** How many game ticks can be stepped back at most; 0 keeps nothing. */
    readonly depth: number,
    engine: Engine,
    start: Keyframe,
  ) {
    const bytes = engine.cells.byteLength + STORE_BYTES;
    this.interval = Math.max(1, Math.min(Math.max(depth, 1), Math.ceil((depth * bytes) / BUDGET_BYTES)));
    this.restart(start);
  }

  /** Game ticks that can be stepped back from `tick`. */
  reach(tick: number): number {
    const oldest = this.frames[0];
    return oldest ? Math.max(0, Math.min(this.depth, tick - oldest.tick)) : 0;
  }

  /** Forgets everything and starts again from `start` (a reset). */
  restart(start: Keyframe): void {
    this.frames = this.depth > 0 ? [start] : [];
    this.inputs.clear();
  }

  /** Call just before a game tick runs: the inputs it begins with. */
  beginning(tick: number, queued: readonly InputEvent[]): void {
    if (this.depth > 0 && queued.length > 0) this.inputs.set(tick, queued.slice());
  }

  /** Call once a game tick has run: a keyframe when one is due, and what is now out of reach is let go. */
  ended(engine: Engine): void {
    if (this.depth <= 0) return;
    const tick = engine.tick;
    if (tick % this.interval !== 0) return;
    this.frames.push(engine.save());
    while (this.frames.length >= 2 && this.frames[1]!.tick <= tick - this.depth) this.frames.shift();
    const oldest = this.frames[0]!.tick;
    for (const t of this.inputs.keys()) if (t <= oldest) this.inputs.delete(t);
  }

  /**
   * Puts the engine back at `target` (within reach) by replaying from the keyframe before it, and drops every keyframe
   * and input after it, the inputs still queued included.
   */
  rewind(engine: Engine, target: number): void {
    if (target < engine.tick) {
      let n = this.frames.length - 1;
      while (n > 0 && this.frames[n]!.tick > target) n--;
      const frame = this.frames[n];
      if (!frame || frame.tick > target) throw new RangeError(`@mineclaw/redstone: tick ${target} is out of reach`);
      this.frames.length = n + 1;
      engine.load(frame);
      while (engine.tick < target) {
        engine.requeue(this.inputs.get(engine.tick + 1) ?? []);
        engine.stepTick();
      }
      engine.takeEvents();
    }
    for (const t of this.inputs.keys()) if (t > target) this.inputs.delete(t);
    engine.requeue([]);
  }
}
