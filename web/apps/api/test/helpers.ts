import { mkdtempSync, rmSync } from 'node:fs';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Role } from '@mineclaw/contracts';
import request from 'supertest';
import { createApp, type ApiApp, type CreateAppOptions } from '../src/app.js';
import { hashPassword } from '../src/auth/passwords.js';
import { writeUsersFile, type UserRecord } from '../src/auth/users-store.js';
import { SignatureVerifier, type BridgeIdentity } from '../src/bridge/signing.js';
import { createLogger } from '../src/common/logger.js';
import { ApiConfigSchema, deepMerge, type ApiConfig } from '../src/config/config.js';

export const PASSWORD = 'correct horse battery staple';
export const ORIGIN = 'http://localhost:3000';
export const KEY = 'test-key-not-a-secret-0123456789abcdef';

export interface Persona {
  username: string;
  roles: Role[];
  player: string | null;
}

/** One user per access level. */
export const PERSONAS: Record<'viewer' | 'chat' | 'chatNoPlayer' | 'commands' | 'admin' | 'story', Persona> = {
  viewer: { username: 'vera', roles: ['viewer'], player: null },
  chat: { username: 'robin', roles: ['chat', 'viewer'], player: 'kiwi' },
  chatNoPlayer: { username: 'nemo', roles: ['chat'], player: null },
  commands: { username: 'cora', roles: ['chat', 'commands', 'viewer'], player: 'cora_mc' },
  admin: { username: 'fox', roles: ['admin', 'chat', 'commands', 'viewer'], player: 'poofimafox' },
  story: { username: 'sky', roles: ['story', 'viewer'], player: null },
};

let cachedHash: Promise<string> | undefined;
export function passwordHash(): Promise<string> {
  cachedHash ??= hashPassword(PASSWORD);
  return cachedHash;
}

export async function writeUsers(path: string, extra: Partial<UserRecord>[] = []): Promise<void> {
  const hash = await passwordHash();
  const users: UserRecord[] = Object.values(PERSONAS).map((p) => ({
    username: p.username,
    passwordHash: hash,
    roles: p.roles,
    player: p.player,
    totp: null,
    disabled: false,
  }));
  for (const e of extra) {
    const i = users.findIndex((u) => u.username === e.username);
    const next = {
      ...(i >= 0
        ? users[i]!
        : { passwordHash: hash, roles: ['viewer'] as Role[], player: null, totp: null, disabled: false }),
      ...e,
    } as UserRecord;
    if (i >= 0) users[i] = next;
    else users.push(next);
  }
  writeUsersFile(path, { version: 1, users });
}

export interface TestApp {
  api: ApiApp;
  url: string;
  dir: string;
  usersFile: string;
  close(): Promise<void>;
}

export async function startApp(
  overrides: { web?: Record<string, unknown>; bridge?: Record<string, unknown> } = {},
  /** `users: null` starts with no users file at all (first-run setup mode). */
  opts: CreateAppOptions & { users?: Partial<UserRecord>[] | null } = {},
): Promise<TestApp> {
  const dir = mkdtempSync(join(tmpdir(), 'mcx-api-'));
  const usersFile = join(dir, 'users.json');
  if (opts.users !== null) await writeUsers(usersFile, opts.users);
  const base = {
    web: {
      allowedOrigins: [ORIGIN],
      dataDir: dir,
      usersFile,
      proxyNext: false,
      validateResponses: 'strict',
      logLevel: 'silent',
    },
    bridge: { url: 'http://127.0.0.1:9', key: KEY, timeoutMs: 3000 },
  };
  const config: ApiConfig = ApiConfigSchema.parse(deepMerge(base, overrides));
  const api = await createApp(config, { logger: createLogger('silent'), ...opts });
  const url = await api.listen(0, '127.0.0.1');
  return {
    api,
    url,
    dir,
    usersFile,
    async close() {
      await api.close();
      try {
        rmSync(dir, { recursive: true, force: true });
      } catch {
        // Windows may hold the WAL briefly
      }
    },
  };
}

export interface Session {
  agent: ReturnType<typeof request.agent>;
  csrf: string;
}

export async function csrfSession(url: string): Promise<Session> {
  const agent = request.agent(url);
  const r = await agent.get('/api/auth/csrf').expect(200);
  return { agent, csrf: (r.body as { csrfToken: string }).csrfToken };
}

export async function login(url: string, username: string, extra: Record<string, unknown> = {}): Promise<Session> {
  const s = await csrfSession(url);
  const r = await s.agent
    .post('/api/auth/login')
    .set('Origin', ORIGIN)
    .set('X-CSRF-Token', s.csrf)
    .send({ username, password: PASSWORD, ...extra });
  if (r.status !== 200) throw new Error(`login ${username}: ${r.status} ${JSON.stringify(r.body)}`);
  return { agent: s.agent, csrf: (r.body as { csrfToken: string }).csrfToken };
}

export interface RecordedRequest {
  method: string;
  target: string;
  headers: IncomingMessage['headers'];
  body: Buffer;
  identity: BridgeIdentity;
}

export type FakeHandler = (req: RecordedRequest, res: ServerResponse) => void | Promise<void>;

/** A fake bridge that verifies every signature (401 on failure) and records each request. */
export async function fakeBridge(
  handler: FakeHandler,
  key = KEY,
): Promise<{ url: string; requests: RecordedRequest[]; server: Server; close(): Promise<void> }> {
  const verifier = new SignatureVerifier(key);
  const requests: RecordedRequest[] = [];
  const server = createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (c: Buffer) => chunks.push(c));
    req.on('end', () => {
      const body = Buffer.concat(chunks);
      const v = verifier.verify({ method: req.method ?? '', rawTarget: req.url ?? '', headers: req.headers, body });
      if (!v.ok) {
        res.writeHead(401, { 'Content-Type': 'application/json' }).end(JSON.stringify({ error: v.reason }));
        return;
      }
      const rec: RecordedRequest = {
        method: req.method ?? '',
        target: req.url ?? '',
        headers: req.headers,
        body,
        identity: v.identity,
      };
      requests.push(rec);
      void handler(rec, res);
    });
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    server,
    close: () =>
      new Promise<void>((r) => {
        server.closeAllConnections();
        server.close(() => r());
      }),
  };
}

export function json(res: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}): void {
  const text = JSON.stringify(body);
  res
    .writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(text), ...headers })
    .end(text);
}

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Reads an SSE response (fetch) until `until` returns true or the timeout passes. */
export async function readSse(
  body: ReadableStream<Uint8Array>,
  until: (frames: Array<{ event: string; id: string; data: unknown }>) => boolean,
  timeoutMs = 4000,
): Promise<Array<{ event: string; id: string; data: unknown }>> {
  const reader = body.getReader();
  const dec = new TextDecoder();
  let buf = '';
  const frames: Array<{ event: string; id: string; data: unknown }> = [];
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline && !until(frames)) {
    const r = await Promise.race([reader.read(), sleep(Math.max(1, deadline - Date.now())).then(() => null)]);
    if (!r || r.done) break;
    buf += dec.decode(r.value, { stream: true });
    let i: number;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const raw = buf.slice(0, i);
      buf = buf.slice(i + 2);
      let event = 'message';
      let id = '';
      const data: string[] = [];
      for (const line of raw.split('\n')) {
        if (line.startsWith('event: ')) event = line.slice(7);
        else if (line.startsWith('id: ')) id = line.slice(4);
        else if (line.startsWith('data: ')) data.push(line.slice(6));
      }
      if (data.length) frames.push({ event, id, data: JSON.parse(data.join('\n')) as unknown });
    }
  }
  reader.releaseLock();
  return frames;
}

/** Login with fetch (for SSE and other raw requests): returns the session cookie and CSRF token. */
export async function fetchLogin(
  url: string,
  username: string,
  headers: Record<string, string> = {},
): Promise<{ cookie: string; csrf: string }> {
  const c = await fetch(`${url}/api/auth/csrf`, { headers });
  const pre = c.headers
    .getSetCookie()
    .map((x) => x.split(';')[0])
    .join('; ');
  const { csrfToken } = (await c.json()) as { csrfToken: string };
  const r = await fetch(`${url}/api/auth/login`, {
    method: 'POST',
    headers: { ...headers, cookie: pre, Origin: ORIGIN, 'X-CSRF-Token': csrfToken, 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password: PASSWORD }),
  });
  if (r.status !== 200) throw new Error(`login ${username}: ${r.status}`);
  const body = (await r.json()) as { csrfToken: string };
  return {
    cookie: r.headers
      .getSetCookie()
      .map((x) => x.split(';')[0])
      .join('; '),
    csrf: body.csrfToken,
  };
}
