/**
 * Nest's runtime config: the contracts `NestConfig` keys plus the gateway's own (documented in deploy/viewer/web/README.md).
 *
 * Sources, lowest precedence first:
 *   1. schema defaults (dev defaults when `--dev` / NODE_ENV=development)
 *   2. JSON files: `--config <path>` or env `MINECLAW_WEB_CONFIG` (comma list), else `/etc/mineclaw-web/config.json` if present.
 *      Keys are PascalCase like the bot's appsettings (`{"Web":{"AllowedOrigins":[...]}}`); camelCase works too.
 *   3. env `MINECLAW_WEB_<Section>__<Key>[__<Sub>]` (e.g. `MINECLAW_WEB_Web__TrustProxy=true`, arrays comma-separated).
 *   4. env `MINECLAW_BRIDGE_KEY` (the HMAC key) and `MINECLAW_BRIDGE_KEY_FILE`; `Bridge:KeyFile` reads the key from a file.
 *   5. argv: `--mock-bridge`, `--port <n>`, `--bind <addr>`.
 * Secrets (`Bridge:Key`) are never logged: `describeConfig` prints a redacted view.
 */
import { existsSync, readFileSync } from 'node:fs';
import { isAbsolute, join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { NestConfig, WebTlsSettings } from '@mineclaw/contracts';
import { z } from 'zod';
import { readHostingOverride } from './web-hosting.js';

const WebExtra = NestConfig.shape.web.extend({
  tls: WebTlsSettings.default(() => WebTlsSettings.parse({})),
  /** Runtime provenance: environment TLS overrides make the Administration editor read-only. */
  tlsEnvOverride: z.boolean().default(false),
  /** Directory of web.db (`<DataDir>/web.db`); `Web:DbPath` overrides the full path. */
  dataDir: z.string().default('/var/lib/mineclaw-web'),
  dbPath: z.string().nullish(),
  /** `auto`: Secure + `__Host-mcx` only when the request is HTTPS (directly or via a trusted proxy); plain HTTP uses `mcx`. */
  cookieSecure: z.enum(['auto', 'always', 'never']).default('auto'),
  /** Prod: everything outside /api is reverse-proxied to Next (`Web:NextUrl`). Dev: Next rewrites /api to Nest instead. */
  proxyNext: z.boolean().default(true),
  /** Validate bridge responses against the contracts schemas: `strict` = 502 on mismatch (tests), `warn` = log (dev). */
  validateResponses: z.enum(['off', 'warn', 'strict']).default('off'),
  logLevel: z.enum(['fatal', 'error', 'warn', 'info', 'debug', 'trace', 'silent']).default('info'),
  /** Max JSON request body. Shard uploads have their own cap (SHARD_UPLOAD_MAX_BYTES). */
  bodyLimitBytes: z
    .number()
    .int()
    .positive()
    .default(1024 * 1024),
  chatRate: z
    .object({ burst: z.number().int().positive().default(5), perMinute: z.number().positive().default(20) })
    .default({ burst: 5, perMinute: 20 }),
  login: z
    .object({
      /** Failures before the per-user lockout starts (then doubling from LockoutBaseSeconds). */
      userThreshold: z.number().int().positive().default(5),
      ipThreshold: z.number().int().positive().default(20),
      lockoutBaseSeconds: z.number().positive().default(30),
      lockoutMaxSeconds: z.number().positive().default(3600),
      /** Raw login attempts per IP per minute before 429 (independent of failures). */
      ipAttemptsPerMinute: z.number().int().positive().default(30),
    })
    .default({
      userThreshold: 5,
      ipThreshold: 20,
      lockoutBaseSeconds: 30,
      lockoutMaxSeconds: 3600,
      ipAttemptsPerMinute: 30,
    }),
  csp: z
    .object({
      /** `nonce`: strict nonce + strict-dynamic for Next pages (pages must render dynamically). `unsafe-inline`: fallback. */
      scripts: z.enum(['nonce', 'unsafe-inline']).default('nonce'),
      reportOnly: z.boolean().default(false),
    })
    .default({ scripts: 'nonce', reportOnly: false }),
  maxSseClients: z.number().int().positive().default(200),
  /**
   * Update checker (system/update-checker.ts): GitHub releases of `Repo`, cached in `<DataDir>/update-cache.json`.
   * `Dir` is the systemd handshake directory (`request.json` / `status.json`). Disabled = no network at all.
   */
  updates: z
    .object({
      enabled: z.boolean().default(true),
      repo: z
        .string()
        .regex(/^[A-Za-z0-9_.-]{1,100}\/[A-Za-z0-9_.-]{1,100}$/, 'owner/name')
        .default('Realynx/MineClaw'),
      intervalHours: z.number().min(1).max(168).default(12),
      prereleases: z.boolean().default(false),
      dir: z.string().min(1).default('/var/lib/mineclaw-web/update'),
    })
    .default({
      enabled: true,
      repo: 'Realynx/MineClaw',
      intervalHours: 12,
      prereleases: false,
      dir: '/var/lib/mineclaw-web/update',
    }),
  /**
   * First-run setup (`/setup`, open while the users file has no users). By default only a direct LAN/loopback client may
   * complete it: never a request through a trusted proxy or one that carries Cloudflare/forwarding headers.
   */
  setup: z
    .object({
      allowProxied: z.boolean().default(false),
      /** POST /api/setup attempts per IP per minute. */
      attemptsPerMinute: z.number().int().positive().default(10),
    })
    .default({ allowProxied: false, attemptsPerMinute: 10 }),
});

const BridgeExtra = NestConfig.shape.bridge.extend({
  keyFile: z.string().nullish(),
  /** Dev only: serve fixtures from an in-process mock bridge (refused when NODE_ENV=production). */
  mock: z.boolean().default(false),
});

export const ApiConfigSchema = z.object({ web: WebExtra, bridge: BridgeExtra });
export type ApiConfig = z.infer<typeof ApiConfigSchema>;
/** Deep-partial input accepted by `loadConfig` overrides and tests. */
export type ApiConfigInput = { web?: Record<string, unknown>; bridge?: Record<string, unknown> };

export interface LoadOptions {
  argv?: readonly string[];
  env?: NodeJS.ProcessEnv;
  /** Highest-precedence overrides (tests). camelCase keys. */
  overrides?: ApiConfigInput;
  cwd?: string;
}

export interface LoadedConfig {
  config: ApiConfig;
  dev: boolean;
  files: string[];
}

const ARRAY_KEYS = new Set(['allowedOrigins', 'trustedProxies']);

function lowerFirst(k: string): string {
  return k.length > 0 ? k[0]!.toLowerCase() + k.slice(1) : k;
}

/** PascalCase → camelCase keys, recursively (config files are written like the bot's appsettings). */
export function normalizeKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalizeKeys);
  if (value === null || typeof value !== 'object') return value;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) out[lowerFirst(k)] = normalizeKeys(v);
  return out;
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return v !== null && typeof v === 'object' && !Array.isArray(v);
}

export function deepMerge(base: Record<string, unknown>, over: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = { ...base };
  for (const [k, v] of Object.entries(over)) {
    if (v === undefined) continue;
    const prev = out[k];
    out[k] = isRecord(prev) && isRecord(v) ? deepMerge(prev, v) : v;
  }
  return out;
}

function coerceEnv(key: string, raw: string): unknown {
  if (ARRAY_KEYS.has(key)) {
    const t = raw.trim();
    if (t.startsWith('[')) return JSON.parse(t) as unknown;
    return t
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
  }
  if (raw === 'true') return true;
  if (raw === 'false') return false;
  if (/^-?\d+(\.\d+)?$/.test(raw)) return Number(raw);
  if (raw.trim().startsWith('{')) return JSON.parse(raw) as unknown;
  return raw;
}

/** `MINECLAW_WEB_Web__TrustProxy=true` → `{web:{trustProxy:true}}`. */
export function envOverrides(env: NodeJS.ProcessEnv): Record<string, unknown> {
  let out: Record<string, unknown> = {};
  for (const [name, raw] of Object.entries(env)) {
    if (raw === undefined || !name.startsWith('MINECLAW_WEB_') || name === 'MINECLAW_WEB_CONFIG') continue;
    const path = name
      .slice('MINECLAW_WEB_'.length)
      .split('__')
      .map((p) => lowerFirst(p));
    if (path.length < 2) continue;
    const leaf = path[path.length - 1]!;
    let node: Record<string, unknown> = { [leaf]: coerceEnv(leaf, raw) };
    for (let i = path.length - 2; i >= 0; i--) node = { [path[i]!]: node };
    out = deepMerge(out, node);
  }
  return out;
}

function argValue(argv: readonly string[], flag: string): string | undefined {
  const i = argv.indexOf(flag);
  if (i >= 0 && i + 1 < argv.length) return argv[i + 1];
  const eq = argv.find((a) => a.startsWith(`${flag}=`));
  return eq ? eq.slice(flag.length + 1) : undefined;
}

function devDefaults(cwd: string): Record<string, unknown> {
  const dataDir = join(cwd, '.data');
  return {
    web: {
      port: 4000,
      bind: '127.0.0.1',
      proxyNext: false,
      validateResponses: 'warn',
      allowedOrigins: [
        'http://localhost:3000',
        'http://127.0.0.1:3000',
        'http://localhost:4000',
        'http://127.0.0.1:4000',
      ],
      dataDir,
      usersFile: join(dataDir, 'users.json'),
    },
  };
}

type Layer = Record<string, unknown>;

/** `--config a.json,b.json` or MINECLAW_WEB_CONFIG, else /etc/mineclaw-web/config.json when it exists. */
function configFiles(argv: readonly string[], env: NodeJS.ProcessEnv): string[] {
  const fileArg = argValue(argv, '--config') ?? env.MINECLAW_WEB_CONFIG;
  if (fileArg)
    return fileArg
      .split(',')
      .map((f) => f.trim())
      .filter(Boolean);
  return existsSync('/etc/mineclaw-web/config.json') ? ['/etc/mineclaw-web/config.json'] : [];
}

/** Dev defaults (`--dev` or NODE_ENV=development), then the test settings under NODE_ENV=test. */
function baseLayer(dev: boolean, nodeEnv: string, cwd: string): Layer {
  const merged: Layer = dev ? devDefaults(cwd) : {};
  return nodeEnv === 'test' ? deepMerge(merged, { web: { validateResponses: 'strict', logLevel: 'silent' } }) : merged;
}

function readConfigFile(f: string, cwd: string): Layer {
  const path = isAbsolute(f) ? f : resolve(cwd, f);
  const json = JSON.parse(readFileSync(path, 'utf8')) as unknown;
  return normalizeKeys(json) as Layer;
}

/** The bridge key (or key file) from MINECLAW_BRIDGE_KEY / MINECLAW_BRIDGE_KEY_FILE. */
function bridgeKeyLayer(merged: Layer, env: NodeJS.ProcessEnv): Layer {
  const keyFromEnv = env.MINECLAW_BRIDGE_KEY;
  const keyFileFromEnv = env.MINECLAW_BRIDGE_KEY_FILE;
  if (keyFromEnv) merged = deepMerge(merged, { bridge: { key: keyFromEnv } });
  if (keyFileFromEnv) merged = deepMerge(merged, { bridge: { keyFile: keyFileFromEnv } });
  return merged;
}

/** `--mock-bridge`, `--port`, `--bind`. */
function argvLayer(merged: Layer, argv: readonly string[]): Layer {
  if (argv.includes('--mock-bridge')) merged = deepMerge(merged, { bridge: { mock: true } });
  const port = argValue(argv, '--port');
  if (port) merged = deepMerge(merged, { web: { port: Number(port) } });
  const bind = argValue(argv, '--bind');
  if (bind) merged = deepMerge(merged, { web: { bind } });
  return merged;
}

/** The key from the key file when none is set; the mock bridge (never in production) gets a throwaway key. */
function resolveBridge(merged: Layer, nodeEnv: string): Layer {
  const bridge = (merged.bridge ?? {}) as Layer;
  if (!bridge.key && typeof bridge.keyFile === 'string' && bridge.keyFile) {
    bridge.key = readFileSync(bridge.keyFile, 'utf8').trim();
  }
  if (bridge.mock === true) {
    if (nodeEnv === 'production') throw new Error('Bridge:Mock is refused when NODE_ENV=production');
    // The in-process mock bridge gets a throwaway key; it never leaves this process.
    bridge.key ??= randomBytes(32).toString('hex');
  }
  return bridge;
}

/** The validated config; the error lists every issue (a bad bridge key never echoes the key). */
function parseConfig(merged: Layer): ApiConfig {
  const parsed = ApiConfigSchema.safeParse(merged);
  if (parsed.success) return parsed.data;
  const issues = parsed.error.issues
    .map(
      (i) =>
        `${i.path.join('.')}: ${i.path.join('.') === 'bridge.key' ? 'missing or shorter than 32 chars' : i.message}`,
    )
    .join('; ');
  throw new Error(`invalid config: ${issues}`);
}

export function loadConfig(opts: LoadOptions = {}): LoadedConfig {
  const argv = opts.argv ?? process.argv.slice(2);
  const env = opts.env ?? process.env;
  const cwd = opts.cwd ?? process.cwd();
  const nodeEnv = env.NODE_ENV ?? '';
  const dev = argv.includes('--dev') || nodeEnv === 'development';

  let merged = baseLayer(dev, nodeEnv, cwd);
  const files = configFiles(argv, env);
  for (const f of files) merged = deepMerge(merged, readConfigFile(f, cwd));
  const environment = envOverrides(env);
  const location = deepMerge(merged, environment).web as Layer | undefined;
  const dataDir = typeof location?.dataDir === 'string' ? location.dataDir : '/var/lib/mineclaw-web';
  const savedHosting = readHostingOverride(dataDir);
  if (savedHosting) merged = deepMerge(merged, { web: { tls: savedHosting } });
  merged = deepMerge(merged, environment);
  merged = deepMerge(merged, {
    web: { tlsEnvOverride: Object.keys(env).some((k) => /^MINECLAW_WEB_Web__Tls(?:__|$)/i.test(k)) },
  });
  merged = argvLayer(bridgeKeyLayer(merged, env), argv);
  if (opts.overrides) merged = deepMerge(merged, opts.overrides);
  merged.bridge = resolveBridge(merged, nodeEnv);
  return { config: parseConfig(merged), dev, files };
}

/** The sqlite path: `Web:DbPath` if set, else `<Web:DataDir>/web.db`. */
export function dbPathOf(config: ApiConfig): string {
  return config.web.dbPath ?? join(config.web.dataDir, 'web.db');
}

/** Config for logs: secrets replaced. */
export function describeConfig(config: ApiConfig): Record<string, unknown> {
  return {
    web: { ...config.web },
    bridge: { ...config.bridge, key: config.bridge.key ? '<redacted>' : '<missing>' },
  };
}
