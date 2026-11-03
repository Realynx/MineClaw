/**
 * Rate limiting.
 * - `LoginLimiter` (web.db, survives restarts): failure counters per `ip:<addr>` and `user:<name>` with exponential lockout.
 *   Once a key's failures reach its threshold it is locked for base·2^(failures−threshold) seconds, capped. Counters for
 *   unknown usernames are kept too, so a lockout reveals nothing about which accounts exist. A success clears both keys;
 *   counters older than an hour (and not locked) start over.
 * - `WindowLimiter` (memory): a sliding window of N events per interval per key (raw login attempts, pre-session creation).
 * - `TokenBucket` (memory): burst + steady refill per key (chat lines per user).
 */
import type { StatementSync } from 'node:sqlite';
import type { Clock } from '../common/clock.js';
import type { WebDb } from '../db/web-db.js';

export interface LockoutPolicy {
  userThreshold: number;
  ipThreshold: number;
  lockoutBaseSeconds: number;
  lockoutMaxSeconds: number;
}

const DECAY_MS = 3600_000;

export class LoginLimiter {
  private readonly q: Record<'get' | 'upsert' | 'del', StatementSync>;

  constructor(
    db: WebDb,
    private readonly clock: Clock,
    private readonly policy: LockoutPolicy,
  ) {
    this.q = {
      get: db.db.prepare('select failures, locked_until, last_at from login_failures where key = ?'),
      upsert: db.db.prepare(
        `insert into login_failures (key, failures, locked_until, last_at) values (?, ?, ?, ?)
         on conflict(key) do update set failures = excluded.failures, locked_until = excluded.locked_until, last_at = excluded.last_at`,
      ),
      del: db.db.prepare('delete from login_failures where key = ?'),
    };
  }

  static keys(ip: string, username: string): { ip: string; user: string } {
    return { ip: `ip:${ip}`, user: `user:${username}` };
  }

  /** Remaining lock in ms (0 = not locked) over all keys. */
  lockedFor(keys: readonly string[]): number {
    const now = this.clock.now();
    let max = 0;
    for (const k of keys) {
      const row = this.q.get.get(k) as { locked_until: number } | undefined;
      if (row && row.locked_until > now) max = Math.max(max, row.locked_until - now);
    }
    return max;
  }

  private threshold(key: string): number {
    return key.startsWith('ip:') ? this.policy.ipThreshold : this.policy.userThreshold;
  }

  /** Records a failure for each key; returns the keys that just became locked and the longest lock (ms). */
  fail(keys: readonly string[]): { newlyLocked: string[]; lockMs: number } {
    const now = this.clock.now();
    const newlyLocked: string[] = [];
    let lockMs = 0;
    for (const k of keys) {
      const row = this.q.get.get(k) as { failures: number; locked_until: number; last_at: number } | undefined;
      let failures = row ? row.failures : 0;
      if (row && row.locked_until <= now && now - row.last_at > DECAY_MS) failures = 0;
      failures++;
      let lockedUntil = row?.locked_until ?? 0;
      const t = this.threshold(k);
      if (failures >= t) {
        const ms = Math.min(this.policy.lockoutBaseSeconds * 2 ** (failures - t), this.policy.lockoutMaxSeconds) * 1000;
        lockedUntil = now + ms;
        lockMs = Math.max(lockMs, ms);
        newlyLocked.push(k);
      }
      this.q.upsert.run(k, failures, lockedUntil, now);
    }
    return { newlyLocked, lockMs };
  }

  reset(keys: readonly string[]): void {
    for (const k of keys) this.q.del.run(k);
  }
}

export class WindowLimiter {
  private readonly hits = new Map<string, number[]>();

  constructor(
    private readonly clock: Clock,
    private readonly max: number,
    private readonly windowMs: number,
  ) {}

  /** Records a hit; returns 0 when allowed, else ms until the next slot frees. */
  hit(key: string): number {
    const now = this.clock.now();
    const list = (this.hits.get(key) ?? []).filter((t) => now - t < this.windowMs);
    if (list.length >= this.max) {
      this.hits.set(key, list);
      return this.windowMs - (now - list[0]!);
    }
    list.push(now);
    this.hits.set(key, list);
    if (this.hits.size > 10_000) this.sweep(now);
    return 0;
  }

  private sweep(now: number): void {
    for (const [k, list] of this.hits) if (list.every((t) => now - t >= this.windowMs)) this.hits.delete(k);
  }
}

export class TokenBucket {
  private readonly buckets = new Map<string, { tokens: number; at: number }>();

  constructor(
    private readonly clock: Clock,
    private readonly burst: number,
    private readonly perMinute: number,
  ) {}

  /** Takes one token; returns 0 when allowed, else ms until one is available. */
  take(key: string): number {
    const now = this.clock.now();
    const rate = this.perMinute / 60_000;
    const b = this.buckets.get(key) ?? { tokens: this.burst, at: now };
    b.tokens = Math.min(this.burst, b.tokens + (now - b.at) * rate);
    b.at = now;
    if (b.tokens >= 1) {
      b.tokens -= 1;
      this.buckets.set(key, b);
      return 0;
    }
    this.buckets.set(key, b);
    return Math.ceil((1 - b.tokens) / rate);
  }
}
