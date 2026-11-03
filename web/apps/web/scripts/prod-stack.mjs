// The production stack on one machine, for e2e tests and investigations: the Next STANDALONE build behind the Nest gateway
// (`--mock-bridge`), exactly like CT 114 (Nest proxies every non-/api request to Next, real CSP nonces, real HTTP/1.1 with the
// browser's 6-connections-per-host limit, the synthetic SSE stream and binary world data from the mock bridge).
//
//   node scripts/prod-stack.mjs [--port 3280] [--next-port 3281] [--build]
//
// --build (or a missing build) runs `next build` first; `@mineclaw/api` must be built (turbo builds it before the web app).
// Prints "prod stack ready on <url>" once Nest answers. Sign in as `fox` / `prod-stack-e2e` (admin, chat, commands; player
// poofimafox; no TOTP). Ctrl+C (or the parent's exit) stops both servers.
import { spawn, spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const app = join(here, '..');
const apiDir = join(app, '..', 'api');
const arg = (name, dflt) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : dflt;
};
const port = Number(arg('--port', process.env.PROD_STACK_PORT ?? 3280));
const nextPort = Number(arg('--next-port', port + 1));
export const USER = 'fox';
export const PASSWORD = 'prod-stack-e2e';

const standalone = join(app, '.next', 'standalone', 'apps', 'web');
const server = join(standalone, 'server.js');
if (process.argv.includes('--build') || !existsSync(server)) {
  console.log('[prod-stack] next build (standalone)…');
  const r = spawnSync('npx', ['next', 'build', '--turbopack'], { cwd: app, stdio: 'inherit', shell: process.platform === 'win32', env: { ...process.env, NEXT_PUBLIC_MOCK: '', NEXT_DIST_DIR: '' } });
  if (r.status !== 0) process.exit(r.status ?? 1);
}
// Standalone output leaves the static assets to the deploy step (deploy-alice.ps1 copies them the same way).
cpSync(join(app, '.next', 'static'), join(standalone, '.next', 'static'), { recursive: true });
if (existsSync(join(app, 'public'))) cpSync(join(app, 'public'), join(standalone, 'public'), { recursive: true });

const apiMain = join(apiDir, 'dist', 'main.js');
if (!existsSync(apiMain)) {
  console.error('[prod-stack] apps/api is not built (npm run build -w @mineclaw/api)');
  process.exit(1);
}
const { hashPassword } = await import(pathToFileURL(join(apiDir, 'dist', 'auth', 'passwords.js')).href);
const { writeUsersFile } = await import(pathToFileURL(join(apiDir, 'dist', 'auth', 'users-store.js')).href);

const dir = mkdtempSync(join(tmpdir(), 'mcx-prod-stack-'));
const usersFile = join(dir, 'users.json');
writeUsersFile(usersFile, {
  version: 1,
  users: [{ username: USER, passwordHash: await hashPassword(PASSWORD), roles: ['admin', 'chat', 'commands', 'viewer'], player: 'poofimafox', totp: null, disabled: false }],
});
const config = join(dir, 'config.json');
const origin = `http://127.0.0.1:${port}`;
writeFileSync(
  config,
  JSON.stringify({
    Web: {
      Port: port,
      Bind: '127.0.0.1',
      NextUrl: `http://127.0.0.1:${nextPort}`,
      ProxyNext: true,
      AllowedOrigins: [origin, `http://localhost:${port}`],
      DataDir: dir,
      UsersFile: usersFile,
      LogLevel: process.env.PROD_STACK_LOG ?? 'warn',
    },
  }),
);

const children = [];
const start = (name, cmd, args, opts) => {
  const c = spawn(cmd, args, { stdio: ['ignore', 'inherit', 'inherit'], ...opts });
  c.on('exit', (code) => {
    if (!stopping) {
      console.error(`[prod-stack] ${name} exited (${code})`);
      stop(1);
    }
  });
  children.push(c);
  console.log(`[prod-stack] ${name} pid ${c.pid}`);
  return c;
};
let stopping = false;
const stop = (code = 0) => {
  if (stopping) return;
  stopping = true;
  for (const c of children) if (c.exitCode === null) c.kill();
  setTimeout(() => {
    try {
      rmSync(dir, { recursive: true, force: true });
    } catch {
      // Windows may still hold web.db for a moment
    }
    process.exit(code);
  }, 500);
};
process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));

start('next', process.execPath, [server], {
  cwd: standalone,
  env: { ...process.env, NODE_ENV: 'production', PORT: String(nextPort), HOSTNAME: '127.0.0.1', NEXT_TELEMETRY_DISABLED: '1' },
});
// The mock bridge is refused under NODE_ENV=production; Nest itself runs its prod code paths (proxy, CSP) regardless.
const env = { ...process.env };
delete env.NODE_ENV;
start('nest', process.execPath, ['--disable-warning=ExperimentalWarning', apiMain, '--mock-bridge', '--config', config], { cwd: apiDir, env });

let ready = false;
for (let i = 0; i < 240 && !ready; i++) {
  try {
    const [a, b] = await Promise.all([fetch(`${origin}/api/auth/me`), fetch(`${origin}/login`)]);
    ready = a.status < 500 && b.status === 200;
  } catch {
    // not up yet
  }
  if (!ready) await new Promise((r) => setTimeout(r, 500));
}
if (!ready) {
  console.error('[prod-stack] the stack did not come up');
  stop(1);
} else console.log(`[prod-stack] prod stack ready on ${origin} (user ${USER})`);

// --run "<command>": run it against the stack (PROD_STACK_URL set), then stop both servers and exit with its code.
const run = arg('--run', null);
if (ready && run) {
  const c = spawn(run, { stdio: 'inherit', shell: true, env: { ...process.env, PROD_STACK_URL: origin } });
  c.on('exit', (code) => stop(code ?? 1));
}
