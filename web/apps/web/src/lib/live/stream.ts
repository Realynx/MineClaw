/**
 * The one SSE connection of the tab (`GET /api/live/stream`). Every widget reads the query cache the stream feeds; nothing
 * opens its own connection or poller.
 *
 * Reconnects:
 * - Transient drops: the browser's EventSource reconnects by itself and sends `Last-Event-ID`; Nest replays the missed frames
 *   (or sends `status` with `resync: true` when the id is too old).
 * - Hard failures (the EventSource closed, e.g. a 401/502 on reconnect): we open a new EventSource with exponential back-off
 *   plus jitter. A new EventSource cannot send `Last-Event-ID`, so `onResync` asks the app to re-fetch its REST state.
 */
import { SSE_EVENT_NAMES, SSE_EVENTS, type SseEventName } from '@mineclaw/contracts';

export type LiveEventName = SseEventName;

export type StreamState = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'closed';

export interface StreamHandlers {
  onMessage(event: LiveEventName, data: unknown, id: string): void;
  onState?(state: StreamState): void;
  /** The client may have missed frames: re-fetch REST state for the cursor topics. */
  onResync?(reason: 'reopened' | 'server'): void;
}

export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((ev: Event) => unknown) | null;
  onerror: ((ev: Event) => unknown) | null;
  addEventListener(type: string, listener: (ev: MessageEvent) => void): void;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

const CLOSED = 2;
const DEV = process.env.NODE_ENV !== 'production';

export interface LiveStreamOptions {
  url?: string;
  factory?: EventSourceFactory;
  /** Validate payloads with the contract schemas (default: development builds only). */
  validate?: boolean;
  minBackoffMs?: number;
  maxBackoffMs?: number;
}

export class LiveStream {
  private es: EventSourceLike | null = null;
  private state: StreamState = 'idle';
  private attempt = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private opened = false;
  private stopped = true;
  lastEventId: string | null = null;

  private readonly url: string;
  private readonly factory: EventSourceFactory;
  private readonly validate: boolean;
  private readonly minBackoff: number;
  private readonly maxBackoff: number;

  constructor(
    private readonly handlers: StreamHandlers,
    opts: LiveStreamOptions = {},
  ) {
    this.url = opts.url ?? '/api/live/stream';
    this.factory = opts.factory ?? ((url) => new EventSource(url, { withCredentials: true }));
    this.validate = opts.validate ?? DEV;
    this.minBackoff = opts.minBackoffMs ?? 1000;
    this.maxBackoff = opts.maxBackoffMs ?? 15000;
  }

  get current(): StreamState {
    return this.state;
  }

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    this.open();
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.es?.close();
    this.es = null;
    this.setState('closed');
  }

  private setState(s: StreamState): void {
    if (this.state === s) return;
    this.state = s;
    this.handlers.onState?.(s);
  }

  private open(): void {
    const reopening = this.opened;
    this.setState(reopening ? 'reconnecting' : 'connecting');
    const es = this.factory(this.url);
    this.es = es;
    es.onopen = () => {
      if (this.es !== es) return;
      this.attempt = 0;
      this.setState('open');
      if (reopening) this.handlers.onResync?.('reopened');
      this.opened = true;
    };
    es.onerror = () => {
      if (this.es !== es || this.stopped) return;
      if (es.readyState === CLOSED) {
        es.close();
        this.es = null;
        this.opened = true;
        this.scheduleReopen();
      } else {
        // The browser is retrying on its own (and will send Last-Event-ID).
        this.setState('reconnecting');
      }
    };
    for (const name of SSE_EVENT_NAMES) {
      es.addEventListener(name, (ev) => this.receive(name, ev));
    }
  }

  private scheduleReopen(): void {
    this.setState('reconnecting');
    const base = Math.min(this.maxBackoff, this.minBackoff * 2 ** this.attempt);
    const jitter = base * (0.75 + Math.random() * 0.5);
    this.attempt++;
    this.timer = setTimeout(() => {
      this.timer = null;
      if (!this.stopped) this.open();
    }, jitter);
  }

  private receive(name: LiveEventName, ev: MessageEvent): void {
    if (ev.lastEventId) this.lastEventId = ev.lastEventId;
    let data: unknown;
    try {
      data = JSON.parse(String(ev.data)) as unknown;
    } catch {
      console.warn(`[live] ${name}: frame is not JSON`);
      return;
    }
    const parsed = SSE_EVENTS[name].safeParse(data);
    if (!parsed.success) {
      if (this.validate) {
        console.error(`[live] ${name}: payload does not match the contract`, parsed.error.issues);
        return;
      }
    } else {
      data = parsed.data;
    }
    this.handlers.onMessage(name, data, ev.lastEventId);
    if (name === 'status' && (data as { resync?: boolean }).resync) this.handlers.onResync?.('server');
  }
}
