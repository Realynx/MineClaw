import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { LiveSocketSource } from '@/lib/live/socket';
import { LiveStream } from '@/lib/live/stream';

class FakeSocket {
  static all: FakeSocket[] = [];
  readyState = 0;
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: ((event: Event) => void) | null = null;
  constructor(readonly url: URL, readonly protocol: string) { FakeSocket.all.push(this); }
  open() { this.readyState = 1; this.onopen?.(new Event('open')); }
  frame(event: string, id: number, data: unknown) { this.onmessage?.(new MessageEvent('message', { data: JSON.stringify({ event, id, data }) })); }
  close() { this.readyState = 3; this.onclose?.(new Event('close')); }
}
let stream: LiveStream;
beforeEach(() => { vi.useFakeTimers(); FakeSocket.all = []; vi.stubGlobal('WebSocket', FakeSocket); });
afterEach(() => { stream?.stop(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it('uses one live socket for viewer events and replays from the last received event after reconnect', async () => {
  const message = vi.fn(), resync = vi.fn();
  stream = new LiveStream({ onMessage: message, onResync: resync }, { validate: false, minBackoffMs: 10 });
  stream.start(); stream.start();
  expect(FakeSocket.all).toHaveLength(1);
  const first = FakeSocket.all[0]!;
  expect(first.url.pathname).toBe('/api/live/socket');
  expect(first.protocol).toBe('mineclaw.live.v1');
  first.open(); first.frame('entities', 43, { rev: 7 });
  expect(message).toHaveBeenLastCalledWith('entities', { rev: 7 }, '43');
  first.close();
  await vi.advanceTimersByTimeAsync(20);
  const second = FakeSocket.all[1]!;
  expect(second.url.searchParams.get('lastEventId')).toBe('43');
  second.open();
  expect(resync).toHaveBeenCalledWith('reopened');
  first.frame('entities', 44, { rev: 0 });
  expect(message).toHaveBeenCalledTimes(1);
  second.frame('entities', 45, { rev: 8 });
  expect(message).toHaveBeenLastCalledWith('entities', { rev: 8 }, '45');
});

it('reconnects a silent connection and stops all reconnects on disposal', async () => {
  stream = new LiveStream({ onMessage: vi.fn() }, { validate: false, minBackoffMs: 10 });
  stream.start(); FakeSocket.all[0]!.open();
  await vi.advanceTimersByTimeAsync(50_100);
  expect(FakeSocket.all).toHaveLength(2);
  stream.stop();
  await vi.advanceTimersByTimeAsync(60_000);
  expect(FakeSocket.all).toHaveLength(2);
  expect(stream.current).toBe('closed');
});

it('hands the parsed payload to the listeners, so a frame is parsed once', () => {
  const message = vi.fn();
  stream = new LiveStream({ onMessage: message }, { validate: false });
  stream.start();
  const socket = FakeSocket.all[0]!;
  socket.open();
  const raw = new LiveSocketSource('/api/live/socket');
  const listener = vi.fn();
  raw.addEventListener('entities', listener);
  const parse = vi.spyOn(JSON, 'parse');
  try {
    socket.frame('entities', 46, { rev: 9 });
    expect(parse).toHaveBeenCalledTimes(1);
  } finally {
    parse.mockRestore();
  }
  expect(message).toHaveBeenLastCalledWith('entities', { rev: 9 }, '46');
  FakeSocket.all[1]!.frame('entities', 47, { rev: 10 });
  expect((listener.mock.calls[0]![0] as MessageEvent).data).toEqual({ rev: 10 });
  raw.close();
});
