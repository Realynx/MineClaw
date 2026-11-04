/**
 * Signed pings of the bridge `/v1/health` (Nest's own identity: empty identity headers). Feeds `GET /api/health` and the
 * `status` SSE event (latency EWMA, bot reachability when the bridge reports it). Results are cached for a few seconds.
 */
import { parseBridgeJson } from '@mineclaw/contracts';
import type { Logger } from '../common/logger.js';
import type { BridgeClient } from './bridge-client.js';
import { NO_IDENTITY } from './signing.js';

export interface BridgeHealthState {
  up: boolean;
  bot: 'up' | 'down' | 'unknown';
  latencyMs: number | null;
  at: number;
}

const CACHE_MS = 5000;

type BotState = 'up' | 'down' | 'unknown';

const upDown = (ok: boolean): BotState => (ok ? 'up' : 'down');

/** One health field: 'up' / 'down', a boolean, or an object with a boolean `ok` (or `up`); null when it says nothing. */
function fieldState(v: unknown): BotState | null {
  if (v === 'up' || v === 'down') return v;
  if (typeof v === 'boolean') return upDown(v);
  if (!v || typeof v !== 'object') return null;
  const ok = (v as Record<string, unknown>).ok ?? (v as Record<string, unknown>).up;
  return typeof ok === 'boolean' ? upDown(ok) : null;
}

function botState(body: unknown): BotState {
  if (!body || typeof body !== 'object') return 'unknown';
  const b = body as Record<string, unknown>;
  const state = fieldState(b.supervisor) ?? fieldState(b.bot);
  if (state) return state;
  return typeof b.supervisor_ok === 'boolean' ? upDown(b.supervisor_ok) : 'unknown';
}

export class BridgeHealth {
  private last: BridgeHealthState | undefined;
  private inflight: Promise<BridgeHealthState> | undefined;
  private ewma: number | null = null;

  constructor(
    private readonly bridge: BridgeClient,
    private readonly log: Logger,
  ) {}

  get latencyMs(): number | null {
    return this.ewma === null ? null : Math.round(this.ewma);
  }

  get lastState(): BridgeHealthState | undefined {
    return this.last;
  }

  check(force = false): Promise<BridgeHealthState> {
    if (!force && this.last && Date.now() - this.last.at < CACHE_MS) return Promise.resolve(this.last);
    this.inflight ??= this.ping().finally(() => {
      this.inflight = undefined;
    });
    return this.inflight;
  }

  private async ping(): Promise<BridgeHealthState> {
    const t0 = performance.now();
    let state: BridgeHealthState;
    try {
      const r = await this.bridge.json({ method: 'GET', target: '/v1/health', identity: NO_IDENTITY, timeoutMs: 2500 });
      const ms = performance.now() - t0;
      const up = r.status >= 200 && r.status < 300;
      let body: unknown;
      try {
        body = parseBridgeJson(r.text);
      } catch {
        body = undefined;
      }
      if (up) this.ewma = this.ewma === null ? ms : this.ewma * 0.7 + ms * 0.3;
      state = { up, bot: up ? botState(body) : 'unknown', latencyMs: this.latencyMs, at: Date.now() };
    } catch {
      state = { up: false, bot: 'unknown', latencyMs: this.latencyMs, at: Date.now() };
    }
    if (this.last?.up !== state.up) this.log.info({ bridge: state.up ? 'up' : 'down' }, 'bridge health changed');
    this.last = state;
    return state;
  }
}
