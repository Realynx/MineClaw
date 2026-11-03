/**
 * `GET /api/live/stream`: ONE signed upstream `GET /v1/live/stream`, held open while at least one browser is connected
 * (closed after a short linger once the last one leaves), fanned out to every browser.
 *
 * - Upstream: reconnects with jittered exponential backoff and `Last-Event-ID` (the bridge's last id); a 45 s silence
 *   watchdog. After a reconnect every browser gets `status {resync: true}` so it re-fetches its REST cursors.
 * - Frames: payloads parsed with `parseBridgeJson`, camelCased, (dev/test) validated against `SSE_EVENTS`; Nest assigns its
 *   own monotonically increasing ids and keeps the last 256 frames for `Last-Event-ID` replay (older → `status resync`).
 * - Per browser: topic/bot/brain/dim filters, `retry: 3000`, `: ping` and a `status` frame every 15 s.
 * - Backpressure: when a socket's buffer is full the client is "congested": state topics coalesce (latest wins), deltas
 *   queue; past 512 KB the queue is dropped and the client gets `status {resync: true}` once it drains; a client congested
 *   for 30 s is disconnected (the browser reconnects with Last-Event-ID).
 * - Streams end when the session ends (checked every 60 s). No compression, `X-Accel-Buffering: no`.
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import {
  SSE_EVENTS,
  formatSseFrame,
  parseBridgeJson,
  toCamel,
  type LiveStatus,
  type LiveStreamQuery,
  type SseEventName,
} from '@mineclaw/contracts';
import type { z } from 'zod';
import { Errors, sendApiError } from '../common/errors.js';
import type { Logger } from '../common/logger.js';
import type { BridgeClient } from '../bridge/bridge-client.js';
import { readText } from '../bridge/bridge-client.js';
import type { BridgeHealth } from '../bridge/bridge-health.js';
import { NO_IDENTITY } from '../bridge/signing.js';
import { BotAliases, coalesceKey, filterOf, filterPayload, type ClientFilter } from './filters.js';
import { SseParser } from './sse-parser.js';

export interface LiveHubOptions {
  maxClients: number;
  maxClientsPerUser: number;
  ringSize: number;
  heartbeatMs: number;
  lingerMs: number;
  backoffBaseMs: number;
  backoffMaxMs: number;
  upstreamIdleMs: number;
  maxQueueBytes: number;
  maxCongestedMs: number;
  sessionCheckMs: number;
  validate: 'off' | 'warn' | 'strict';
}

export const DEFAULT_HUB_OPTIONS: LiveHubOptions = {
  maxClients: 200,
  maxClientsPerUser: 12,
  ringSize: 256,
  heartbeatMs: 15_000,
  lingerMs: 10_000,
  backoffBaseMs: 500,
  backoffMaxMs: 15_000,
  upstreamIdleMs: 45_000,
  maxQueueBytes: 512 * 1024,
  maxCongestedMs: 30_000,
  sessionCheckMs: 60_000,
  validate: 'off',
};

interface Frame {
  id: number;
  event: SseEventName;
  data: unknown;
}

interface Queued {
  key?: string;
  text: string;
}

interface Client {
  id: number;
  res: ServerResponse;
  username: string;
  filter: ClientFilter;
  stillValid: () => boolean;
  congested: boolean;
  congestedSince: number;
  queue: Queued[];
  queueBytes: number;
  resyncPending: boolean;
  closed: boolean;
}

export interface AttachOptions {
  username: string;
  stillValid: () => boolean;
}

type UpstreamState = LiveStatus['upstream'];

export class LiveHub {
  private readonly opts: LiveHubOptions;
  private readonly clients = new Map<number, Client>();
  private readonly ring: Frame[] = [];
  private readonly aliases = new BotAliases();
  private nextClientId = 1;
  private seq = 0;
  private evictedUpTo = 0;
  private upstream: UpstreamState = 'down';
  private lastUpstreamId: string | undefined;
  private wanted = false;
  private running: Promise<void> | undefined;
  private abort: AbortController | undefined;
  private wake: (() => void) | undefined;
  private lingerTimer: NodeJS.Timeout | undefined;
  private heartbeat: NodeJS.Timeout | undefined;
  private sessionTimer: NodeJS.Timeout | undefined;
  private everConnected = false;
  private closed = false;
  /** Connections to the bridge stream so far (tests assert there is exactly one). */
  upstreamConnects = 0;

  constructor(
    private readonly bridge: BridgeClient,
    private readonly health: BridgeHealth,
    private readonly log: Logger,
    opts: Partial<LiveHubOptions> = {},
  ) {
    this.opts = { ...DEFAULT_HUB_OPTIONS, ...opts };
  }

  get clientCount(): number {
    return this.clients.size;
  }

  get upstreamState(): UpstreamState {
    return this.upstream;
  }

  get lastId(): number {
    return this.seq;
  }

  status(resync = false, message?: string): LiveStatus {
    const h = this.health.lastState;
    return {
      at: new Date().toISOString(),
      upstream: this.upstream,
      bot: this.upstream === 'connected' && h?.up ? h.bot : 'unknown',
      latencyMs: this.health.latencyMs,
      resync,
      ...(message ? { message } : {}),
    };
  }

  // ---------------------------------------------------------------- browsers

  attach(req: IncomingMessage, res: ServerResponse, query: LiveStreamQuery | undefined, o: AttachOptions): void {
    if (this.closed || this.clients.size >= this.opts.maxClients) {
      sendApiError(res, Errors.busy(5000));
      return;
    }
    let mine = 0;
    for (const c of this.clients.values()) if (c.username === o.username) mine++;
    if (mine >= this.opts.maxClientsPerUser) {
      sendApiError(res, Errors.rateLimited(5000, 'too many live streams open'));
      return;
    }

    res.statusCode = 200;
    res.setHeader('Content-Type', 'text/event-stream; charset=utf-8');
    res.setHeader('Cache-Control', 'no-cache, no-transform');
    res.setHeader('Connection', 'keep-alive');
    res.setHeader('X-Accel-Buffering', 'no');
    res.flushHeaders();
    req.socket.setNoDelay(true);
    req.socket.setKeepAlive(true, 30_000);

    const client: Client = {
      id: this.nextClientId++,
      res,
      username: o.username,
      filter: filterOf(query),
      stillValid: o.stillValid,
      congested: false,
      congestedSince: 0,
      queue: [],
      queueBytes: 0,
      resyncPending: false,
      closed: false,
    };
    this.clients.set(client.id, client);
    const drop = () => this.detach(client);
    req.on('close', drop);
    res.on('close', drop);
    res.on('error', drop);

    this.write(client, 'retry: 3000\n\n');

    const lastHeader = req.headers['last-event-id'];
    const last = Array.isArray(lastHeader) ? lastHeader[0] : lastHeader;
    if (last !== undefined && last !== '') {
      const n = Number(last);
      if (!Number.isInteger(n) || n < this.evictedUpTo || n > this.seq) {
        this.sendStatus(client, true, 'missed frames; re-fetch');
      } else {
        this.sendStatus(client, false);
        for (const f of this.ring) if (f.id > n) this.deliver(client, f);
      }
    } else {
      this.sendStatus(client, false);
    }

    this.ensureTimers();
    if (this.lingerTimer) {
      clearTimeout(this.lingerTimer);
      this.lingerTimer = undefined;
    }
    this.start();
  }

  private detach(c: Client): void {
    if (c.closed) return;
    c.closed = true;
    this.clients.delete(c.id);
    if (!c.res.writableEnded) c.res.end();
    if (this.clients.size === 0 && !this.closed) {
      this.lingerTimer ??= setTimeout(() => {
        this.lingerTimer = undefined;
        if (this.clients.size === 0) this.stop();
      }, this.opts.lingerMs);
      this.lingerTimer.unref();
    }
  }

  private ensureTimers(): void {
    if (!this.heartbeat) {
      this.heartbeat = setInterval(() => this.tick(), this.opts.heartbeatMs);
      this.heartbeat.unref();
    }
    if (!this.sessionTimer) {
      this.sessionTimer = setInterval(() => this.checkSessions(), this.opts.sessionCheckMs);
      this.sessionTimer.unref();
    }
  }

  private tick(): void {
    if (this.clients.size === 0) return;
    void this.health.check().then(() => {
      const now = Date.now();
      for (const c of this.clients.values()) {
        if (c.congested && now - c.congestedSince > this.opts.maxCongestedMs) {
          this.log.info({ client: c.id }, 'dropping a live client that stayed congested');
          c.res.destroy();
          continue;
        }
        this.write(c, ': ping\n\n');
        this.sendStatus(c, false);
      }
    });
  }

  private checkSessions(): void {
    for (const c of this.clients.values()) {
      let ok: boolean;
      try {
        ok = c.stillValid();
      } catch {
        ok = false;
      }
      if (!ok) this.detach(c);
    }
  }

  private sendStatus(c: Client, resync: boolean, message?: string): void {
    // Status frames reuse the current id (no increment): replaying from it is always correct.
    this.write(c, formatSseFrame('status', this.seq, this.status(resync, message)), resync ? undefined : 'status');
  }

  private broadcastStatus(resync: boolean, message?: string): void {
    for (const c of this.clients.values()) this.sendStatus(c, resync, message);
  }

  private deliver(c: Client, f: Frame): void {
    const payload = filterPayload(c.filter, f.event, f.data, this.aliases);
    if (payload === null) return;
    this.write(c, formatSseFrame(f.event, f.id, payload), coalesceKey(f.event, payload));
  }

  private write(c: Client, text: string, key?: string): void {
    if (c.closed) return;
    if (c.congested) {
      this.enqueue(c, text, key);
      return;
    }
    const ok = c.res.write(text);
    if (!ok) {
      c.congested = true;
      c.congestedSince = Date.now();
      c.res.once('drain', () => this.drain(c));
    }
  }

  private enqueue(c: Client, text: string, key?: string): void {
    if (c.resyncPending) return; // everything will be re-fetched anyway
    if (key?.endsWith(':full')) {
      const prefix = `event: ${key.slice(0, -':full'.length)}\n`;
      c.queue = c.queue.filter((q) => !q.text.startsWith(prefix));
      c.queueBytes = c.queue.reduce((n, q) => n + q.text.length, 0);
    }
    if (key) {
      const i = c.queue.findIndex((q) => q.key === key);
      if (i >= 0) {
        c.queueBytes -= c.queue[i]!.text.length;
        c.queue.splice(i, 1);
      }
    }
    c.queue.push({ key, text });
    c.queueBytes += text.length;
    if (c.queueBytes > this.opts.maxQueueBytes) {
      c.queue = [];
      c.queueBytes = 0;
      c.resyncPending = true;
    }
  }

  private drain(c: Client): void {
    if (c.closed) return;
    c.congested = false;
    if (c.resyncPending) {
      c.resyncPending = false;
      c.queue = [];
      c.queueBytes = 0;
      this.sendStatus(c, true, 'stream fell behind; re-fetch');
      return;
    }
    const queue = c.queue;
    c.queue = [];
    c.queueBytes = 0;
    for (let i = 0; i < queue.length; i++) {
      if (c.congested) {
        for (const q of queue.slice(i)) this.enqueue(c, q.text, q.key);
        return;
      }
      this.write(c, queue[i]!.text, queue[i]!.key);
    }
  }

  // ---------------------------------------------------------------- frames

  /** Learns bot name → short aliases from a REST reply (e.g. `/api/bots`), so `?bots=main` works from the first frame. */
  learn(payload: unknown): void {
    this.aliases.learn(payload);
  }

  /** Adds a frame (public, camelCase) to the ring and fans it out. */
  publish(event: SseEventName, data: unknown): void {
    if (event === 'status') return;
    this.aliases.learn(data);
    const f: Frame = { id: ++this.seq, event, data };
    this.ring.push(f);
    while (this.ring.length > this.opts.ringSize) this.evictedUpTo = this.ring.shift()!.id;
    for (const c of this.clients.values()) this.deliver(c, f);
  }

  private onUpstreamEvent(event: string, data: string, id: string | undefined): void {
    if (id) this.lastUpstreamId = id;
    if (event === 'status' || !(event in SSE_EVENTS)) return;
    const name = event as SseEventName;
    let payload: unknown;
    try {
      payload = toCamel(parseBridgeJson(data));
    } catch {
      this.log.warn({ event }, 'bridge sent an unparseable SSE frame');
      return;
    }
    if (this.opts.validate !== 'off') {
      const r = (SSE_EVENTS[name] as z.ZodType).safeParse(payload);
      if (!r.success) {
        this.log.warn({ event, issues: r.error.issues.slice(0, 5) }, 'SSE frame does not match the contract');
        if (this.opts.validate === 'strict') return;
      }
    }
    this.publish(name, payload);
  }

  // ---------------------------------------------------------------- upstream

  private setUpstream(s: UpstreamState, resync = false): void {
    if (this.upstream === s && !resync) return;
    this.upstream = s;
    this.broadcastStatus(resync, resync ? 'live stream reconnected; re-fetch' : undefined);
  }

  private start(): void {
    this.wanted = true;
    this.running ??= this.run().finally(() => {
      this.running = undefined;
      if (this.wanted && !this.closed) this.start();
    });
  }

  private stop(): void {
    this.wanted = false;
    this.abort?.abort();
    this.wake?.();
    this.everConnected = false;
    this.upstream = 'down';
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => {
      const t = setTimeout(resolve, ms);
      t.unref();
      this.wake = () => {
        clearTimeout(t);
        resolve();
      };
    });
  }

  private async run(): Promise<void> {
    let attempt = 0;
    while (this.wanted && !this.closed) {
      this.setUpstream(this.everConnected ? 'reconnecting' : 'connecting');
      const ac = new AbortController();
      this.abort = ac;
      try {
        const res = await this.bridge.request({
          method: 'GET',
          target: '/v1/live/stream',
          identity: NO_IDENTITY,
          headers: {
            Accept: 'text/event-stream',
            ...(this.lastUpstreamId ? { 'Last-Event-ID': this.lastUpstreamId } : {}),
          },
          timeoutMs: 10_000,
          idleTimeoutMs: this.opts.upstreamIdleMs,
          stream: true,
          signal: ac.signal,
        });
        const type = String(res.headers['content-type'] ?? '');
        if (res.statusCode !== 200 || !type.includes('text/event-stream')) {
          await readText(res, 64 * 1024).catch(() => '');
          throw new Error(`bridge live stream answered ${res.statusCode}`);
        }
        this.upstreamConnects++;
        const reconnect = this.everConnected;
        this.everConnected = true;
        attempt = 0;
        this.setUpstream('connected', reconnect);
        void this.health.check(true).then(() => this.broadcastStatus(false));
        const parser = new SseParser((e) => this.onUpstreamEvent(e.event, e.data, e.id));
        res.setEncoding('utf8');
        await new Promise<void>((resolve) => {
          res.on('data', (chunk: string) => parser.push(chunk));
          res.on('end', resolve);
          res.on('error', () => resolve());
          res.on('close', resolve);
        });
      } catch (e) {
        if (!this.wanted || this.closed) break;
        this.log.warn({ err: e instanceof Error ? e.message : String(e) }, 'bridge live stream failed');
      }
      if (!this.wanted || this.closed) break;
      this.setUpstream('reconnecting');
      const delay = Math.min(this.opts.backoffMaxMs, this.opts.backoffBaseMs * 2 ** attempt);
      attempt++;
      await this.sleep(delay / 2 + Math.random() * (delay / 2));
    }
  }

  close(): void {
    this.closed = true;
    this.stop();
    if (this.heartbeat) clearInterval(this.heartbeat);
    if (this.sessionTimer) clearInterval(this.sessionTimer);
    if (this.lingerTimer) clearTimeout(this.lingerTimer);
    for (const c of [...this.clients.values()]) this.detach(c);
  }
}
