import { describe, expect, it } from 'vitest';
import { camelToSnake, parseBridgeJson, snakeToCamel, toCamel, toSnake } from '../src/index.js';

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
