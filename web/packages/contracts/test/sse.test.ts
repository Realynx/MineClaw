import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { loadFixture } from './helpers.js';

describe('SSE catalogue', () => {
  const live = loadFixture('live');
  const samples: Record<C.SseEventName, unknown> = {
    bots: live.status,
    event: live.events,
    feed: live.feed,
    now: live.now,
    entities: live.worldEntities,
    hive: live.hiveTree,
    memoryActivity: live.memoryActivity,
    blocks: {
      at: 1762077600000,
      rev: 42,
      full: false,
      more: false,
      blocks: { '0': [[3, 64, -2, 4, 0]], '-1': [[1, 70, 2, 87, 0]] },
      columns: [{ rev: 41, at: 1762077599000, dim: 0, cx: 0, cz: -1, kind: 'loaded' }],
    },
    status: { at: '2026-11-02T10:00:00Z', upstream: 'connected', bot: 'up', latency_ms: 4.2 },
  };

  it.each(C.SSE_EVENT_NAMES)('%s frame round-trips', (name) => {
    const data = C.toCamel(samples[name]);
    const frame = C.formatSseFrame(name, 7, data);
    expect(frame.startsWith(`event: ${name}\nid: 7\ndata: `)).toBe(true);
    expect(frame.endsWith('\n\n')).toBe(true);
    const json = frame.split('\n')[2]!.slice('data: '.length);
    expect(C.parseSseFrame(name, JSON.parse(json))?.event).toBe(name);
  });

  it('ignores unknown events and parses topics', () => {
    expect(C.parseSseFrame('nope', {})).toBeNull();
    expect(C.parseTopics('feed,now,bogus')).toEqual(['feed', 'now']);
    expect(C.parseTopics(undefined)).toHaveLength(8);
  });

  it('decays heat by half-life', () => {
    expect(C.decayHeat(0, 120_000, 120)).toBeCloseTo(0.5);
    expect(C.decayHeat(null, 1, 120)).toBe(0);
  });
});
