/** Native MSW socket fixture shares the HTTP-acquired lease, movement and inventory state. */
import { PawnControlActionRequest, PawnControlInputRequest, PawnControlReleaseRequest, toCamel, toSnake } from '@mineclaw/contracts';
import { ws, type WebSocketHandlerConnection } from 'msw';
import { checkAccess, currentUser, pawnControl } from './api';
import { rng } from './util';

// The supervisor serializes the entire typed envelope, including its `data` payload, as snake_case.
const controlCase = { opaque: new Set<string>() };

/**
 * A slow link, for judging the takeover camera by hand. Set either before taking control:
 * `sessionStorage['mcx-mock-control-lag'] = '150'` adds that many ms of round trip, half before a frame from the browser
 * reaches the pawn and half before a frame from the pawn reaches the browser;
 * `sessionStorage['mcx-mock-control-jitter'] = '30'` makes every frame up to that many ms later still.
 * As without them, an input also waits up to 50 ms for the pawn's tick and its state up to 50 ms for the next frame.
 */
function slowLink(): { lag: number; jitter: number } {
  const ms = (key: string, most: number) => {
    try {
      const value = Number(sessionStorage.getItem(key));
      return value > 0 ? Math.min(value, most) : 0;
    } catch { return 0; }
  };
  return { lag: ms('mcx-mock-control-lag', 2000), jitter: ms('mcx-mock-control-jitter', 500) };
}

export function connectMockControl({ client, info }: Pick<WebSocketHandlerConnection, 'client' | 'info'>): void {
  const protocols = Array.isArray(info.protocols) ? info.protocols : [info.protocols ?? ''];
  const csrf = protocols.find((p) => p.startsWith('csrf.'))?.slice(5) ?? null;
  const owner = currentUser()?.username, body = client.url.pathname.split('/')[3];
  const authorize = () => {
    checkAccess('bodies.controlInput', csrf, 'POST');
    if (currentUser()?.username !== owner) throw new Error('Control session ended');
  };
  try {
    authorize();
    if (!protocols.includes('mineclaw.control.v1') || client.url.search) throw new Error('Invalid control connection');
  } catch { client.close(1008, 'Control connection refused'); return; }

  let session: string | null = null, inputSeq = -1, ended = false;
  const created = Date.now();
  const link = slowLink(), late = rng(70), inFlight = new Set<ReturnType<typeof setTimeout>>();
  /** One direction of the link. Frames arrive in the order they were sent, as on a real socket: a timer that fires
   * hands over the oldest frame still on its way, whichever frame set it. */
  const lane = () => {
    const waiting: Array<() => void> = [];
    return (deliver: () => void) => {
      const delay = Math.round(link.lag / 2 + late() * link.jitter);
      if (!delay && !waiting.length) { deliver(); return; }
      waiting.push(deliver);
      const arrival = setTimeout(() => { inFlight.delete(arrival); waiting.shift()?.(); }, delay);
      inFlight.add(arrival);
    };
  };
  const toPawn = lane(), toBrowser = lane();
  const send = (frame: unknown) => {
    const text = JSON.stringify(toSnake(frame, controlCase));
    toBrowser(() => client.send(text));
  };
  const snapshot = () => {
    authorize();
    if (!session) throw new Error('Attach a control session first');
    const state = pawnControl.snapshot(session);
    if (state.body !== body || Date.parse(state.expiresAt) <= Date.now()) throw new Error('Control session ended');
    return state;
  };
  const state = (inventory = false) => {
    const value = snapshot();
    if (!inventory) value.inventory = null;
    value.action ??= null;
    send({ type: 'state', sampledAtMs: Date.now(), data: value });
  };
  const fail = (error: unknown) => {
    if (ended) return;
    ended = true;
    clearInterval(timer);
    send({ type: 'error', message: error instanceof Error ? error.message : 'Invalid control frame' });
    toBrowser(() => client.close(1008, 'Control ended'));
  };
  const timer = setInterval(() => {
    try {
      if (session) state();
      else if (Date.now() - created > 3000) throw new Error('Control attach timed out');
    } catch (error) { fail(error); }
  }, 50);
  client.addEventListener('message', (event) => toPawn(() => {
    if (ended) return;
    try {
      if (typeof event.data !== 'string' || event.data.length > 8192) throw new Error('Invalid control frame');
      const frame = toCamel<Record<string, unknown>>(JSON.parse(event.data) as unknown);
      const { type, requestId, ...command } = frame;
      if (type === 'attach') {
        if (session) throw new Error('Control session already attached');
        const requested = PawnControlReleaseRequest.parse(command).session;
        if (pawnControl.snapshot(requested).body !== body) throw new Error('Wrong control body');
        session = requested;
        state(true);
      } else {
        snapshot();
        if (frame.session !== session) throw new Error('Wrong control session');
        if (type === 'input') {
          const input = PawnControlInputRequest.parse(command);
          if (input.seq > inputSeq) { inputSeq = input.seq; pawnControl.input(input); }
        } else if (type === 'action') {
          if (typeof requestId !== 'string' || !requestId || requestId.length > 128) throw new Error('Missing action id');
          send({ type: 'action', requestId, data: pawnControl.action(PawnControlActionRequest.parse(command)) });
        } else throw new Error('Unknown control frame');
      }
    } catch (error) { fail(error); }
  }));
  client.addEventListener('close', () => {
    ended = true;
    clearInterval(timer);
    for (const arrival of inFlight) clearTimeout(arrival);
    inFlight.clear();
    if (session && pawnControl.current?.session === session) pawnControl.release(session);
  });
}

export const controlSocket = ws.link(/^wss?:\/\/[^/]+\/api\/bodies\/[A-Za-z0-9_-]{1,64}\/control\/socket(?:\?.*)?$/)
  .addEventListener('connection', connectMockControl);
