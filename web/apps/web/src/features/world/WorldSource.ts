/**
 * `WorldSource`: everything @mineclaw/world-viewer needs, implemented against the public /api/world/* routes. The viewer
 * package renders in the browser (no server images); this adapter is its only door to the network.
 *
 * - `entities()` prefers the SSE-fed query cache (no extra polling); it falls back to GET /api/world/entities.
 * - `chunks()` splits requests into the server's 64-column cap and merges the MCWC payloads into one buffer.
 * - `blocks()` / `subscribe()` carry live per-block updates (GET /api/world/blocks + the `blocks` SSE event).
 */
import type { QueryClient } from '@tanstack/react-query';
import type { WorldAtlasJson, WorldEntities, WorldIndex, WorldOverlays } from '@mineclaw/contracts';
import { api, apiBlob, ApiError } from '@/lib/api/client';
import { liveKeys } from '@/lib/live/dispatch';
import { emptyBlockDeltas, publishBlocks, subscribeBlocks, type WorldBlockDeltas } from './blocks';

export type EntityDelta = WorldEntities;
export type Overlays = WorldOverlays;

export interface WorldSource {
  index(dim: number, x: number, z: number, r: number): Promise<WorldIndex>;
  chunks(dim: number, cols: ReadonlyArray<readonly [number, number]>): Promise<ArrayBuffer>;
  entities(since?: number): Promise<EntityDelta>;
  overlays(dim: number): Promise<Overlays>;
  atlas(): Promise<{ image: ImageBitmap | HTMLImageElement; map: WorldAtlasJson }>;
  blocks(since: number, dim?: number): Promise<WorldBlockDeltas>;
  /** Live block deltas pushed by the shared SSE client. Returns an unsubscribe function. */
  subscribe(onBlocks: (d: WorldBlockDeltas) => void): () => void;
}

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
  v.setUint16(4, version, true);
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

async function loadImage(blob: Blob): Promise<ImageBitmap | HTMLImageElement> {
  if (typeof createImageBitmap === 'function') return createImageBitmap(blob);
  const url = URL.createObjectURL(blob);
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    return img;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Blocks route: called through fetch directly because the route is not in the contracts ROUTES yet (see blocks.ts). */
async function fetchBlocks(since: number, dim?: number): Promise<WorldBlockDeltas> {
  const qs = new URLSearchParams({ since: String(since) });
  if (dim !== undefined) qs.set('dim', String(dim));
  const res = await fetch(`/api/world/blocks?${qs.toString()}`, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
  if (res.status === 404) return emptyBlockDeltas(since);
  if (!res.ok) throw new ApiError(res.status, `world blocks: HTTP ${res.status}`, {});
  return (await res.json()) as WorldBlockDeltas;
}

export function createWorldSource(qc?: QueryClient): WorldSource {
  let atlasPromise: Promise<{ image: ImageBitmap | HTMLImageElement; map: WorldAtlasJson }> | null = null;
  return {
    index: (dim, x, z, r) => api('world.index', { query: { dim, x: Math.round(x), z: Math.round(z), r: Math.max(16, Math.min(256, Math.round(r))) } }),

    async chunks(dim, cols) {
      if (cols.length === 0) return mergeMcwc([emptyMcwc(dim)]);
      const batches: string[] = [];
      for (let i = 0; i < cols.length; i += MAX_COLS_PER_REQUEST) {
        batches.push(
          cols
            .slice(i, i + MAX_COLS_PER_REQUEST)
            .map(([cx, cz]) => `${cx},${cz}`)
            .join(';'),
        );
      }
      const parts = await Promise.all(
        batches.map(async (b) => (await apiBlob('world.chunks', { query: { dim, cols: b, light: true } })).blob.arrayBuffer()),
      );
      return mergeMcwc(parts);
    },

    async entities(since) {
      const cached = qc?.getQueryData<WorldEntities>(liveKeys.entities);
      if (cached && (since === undefined || (cached.rev ?? 0) > since)) return { ...cached, full: true };
      if (cached && since !== undefined && (cached.rev ?? 0) === since) return { ...cached, entities: [], removed: [], full: false };
      return api('world.entities', { query: since === undefined ? {} : { since } });
    },

    overlays: (dim) => api('world.overlays', { query: { dim } }),

    atlas() {
      atlasPromise ??= (async () => {
        const [map, png] = await Promise.all([api('world.atlasJson'), apiBlob('world.atlasPng')]);
        return { map, image: await loadImage(png.blob) };
      })().catch((e: unknown) => {
        atlasPromise = null;
        throw e;
      });
      return atlasPromise;
    },

    async blocks(since, dim) {
      const d = await fetchBlocks(since, dim);
      return d;
    },

    subscribe: (onBlocks) => subscribeBlocks(onBlocks),
  };
}

function emptyMcwc(dim: number): ArrayBuffer {
  const b = new ArrayBuffer(16);
  const v = new DataView(b);
  MAGIC.forEach((m, i) => v.setUint8(i, m));
  v.setUint16(4, 1, true);
  v.setInt32(8, dim, true);
  return b;
}

export { publishBlocks };
