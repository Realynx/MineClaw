import { SSE_EVENT_NAMES } from '@mineclaw/contracts';
import type { EventSourceLike } from './stream';

/** Persistent viewer feed. LiveStream owns reconnect/backoff and the query cache owns resync. */
export class LiveSocketSource implements EventSourceLike {
  onopen: ((event: Event) => unknown) | null = null;
  onerror: ((event: Event) => unknown) | null = null;
  private readonly listeners = new Map<string, Array<(event: MessageEvent) => void>>();
  private readonly socket: WebSocket;
  private readonly watchdog: ReturnType<typeof setInterval>;
  private lastFrame = Date.now();
  private stopped = false;
  constructor(path: string, lastEventId: string | null = null) {
    const url = new URL(path, window.location.href);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    if (lastEventId) url.searchParams.set('lastEventId', lastEventId);
    this.socket = new WebSocket(url, 'mineclaw.live.v1');
    this.socket.onopen = (event) => { this.lastFrame = Date.now(); this.onopen?.(event); };
    this.socket.onmessage = (event) => {
      this.lastFrame = Date.now();
      try {
        const frame = JSON.parse(String(event.data)) as { event: string; id?: string | number; data: unknown };
        if (!(SSE_EVENT_NAMES as readonly string[]).includes(frame.event)) return;
        const message = new MessageEvent(frame.event, { data: JSON.stringify(frame.data), lastEventId: String(frame.id ?? '') });
        for (const listener of this.listeners.get(frame.event) ?? []) listener(message);
      } catch { /* A malformed frame cannot replace the last valid world state. */ }
    };
    this.socket.onclose = (event) => { clearInterval(this.watchdog); if (!this.stopped) this.onerror?.(event); };
    this.watchdog = setInterval(() => { if (Date.now() - this.lastFrame > 45_000) this.socket.close(); }, 5000);
  }
  get readyState(): number { return this.socket.readyState < 2 ? this.socket.readyState : 2; }
  addEventListener(name: string, listener: (event: MessageEvent) => void): void {
    const list = this.listeners.get(name) ?? [];
    list.push(listener); this.listeners.set(name, list);
  }
  close(): void { this.stopped = true; clearInterval(this.watchdog); this.socket.close(); this.listeners.clear(); }
}
