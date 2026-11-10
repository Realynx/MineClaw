/**
 * Pistons and sticky pistons, their heads and the blocks they move.
 *
 * Power: a piston is powered from any side but its face, and also when the cell above it would be (quasi-connectivity,
 * twelve checks in all). It looks only when it hears an update, so a piston that becomes powered through the cell
 * above without hearing of it stays where it is until something updates it.
 *
 * Starting a move is a block event. b1.8.1 runs it at once, inside the update that asked for it, and marks the piston
 * extended before; while it runs, every piston of the same kind ignores the updates it hears (one flag per kind in the
 * game). 1.12.2 queues it for the end of the tick, after the scheduled ticks, and runs the queue in rounds: what a round
 * queues runs in the next round of the same tick, an event equal to one already waiting is dropped, and the piston looks
 * at its power again when its event runs.
 *
 * A move carries each block one cell; a cell in transit holds a moving block (not solid, no power, immovable) that shows
 * what it carries. A move lands on its third update in the moving-blocks phase; one made in a tick's moving-blocks phase
 * gets its first update in the next tick. A sticky piston that retracts while the block it pushed is still in transit
 * leaves that block where it is going (the "drop" on a 2 game tick pulse).
 *
 * Who hears what, by version (b1.8.1 read from mc-dev `BlockPiston`, `BlockPistonExtension`, `BlockPistonMoving` and
 * `TileEntityPiston`): b1.8.1 writes the moved cells, and a retracting piston's own cell, without telling anyone; an
 * extending piston's new state, every block it clears and every landing tell their six neighbours, and the per-kind flag
 * is lowered while a retracting piston clears its head or the block it pulls. 1.12.2 tells the neighbours of the piston,
 * of every cell a block left, of the head's cell and of a broken block, and a block that lands updates itself too. In
 * both, a head passes the updates it hears to its piston and goes when its piston is gone.
 *
 * Simplified: no partial positions, entities or items; slime moves as a plain block (and is reported); a push that would
 * leave the plot fails (and is reported). Which blocks are immovable or destroyed is our own table of the game's rules.
 */
import { INERT } from '../grid.js';
import { DOWN, UP, opposite, type Dir } from '../orientation.js';
import { powerFrom } from '../power.js';
import { UPDATE_ORDER_TEXT, outOfPlotText, slimeText } from '../diagnostics.js';
import { dustTelling } from '../wire.js';
import type { ComponentHandler, Store, World } from './handler.js';
import { idOf, replace } from './util.js';

const STICKY = 29;
const PISTON = 33;
const HEAD = 34;
const MOVING = 36;
const SLIME = 165;
const EXTENDED = 8;
const STICKY_HEAD = 8;
const PUSH_LIMIT = 12;

const range = (a: number, b: number): number[] => Array.from({ length: b - a + 1 }, (_, i) => a + i);

/** Blocks a piston can neither push nor pull: unbreakable ones, obsidian, piston parts and blocks with stored data. */
const IMMOVABLE: readonly number[] = [
  7, 23, 25, 26, 34, 36, 49, 52, 54, 61, 62, 63, 68, 84, 90, 116, 117, 119, 120, 130, 137, 138, 144, 145, 146, 151, 154,
  158, 166, 176, 177, 178, 209, 210, 211, 255,
];
/** Blocks a piston breaks instead of pushing: plants, liquids, thin parts, doors, gourds, leaves. */
const BREAKS: readonly number[] = [
  6,
  8,
  9,
  10,
  11,
  18,
  27,
  28,
  30,
  31,
  32,
  37,
  38,
  39,
  40,
  50,
  51,
  55,
  59,
  64,
  65,
  66,
  69,
  70,
  71,
  72,
  75,
  76,
  77,
  78,
  81,
  83,
  86,
  91,
  92,
  93,
  94,
  103,
  104,
  105,
  106,
  111,
  115,
  122,
  127,
  131,
  132,
  140,
  141,
  142,
  143,
  147,
  148,
  149,
  150,
  157,
  161,
  171,
  175,
  193,
  194,
  195,
  196,
  197,
  199,
  200,
  207,
  ...range(219, 234),
];
/** Blocks a piston pushes but a sticky piston does not pull (1.12.2 glazed terracotta). */
const PUSH_ONLY: readonly number[] = range(235, 250);

/** What a push does to a block: moves it, breaks it, cannot move it, or moves it but cannot pull it back. */
const NORMAL = 0;
const BREAK = 1;
const IMMOVABLE_BLOCK = 2;
const PUSHED_ONLY = 3;

const REACTION = new Uint8Array(256);
for (const id of IMMOVABLE) REACTION[id] = IMMOVABLE_BLOCK;
for (const id of BREAKS) REACTION[id] = BREAK;
for (const id of PUSH_ONLY) REACTION[id] = PUSHED_ONLY;

const isPiston = (id: number): boolean => id === STICKY || id === PISTON;

/** What a push does to the block in a state: the ground does not move, an extended piston does not either. */
function reactionOf(state: number): number {
  if (state < 0) return IMMOVABLE_BLOCK;
  const id = (state & (INERT - 1)) >> 4;
  if (isPiston(id)) return state < INERT && (state & EXTENDED) !== 0 ? IMMOVABLE_BLOCK : NORMAL;
  return REACTION[id]!;
}

// ---------------------------------------------------------------------------------------------- state

/** A block in transit: what it carries and how far along it is (0, 1 or 2 updates done). */
interface Move {
  carried: number;
  age: number;
  extending: boolean;
  /** The facing of the piston that moves it. */
  facing: Dir;
}

class Moves implements Store {
  map = new Map<number, Move>();

  save(): Array<[number, Move]> {
    return [...this.map].map(([at, m]) => [at, { ...m }]);
  }

  load(saved: unknown): void {
    this.map = new Map((saved as Array<[number, Move]>).map(([at, m]) => [at, { ...m }]));
  }
}

/** 1.12.2: a piston's block event, waiting for the end of the tick. */
interface BlockEvent {
  at: number;
  id: number;
  /** 0 extends, 1 retracts. */
  type: 0 | 1;
  facing: Dir;
}

class BlockEvents implements Store {
  list: BlockEvent[] = [];

  add(e: BlockEvent): void {
    const same = (o: BlockEvent): boolean =>
      o.at === e.at && o.id === e.id && o.type === e.type && o.facing === e.facing;
    if (!this.list.some(same)) this.list.push(e);
  }

  save(): BlockEvent[] {
    return this.list.map((e) => ({ ...e }));
  }

  load(saved: unknown): void {
    this.list = (saved as BlockEvent[]).map((e) => ({ ...e }));
  }
}

const movesOf = (w: World): Moves => w.state('piston.moves', () => new Moves());
const eventsOf = (w: World): BlockEvents => w.state('piston.events', () => new BlockEvents());

/**
 * b1.8.1: the per-kind flag that makes pistons of a kind deaf while one of them runs its event, and the pistons whose
 * events are running (one can start inside another). It is always clear between game ticks, so no keyframe needs it.
 */
interface Guard {
  busy: [sticky: boolean, plain: boolean];
  running: number[];
}

const GUARDS = new WeakMap<World, Guard>();

function guardOf(w: World): Guard {
  let g = GUARDS.get(w);
  if (!g) {
    g = { busy: [false, false], running: [] };
    GUARDS.set(w, g);
  }
  return g;
}

const kindOf = (id: number): 0 | 1 => (id === STICKY ? 0 : 1);

// ---------------------------------------------------------------------------------------------- power and structure

/** The strongest power among the twelve cells a piston listens to: its sides and back, and around the cell above. */
function powerOf(w: World, at: number, facing: Dir): number {
  let best = 0;
  for (let d = 0; d < 6; d++) if (d !== facing) best = Math.max(best, powerFrom(w, at + w.step[d]!, opposite(d)));
  const above = at + w.step[UP]!;
  for (let d = 0; d < 6; d++) if (d !== DOWN) best = Math.max(best, powerFrom(w, above + w.step[d]!, opposite(d)));
  return best;
}

/** The cells a push carries, nearest first, and the cell it breaks (-1: none). Null when the push cannot happen. */
interface Structure {
  list: number[];
  breaks: number;
}

function structure(w: World, at: number, facing: Dir): Structure | null {
  const step = w.step[facing]!;
  if (!w.writable(at + step)) {
    w.diagnose('outOfPlot', at, outOfPlotText());
    return null;
  }
  const list: number[] = [];
  for (let c = at + step; ; c += step) {
    const state = w.cells[c]!;
    if (state === 0) return { list, breaks: -1 };
    const reaction = reactionOf(state);
    if (reaction === IMMOVABLE_BLOCK) return null;
    if (reaction === BREAK) return { list, breaks: c };
    if (list.length >= PUSH_LIMIT) return null;
    if (!w.writable(c + step)) {
      w.diagnose('outOfPlot', c, outOfPlotText());
      return null;
    }
    if (idOf(state) === SLIME) w.diagnose('slime', c, slimeText());
    list.push(c);
  }
}

/** Whether a sticky piston pulls the block in a state: not air, not breakable, not push-only, not immovable. */
const pullable = (state: number): boolean => state > 0 && reactionOf(state) === NORMAL;

// ---------------------------------------------------------------------------------------------- moves

/** Puts a cell in transit, carrying `carried`. */
function begin(w: World, at: number, carried: number, extending: boolean, facing: Dir): void {
  const moves = movesOf(w);
  moves.map.delete(at);
  moves.map.set(at, { carried, age: 0, extending, facing });
  w.set(at, (MOVING << 4) | facing);
}

/** A move ends: the carried block is put in place, is "added", and its neighbours (and in 1.12.2 itself) hear of it. */
function land(w: World, at: number, m: Move): void {
  const moves = movesOf(w);
  if (idOf(w.cells[at]!) !== MOVING) {
    moves.map.delete(at);
    return;
  }
  w.set(at, m.carried);
  moves.map.delete(at);
  const id = idOf(m.carried);
  w.handlers[id]?.placed?.(w, at, m.carried);
  if (w.profile.modern) {
    w.notifyAround(at, MOVING);
    w.notify(at, at, id);
  } else w.notifyAround(at, id);
}

/** Lands a move at once, before its time (the game finishes a move it has to replace). */
function finish(w: World, at: number): void {
  const m = movesOf(w).map.get(at);
  if (m) land(w, at, m);
}

/** The moving-blocks phase: one update for every move that existed when it began. */
function advance(w: World): void {
  const moves = movesOf(w);
  for (const [at, m] of [...moves.map]) {
    if (moves.map.get(at) !== m) continue;
    if (m.age >= 2) land(w, at, m);
    else m.age += 1;
  }
}

/** Pushes the blocks in front out by one and starts the head. False when nothing can move. */
function push(w: World, at: number, facing: Dir, sticky: boolean): boolean {
  const found = structure(w, at, facing);
  if (!found) return false;
  const { list, breaks } = found;
  const modern = w.profile.modern;
  const step = w.step[facing]!;

  const broken = breaks >= 0 ? w.cells[breaks]! : 0;
  if (breaks >= 0) {
    w.event('destroyed', breaks, `${w.handlers[idOf(broken)]?.name ?? 'A block'}: broken by a piston`);
    w.set(breaks, 0);
    if (!modern) w.notifyAround(breaks, 0);
  }
  const carried = list.map((c) => w.cells[c]!);
  for (let i = list.length - 1; i >= 0; i--) {
    if (modern) w.set(list[i]!, 0);
    begin(w, list[i]! + step, carried[i]!, true, facing);
  }
  begin(w, at + step, (HEAD << 4) | facing | (sticky ? STICKY_HEAD : 0), true, facing);

  if (modern) {
    if (breaks >= 0) w.notifyAround(breaks, idOf(broken));
    for (let i = list.length - 1; i >= 0; i--) w.notifyAround(list[i]!, idOf(carried[i]!));
    w.notifyAround(at + step, HEAD);
  }
  return true;
}

/** b1.8.1: runs `fn` with the kind's flag lowered, as the game does around the blocks it clears while retracting. */
function unguarded(w: World, id: number, fn: () => void): void {
  if (w.profile.modern) return fn();
  const guard = guardOf(w);
  guard.busy[kindOf(id)] = false;
  try {
    fn();
  } finally {
    guard.busy[kindOf(id)] = true;
  }
}

/** Retracts: the base goes in transit, a sticky piston pulls the block two cells out or leaves it. */
function retract(w: World, at: number, id: number, facing: Dir): void {
  const modern = w.profile.modern;
  const step = w.step[facing]!;
  const head = at + step;
  const far = head + step;
  const moves = movesOf(w);

  finish(w, head);
  begin(w, at, (id << 4) | facing, false, facing);
  if (modern) w.notifyAround(at, id);

  if (id === STICKY) {
    const outward = moves.map.get(far);
    if (idOf(w.cells[far]!) === MOVING && outward?.extending && outward.facing === facing) {
      // Still on its way out: it lands where it is going and stays there.
      finish(w, far);
      return;
    }
    if (pullable(w.cells[far]!) && w.writable(head)) {
      if (modern) {
        replace(w, head, 0);
        const pulled = w.cells[far]!;
        if (!pullable(pulled)) return;
        w.set(far, 0);
        begin(w, head, pulled, false, facing);
        w.notifyAround(far, idOf(pulled));
      } else {
        const pulled = w.cells[far]!;
        unguarded(w, id, () => replace(w, far, 0));
        begin(w, head, pulled, false, facing);
      }
      return;
    }
    // 1.12.2 leaves the head; it goes when it hears that its piston is gone.
    if (modern) return;
  }
  unguarded(w, id, () => replace(w, head, 0));
}

/** A piston's block event: 0 extends, 1 retracts. */
function received(w: World, at: number, state: number, type: 0 | 1): void {
  const id = state >> 4;
  const facing = state & 7;
  if (w.profile.modern) {
    const powered = powerOf(w, at, facing) > 0;
    if (powered && type === 1) {
      w.set(at, state | EXTENDED);
      return;
    }
    if (!powered && type === 0) {
      w.diagnose('updateOrder', at, UPDATE_ORDER_TEXT.zeroTick);
      return;
    }
    if (type === 0) {
      if (push(w, at, facing, id === STICKY)) replace(w, at, state | EXTENDED);
    } else retract(w, at, id, facing);
    return;
  }

  const guard = guardOf(w);
  const kind = kindOf(id);
  guard.busy[kind] = true;
  guard.running.push(at);
  try {
    if (type === 0) {
      // `look` has marked it extended already; the game writes that state again, which tells the neighbours.
      if (push(w, at, facing, id === STICKY)) w.notifyAround(at, id);
    } else retract(w, at, id, facing);
  } finally {
    guard.busy[kind] = false;
    guard.running.pop();
  }
}

/** The piston looks at its power and starts (b1.8.1) or asks for (1.12.2) the move it needs. */
function look(w: World, at: number, state: number): void {
  const facing = state & 7;
  const extended = (state & EXTENDED) !== 0;
  const powered = powerOf(w, at, facing) > 0;
  if (powered === extended) return;
  if (powered && !structure(w, at, facing)) {
    // A block in transit in front is a move under way (often this piston's own), not an obstacle worth reporting.
    if (idOf(w.cells[at + w.step[facing]!]!) !== MOVING) w.event('blocked', at);
    return;
  }
  const type = powered ? 0 : 1;
  if (w.profile.modern) {
    eventsOf(w).add({ at, id: state >> 4, type, facing });
    return;
  }
  if (dustTelling(w)) w.diagnose('updateOrder', at, UPDATE_ORDER_TEXT.dust);
  const marked = powered ? state | EXTENDED : state & ~EXTENDED;
  w.set(at, marked);
  received(w, at, marked, type);
}

/** 1.12.2: the end-of-tick block events, in rounds until none are left. */
function runEvents(w: World): void {
  const events = eventsOf(w);
  while (events.list.length > 0) {
    const round = events.list;
    events.list = [];
    for (const e of round) {
      const state = w.cells[e.at]!;
      // An event for a block that is no longer there is dropped.
      if (idOf(state) === e.id) received(w, e.at, state, e.type);
    }
  }
}

/** A neighbour update for the piston at `at`, with b1.8.1's per-kind deafness. */
function heard(w: World, at: number, state: number): void {
  if (!w.profile.modern) {
    const guard = guardOf(w);
    if (guard.busy[kindOf(state >> 4)]) {
      // A piston in the middle of its own move is deaf in the game too, without consequence.
      if (!guard.running.includes(at)) w.diagnose('updateOrder', at, UPDATE_ORDER_TEXT.guard);
      return;
    }
  }
  look(w, at, state);
}

export const piston: ComponentHandler = {
  name: 'Piston',
  ids: () => [STICKY, PISTON],
  valid: (state) => (state & 7) <= 5,
  normalize: (state) => state & ~EXTENDED,
  init: (w) => {
    const moves = movesOf(w);
    const events = w.profile.modern ? eventsOf(w) : null;
    if (events) w.onPhase(4, () => runEvents(w));
    w.onPhase(5, () => advance(w));
    w.onBusy(() => moves.map.size > 0 || (events !== null && events.list.length > 0));
  },
  start: (w, at, state) => look(w, at, state),
  update: (w, at, state) => heard(w, at, state),
  // Put in place by a move: the game looks at its power whatever the other pistons are doing.
  placed: (w, at, state) => look(w, at, state),
  power: (w, at, state) => powerOf(w, at, state & 7),
};

/** The head: it passes the updates it hears on to its piston, and goes when its piston is gone. */
export const pistonHead: ComponentHandler = {
  name: 'Piston head',
  ids: () => [HEAD],
  // Only a piston puts a head in place.
  valid: () => false,
  update: (w, at, state) => {
    const base = at + w.step[opposite(state & 7)]!;
    const baseState = w.cells[base]!;
    if (isPiston(idOf(baseState))) heard(w, base, baseState);
    else replace(w, at, 0);
  },
};

/** A cell in transit. It shows the block it carries; for power it is nothing. */
export const movingBlock: ComponentHandler = {
  name: 'Moving block',
  ids: () => [MOVING],
  valid: () => false,
  shown: (w, at, state) => {
    const carried = movesOf(w).map.get(at)?.carried;
    return carried === undefined ? state : carried & (INERT - 1);
  },
};
