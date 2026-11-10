/**
 * Scheduled ticks: a part asks to be called again some game ticks later, and decides what to do when the call comes,
 * from the world as it is then. The two versions run them differently, and circuits rely on the difference.
 *
 * - 1.12.2: when a game tick starts, every entry that is due moves to that tick's own list, ordered by due tick, then
 *   priority (lower first), then the order they were asked for. An entry leaves the list just before it runs. A new
 *   entry is dropped only when an equal one still waits for a later turn, so a part may ask again while its own entry
 *   is on this tick's list; "due now" (which a repeater checks) means still on that list.
 * - b1.8.1: no priority and no list: the earliest entry is taken, removed and run, one at a time, until none is due. A
 *   new entry is dropped when an equal one waits at all.
 *
 * Two entries are equal when they are for the same cell and the same block. Which block ids count as the same (a lit
 * and an unlit torch do in 1.12.2 and do not in b1.8.1) is the caller's `key`. An entry whose block is gone when its
 * turn comes is skipped by the caller.
 */
import type { PendingTick } from './api.js';
import type { Store } from './components/handler.js';

export interface Entry {
  /** The game tick it runs on. */
  due: number;
  priority: number;
  /** The order entries were asked for: the tie-break. */
  seq: number;
  /** The cell. */
  at: number;
  /** The block it is for. */
  key: number;
}

interface Saved {
  seq: number;
  list: Entry[];
}

const identity = (at: number, key: number): number => at * 256 + key;

export class Scheduler implements Store {
  /** Entries that wait, in the order they will run. */
  private waiting: Entry[] = [];
  private readonly byIdentity = new Map<number, Entry>();
  /** 1.12.2: this tick's list, from `cursor` on. */
  private current: Entry[] = [];
  private cursor = 0;
  private seq = 0;

  constructor(
    private readonly modern: boolean,
    /** The most entries one game tick runs. */
    private readonly cap: number,
  ) {}

  /** How many entries wait, counting the ones of the tick being run. */
  get size(): number {
    return this.waiting.length + this.current.length - this.cursor;
  }

  /** Adds an entry unless an equal one waits. */
  add(at: number, key: number, due: number, priority: number): void {
    const id = identity(at, key);
    if (this.byIdentity.has(id)) return;
    const entry: Entry = { due, priority: this.modern ? priority : 0, seq: this.seq++, at, key };
    this.byIdentity.set(id, entry);
    this.insert(entry);
  }

  /** 1.12.2: whether an entry for this cell and block is on the list of the tick being run and has not run yet. */
  dueNow(at: number, key: number): boolean {
    for (let i = this.cursor; i < this.current.length; i++) {
      const e = this.current[i]!;
      if (e.at === at && e.key === key) return true;
    }
    return false;
  }

  /** Runs what is due at `now`. When `each` throws, the entries that did not run stay scheduled. */
  run(now: number, each: (e: Entry) => void): void {
    if (this.modern) this.runListed(now, each);
    else this.runOneByOne(now, each);
  }

  /** The entries of one cell, soonest first. */
  pendingAt(at: number): PendingTick[] {
    const out: PendingTick[] = [];
    for (let i = this.cursor; i < this.current.length; i++) {
      const e = this.current[i]!;
      if (e.at === at) out.push({ dueTick: e.due, priority: e.priority });
    }
    for (const e of this.waiting) if (e.at === at) out.push({ dueTick: e.due, priority: e.priority });
    return out;
  }

  save(): Saved {
    return { seq: this.seq, list: this.waiting.map((e) => ({ ...e })) };
  }

  load(saved: unknown): void {
    const { seq, list } = saved as Saved;
    this.seq = seq;
    this.waiting = list.map((e) => ({ ...e }));
    this.current = [];
    this.cursor = 0;
    this.byIdentity.clear();
    for (const e of this.waiting) this.byIdentity.set(identity(e.at, e.key), e);
  }

  rebase(by: number): void {
    for (const e of this.waiting) e.due -= by;
  }

  private insert(entry: Entry): void {
    const list = this.waiting;
    let lo = 0;
    let hi = list.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      const e = list[mid]!;
      const after =
        e.due !== entry.due
          ? e.due > entry.due
          : e.priority !== entry.priority
            ? e.priority > entry.priority
            : e.seq > entry.seq;
      if (after) hi = mid;
      else lo = mid + 1;
    }
    list.splice(lo, 0, entry);
  }

  private runListed(now: number, each: (e: Entry) => void): void {
    let count = 0;
    while (count < this.waiting.length && count < this.cap && this.waiting[count]!.due <= now) count++;
    this.current = this.waiting.splice(0, count);
    this.cursor = 0;
    for (const e of this.current) this.byIdentity.delete(identity(e.at, e.key));
    try {
      while (this.cursor < this.current.length) each(this.current[this.cursor++]!);
    } finally {
      // Only a tick that was cut short leaves entries here: they keep their place for the next tick.
      for (let i = this.cursor; i < this.current.length; i++) {
        const e = this.current[i]!;
        const id = identity(e.at, e.key);
        if (this.byIdentity.has(id)) continue;
        this.byIdentity.set(id, e);
        this.insert(e);
      }
      this.current = [];
      this.cursor = 0;
    }
  }

  private runOneByOne(now: number, each: (e: Entry) => void): void {
    const most = Math.min(this.waiting.length, this.cap);
    for (let n = 0; n < most; n++) {
      const e = this.waiting[0];
      if (!e || e.due > now) break;
      this.waiting.shift();
      this.byIdentity.delete(identity(e.at, e.key));
      each(e);
    }
  }
}
