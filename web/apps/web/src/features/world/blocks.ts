/**
 * Live per-block world updates: `GET /api/world/blocks?since&dim` and the `blocks` SSE event, in the bot's shape (camelCased
 * by Nest): `{at, rev, full, more, blocks: {"<dim>": [[x,y,z,id,meta], …]}, columns: [{rev, at, dim, cx, cz, kind}]}`.
 * `blocks` is keyed by dimension; the keys are opaque and kept exactly as sent.
 *
 * The type is the world viewer's `BlockDeltas`. The contract schemas (`WorldBlocksQuery` / `WorldBlockDeltas`) are being added
 * to @mineclaw/contracts in another branch; switch the route to the typed client once they land.
 */
import type { BlockChange, BlockDeltas } from '@mineclaw/world-viewer';

export type WorldBlockDeltas = BlockDeltas;
export type BlockDelta = BlockChange;

export interface WorldBlocksQuery {
  since?: number;
  dim?: number;
}

export function isBlockDeltas(v: unknown): v is WorldBlockDeltas {
  if (!v || typeof v !== 'object') return false;
  const o = v as Record<string, unknown>;
  return typeof o.rev === 'number' && !!o.blocks && typeof o.blocks === 'object';
}

/** All block changes of one dimension. */
export function blocksForDim(d: WorldBlockDeltas, dim: number): BlockChange[] {
  if (Array.isArray(d.blocks)) return (d.dim ?? 0) === dim ? d.blocks : [];
  return d.blocks[String(dim)] ?? [];
}

export const emptyBlockDeltas = (since: number): WorldBlockDeltas => ({ at: Date.now(), rev: since, full: false, more: false, blocks: {}, columns: [] });

type Listener = (d: WorldBlockDeltas) => void;
const listeners = new Set<Listener>();
const reconnectListeners = new Set<() => void>();
let lastRev = 0;

/** The shared SSE client calls this for every `blocks` frame. */
export function publishBlocks(d: WorldBlockDeltas): void {
  if (d.rev <= lastRev && !d.full) return;
  lastRev = Math.max(lastRev, d.rev);
  for (const l of listeners) l(d);
}

/** The shared SSE client calls this after the stream dropped and came back (frames may be missing). */
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

export function lastBlocksRev(): number {
  return lastRev;
}
