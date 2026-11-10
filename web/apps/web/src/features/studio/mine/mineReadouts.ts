/**
 * The mine's heavy readouts (the X-ray plan and its veins, the dig estimate) wait until the canvas has been still for a moment
 * and no pointer gesture is held, so a brush stroke, a drag or a run of key presses is not slowed by recomputing them each step.
 */
import { useCallback, useEffect, useRef, useState } from 'react';

/** How long the canvas must stay unchanged before the readouts are computed again. */
export const READOUT_IDLE_MS = 200;

/** Whether a pointer gesture (a brush stroke, a box or gizmo drag) is held on the canvas. */
export class Gesture {
  private on = false;
  private readonly listeners = new Set<(held: boolean) => void>();

  get held(): boolean {
    return this.on;
  }

  set(held: boolean): void {
    if (held === this.on) return;
    this.on = held;
    for (const listener of this.listeners) listener(held);
  }

  subscribe(listener: (held: boolean) => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }
}

/** Calls `run` once nothing has changed for `delay` ms and no gesture is held; `flush` runs a stale one at once. */
export class IdleRunner {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private stale = false;
  private held = false;

  constructor(
    private readonly run: () => void,
    private readonly delay = READOUT_IDLE_MS,
  ) {}

  /** A change is waiting to be computed. */
  get pending(): boolean {
    return this.stale;
  }

  changed(): void {
    this.stale = true;
    this.arm();
  }

  hold(held: boolean): void {
    this.held = held;
    if (held) this.stop();
    else if (this.stale) this.arm();
  }

  flush(): void {
    if (!this.stale) return;
    this.stop();
    this.stale = false;
    this.run();
  }

  dispose(): void {
    this.stop();
  }

  private arm(): void {
    this.stop();
    if (this.held) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.flush();
    }, this.delay);
  }

  private stop(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}

/**
 * A readout of the canvas: computed when the component mounts, then READOUT_IDLE_MS after the last change of `deps` and never
 * while `gesture` is held. `fresh()` brings it up to date at once and returns it (a saved picture must not show an old plan).
 */
export function useIdleReadout<T>(
  compute: () => T,
  deps: readonly unknown[],
  gesture?: Gesture | null,
  delay = READOUT_IDLE_MS,
): { value: T; fresh: () => T } {
  const [value, setValue] = useState(compute);
  const current = useRef(value);
  const latest = useRef(compute);
  const runner = useRef<IdleRunner | null>(null);
  useEffect(() => {
    // The newest `compute`: it reads the canvas as it is when the runner fires.
    latest.current = compute;
  });
  useEffect(() => {
    const idle = new IdleRunner(() => {
      const next = latest.current();
      current.current = next;
      setValue(() => next);
    }, delay);
    runner.current = idle;
    return () => {
      idle.dispose();
      if (runner.current === idle) runner.current = null;
    };
  }, [delay]);
  const mounted = useRef(false);
  useEffect(() => {
    // The first value was computed while mounting; only later changes wait.
    if (!mounted.current) {
      mounted.current = true;
      return;
    }
    runner.current?.changed();
  }, deps); // eslint-disable-line react-hooks/exhaustive-deps -- the caller names what the readout depends on
  useEffect(() => {
    const idle = runner.current;
    if (!gesture || !idle) return;
    idle.hold(gesture.held);
    return gesture.subscribe((held) => idle.hold(held));
  }, [gesture, delay]);
  const fresh = useCallback(() => {
    runner.current?.flush();
    return current.current;
  }, []);
  return { value, fresh };
}
