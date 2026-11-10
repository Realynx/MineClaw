/**
 * The redstone torch: an inverter with a 2 game tick delay. It is off while the block it hangs or stands on is
 * powered. Lit, it powers every cell around it except that block, and strongly powers the block above it.
 *
 * The versions differ in when a torch asks for its tick. 1.12.2 asks only when its state disagrees with its block.
 * b1.8.1 asks on every neighbour update, whatever the state, and a lit and an unlit torch are different blocks to its
 * scheduler: a torch whose tick is already waiting for an unrelated reason answers a change of its block in less than
 * 2 game ticks.
 *
 * Burnout: every turn-off is logged (one log for the whole world, trimmed whenever any torch ticks). The turn-off that
 * makes it `limit` entries for one torch within the window burns the torch out: it stays off while that many entries
 * remain. 1.12.2 then asks for one more tick 160 game ticks later; b1.8.1 leaves it until something updates it.
 */
import { DOWN, EAST, NORTH, SOUTH, UP, WEST, opposite, torchSupport, type Dir } from '../orientation.js';
import { powerFrom } from '../power.js';
import type { ComponentHandler, Store, World } from './handler.js';

const UNLIT = 75;
const LIT = 76;
const DELAY = 2;

/** The order each version walks the six neighbours when a torch tells the cells around every one of them. */
const RING_BETA: readonly Dir[] = [DOWN, UP, WEST, EAST, NORTH, SOUTH];
const RING_MODERN: readonly Dir[] = [DOWN, UP, NORTH, SOUTH, WEST, EAST];

/** The turn-off log of a world: cell and game tick in pairs, oldest first. */
class BurnoutLog implements Store {
  private entries: number[] = [];

  add(at: number, tick: number): void {
    this.entries.push(at, tick);
  }

  /** Drops the entries older than `window` game ticks from the head. */
  trim(now: number, window: number): void {
    let drop = 0;
    while (drop < this.entries.length && now - this.entries[drop + 1]! > window) drop += 2;
    if (drop > 0) this.entries.splice(0, drop);
  }

  count(at: number): number {
    let n = 0;
    for (let i = 0; i < this.entries.length; i += 2) if (this.entries[i] === at) n++;
    return n;
  }

  save(): number[] {
    return this.entries.slice();
  }

  load(saved: unknown): void {
    this.entries = (saved as number[]).slice();
  }

  rebase(by: number): void {
    for (let i = 1; i < this.entries.length; i += 2) this.entries[i]! -= by;
  }
}

const logOf = (w: World): BurnoutLog => w.state('torch.burnout', () => new BurnoutLog());

/** Whether the torch's block is powered, which is when the torch should be off. */
function blocked(w: World, at: number, state: number): boolean {
  const dir = torchSupport(state & 15);
  return powerFrom(w, at + w.step[dir]!, opposite(dir)) > 0;
}

/** Sets the torch's new state and tells the cells around its six neighbours, then the neighbours themselves. */
function flip(w: World, at: number, next: number): void {
  const id = next >> 4;
  w.set(at, next);
  for (const d of w.profile.modern ? RING_MODERN : RING_BETA) w.notifyAround(at + w.step[d]!, id);
  w.notifyAround(at, id);
}

export const torch: ComponentHandler = {
  name: 'Redstone torch',
  ids: () => [UNLIT, LIT],
  valid: (state) => torchSupport(state & 15) >= 0,
  normalize: (state) => (LIT << 4) | (state & 15),
  supported: (w, at, state) => {
    const dir = torchSupport(state & 15);
    const block = w.cells[at + w.step[dir]!]!;
    return dir === DOWN ? w.profile.holdsTorch(block) : w.profile.holdsOnFace(block, opposite(dir));
  },
  weak: (_w, _at, state, toward) => (state >> 4 === LIT && toward !== torchSupport(state & 15) ? 15 : 0),
  strong: (_w, _at, state, toward) => (state >> 4 === LIT && toward === UP ? 15 : 0),
  init: (w) => void logOf(w),
  update: (w, at, state) => {
    if (!w.profile.modern || (state >> 4 === LIT) === blocked(w, at, state)) w.schedule(at, DELAY);
  },
  tick: (w, at, state) => {
    const { limit, window, retry } = w.profile.burnout;
    const log = logOf(w);
    const off = blocked(w, at, state);
    log.trim(w.tick, window);
    const meta = state & 15;
    if (state >> 4 === LIT) {
      if (!off) return;
      flip(w, at, (UNLIT << 4) | meta);
      log.add(at, w.tick);
      if (log.count(at) < limit) return;
      w.event('burnout', at);
      if (retry > 0) w.schedule(at, retry);
    } else if (!off && log.count(at) < limit) flip(w, at, (LIT << 4) | meta);
  },
  // 1.12.2 counts a lit and an unlit torch as one block for its scheduled ticks; b1.8.1 does not.
  tickKey: (id, v) => (v === '1.12.2' ? LIT : id),
  power: (_w, _at, state) => (state >> 4 === LIT ? 15 : 0),
};
