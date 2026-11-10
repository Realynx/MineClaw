/**
 * The one live WebSocket of the tab (`/api/live/socket`). Every widget reads the query cache the stream feeds; nothing
 * opens its own connection or poller.
 *
 * Reconnects:
 * - Transient drops: reconnect with the last event cursor; Nest replays the missed frames
 *   (or sends `status` with `resync: true` when the id is too old).
 * - Hard failures: reconnect with exponential backoff plus jitter. `onResync` also refreshes REST cursors after a gap.
 */
import { SSE_EVENT_NAMES, SSE_EVENTS, type SseEventName } from '@mineclaw/contracts';
import { LiveSocketSource } from './socket';

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

/**
 * Topics whose schemas fill no defaults and transform nothing, so a parsed frame equals the raw one. They arrive up to ten
 * times a second; with validation off only one frame in `SAMPLE_EVERY` is checked, and a rejected sample is logged at most
 * once a minute. Every other topic is always parsed (the Story schemas fill defaults in production too).
 */
const SAMPLED = new Set<LiveEventName>(['entities', 'blocks']);
const SAMPLE_EVERY = 100;
const WARN_EVERY_MS = 60_000;

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
  private sampled = 0;
  private warnedAt = -Infinity;
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
    this.url = opts.url ?? '/api/live/socket';
    this.factory = opts.factory ?? ((url) => new LiveSocketSource(url, this.lastEventId));
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
      es.addEventListener(name, (ev) => {
        if (this.es === es && !this.stopped) this.receive(name, ev);
      });
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
    // The live socket hands the parsed payload; an EventSource (and the tests) hand the JSON text.
    let data: unknown = ev.data;
    if (typeof data === 'string') {
      try {
        data = JSON.parse(data) as unknown;
      } catch {
        console.warn(`[live] ${name}: frame is not JSON`);
        return;
      }
    }
    if (this.checks(name)) {
      const parsed = SSE_EVENTS[name].safeParse(data);
      if (!parsed.success) {
        if (this.validate) {
          console.error(`[live] ${name}: payload does not match the contract`, parsed.error.issues);
          return;
        }
        if (SAMPLED.has(name)) this.warnSample(name, parsed.error.issues);
      } else {
        data = parsed.data;
      }
    }
    this.handlers.onMessage(name, data, ev.lastEventId);
    if (name === 'status' && (data as { resync?: boolean }).resync) this.handlers.onResync?.('server');
  }

  /** Whether this frame is checked against its schema (always, except the sampled topics with validation off). */
  private checks(name: LiveEventName): boolean {
    if (this.validate || !SAMPLED.has(name)) return true;
    return ++this.sampled % SAMPLE_EVERY === 0;
  }

  private warnSample(name: LiveEventName, issues: unknown): void {
    const now = Date.now();
    if (now - this.warnedAt < WARN_EVERY_MS) return;
    this.warnedAt = now;
    console.warn(`[live] ${name}: a sampled payload does not match the contract`, issues);
  }
}
