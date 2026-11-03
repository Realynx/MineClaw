/** Injectable time source so session expiry, lockouts and TOTP windows are testable. */
export interface Clock {
  /** Unix milliseconds. */
  now(): number;
}

export const systemClock: Clock = { now: () => Date.now() };

/** A clock tests can move. */
export class ManualClock implements Clock {
  constructor(private t = Date.UTC(2026, 10, 2, 10, 0, 0)) {}
  now(): number {
    return this.t;
  }
  advance(ms: number): void {
    this.t += ms;
  }
  set(ms: number): void {
    this.t = ms;
  }
}
