import { cellIndex } from '../src/wire/chunk';
import { encodeColumn, encodeMcwc, lightSections, type EncodeEntry } from '../src/wire/encode';
import { combineHash } from '../src/wire/hash';
import { ColumnStore } from '../src/mesh/columns';

/** [x, y, z, id, meta] in world coordinates. */
export type B = [number, number, number, number, number?];

/** A store holding the columns the blocks touch (plus any extra empty columns), through a real MCWC payload. */
export async function storeWith(blocks: B[], opts: { height?: number; light?: boolean; extra?: Array<[number, number]>; live?: boolean } = {}): Promise<ColumnStore> {
  const height = opts.height ?? 128;
  const cols = new Map<string, { cx: number; cz: number; ids: Uint8Array; meta: Uint8Array }>();
  const col = (cx: number, cz: number) => {
    const k = `${cx},${cz}`;
    let c = cols.get(k);
    if (!c) cols.set(k, (c = { cx, cz, ids: new Uint8Array(256 * height), meta: new Uint8Array(256 * height) }));
    return c;
  };
  for (const [cx, cz] of opts.extra ?? []) col(cx, cz);
  for (const [x, y, z, id, meta] of blocks) {
    const c = col(x >> 4, z >> 4);
    const i = cellIndex(x & 15, y, z & 15);
    c.ids[i] = id;
    c.meta[i] = meta ?? 0;
  }
  const entries: EncodeEntry[] = [...cols.values()].map((c) => {
    const light = opts.light === false ? null : lightSections(height, () => 15, () => 0);
    const e = encodeColumn(c.ids, c.meta, light);
    return { cx: c.cx, cz: c.cz, hash: e.lightHash != null ? combineHash(e.blockHash, e.lightHash) : e.blockHash, seenMs: 1, by: 't', live: opts.live ?? true, data: e.data, light: e.light };
  });
  const store = new ColumnStore();
  await store.applyPayload(encodeMcwc(0, entries));
  return store;
}
