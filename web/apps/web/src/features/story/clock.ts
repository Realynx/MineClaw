'use client';

/**
 * Story Mode's one clock. The Minecraft server owns the time: every place the page shows the time of day (the HUD clock and
 * its day/night icon, the time slider, the schedule's current column, the map's sky light) reads this one clock, anchored to
 * the server's newest sample and never to the browser's wall clock. Two feeds sample the same server clock: the colony state
 * (`time_of_day`) and the viewer's world index replies (`time`); the newer one by receipt wins, and the viewer is fed back
 * from the colony's sample when that one is newer, so the map's light and the HUD agree to the second. Between samples the
 * time advances at 20 ticks a second, like the server; a stopped daylight cycle (1.12.2: a negative time, or `timeFrozen`)
 * never advances; a sample older than a minute is stale (shown dimmed, not free-running).
 *
 * Conventions (the bot's StoryCharacter): tick 0 = 06:00, 1000 ticks an hour; the clock hour (0 = midnight) is the schedule
 * column in force; night is 19:00–05:59 (`StoryCharacter.Night`), about ticks 13000–23000 where vanilla's sky goes dark.
 */
import { createContext, useCallback, useContext, useRef, useSyncExternalStore } from 'react';

export const DAY_TICKS = 24000;
export const TICKS_PER_SECOND = 20;
/** A sample older than this is stale: shown dimmed, and no longer advanced. */
export const STALE_MS = 60_000;

export type ClockSource = 'colony' | 'index';

export interface ClockAnchor {
  /** Time of day, 0..23999 (tick 0 = 06:00). */
  ticks: number;
  /** When it was received (Date.now() ms). */
  at: number;
  /** The server stopped the daylight cycle: the time holds. */
  frozen: boolean;
  source: ClockSource;
}

export interface ClockReading {
  known: boolean;
  ticks: number;
  /** The clock hour 0..23 (0 = midnight): the schedule column in force. */
  hour: number;
  minute: number;
  /** "HH:MM", or "–" when unknown. */
  text: string;
  night: boolean;
  frozen: boolean;
  stale: boolean;
  /** When the anchor was received (ms), 0 when unknown. */
  at: number;
  source: ClockSource | null;
}

const mod = (n: number, m: number) => ((n % m) + m) % m;

/**
 * A server time sample → time of day and whether the cycle is stopped. A negative time is 1.12.2's "doDaylightCycle false"
 * (the absolute value is the time). Null for anything that is not a number.
 */
export function parseWorldTime(
  raw: number | null | undefined,
  frozen?: boolean | null,
): { ticks: number; frozen: boolean } | null {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) return null;
  return { ticks: mod(Math.floor(Math.abs(raw)), DAY_TICKS), frozen: frozen === true || raw < 0 };
}

/** The clock hour (0 = midnight) and minute of a time of day: hour = (t / 1000 + 6) mod 24, minute = (t mod 1000) · 60 / 1000. */
export function clockParts(ticks: number): { hour: number; minute: number; text: string } {
  const t = mod(Math.floor(ticks), DAY_TICKS);
  const hour = (Math.floor(t / 1000) + 6) % 24;
  const minute = Math.floor(((t % 1000) * 60) / 1000);
  return { hour, minute, text: `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}` };
}

/** Night by the clock hour, as the bot's StoryCharacter.Night: 19:00–05:59. */
export function isNightHour(hour: number): boolean {
  return hour >= 19 || hour < 6;
}

/** The time of day now from an anchor: advanced at 20 ticks/s for at most STALE_MS; a frozen one holds. */
export function ticksAt(a: ClockAnchor, nowMs: number): number {
  if (a.frozen) return a.ticks;
  const dt = Math.min(STALE_MS, Math.max(0, nowMs - a.at));
  return mod(a.ticks + (dt / 1000) * TICKS_PER_SECOND, DAY_TICKS);
}

export function readClock(a: ClockAnchor | null, nowMs: number): ClockReading {
  if (!a)
    return {
      known: false,
      ticks: 0,
      hour: 6,
      minute: 0,
      text: '–',
      night: false,
      frozen: false,
      stale: false,
      at: 0,
      source: null,
    };
  const ticks = ticksAt(a, nowMs);
  const p = clockParts(ticks);
  return {
    known: true,
    ticks,
    hour: p.hour,
    minute: p.minute,
    text: p.text,
    night: isNightHour(p.hour),
    frozen: a.frozen,
    stale: nowMs - a.at > STALE_MS,
    at: a.at,
    source: a.source,
  };
}

/** What the viewer offers for the shared anchor (WorldRuntime.worldTimeAnchor / setWorldTime). */
export interface ViewerClock {
  anchor(): { time: number; at: number; frozen: boolean } | null;
  set(ticks: number, at: number, frozen: boolean): void;
}

/** The page's clock: one anchor, the newest server sample by receipt; subscribers hear when the shown minute changes. */
export class WorldClock {
  private a: ClockAnchor | null = null;
  private readonly listeners = new Set<() => void>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private lastKey = '';
  /** The last colony sample taken (a merged state repeats it; a repeat is not a new sample). */
  private lastColony: string | null = null;

  constructor(private readonly now: () => number = () => Date.now()) {}

  get anchor(): ClockAnchor | null {
    return this.a;
  }

  /** Anchors to a sample received at `at`; an older one than the anchor is ignored. True when it took. */
  set(ticks: number, at: number, frozen: boolean, source: ClockSource): boolean {
    if (this.a && at < this.a.at) return false;
    this.a = { ticks: mod(ticks, DAY_TICKS), at, frozen, source };
    this.emit(true);
    return true;
  }

  /** The colony state's `time_of_day` (and `timeFrozen`): a new sample when it differs from the last one taken. */
  colony(raw: number | null | undefined, frozen?: boolean | null, at = this.now()): boolean {
    const t = parseWorldTime(raw, frozen);
    if (!t) return false;
    const key = `${raw}|${t.frozen}`;
    if (key === this.lastColony) return false;
    this.lastColony = key;
    return this.set(t.ticks, at, t.frozen, 'colony');
  }

  /**
   * Joins the viewer's time (its index replies) to this anchor: the newer sample wins and the other side takes it, so the
   * map's sky light and every clock on the page show the same server time.
   */
  syncViewer(v: ViewerClock): void {
    const va = v.anchor();
    const c = this.a;
    if (va && (!c || va.at > c.at)) {
      const t = parseWorldTime(va.time, va.frozen);
      if (t) this.set(t.ticks, va.at, t.frozen, 'index');
    } else if (c && (!va || c.at > va.at)) v.set(c.ticks, c.at, c.frozen);
  }

  read(nowMs = this.now()): ClockReading {
    return readClock(this.a, nowMs);
  }

  subscribe(fn: () => void): () => void {
    this.listeners.add(fn);
    if (!this.timer) this.timer = setInterval(() => this.emit(false), 250);
    return () => {
      this.listeners.delete(fn);
      if (!this.listeners.size && this.timer) {
        clearInterval(this.timer);
        this.timer = null;
      }
    };
  }

  /** The displayed state; subscribers re-render only when it changes (an in-game minute is 0.83 s). */
  key(nowMs = this.now()): string {
    const r = this.read(nowMs);
    return `${r.text}|${r.frozen}|${r.stale}|${r.at}`;
  }

  private emit(force: boolean): void {
    const k = this.key();
    if (!force && k === this.lastKey) return;
    this.lastKey = k;
    for (const l of [...this.listeners]) l();
  }
}

export const WorldClockContext = createContext<WorldClock | null>(null);

const UNKNOWN = readClock(null, 0);

/**
 * The page clock's reading (the given clock, else the context's), re-rendering when the shown minute (or frozen / stale)
 * changes. Unknown without one.
 */
export function useWorldClock(given?: WorldClock | null): ClockReading {
  const ctx = useContext(WorldClockContext);
  const clock = given ?? ctx;
  const cache = useRef<{ key: string; reading: ClockReading } | null>(null);
  const subscribe = useCallback((fn: () => void) => (clock ? clock.subscribe(fn) : () => undefined), [clock]);
  const snapshot = useCallback(() => {
    if (!clock) return UNKNOWN;
    const now = Date.now();
    const key = clock.key(now);
    if (cache.current?.key !== key) cache.current = { key, reading: clock.read(now) };
    return cache.current.reading;
  }, [clock]);
  return useSyncExternalStore(subscribe, snapshot, () => UNKNOWN);
}
