/**
 * Live per-block world updates (contracts `world.blocks` route and the `blocks` SSE event): the shared SSE client publishes
 * every `blocks` frame here and the world viewer subscribes through its `WorldSource.subscribe`.
 */
import type { WorldBlockChange, WorldBlockDeltas } from '@mineclaw/contracts';

export type { WorldBlockDeltas };

/** All block changes of one dimension (keys are the dim id as a string, kept opaque). */
export function blocksForDim(d: WorldBlockDeltas, dim: number): WorldBlockChange[] {
  return d.blocks[String(dim)] ?? [];
}

type Listener = (d: WorldBlockDeltas) => void;
const listeners = new Set<Listener>();
const reconnectListeners = new Set<() => void>();
let lastRev = 0;

/** The shared SSE client calls this for every `blocks` frame. Stale revisions are dropped unless the frame is `full`. */
export function publishBlocks(d: WorldBlockDeltas): void {
  if (d.rev <= lastRev && !d.full) return;
  lastRev = Math.max(lastRev, d.rev);
  for (const l of listeners) l(d);
}

/** The shared SSE client calls this after the stream was reopened (frames may be missing: the viewer re-runs its index). */
export function publishReconnect(): void {
  for (const l of reconnectListeners) l();
}

export function subscribeBlocks(fn: Listener, onReconnect?: () => void): () => void {
  listeners.add(fn);
  if (onReconnect) reconnectListeners.add(onReconnect);
  return () => {
    listeners.delete(fn);
    if (onReconnect) reconnectListeners.delete(onReconnect);
  };
}

/** For tests. */
export function resetBlocks(): void {
  lastRev = 0;
  listeners.clear();
  reconnectListeners.clear();
}
