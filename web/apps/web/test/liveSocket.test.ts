import { afterEach, beforeEach, expect, it, vi } from 'vitest';
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
