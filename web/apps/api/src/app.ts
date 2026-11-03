/**
 * Builds the gateway: services (web.db, users, sessions, audit, bridge client, live hub), the Nest app with the generated
 * route-table controller, and the Express middleware chain:
 *   request context (id, client IP, scheme) → access log → Cloudflare Access gate → security headers → setup gate → Next proxy
 *   → /api
 * Used by `main.ts` and by the tests (each test gets its own app on an ephemeral port).
 */
import 'reflect-metadata';
import type { IncomingMessage, Server, ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import type { AddressInfo, Socket } from 'node:net';
import { Module, type DynamicModule } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import type { NestExpressApplication } from '@nestjs/platform-express';
import { pinoHttp } from 'pino-http';
import { AuditService } from './audit/audit.service.js';
import { AuthService } from './auth/auth.service.js';
import { PasswordVerifier } from './auth/passwords.js';
import { LoginLimiter, TokenBucket } from './auth/rate-limit.js';
import { SessionService } from './auth/sessions.js';
import { SetupService } from './auth/setup.service.js';
import { UsersStore } from './auth/users-store.js';
import { BridgeClient } from './bridge/bridge-client.js';
import { BridgeHealth } from './bridge/bridge-health.js';
import { systemClock, type Clock } from './common/clock.js';
import { Errors, sendApiError } from './common/errors.js';
import { ApiExceptionFilter } from './common/exception.filter.js';
import { contextOf, setContext } from './common/http.js';
import { createLogger, PinoNestLogger, type Logger } from './common/logger.js';
import { dbPathOf, type ApiConfig } from './config/config.js';
import { WebDb } from './db/web-db.js';
import { LiveHub, type LiveHubOptions } from './live/live-hub.js';
import { startMockBridge, type MockBridge } from './mock/mock-bridge.js';
import { createNextProxy, isApiPath } from './proxy/next-proxy.js';
import { RouteDispatcher } from './routes/dispatcher.js';
import { buildRouteTableController, DISPATCHER } from './routes/route-table.controller.js';
import { CloudflareAccess } from './security/cf-access.js';
import { securityHeaders } from './security/headers.js';
import { TrustedProxies } from './security/client-ip.js';
import type { JWTVerifyGetKey } from 'jose';

export interface CreateAppOptions {
  logger?: Logger;
  clock?: Clock;
  hub?: Partial<LiveHubOptions>;
  /** Tests: a local JWKS instead of fetching the team's certs. */
  cfAccessJwks?: JWTVerifyGetKey;
  /** Mock bridge stream tick (ms). */
  mockIntervalMs?: number;
}

export interface ApiApp {
  app: NestExpressApplication;
  server: Server;
  config: ApiConfig;
  log: Logger;
  services: {
    db: WebDb;
    users: UsersStore;
    sessions: SessionService;
    auth: AuthService;
    setup: SetupService;
    audit: AuditService;
    bridge: BridgeClient;
    hub: LiveHub;
    mock?: MockBridge;
  };
  listen(port?: number, host?: string): Promise<string>;
  close(): Promise<void>;
}

type Next = (err?: unknown) => void;

export async function createApp(config: ApiConfig, opts: CreateAppOptions = {}): Promise<ApiApp> {
  const log = opts.logger ?? createLogger(config.web.logLevel);
  const clock = opts.clock ?? systemClock;

  let mock: MockBridge | undefined;
  if (config.bridge.mock) {
    if (process.env.NODE_ENV === 'production') throw new Error('Bridge:Mock is refused when NODE_ENV=production');
    mock = await startMockBridge(config.bridge.key, { streamIntervalMs: opts.mockIntervalMs });
    config = { ...config, bridge: { ...config.bridge, url: mock.url } };
    log.warn({ url: mock.url }, 'MOCK BRIDGE: serving fixture data, no bot involved');
  }

  const db = new WebDb(dbPathOf(config));
  const users = new UsersStore(config.web.usersFile, log);
  const sessions = new SessionService(db, clock, {
    idleHours: config.web.sessionIdleHours,
    absoluteDays: config.web.sessionAbsoluteDays,
    cookieSecure: config.web.cookieSecure,
  });
  const audit = new AuditService(db, clock);
  const limiter = new LoginLimiter(db, clock, config.web.login);
  const auth = new AuthService(
    users,
    sessions,
    limiter,
    new PasswordVerifier(),
    audit,
    db,
    clock,
    log,
    config.web.login.ipAttemptsPerMinute,
  );
  const bridge = new BridgeClient(
    { url: config.bridge.url, key: config.bridge.key, timeoutMs: config.bridge.timeoutMs },
    log,
  );
  const health = new BridgeHealth(bridge, log);
  const hub = new LiveHub(bridge, health, log, {
    maxClients: config.web.maxSseClients,
    validate: config.web.validateResponses,
    ...opts.hub,
  });
  const trusted = new TrustedProxies(config.web);
  const setup = new SetupService(users, sessions, auth, audit, trusted, config.web.setup, clock, log);
  const dispatcher = new RouteDispatcher({
    auth,
    setup,
    audit,
    bridge,
    health,
    hub,
    chatLimiter: new TokenBucket(clock, config.web.chatRate.burst, config.web.chatRate.perMinute),
    log,
    allowedOrigins: config.web.allowedOrigins,
    bodyLimitBytes: config.web.bodyLimitBytes,
    validateResponses: config.web.validateResponses,
  });

  @Module({})
  class ApiModule {}
  const root: DynamicModule = {
    module: ApiModule,
    controllers: [buildRouteTableController()],
    providers: [{ provide: DISPATCHER, useValue: dispatcher }],
  };
  const app = await NestFactory.create<NestExpressApplication>(root, {
    bodyParser: false,
    logger: new PinoNestLogger(log.child({ ctx: 'nest' })),
    abortOnError: false,
  });
  const express = app.getHttpAdapter().getInstance() as { disable(k: string): void; set(k: string, v: unknown): void };
  express.disable('x-powered-by');
  express.set('etag', false);

  const cf = config.web.cloudflareAccess
    ? new CloudflareAccess(config.web.cloudflareAccess, opts.cfAccessJwks)
    : undefined;

  const context = (req: IncomingMessage, res: ServerResponse | undefined) => {
    const { ip, https } = trusted.resolve(req);
    const requestId = randomUUID();
    setContext(req, { requestId, ip, https });
    res?.setHeader('X-Request-Id', requestId);
  };

  app.use((req: IncomingMessage, res: ServerResponse, next: Next) => {
    context(req, res);
    next();
  });
  app.use(
    pinoHttp({
      logger: log,
      genReqId: (req) => contextOf(req).requestId,
      customProps: (req) => {
        const ctx = contextOf(req);
        return { ip: ctx.ip, user: ctx.username };
      },
      serializers: {
        req: (r: { method?: string; url?: string }) => ({
          method: r.method,
          path: (r.url ?? '').split('?')[0]?.slice(0, 300),
        }),
        res: (r: { statusCode?: number }) => ({ status: r.statusCode }),
      },
      autoLogging: { ignore: (req) => (req.url ?? '').startsWith('/_next/static') || req.url === '/api/health' },
      customLogLevel: (_req, res, err) =>
        err || res.statusCode >= 500 ? 'error' : res.statusCode >= 400 ? 'warn' : 'info',
    }),
  );
  if (cf) {
    app.use((req: IncomingMessage, res: ServerResponse, next: Next) => {
      if ((req.url ?? '').split('?')[0] === '/api/health') return next();
      cf.verify(req).then(
        (ok) => (ok ? next() : sendApiError(res, Errors.forbidden('Cloudflare Access required'))),
        () => sendApiError(res, Errors.forbidden('Cloudflare Access required')),
      );
    });
  }
  app.use(securityHeaders(config.web.csp));
  app.use(setup.gate());
  const nextProxy = config.web.proxyNext ? createNextProxy(config.web.nextUrl, config.web.csp, log) : undefined;
  if (nextProxy) app.use(nextProxy.middleware);
  app.useGlobalFilters(new ApiExceptionFilter(log));

  await app.init();
  const server = app.getHttpServer();
  server.keepAliveTimeout = 65_000;
  server.headersTimeout = 20_000;
  server.requestTimeout = 300_000;
  // Upgraded sockets leave the HTTP server's bookkeeping; track them so shutdown can end them.
  const upgraded = new Set<Socket>();
  server.on('upgrade', (req: IncomingMessage, socket: Socket, head: Buffer) => {
    upgraded.add(socket);
    socket.on('close', () => upgraded.delete(socket));
    context(req, undefined);
    if (!nextProxy || isApiPath(req.url)) {
      socket.destroy();
      return;
    }
    if (!cf) return nextProxy.upgrade(req, socket, head);
    cf.verify(req).then(
      (ok) => (ok ? nextProxy.upgrade(req, socket, head) : socket.destroy()),
      () => socket.destroy(),
    );
  });

  const gc = setInterval(() => sessions.gc(), 10 * 60_000);
  gc.unref();

  let closed = false;
  return {
    app,
    server,
    config,
    log,
    services: { db, users, sessions, auth, setup, audit, bridge, hub, mock },
    async listen(port = config.web.port, host = config.web.bind) {
      await app.listen(port, host);
      const addr = server.address() as AddressInfo;
      return `http://${addr.address.includes(':') ? `[${addr.address}]` : addr.address}:${addr.port}`;
    },
    async close() {
      if (closed) return;
      closed = true;
      clearInterval(gc);
      hub.close();
      for (const s of upgraded) s.destroy();
      server.closeAllConnections?.();
      await app.close();
      await mock?.close();
      db.close();
    },
  };
}
