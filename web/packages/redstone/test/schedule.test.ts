/** Scheduled ticks: each version's run order, identity rule and per-tick cap (report 4.1). */
import { describe, expect, it } from 'vitest';
import { Scheduler } from '../src/schedule.js';

const ran = (s: Scheduler, now: number, during?: (at: number) => void): number[] => {
  const out: number[] = [];
  s.run(now, (e) => {
    out.push(e.at);
    during?.(e.at);
  });
  return out;
};

describe('the 1.12.2 scheduler', () => {
  it('runs by due tick, then priority, then the order the ticks were asked for', () => {
    const s = new Scheduler(true, 65536);
    s.add(1, 7, 5, 0);
    s.add(2, 7, 5, -2);
    s.add(3, 7, 5, -3);
    s.add(4, 7, 4, 0);
    s.add(5, 7, 5, -2);
    expect(ran(s, 5)).toEqual([4, 3, 2, 5, 1]);
    expect(s.size).toBe(0);
  });

  it("counts a tick as pending only while it waits on this tick's list, and takes a new one meanwhile", () => {
    const s = new Scheduler(true, 65536);
    s.add(1, 7, 3, 0);
    s.add(2, 7, 3, 0);
    const seen: boolean[] = [];
    ran(s, 3, (at) => {
      if (at !== 1) return;
      seen.push(s.dueNow(2, 7), s.dueNow(1, 7));
      // Cell 2's tick is on this tick's list, not waiting, so an equal one for later is taken.
      s.add(2, 7, 5, 0);
    });
    expect(seen).toEqual([true, false]);
    expect(s.pendingAt(2)).toEqual([{ dueTick: 5, priority: 0 }]);
  });

  it('drops a tick equal to one still waiting, and runs at most its cap per game tick', () => {
    const s = new Scheduler(true, 2);
    s.add(1, 7, 2, 0);
    s.add(1, 7, 9, -1);
    s.add(2, 7, 2, 0);
    s.add(3, 7, 2, 0);
    expect(s.pendingAt(1)).toEqual([{ dueTick: 2, priority: 0 }]);
    expect(ran(s, 2)).toEqual([1, 2]);
    expect(ran(s, 3)).toEqual([3]);
  });
});

describe('the b1.8.1 scheduler', () => {
  it('has no priorities: due tick, then the order asked for', () => {
    const s = new Scheduler(false, 1000);
    s.add(1, 7, 5, 0);
    s.add(2, 7, 5, -3);
    s.add(3, 7, 4, 0);
    expect(s.pendingAt(2)).toEqual([{ dueTick: 5, priority: 0 }]);
    expect(ran(s, 5)).toEqual([3, 1, 2]);
  });

  it('drops a tick equal to one waiting; another block at the same cell is not equal', () => {
    const s = new Scheduler(false, 1000);
    s.add(1, 75, 4, 0);
    s.add(1, 75, 3, 0);
    s.add(1, 76, 3, 0);
    expect(s.pendingAt(1)).toEqual([
      { dueTick: 3, priority: 0 },
      { dueTick: 4, priority: 0 },
    ]);
  });

  it('runs at most its cap per game tick', () => {
    const s = new Scheduler(false, 2);
    for (let at = 1; at <= 3; at++) s.add(at, 7, 1, 0);
    expect(ran(s, 1)).toEqual([1, 2]);
    expect(ran(s, 2)).toEqual([3]);
  });
});

describe.each([true, false])('a scheduler (1.12.2: %s) as a store', (modern) => {
  it('saves a copy, loads it back with its identities, and moves its ticks when rebased', () => {
    const s = new Scheduler(modern, 1000);
    s.add(1, 7, 10, 0);
    s.add(2, 7, 12, 0);
    const saved = s.save();
    s.add(3, 7, 11, 0);
    s.load(saved);
    s.add(3, 7, 11, 0);
    s.add(1, 7, 99, 0);
    s.rebase(10);
    expect([s.pendingAt(1), s.pendingAt(2), s.pendingAt(3)]).toEqual([
      [{ dueTick: 0, priority: 0 }],
      [{ dueTick: 2, priority: 0 }],
      [{ dueTick: 1, priority: 0 }],
    ]);
    expect(ran(s, 2)).toEqual([1, 3, 2]);
  });
});
