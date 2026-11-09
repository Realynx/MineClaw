import type { IncomingMessage } from 'node:http';
import type { Socket } from 'node:net';
import WebSocket, { WebSocketServer } from 'ws';
import { hasRole, LiveStreamQuery } from '@mineclaw/contracts';
import type { AuthService } from '../auth/auth.service.js';
import { tokensEqual } from '../auth/sessions.js';
import { identityOf } from '../bridge/signing.js';
import type { BridgeClient } from '../bridge/bridge-client.js';
import { contextOf, parseQuery } from '../common/http.js';
import type { LiveHub } from './live-hub.js';

const CONTROL = /^\/api\/bodies\/([A-Za-z0-9_-]{1,64})\/control\/socket$/;
const normalize = (origin: string) => origin.trim().replace(/\/+$/, '').toLowerCase();

/** Authenticate before upgrade; relay ordered control frames without request/response gating. */
export class SocketGateway {
  private readonly server = new WebSocketServer({ noServer: true, maxPayload: 8192, perMessageDeflate: false,
    handleProtocols: (_offered, req) => CONTROL.test((req.url ?? '').split('?')[0]!) ? 'mineclaw.control.v1' : 'mineclaw.live.v1' });
  private readonly controls = new Map<WebSocket, string>();
  private readonly origins: Set<string>;
  constructor(private readonly auth: AuthService, private readonly bridge: BridgeClient,
    private readonly hub: LiveHub, allowedOrigins: readonly string[]) {
    this.origins = new Set(allowedOrigins.map(normalize));
  }
  upgrade(req: IncomingMessage, socket: Socket, head: Buffer): void {
    const url = new URL(req.url ?? '/', 'http://localhost'), control = CONTROL.exec(url.pathname);
    const live = url.pathname === '/api/live/socket';
    const reject = (status: number) => { socket.end(`HTTP/1.1 ${status} Rejected\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`); };
    if (!live && !control) return reject(404);
    const origin = req.headers.origin;
    if (!origin || !this.origins.has(normalize(origin))) return reject(403);
    const state = this.auth.resolve(req, contextOf(req));
    if (!state.user || !state.session) return reject(401);
    if (!hasRole(state.user.roles, control ? 'story' : 'viewer')) return reject(403);
    const offered = (req.headers['sec-websocket-protocol'] ?? '').split(',').map((s) => s.trim());
    const protocol = control ? 'mineclaw.control.v1' : 'mineclaw.live.v1';
    if (!offered.includes(protocol)) return reject(400);
    if (control && !offered.some((p) => p.startsWith('csrf.') && tokensEqual(p.slice(5), state.session!.csrf))) return reject(403);
    if (control && (this.controls.size >= 64 || [...this.controls.values()].filter((name) => name === state.user!.username).length >= 4)) return reject(429);
    const query = LiveStreamQuery.safeParse(parseQuery(req.url));
    if (live && !query.success) return reject(400);
    if (control && url.search) return reject(400); // lease tokens never belong in URLs
    const stillValid = () => {
      const fresh = this.auth.resolve(req, contextOf(req));
      return fresh.session?.idHash === state.session!.idHash && !!fresh.user && hasRole(fresh.user.roles, control ? 'story' : 'viewer');
    };
    this.server.handleUpgrade(req, socket, head, (ws) => {
      socket.setNoDelay(true);
      if (live) this.hub.attachSocket(ws, query.success ? query.data : undefined,
        { username: state.user!.username, stillValid }, url.searchParams.get('lastEventId') ?? undefined);
      else {
        this.controls.set(ws, state.user!.username);
        const upstream = this.bridge.socket(`/v1/sup/bodies/${control![1]}/control/socket`, identityOf(state.user!));
        relayControl(ws, upstream);
        ws.once('close', () => this.controls.delete(ws));
      }
      const check = setInterval(() => {
        try { if (!stillValid()) ws.terminate(); }
        catch { ws.terminate(); }
      }, 15_000);
      check.unref();
      ws.once('close', () => clearInterval(check));
    });
  }
  close(): void { for (const ws of this.server.clients) ws.terminate(); this.server.close(); }
}

function relayControl(client: WebSocket, upstream: WebSocket): void {
  const waiting: Buffer[] = [];
  let queued = 0, second = Date.now(), frames = 0;
  const stop = () => { if (upstream.readyState !== WebSocket.CLOSED) upstream.terminate(); };
  client.on('close', stop);
  client.on('error', stop);
  upstream.on('error', () => client.close(1011, 'Control connection unavailable'));
  upstream.on('close', () => client.close(1000, 'Control connection ended'));
  upstream.on('open', () => { for (const data of waiting) upstream.send(data, { binary: false }); waiting.length = 0; });
  upstream.on('message', (data, binary) => {
    if (client.readyState !== WebSocket.OPEN) return;
    if (binary || client.bufferedAmount > 256 * 1024) { client.close(1008, 'Control connection fell behind'); return; }
    client.send(data, { binary: false });
  });
  client.on('message', (data, binary) => {
    if (client.readyState !== WebSocket.OPEN) return;
    if (Date.now() - second >= 1000) { second = Date.now(); frames = 0; }
    if (binary || ++frames > 120 || upstream.bufferedAmount > 64 * 1024) { stop(); client.close(1008, 'Invalid control traffic'); return; }
    if (upstream.readyState === WebSocket.OPEN) upstream.send(data, { binary: false });
    else if (upstream.readyState === WebSocket.CONNECTING) {
      const bytes = Buffer.isBuffer(data) ? data : Buffer.from(data as ArrayBuffer);
      queued += bytes.length;
      if (queued > 8192) client.close(1008, 'Control connection not ready');
      else waiting.push(bytes);
    }
  });
}
