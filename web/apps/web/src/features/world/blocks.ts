/**
 * Live per-block world updates. `GET /api/world/blocks?since&dim` and the `blocks` SSE event.
 *
 * LOCAL TYPES: the contract schemas (`WorldBlocksQuery` / `WorldBlockDeltas`) are being added to @mineclaw/contracts in
 * another branch. These mirror the bot-side shape (camelCased by Nest); replace them with the contract imports on merge.
 *
 *   {at, rev, full, more, blocks: {"<dim>": [[x,y,z,id,meta], …]}, columns: [{rev, at, dim, cx, cz, kind}]}
 *
 * `blocks` is keyed by dimension; the keys are opaque (kept exactly as sent, e.g. "0", "-1").
 */

/** One changed block: [x, y, z, id, meta]. */
export type BlockDelta = [number, number, number, number, number];

export interface BlockColumnChange {
  rev: number;
  at: number | string;
  dim: number;
  cx: number;
  cz: number;
  kind: 'loaded' | 'unloaded';
}

export interface WorldBlockDeltas {
  at: number | string;
  rev: number;
  /** true = the client was too far behind: re-fetch the affected columns instead of applying `blocks`. */
  full: boolean;
  /** true = more deltas are waiting; call again with `since = rev`. */
  more: boolean;
  /** dimension key (opaque, e.g. "0" / "-1") → changed blocks. */
  blocks: Record<string, BlockDelta[]>;
  columns: BlockColumnChange[];
}

export interface WorldBlocksQuery {
  since?: number;
  dim?: number;
}

export function isBlockDeltas(v: unknown): v is WorldBlockDeltas {
  if (!v || typeof v !== 'object') return false;
  const o = v as Record<string, unknown>;
  return typeof o.rev === 'number' && !!o.blocks && typeof o.blocks === 'object' && !Array.isArray(o.blocks) && Array.isArray(o.columns);
}

/** All block deltas of one dimension (helper for consumers). */
export function blocksForDim(d: WorldBlockDeltas, dim: number): BlockDelta[] {
  return d.blocks[String(dim)] ?? [];
}

export const emptyBlockDeltas = (since: number): WorldBlockDeltas => ({ at: Date.now(), rev: since, full: false, more: false, blocks: {}, columns: [] });

type Listener = (d: WorldBlockDeltas) => void;
const listeners = new Set<Listener>();
let lastRev = 0;

/** The shared SSE client calls this for every `blocks` frame. */
export function publishBlocks(d: WorldBlockDeltas): void {
  if (d.rev <= lastRev && !d.full) return;
  lastRev = Math.max(lastRev, d.rev);
  for (const l of listeners) l(d);
}

export function subscribeBlocks(fn: Listener): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

export function lastBlocksRev(): number {
  return lastRev;
}
