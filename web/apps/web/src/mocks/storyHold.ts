/**
 * The mock's hook for colony writes: `window.__mockStory` (mock mode only), for tests that need a slow or a refusing colony.
 * The mock answers in this page (the service worker hands each request to it), so a reply can only be held here.
 *
 * - `hold()`: from now on a colony write waits before it is applied, as a command waits for the director. Nothing changes and
 *   nothing is answered until `release()`, which lets the waiting writes through in the order they came.
 * - `failNext(status, message)`: the next colony write is refused and not applied, answered as Nest relays the bot's refusal
 *   (each call arms one refusal). A 503 whose message says "busy" is the colony's definite "not applied": `code: busy`,
 *   `retryAfterMs` and a `Retry-After` header. Another 503 is "unavailable" and another 5xx an upstream error: answers the
 *   page reads as "outcome unknown".
 *
 * A colony write is `POST /api/story/colonies/{id}/{action}`, what the page sends through `write`. Directives, the time of
 * day and founding a colony are not held: a command sends its directive beside its write, and one refusal must hit one request.
 */
import { ROUTES, type RouteId } from '@mineclaw/contracts';
import { MockError } from './api';

export interface MockStoryHook {
  hold(): void;
  release(): void;
  failNext(status: number, message: string): void;
}

/** What Nest's `busy` and `rate_limited` answers carry: retry in a second. */
const RETRY = { extra: { retryAfterMs: 1000 }, headers: { 'Retry-After': '1' } };

const REFUSAL_CODES: Record<number, string> = {
  403: 'forbidden',
  404: 'not_found',
  409: 'conflict',
  413: 'payload_too_large',
};

/**
 * A bot refusal as it reaches the browser: the bridge names a 503 `busy` when its text says so, and Nest then answers its
 * own busy text; other 5xx answers never carry the bot's words (web/apps/api/src/bridge/bridge-client.ts).
 */
function relayed(status: number, message: string): MockError {
  if (status === 503 && /busy/i.test(message))
    return new MockError(503, 'busy; retry shortly', 'busy', RETRY.extra, RETRY.headers);
  if (status === 503) return new MockError(503, 'bridge or bot unavailable', 'upstream_unavailable');
  if (status >= 500) return new MockError(502, 'upstream error', 'upstream_error');
  if (status === 429) return new MockError(429, message, 'rate_limited', RETRY.extra, RETRY.headers);
  return new MockError(status, message, REFUSAL_CODES[status] ?? 'bad_request');
}

let holding = false;
let waiting: Array<() => void> = [];
const refusals: MockError[] = [];

export const mockStory: MockStoryHook = {
  hold() {
    holding = true;
  },
  release() {
    holding = false;
    const go = waiting;
    waiting = [];
    for (const resume of go) resume();
  },
  failNext(status, message) {
    if (!Number.isInteger(status) || status < 400 || status > 599)
      throw new RangeError(`failNext: status must be 400..599, got ${status}`);
    refusals.push(relayed(status, message));
  },
};

export function isColonyWrite(id: RouteId): boolean {
  const route = ROUTES[id];
  return route.method === 'POST' && route.path.startsWith('/api/story/colonies/:id/');
}

/**
 * A colony write passes here before the mock applies it: it waits while writes are held, then throws the next armed refusal
 * (so a refusal armed while a write waits refuses that write when it is released). Other routes pass at once.
 */
export async function colonyWriteGate(id: RouteId): Promise<void> {
  if (!isColonyWrite(id)) return;
  if (holding) await new Promise<void>((resolve) => waiting.push(resolve));
  const refusal = refusals.shift();
  if (refusal) throw refusal;
}

if (typeof window !== 'undefined') (window as unknown as { __mockStory?: MockStoryHook }).__mockStory = mockStory;
