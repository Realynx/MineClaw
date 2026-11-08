/**
 * The app's `WorldSource` for @mineclaw/world-viewer, implemented against the public /api/world/* routes. The viewer renders
 * the world in the browser (no server images); this adapter is its only door to the network.
 *
 * - `chunks()` asks with `light=1`, splits requests into the server's 64-column cap and merges the MCWC payloads.
 * - `entities()` prefers the SSE-fed query cache (no extra polling) and falls back to GET /api/world/entities.
 * - `blocks()` / `subscribe()` carry live per-block updates (GET /api/world/blocks + the shared stream's `blocks` event; the
 *   reconnect hook fires when the stream had to be reopened).
 * - `chat()` feeds the chat bubbles over heads from the shared stream's `feed` event (./chatBubbles.ts).
 */
import type { QueryClient } from '@tanstack/react-query';
import { toNumber, type WorldEntities } from '@mineclaw/contracts';
import type { BlockEntities, EntityDelta, WorldSource } from '@mineclaw/world-viewer';
import { api, apiBlob } from '@/lib/api/client';
import { liveKeys } from '@/lib/live/dispatch';
import { subscribeBlocks } from './blocks';
import { subscribeChat } from './chatBubbles';
import { subscribeEntityFrames } from './entityFrames';

export type { WorldSource };

/** Server cap per /api/world/chunks request. */
export const MAX_COLS_PER_REQUEST = 64;

const MAGIC = [0x4d, 0x43, 0x57, 0x43]; // "MCWC"

/** Merges MCWC payloads of one dimension: one header, the column records concatenated, the count summed. */
export function mergeMcwc(parts: ArrayBuffer[]): ArrayBuffer {
  if (parts.length === 1) return parts[0]!;
  let version = 0;
  let dim = 0;
  let count = 0;
  let bodyLen = 0;
  for (const p of parts) {
    const v = new DataView(p);
    if (p.byteLength < 16 || MAGIC.some((m, i) => v.getUint8(i) !== m)) throw new Error('not an MCWC payload');
    const ver = v.getUint16(4, true);
    if (version && ver !== version) throw new Error('MCWC version mismatch');
    version = ver;
    dim = v.getInt32(8, true);
    count += v.getInt32(12, true);
    bodyLen += p.byteLength - 16;
  }
  const out = new Uint8Array(16 + bodyLen);
  const v = new DataView(out.buffer);
  MAGIC.forEach((m, i) => v.setUint8(i, m));
  v.setUint16(4, version || 1, true);
  v.setUint16(6, 0, true);
  v.setInt32(8, dim, true);
  v.setInt32(12, count, true);
  let o = 16;
  for (const p of parts) {
    out.set(new Uint8Array(p, 16), o);
    o += p.byteLength - 16;
  }
  return out.buffer;
}

function toDelta(e: WorldEntities, full: boolean): EntityDelta {
  return { ...e, rev: e.rev ?? 0, full, removed: full ? [] : (e.removed ?? []) } as EntityDelta;
}

const finite = (n: number): number => (Number.isFinite(n) ? n : 0);

export interface WorldSourceOptions {
  /** The signed-in user's player (whispers they sent get a bubble). */
  player?: () => string | null | undefined;
}

export function createWorldSource(qc?: QueryClient, opts: WorldSourceOptions = {}): WorldSource {
  const pending = new Set<AbortController>();
  const read = async <T>(fetch: (signal: AbortSignal) => Promise<T>): Promise<T> => {
    const controller = new AbortController();
    pending.add(controller);
    try { return await fetch(controller.signal); }
    finally { pending.delete(controller); }
  };
  // WorldHost owns this cancellable source for its shared runtime. Each cached entity frame gets a number (the dispatcher
  // replaces the object per stream frame), so rereading it preserves the runtime's current movement interpolation.
  const frames = new WeakMap<WorldEntities, number>();
  let frameSeq = 0;
  const frameOf = (c: WorldEntities): number => {
    let n = frames.get(c);
    if (n === undefined) frames.set(c, (n = ++frameSeq));
    return n;
  };
  return {
    cancelPending: () => {
      for (const controller of pending) controller.abort();
      pending.clear();
    },
    index: async (dim, x, z, r) => {
      const i = await read((signal) => api('world.index', { signal, query: { dim, x: Math.round(x), z: Math.round(z), r: Math.max(16, Math.min(256, Math.round(r))), light: true } }));
      // weather doubles may arrive as named literals ("NaN"); the viewer wants plain numbers
      return { ...i, rain: finite(toNumber(i.rain)), thunder: finite(toNumber(i.thunder)) };
    },

    async chunks(dim, cols) {
      const batches: string[] = [];
      for (let i = 0; i < cols.length; i += MAX_COLS_PER_REQUEST) {
        batches.push(
          cols
            .slice(i, i + MAX_COLS_PER_REQUEST)
            .map(([cx, cz]) => `${cx},${cz}`)
            .join(';'),
        );
      }
      if (batches.length === 0) return mergeMcwc([new Uint8Array([...MAGIC, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).buffer]);
      const parts = await read((signal) => Promise.all(batches.map(async (b) => (await apiBlob('world.chunks', { signal, query: { dim, cols: b, light: true } })).blob.arrayBuffer())));
      return mergeMcwc(parts);
    },

    async entities(since) {
      const cached = qc?.getQueryData<WorldEntities>(liveKeys.entities);
      if (cached && (cached.rev ?? 0) > 0) {
        const frame = frameOf(cached);
        if (since === undefined || since === 0 || (cached.rev ?? 0) > since) return { ...toDelta(cached, true), frame };
        return { ...toDelta({ ...cached, entities: [], removed: [] }, false), frame };
      }
      return toDelta(await read((signal) => api('world.entities', { signal, query: since ? { since } : {} })), !since);
    },

    onEntities: qc ? (onFrame) => subscribeEntityFrames(onFrame) : undefined,

    overlays: (dim) => read((signal) => api('world.overlays', { signal, query: { dim } })),

    // No atlas(): the viewer builds it from this browser's imported local game assets.

    // Sign text, bed colours, banners, skulls, flower pots, spawners; the viewer batches ≤ 48 columns, the route takes 64.
    blockEntities: async (dim, cols) =>
      read((signal) => api('world.blockEntities', { signal, query: { dim, cols: cols.map(([cx, cz]) => `${cx},${cz}`).join(';') } })) as unknown as Promise<BlockEntities>,

    blocks: (since) => read((signal) => api('world.blocks', { signal, query: { since: since ?? 0 } })),

    subscribe: (onBlocks, onReconnect) => subscribeBlocks((d) => onBlocks(d), onReconnect),

    chat: (onSay) => subscribeChat(onSay, opts.player),
  };
}
