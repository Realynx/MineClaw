import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { decodeMcwc, McwcError } from '../src/wire/mcwc';
import { cellIndex, lightPacked, parseLight, unpackSnapshot } from '../src/wire/chunk';
import { inflateRaw, inflateRawNative, inflateRawPako } from '../src/wire/inflate';
import { combineHash, hashKey, lightHash, snapshotHash } from '../src/wire/hash';
import { encodeColumn, encodeLight, encodeMcwc, encodeSnapshot, lightSections } from '../src/wire/encode';
import { ColumnStore } from '../src/mesh/columns';

// Payloads written by the production C# encoders (test/fixtures/gen): .NET DeflateStream bytes, C# hashes.
const fx = JSON.parse(readFileSync(new URL('./fixtures/csharp-payload.json', import.meta.url), 'utf8'));
const b64 = (s: string) => new Uint8Array(Buffer.from(s, 'base64'));

interface Col {
  cx: number;
  cz: number;
  height: number;
  block_hash: string;
  light_hash: string | null;
  hash: string;
  full_sections: number;
  biome_0_0: number;
  biome_5_9: number;
  samples: number[][];
  missing?: boolean;
}

describe('MCWC container (ColumnPayload.cs)', () => {
  it('decodes the C# v2 payload: header, every entry field, missing and light-less columns', () => {
    const p = decodeMcwc(b64(fx.v2));
    expect(p.version).toBe(2);
    expect(p.dim).toBe(0);
    expect(p.entries).toHaveLength(4);
    const [a, b, c, d] = p.entries;
    expect([a.cx, a.cz, a.seenMs, a.live, a.by]).toEqual([3, -2, 1_700_000_000_123, true, 'Alice']);
    expect(a.hash.toString()).toBe(fx.v2_columns[0].hash);
    expect([b.cx, b.cz, b.by]).toEqual([-5, 7, 'Bøb']);
    expect(b.hash.toString()).toBe(fx.v2_columns[1].hash);
    expect(c.data.length).toBe(0);
    expect(c.by).toBeNull();
    expect(c.live).toBe(false);
    expect([d.cx, d.cz]).toEqual([1000000, -1000000]);
    expect(d.light).toBeNull();
    expect(a.light!.length).toBeGreaterThan(0);
  });

  it('decodes the C# v1 payload (no light_len fields)', () => {
    const p = decodeMcwc(b64(fx.v1));
    expect(p.version).toBe(1);
    expect(p.dim).toBe(-1);
    expect(p.entries[0].light).toBeNull();
    expect(p.entries[0].hash.toString()).toBe(fx.v1_columns[0].hash);
  });

  it('the TS encoder writes the same container bytes as ColumnPayload.Encode', () => {
    for (const key of ['v2', 'v1'] as const) {
      const src = b64(fx[key]);
      const p = decodeMcwc(src);
      const again = new Uint8Array(encodeMcwc(p.dim, p.entries.map((e) => ({ cx: e.cx, cz: e.cz, hash: e.hash, seenMs: e.seenMs, by: e.by, live: e.live, data: e.data, light: e.light }))));
      expect(Buffer.from(again).equals(Buffer.from(src))).toBe(true);
    }
  });

  it('rejects bad magic, version, count, live flag, sizes, truncation and trailing bytes', () => {
    const good = b64(fx.v1);
    const mut = (f: (b: Uint8Array, dv: DataView) => void, len = good.length) => {
      const b = new Uint8Array(len);
      b.set(good.subarray(0, Math.min(len, good.length)));
      f(b, new DataView(b.buffer));
      return b;
    };
    expect(() => decodeMcwc(mut((b) => (b[0] = 0x58)))).toThrow(McwcError);
    expect(() => decodeMcwc(mut((_, dv) => dv.setUint16(4, 3, true)))).toThrow(/version 3/);
    expect(() => decodeMcwc(mut((_, dv) => dv.setInt32(12, 1025, true)))).toThrow(/count/);
    expect(() => decodeMcwc(mut((b) => (b[16 + 24] = 2)))).toThrow(/live flag/);
    expect(() => decodeMcwc(mut((_, dv) => dv.setInt32(16 + 26, 300 * 1024, true)))).toThrow(/size/);
    expect(() => decodeMcwc(good.subarray(0, good.length - 1))).toThrow(/truncated/);
    expect(() => decodeMcwc(mut(() => undefined, good.length + 1))).toThrow(/trailing/);
    expect(() => decodeMcwc(new Uint8Array(4))).toThrow(/magic/);
  });
});

describe('column blobs (ChunkSnapshot / LightSnapshot)', () => {
  it.each([
    ['native DecompressionStream', inflateRawNative],
    ['pako fallback', async (d: Uint8Array) => inflateRawPako(d)],
  ])('inflates .NET raw DEFLATE with the %s and reads ids, meta, light, biomes and hashes', async (_name, inflate) => {
    const p = decodeMcwc(b64(fx.v2));
    for (let i = 0; i < p.entries.length; i++) {
      const e = p.entries[i];
      const want: Col = fx.v2_columns[i];
      if (want.missing) continue;
      const blocks = unpackSnapshot(await inflate(e.data));
      expect(blocks.height).toBe(want.height);
      expect(snapshotHash(blocks.ids, blocks.meta).toString()).toBe(want.block_hash);
      const light = e.light ? parseLight(await inflate(e.light)) : null;
      expect(light?.full.filter(Boolean).length ?? 0).toBe(want.full_sections);
      for (const [x, y, z, id, meta, sky, block] of want.samples) {
        const ci = cellIndex(x, y, z);
        expect([blocks.ids[ci], blocks.meta[ci]]).toEqual([id, meta]);
        if (light) {
          const v = lightPacked(light, x, y, z);
          expect([v >> 4, v & 15]).toEqual([sky, block]);
        }
      }
      if (light) {
        expect(light.biomes ? light.biomes[9 * 16 + 5] : -1).toBe(want.biome_5_9);
        expect(light.biomes ? light.biomes[0] : -1).toBe(want.biome_0_0);
        const sections = light.full.map((f, s) => (f ? { full: f } : { uniform: light.uniform[s] }));
        const lh = lightHash(sections, light.biomes);
        expect(lh.toString()).toBe(want.light_hash);
        expect(combineHash(BigInt(want.block_hash), lh).toString()).toBe(want.hash);
      } else expect(e.hash.toString()).toBe(want.block_hash);
    }
  });

  it('cell index: two stacked 128-high halves, x-major, y fastest', () => {
    expect(cellIndex(0, 0, 0)).toBe(0);
    expect(cellIndex(0, 1, 0)).toBe(1);
    expect(cellIndex(0, 0, 1)).toBe(128);
    expect(cellIndex(1, 0, 0)).toBe(16 * 128);
    expect(cellIndex(15, 127, 15)).toBe(32767);
    expect(cellIndex(0, 128, 0)).toBe(32768);
    expect(cellIndex(15, 255, 15)).toBe(65535);
  });

  it('rejects snapshots of the wrong length and malformed light', async () => {
    expect(() => unpackSnapshot(new Uint8Array(1000))).toThrow(/truncated/);
    const bad = new Uint8Array([2, 8, 0]);
    expect(() => parseLight(bad)).toThrow(/version/);
    expect(() => parseLight(new Uint8Array([1, 9, 0]))).toThrow(/sections/);
    expect(() => parseLight(new Uint8Array([1, 8, 0, 0, 1, 1, 5]))).toThrow(/truncated/);
  });

  it('TS encoders round-trip through both inflaters and agree with the C# hash of the same blocks', async () => {
    const ids = new Uint8Array(32768), meta = new Uint8Array(32768);
    for (let i = 0; i < ids.length; i++) {
      ids[i] = (i * 13) % 7 === 0 ? (i % 200) + 1 : 0;
      meta[i] = i & 15;
    }
    const enc = encodeSnapshot(ids, meta);
    for (const out of [await inflateRaw(enc), inflateRawPako(enc), await inflateRaw(enc, { forcePako: true })]) {
      const b = unpackSnapshot(out);
      expect(Buffer.from(b.ids).equals(Buffer.from(ids))).toBe(true);
      expect(Buffer.from(b.meta).equals(Buffer.from(meta))).toBe(true);
    }
    const sections = lightSections(128, (x, y) => (y > 64 ? 15 : 0), (x, y, z) => (x + y + z) % 15);
    const l = parseLight(inflateRawPako(encodeLight(sections, null)));
    expect(lightPacked(l, 3, 70, 4)).toBe((15 << 4) | ((3 + 70 + 4) % 15));
    expect(encodeColumn(ids, meta, null).blockHash).toBe(snapshotHash(ids, meta));
  });

  it('FNV-1a 64 matches the reference vectors', () => {
    expect(BigInt.asUintN(64, snapshotHash(new Uint8Array(0), new Uint8Array(0)))).toBe(0xcbf29ce484222325n);
    expect(BigInt.asUintN(64, snapshotHash(new Uint8Array([0x61]), new Uint8Array(0)))).toBe(0xaf63dc4c8601ec8cn);
    // Two id bytes, no meta: FNV-1a 64 of "ab".
    expect(BigInt.asUintN(64, snapshotHash(new Uint8Array([0x61, 0x62]), new Uint8Array(0)))).toBe(0x089c4407b545986an);
  });

  it('hash keys compare equal across i64 (payload), decimal strings and lossy JSON numbers', () => {
    const exact = BigInt(fx.v2_columns[0].hash);
    const asJsonNumber = JSON.parse(`{"h":${fx.v2_columns[0].hash}}`).h as number;
    expect(hashKey(exact)).toBe(hashKey(asJsonNumber));
    expect(hashKey(exact)).toBe(hashKey(fx.v2_columns[0].hash));
    expect(hashKey(exact)).not.toBe(hashKey(BigInt(fx.v2_columns[1].hash)));
  });
});

describe('ColumnStore.applyPayload', () => {
  it('stores live and remembered columns, drops missing ones, reports dirty sections', async () => {
    const store = new ColumnStore();
    const res = await store.applyPayload(b64(fx.v2));
    expect(res.columns.map((c) => c.missing)).toEqual([false, false, true, false]);
    expect(store.get(0, 3, -2)?.light).not.toBeNull();
    expect(store.get(0, -5, 7)?.height).toBe(256);
    expect(store.get(0, 1000000, -1000000)?.light).toBeNull();
    expect(store.get(0, 0, 0)).toBeUndefined();
    expect(res.dirty.length).toBeGreaterThan(0);
    expect(res.columns[0].hash).toBe(fx.v2_columns[0].hash);
  });
});
