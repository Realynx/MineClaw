import { PawnControlActionResponse, PawnControlSession, toCamel, toSnake, type PawnControlActionRequest } from '@mineclaw/contracts';
import { z } from 'zod';
import type { PlayerInput } from './playerInput';

const Incoming = z.discriminatedUnion('type', [
  z.object({ type: z.literal('state'), data: PawnControlSession, sampledAtMs: z.number().optional() }),
  z.object({ type: z.literal('action'), requestId: z.string(), data: PawnControlActionResponse }),
  z.object({ type: z.literal('error'), message: z.string() }),
]);
type PendingAction = { resolve: (reply: PawnControlActionResponse) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> };

/** One ordered stream; neither movement nor mouse release waits for an action acknowledgement. */
export class PawnControlSocket {
  private readonly socket: WebSocket;
  private readonly pending = new Map<string, PendingAction>();
  private readonly watchdog: ReturnType<typeof setInterval>;
  private stopped = false;
  private ready = false;
  private inputSeq = 0;
  private lastState = performance.now();
  private leaseMs: number;
  get active(): boolean { return this.ready && !this.stopped; }

  constructor(body: string, private readonly session: PawnControlSession, csrf: string,
    private readonly onState: (reply: PawnControlSession, at: number, sampledAtMs?: number) => void,
    private readonly onEnd: (reason: string) => void) {
    this.leaseMs = session.leaseTimeoutMs;
    const url = new URL(`/api/bodies/${encodeURIComponent(body)}/control/socket`, location.href);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    this.socket = new WebSocket(url.href, ['mineclaw.control.v1', `csrf.${csrf}`]);
    this.socket.onopen = () => { this.send({ type: 'attach', session: session.session }); };
    this.socket.onmessage = (event: MessageEvent<unknown>) => this.receive(event.data);
    this.socket.onerror = () => this.fail('The control connection failed. Take control again.');
    this.socket.onclose = () => this.fail('The control connection closed. Take control again.');
    this.watchdog = setInterval(() => {
      if (performance.now() - this.lastState >= Math.max(1, this.leaseMs - 250)) this.fail('Control updates timed out. Take control again.');
    }, 50);
  }

  input(input: PlayerInput): boolean {
    return this.ready && this.send({ type: 'input', session: this.session.session, seq: ++this.inputSeq, ...input });
  }

  action(command: Omit<PawnControlActionRequest, 'session'>): Promise<PawnControlActionResponse> {
    if (!this.ready || this.stopped) return Promise.reject(new Error('The control connection is not ready.'));
    if (this.pending.size >= 32) { this.fail('Too many control actions are waiting. Take control again.'); return Promise.reject(new Error('Control ended.')); }
    const requestId = String(command.seq);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.fail('A control action timed out. Take control again.'), this.leaseMs);
      this.pending.set(requestId, { resolve, reject, timer });
      this.send({ type: 'action', requestId, session: this.session.session, ...command });
    });
  }

  dispose(): void {
    if (this.stopped) return;
    this.stopped = true;
    clearInterval(this.watchdog);
    this.socket.onopen = this.socket.onmessage = this.socket.onerror = this.socket.onclose = null;
    this.socket.close();
    for (const action of this.pending.values()) { clearTimeout(action.timer); action.reject(new Error('Control ended.')); }
    this.pending.clear();
  }

  private send(value: unknown): boolean {
    if (this.stopped) return false;
    if (this.socket.readyState !== WebSocket.OPEN || this.socket.bufferedAmount > 16_384) {
      this.fail('The control connection cannot keep up. Take control again.'); return false;
    }
    try { this.socket.send(JSON.stringify(toSnake(value))); return true; }
    catch { this.fail('The control connection closed. Take control again.'); return false; }
  }

  private receive(raw: unknown): void {
    if (this.stopped) return;
    try {
      if (typeof raw !== 'string' || raw.length > 262_144) throw new Error('Invalid control frame.');
      const message = Incoming.parse(toCamel(JSON.parse(raw) as unknown));
      if (message.type === 'error') { this.fail(message.message); return; }
      if (message.type === 'state') {
        if (message.data.session !== this.session.session) throw new Error('Wrong control session.');
        this.lastState = performance.now(); this.leaseMs = message.data.leaseTimeoutMs; this.ready = true;
        this.onState(message.data, this.lastState, message.sampledAtMs);
      } else {
        const pending = this.pending.get(message.requestId);
        if (!pending) return;
        this.pending.delete(message.requestId); clearTimeout(pending.timer); pending.resolve(message.data);
      }
    } catch { this.fail('Invalid control update. Take control again.'); }
  }

  private fail(reason: string): void {
    if (this.stopped) return;
    this.dispose(); this.onEnd(reason);
  }
}
