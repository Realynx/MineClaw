/** LiveHub backpressure and filters as units (fake sockets, no network). */
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { describe, expect, it } from 'vitest';
import { BridgeClient } from '../src/bridge/bridge-client.js';
import { BridgeHealth } from '../src/bridge/bridge-health.js';
import { createLogger } from '../src/common/logger.js';
import { BotAliases, coalesceKey, filterOf, filterPayload } from '../src/live/filters.js';
import { LiveHub } from '../src/live/live-hub.js';
import { SseParser } from '../src/live/sse-parser.js';

const log = createLogger('silent');

class FakeRes extends EventEmitter {
  writes: string[] = [];
  full = false;
  writableEnded = false;
  statusCode = 0;
  setHeader(): void {}
  flushHeaders(): void {}
  write(text: string): boolean {
    this.writes.push(text);
    return !this.full;
  }
  end(): void {
    this.writableEnded = true;
  }
  destroy(): void {
    this.end();
    this.emit('close');
  }
}

function fakeReq(): IncomingMessage {
  const req = new EventEmitter() as unknown as IncomingMessage;
  Object.assign(req, { headers: {}, socket: { setNoDelay() {}, setKeepAlive() {} } });
  return req;
}

function hub(opts = {}) {
  const bridge = new BridgeClient({ url: 'http://127.0.0.1:9', key: 'k'.repeat(32), timeoutMs: 50 }, log);
  return new LiveHub(bridge, new BridgeHealth(bridge, log), log, {
    backoffBaseMs: 10_000,
    maxQueueBytes: 2000,
    ...opts,
  });
}

const nowFrame = (bot: string, n: number) => ({ bot, at: `t${n}` });

describe('backpressure', () => {
  it('coalesces state topics, queues deltas and flushes in order on drain', () => {
    const h = hub();
    const res = new FakeRes();
    h.attach(fakeReq(), res as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    res.full = true;
    h.publish('now', nowFrame('a', 1)); // write returns false → congested
    res.writes = [];
    h.publish('now', nowFrame('a', 2));
    h.publish('feed', { lines: [{ bot: 'a', seq: 1 }], last: 1 });
    h.publish('now', nowFrame('a', 3));
    h.publish('now', nowFrame('b', 1));
    h.publish('feed', { lines: [{ bot: 'a', seq: 2 }], last: 2 });
    expect(res.writes).toEqual([]);
    res.full = false;
    res.emit('drain');
    const events = res.writes.map((w) => `${/event: (\w+)/.exec(w)?.[1]}:${/"(?:at|last)":"?(\w+)/.exec(w)?.[1]}`);
    expect(events).toEqual(['feed:1', 'now:t3', 'now:t1', 'feed:2']);
    h.close();
  });

  it('drops the queue past the cap and sends status resync once drained', () => {
    const h = hub();
    const res = new FakeRes();
    h.attach(fakeReq(), res as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    res.full = true;
    h.publish('feed', { lines: [], last: 0 });
    for (let i = 0; i < 50; i++) h.publish('feed', { lines: [{ bot: 'a', seq: i, text: 'x'.repeat(100) }], last: i });
    res.full = false;
    res.writes = [];
    res.emit('drain');
    expect(res.writes).toHaveLength(1);
    expect(res.writes[0]).toMatch(/^event: status\n/);
    expect(res.writes[0]).toContain('"resync":true');
    h.close();
  });

  it('a full frame supersedes queued deltas of the same event', () => {
    expect(coalesceKey('entities', { full: true })).toBe('entities:full');
    expect(coalesceKey('entities', { full: false })).toBeUndefined();
    expect(coalesceKey('blocks' as never, { full: true })).toBe('blocks:full');
    const h = hub();
    const res = new FakeRes();
    h.attach(fakeReq(), res as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    res.full = true;
    h.publish('feed', { lines: [], last: 0 });
    h.publish('entities', { at: 1, rev: 1, full: false, bots: [], entities: [] });
    h.publish('entities', { at: 2, rev: 2, full: false, bots: [], entities: [] });
    h.publish('entities', { at: 3, rev: 3, full: true, bots: [], entities: [] });
    res.full = false;
    res.writes = [];
    res.emit('drain');
    expect(res.writes.filter((w) => w.startsWith('event: entities'))).toHaveLength(1);
    expect(res.writes[0]).toContain('"rev":3');
    h.close();
  });

  it('limits streams per user', () => {
    const h = hub({ maxClientsPerUser: 1 });
    const a = new FakeRes();
    const b = new FakeRes();
    h.attach(fakeReq(), a as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    h.attach(fakeReq(), b as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    expect(b.statusCode).toBe(429);
    h.close();
  });
});

describe('filters', () => {
  const aliases = new BotAliases();
  aliases.learn({ bots: [{ name: 'maid_chan', short: 'main' }] });

  it('topics: any SSE_EVENTS name, unknown names ignored, status always passes', () => {
    const f = filterOf({ topics: 'feed,nope' });
    expect(f.topics).toEqual(new Set(['feed']));
    expect(filterPayload(f, 'now', { bot: 'x' }, aliases)).toBeNull();
    expect(filterPayload(f, 'status', { a: 1 }, aliases)).toEqual({ a: 1 });
    expect(filterOf({ topics: 'nope' }).topics).toBeNull();
  });

  it('bots: names or learned shorts; * is everything', () => {
    expect(filterPayload(filterOf({ bots: 'main' }), 'now', { bot: 'maid_chan' }, aliases)).not.toBeNull();
    expect(filterPayload(filterOf({ bots: 'MAID_CHAN' }), 'now', { bot: 'maid_chan' }, aliases)).not.toBeNull();
    expect(filterPayload(filterOf({ bots: 'other' }), 'now', { bot: 'maid_chan' }, aliases)).toBeNull();
    expect(filterOf({ bots: '*' }).bots).toBeNull();
  });

  it('brain limits memoryActivity', () => {
    const d = { events: [{ brain: 'main' }, { brain: '67' }], inContext: [{ brain: '67' }], last: 2 };
    expect(filterPayload(filterOf({ brain: 'main' }), 'memoryActivity', d, aliases)).toEqual({
      events: [{ brain: 'main' }],
      inContext: [],
      last: 2,
    });
  });

  it('dim limits blocks frames (frame dim or column dims)', () => {
    const f = filterOf({ dim: -1 });
    expect(filterPayload(f, 'blocks' as never, { dim: 0, blocks: [[1, 2, 3, 4, 0]] }, aliases)).toBeNull();
    expect(filterPayload(f, 'blocks' as never, { dim: -1, blocks: [] }, aliases)).toEqual({ dim: -1, blocks: [] });
    expect(
      filterPayload(
        f,
        'blocks' as never,
        {
          rev: 1,
          blocks: [],
          columns: [
            { dim: 0, cx: 0, cz: 0 },
            { dim: -1, cx: 1, cz: 1 },
          ],
        },
        aliases,
      ),
    ).toEqual({ rev: 1, blocks: [], columns: [{ dim: -1, cx: 1, cz: 1 }] });
  });
});

describe('SSE parser', () => {
  it('handles CRLF split across chunks, multi-line data, comments and ids', () => {
    const got: unknown[] = [];
    const comments: string[] = [];
    const p = new SseParser(
      (e) => got.push(e),
      (c) => comments.push(c),
    );
    p.push(': hi\r');
    p.push('\nevent: now\r\nid: 7\r\ndata: {"a":\r\ndata: 1}\r');
    p.push('\n\r\n');
    p.push('data: plain\n\n');
    expect(comments).toEqual(['hi']);
    expect(got).toEqual([
      { event: 'now', data: '{"a":\n1}', id: '7' },
      { event: 'message', data: 'plain', id: '7' },
    ]);
  });
});
