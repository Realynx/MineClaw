/** Big live frames stay cheap in the hub: one format per frame, socket messages spliced not re-parsed, sampled contracts. */
import { EventEmitter } from 'node:events';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { formatSseFrame } from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type WebSocket from 'ws';
import { BridgeClient } from '../src/bridge/bridge-client.js';
import { BridgeHealth } from '../src/bridge/bridge-health.js';
import { createLogger } from '../src/common/logger.js';
import { contractSample } from '../src/live/contract-sample.js';
import { LiveHub } from '../src/live/live-hub.js';
import { SocketSink } from '../src/live/socket-sink.js';
import { SseParser } from '../src/live/sse-parser.js';

const log = createLogger('silent');

class FakeRes extends EventEmitter {
  writes: string[] = [];
  writableEnded = false;
  statusCode = 0;
  setHeader(): void {}
  flushHeaders(): void {}
  write(text: string): boolean {
    this.writes.push(text);
    return true;
  }
  end(): void {
    this.writableEnded = true;
  }
  destroy(): void {
    this.end();
  }
}

function fakeReq(): IncomingMessage {
  const req = new EventEmitter() as unknown as IncomingMessage;
  Object.assign(req, { headers: {}, socket: { setNoDelay() {}, setKeepAlive() {} } });
  return req;
}

function fakeSocket(): { socket: WebSocket; sent: string[] } {
  const sent: string[] = [];
  const socket = {
    readyState: 1,
    bufferedAmount: 0,
    send(text: string, cb: (error?: Error) => void) {
      sent.push(text);
      cb();
    },
    on() {},
    close() {},
    terminate() {},
  } as unknown as WebSocket;
  return { socket, sent };
}

/** A synthetic colony delta of the strip mine's shape: thousands of ghost tuples next to ordinary fields. */
function stripMineFrame(rows: number) {
  return {
    id: 'story-1',
    rev: 6_154_020,
    full: false,
    builds: [{ id: 'b300000', kind: 'mine', label: 'can\'t place é "here"', material: '', blocksTotal: 3920 }],
    ghosts: {
      rev: 6_154_020,
      blocks: Array.from({ length: rows }, (_, i) => [
        1680 + (i % 80),
        17,
        1026 + (i % 193),
        1,
        0,
        'b300000',
        'todo',
        'dig',
      ]),
    },
    pawns: [{ name: 'maid_chan_101', pos: [1800.5, 64, 1210.5], priorities: { mine: 1, build: 4 } }],
  };
}

/** The socket message as the sink built it before: the SSE text parsed, its data parsed and stringified again. */
function reparsed(sseText: string): string[] {
  const out: string[] = [];
  new SseParser((f) =>
    out.push(JSON.stringify({ event: f.event, id: f.id, data: JSON.parse(f.data) as unknown })),
  ).push(sseText);
  return out;
}

afterEach(() => vi.restoreAllMocks());

describe('socket sink', () => {
  it('sends a formatted frame as the same message, without parsing its data again', () => {
    const { socket, sent } = fakeSocket();
    const sink = new SocketSink(socket);
    const text = formatSseFrame('story', 41, stripMineFrame(5000));
    const parse = vi.spyOn(JSON, 'parse');
    sink.write(text);
    expect(parse).not.toHaveBeenCalled();
    parse.mockRestore();
    expect(sent).toEqual(reparsed(text));
  });

  it('every client of a frame gets the same message; the next frame gets its own', () => {
    const a = fakeSocket();
    const b = fakeSocket();
    const first = formatSseFrame('story', 1, stripMineFrame(10));
    const second = formatSseFrame('story', 2, stripMineFrame(11));
    new SocketSink(a.socket).write(first);
    new SocketSink(b.socket).write(first);
    new SocketSink(a.socket).write(second);
    expect(a.sent).toEqual([...reparsed(first), ...reparsed(second)]);
    expect(b.sent).toEqual(reparsed(first));
  });

  it('text that is not one whole formatted frame still goes through the SSE parser', () => {
    const { socket, sent } = fakeSocket();
    const sink = new SocketSink(socket);
    const frame = formatSseFrame('status', 3, { upstream: 'connected', resync: false });
    sink.write('retry: 3000\n\n');
    sink.write(': ping\n\n');
    sink.write(frame.slice(0, 20));
    sink.write(frame.slice(20));
    sink.write('event: feed\ndata: {"last":1}\n\n');
    expect(sent).toEqual([...reparsed(frame), JSON.stringify({ event: 'feed', id: '3', data: { last: 1 } })]);
  });
});

describe('hub fan-out', () => {
  function hub(opts = {}) {
    const bridge = new BridgeClient({ url: 'http://127.0.0.1:9', key: 'k'.repeat(32), timeoutMs: 50 }, log);
    return new LiveHub(bridge, new BridgeHealth(bridge, log), log, { backoffBaseMs: 10_000, ...opts });
  }

  it('formats a frame once for every client that gets it unfiltered', () => {
    const h = hub();
    const clients = [new FakeRes(), new FakeRes(), new FakeRes()];
    for (const res of clients)
      h.attach(fakeReq(), res as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    for (const res of clients) res.writes = [];
    const data = stripMineFrame(2000);
    const stringify = vi.spyOn(JSON, 'stringify');
    h.publish('story', data);
    expect(stringify).toHaveBeenCalledTimes(1);
    stringify.mockRestore();
    const expected = formatSseFrame('story', h.lastId, data);
    for (const res of clients) expect(res.writes).toEqual([expected]);
    h.close();
  });

  it('a filtered client still gets its own narrowed frame', () => {
    const h = hub();
    const all = new FakeRes();
    const nether = new FakeRes();
    h.attach(fakeReq(), all as unknown as ServerResponse, undefined, { username: 'u', stillValid: () => true });
    h.attach(fakeReq(), nether as unknown as ServerResponse, { dim: -1 }, { username: 'u', stillValid: () => true });
    all.writes = [];
    nether.writes = [];
    const data = {
      at: 1,
      rev: 1,
      full: false,
      bots: [],
      entities: [
        { id: 1, dim: 0 },
        { id: 2, dim: -1 },
      ],
    };
    h.publish('entities', data);
    expect(all.writes).toEqual([formatSseFrame('entities', h.lastId, data)]);
    expect(nether.writes).toEqual([formatSseFrame('entities', h.lastId, { ...data, entities: [{ id: 2, dim: -1 }] })]);
    h.close();
  });
});

describe('contract sample', () => {
  it('cuts bulk tuple arrays to their first rows and keeps everything else', () => {
    const frame = stripMineFrame(5000);
    const sample = contractSample(frame) as ReturnType<typeof stripMineFrame>;
    expect(sample.ghosts.blocks).toHaveLength(64);
    expect(sample.ghosts.blocks[0]).toBe(frame.ghosts.blocks[0]);
    expect(sample.ghosts.rev).toBe(frame.ghosts.rev);
    expect(sample.builds).toBe(frame.builds);
    expect(sample.pawns).toBe(frame.pawns);
    expect(frame.ghosts.blocks).toHaveLength(5000);
  });

  it('returns a payload with nothing bulky as it is', () => {
    const frame = stripMineFrame(64);
    expect(contractSample(frame)).toBe(frame);
    const objects = { storages: Array.from({ length: 500 }, (_, i) => ({ chest: [i, 64, 0] })) };
    expect(contractSample(objects)).toBe(objects);
  });
});
