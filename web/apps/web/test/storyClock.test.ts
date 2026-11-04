import { storyClock, storyHour, type StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import {
  clockParts,
  isNightHour,
  parseWorldTime,
  readClock,
  STALE_MS,
  WorldClock,
  type ClockAnchor,
} from '@/features/story/clock';
import { mergeColony } from '@/features/story/model';

describe('the world clock', () => {
  it('formats ticks as the clock: tick 0 = 06:00, 1000 an hour, minutes from the remainder', () => {
    expect(clockParts(0).text).toBe('06:00');
    expect(clockParts(6000).text).toBe('12:00');
    expect(clockParts(12000).text).toBe('18:00');
    expect(clockParts(18000).text).toBe('00:00');
    expect(clockParts(23999).text).toBe('05:59');
    expect(clockParts(7500)).toEqual({ hour: 13, minute: 30, text: '13:30' });
    expect(clockParts(24000 * 3 + 500).text).toBe('06:30');
    // The bot's StoryCharacter.Hour and the contracts' storyHour agree with it (the schedule column in force).
    for (const t of [0, 999, 1000, 5999, 13000, 18000, 23999]) expect(storyHour(t)).toBe(clockParts(t).hour);
    expect(storyClock(clockParts(18500).hour)).toBe('00:00');
  });

  it('night is 19:00–05:59 (the bot’s StoryCharacter.Night), about ticks 13000–23000', () => {
    expect(isNightHour(clockParts(12999).hour)).toBe(false); // 18:59
    expect(isNightHour(clockParts(13000).hour)).toBe(true); // 19:00
    expect(isNightHour(clockParts(18000).hour)).toBe(true); // midnight
    expect(isNightHour(clockParts(22999).hour)).toBe(true); // 04:59
    expect(isNightHour(clockParts(23999).hour)).toBe(true); // 05:59
    expect(isNightHour(clockParts(0).hour)).toBe(false); // 06:00
    expect(isNightHour(13)).toBe(false); // the old bug: clock 13:00 is afternoon
  });

  it('a negative server time is a stopped daylight cycle (1.12.2): its absolute value is the time', () => {
    expect(parseWorldTime(-6000)).toEqual({ ticks: 6000, frozen: true });
    expect(parseWorldTime(30000)).toEqual({ ticks: 6000, frozen: false });
    expect(parseWorldTime(100, true)).toEqual({ ticks: 100, frozen: true });
    expect(parseWorldTime(null)).toBeNull();
    expect(parseWorldTime(Number.NaN)).toBeNull();
  });

  it('advances at 20 ticks a second from the anchor and re-anchors on every new sample', () => {
    let now = 1_000_000;
    const c = new WorldClock(() => now);
    expect(c.read().known).toBe(false);
    expect(c.read().text).toBe('–');
    expect(c.colony(2029)).toBe(true);
    now += 5000; // 100 ticks later
    expect(c.read().ticks).toBe(2129);
    expect(c.read().text).toBe('08:07');
    // The same sample again (a merged state repeats it) is not a new anchor.
    expect(c.colony(2029)).toBe(false);
    expect(c.read().ticks).toBe(2129);
    // A new sample (the server's) wins over the local advance.
    expect(c.colony(2229)).toBe(true);
    expect(c.read().ticks).toBe(2229);
    // A frozen time never advances.
    c.colony(-13000);
    now += 30_000;
    expect(c.read()).toMatchObject({ ticks: 13000, frozen: true, night: true });
  });

  it('goes stale after a minute without a sample instead of free-running', () => {
    const a: ClockAnchor = { ticks: 0, at: 0, frozen: false, source: 'colony' };
    expect(readClock(a, STALE_MS).stale).toBe(false);
    const late = readClock(a, STALE_MS + 1);
    expect(late.stale).toBe(true);
    expect(readClock(a, STALE_MS * 5).ticks).toBe(late.ticks); // held, not advancing
  });

  it('joins the viewer: the newer sample wins and the other side takes it', () => {
    let now = 10_000;
    const c = new WorldClock(() => now);
    let viewer: { time: number; at: number; frozen: boolean } | null = {
      time: 24000 * 2 + 18000,
      at: 9000,
      frozen: false,
    };
    const v = {
      anchor: () => viewer,
      set: (time: number, at: number, frozen: boolean) => void (viewer = { time, at, frozen }),
    };
    c.syncViewer(v); // only the viewer knows: the clock takes its (full world) time
    expect(c.anchor).toMatchObject({ ticks: 18000, at: 9000, source: 'index' });
    now = 12_000;
    c.colony(6000); // the colony state, newer: the viewer is fed from it
    c.syncViewer(v);
    expect(viewer).toEqual({ time: 6000, at: 12_000, frozen: false });
    c.syncViewer(v); // in step: nothing moves
    expect(c.anchor?.source).toBe('colony');
    viewer = { time: 6100, at: 13_000, frozen: false }; // a newer index reply
    c.syncViewer(v);
    expect(c.anchor).toMatchObject({ ticks: 6100, source: 'index' });
  });
});

describe('the colony state keeps the server clock', () => {
  const base = {
    rev: 5,
    at: 1,
    timeOfDay: 1000,
    hour: 7,
    pawns: [],
    zones: [],
    designations: [],
    orders: [],
  } as unknown as StoryColonyState;
  it('a catch-up with no newer revision still brings the time', () => {
    const next = mergeColony(base, { ...base, rev: 5, at: 2, timeOfDay: 1100, hour: 7, full: false });
    expect(next.timeOfDay).toBe(1100);
    expect(next.at).toBe(2);
    expect(mergeColony(next, { ...next, timeOfDay: 1100 })).toBe(next); // nothing changed
  });
});
