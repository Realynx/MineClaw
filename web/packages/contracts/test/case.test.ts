import { afterEach, describe, expect, it, vi } from 'vitest';
import { camelToSnake, parseBridgeJson, snakeToCamel, toCamel, toCamelJson, toSnake } from '../src/index.js';

describe('key conversion', () => {
  it.each([
    ['in_flight', 'inFlight'],
    ['cache_write1h', 'cacheWrite1h'],
    ['model_input_tokens', 'modelInputTokens'],
    ['window_s', 'windowS'],
    ['avg_ms', 'avgMs'],
    ['min_x', 'minX'],
    ['cx', 'cx'],
    ['sha256', 'sha256'],
    ['__reason', '__reason'],
  ])('%s ⇄ %s', (snake, camel) => {
    expect(snakeToCamel(snake)).toBe(camel);
    expect(camelToSnake(camel)).toBe(snake);
  });

  it('converts nested objects and arrays, not values', () => {
    const wire = { in_flight: { since_at: 'a_b' }, turns: [{ cache_read: 1 }] };
    expect(toCamel(wire)).toEqual({ inFlight: { sinceAt: 'a_b' }, turns: [{ cacheRead: 1 }] });
    expect(toSnake(toCamel(wire))).toEqual(wire);
  });

  it('leaves opaque values alone in both directions', () => {
    const wire = { params: { store_at: 'home' }, fields: { __reason: 'x', min_y: '1' }, counts: { trapped_chest: 2 } };
    const pub = toCamel<Record<string, unknown>>(wire);
    expect(pub).toEqual(wire);
    expect(toSnake({ editPrefill: { someKey: 1 } })).toEqual({ edit_prefill: { someKey: 1 } });
  });

  it('parseBridgeJson keeps only `hash` numbers as strings', () => {
    const v = parseBridgeJson('{"hash":9223372036854775807,"n":9007199254740993,"x":{"hash":1}}') as Record<
      string,
      unknown
    >;
    expect(v.hash).toBe('9223372036854775807');
    expect(typeof v.n).toBe('number');
    expect((v.x as Record<string, unknown>).hash).toBe('1');
  });
});

describe('cheap bridge JSON (big colony frames)', () => {
  afterEach(() => vi.restoreAllMocks());

  it('parseBridgeJson parses without a reviver when no int64 key can be in the text', () => {
    const parse = vi.spyOn(JSON, 'parse');
    const text = '{"rev":9007199254740993,"ghosts":{"blocks":[[1,2,3,4,0,"b1","todo","dig"]]},"label":"can\\u0027t"}';
    expect(parseBridgeJson(text)).toEqual(JSON.parse(text));
    expect(parse.mock.calls[0]).toHaveLength(1);
  });

  it('parseBridgeJson still keeps `hash` exact when the key is written with escapes', () => {
    const parse = vi.spyOn(JSON, 'parse');
    const v = parseBridgeJson('{"h\\u0061sh":9223372036854775807,"x":{"HASH":1}}') as Record<string, unknown>;
    expect(v.hash).toBe('9223372036854775807');
    expect(parse.mock.calls[0]).toHaveLength(2);
  });

  const wire = {
    rev: 7,
    in_flight: { since_at: 'a_b' },
    pos: [200.5, 64, 410.5],
    ghosts: { blocks: [[1, 2, 3, 4, 0, 'b1', 'todo', 'dig', null]] },
    storages: [{ chest: [1, 2, 3], free_slots: 27, contents: [[4, 0, 8]], filter: { allow: ['*'], deny: [] } }],
    rows: [[{ min_skill: 2 }], []],
    fields: { __reason: 'x', min_y: '1' },
    counts: { trapped_chest: 2 },
    __private_key: true,
  };

  it('toCamelJson gives exactly what toCamel gives', () => {
    const fresh = () => JSON.parse(JSON.stringify(wire)) as unknown;
    const camel = toCamel(fresh());
    const json = toCamelJson(fresh());
    expect(json).toEqual(camel);
    expect(JSON.stringify(json)).toBe(JSON.stringify(camel));
    expect(toCamelJson(fresh(), { opaque: new Set() })).toEqual(toCamel(fresh(), { opaque: new Set() }));
  });

  it('toCamelJson shares arrays of primitives and copies the rest', () => {
    const input = JSON.parse(JSON.stringify(wire)) as typeof wire;
    const out = toCamelJson<{
      pos: unknown;
      ghosts: { blocks: unknown[] };
      rows: unknown[];
      storages: Array<{ freeSlots: number }>;
    }>(input);
    expect(out.pos).toBe(input.pos);
    expect(out.ghosts.blocks[0]).toBe(input.ghosts.blocks[0]);
    expect(out.ghosts.blocks).not.toBe(input.ghosts.blocks);
    expect(out.rows).not.toBe(input.rows);
    expect(out.storages[0]!.freeSlots).toBe(27);
  });
});
