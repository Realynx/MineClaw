// /app/start.mjs in the ghcr.io/realynx/mineclaw-web image (deploy/docker/web.Dockerfile): one container, two processes,
// the same pair deploy/viewer/web/mineclaw-web-{ui,api}.service run under systemd:
//   ui   Next standalone server  127.0.0.1:3000  (cwd apps/web/.next/standalone/apps/web)
//   api  Nest gateway            Web:Bind:Web:Port (0.0.0.0:8080), proxies everything outside /api to the ui
// SIGTERM/SIGINT are forwarded to both. When either exits on its own the other is stopped and this exits non-zero, so the
// container's restart policy (or Kubernetes) brings the pair back together.
// Heap caps (MB, like the systemd units): MINECLAW_API_HEAP_MB (384), MINECLAW_UI_HEAP_MB (256).
import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const env = { ...process.env };
const log = (msg) => console.log(`[start] ${msg}`);

const port = env.MINECLAW_WEB_Web__Port || '8080';
if (!env.MINECLAW_WEB_Web__AllowedOrigins && !env.MINECLAW_WEB_CONFIG) {
  env.MINECLAW_WEB_Web__AllowedOrigins = `http://localhost:${port},http://127.0.0.1:${port}`;
  log(`MINECLAW_WEB_Web__AllowedOrigins is not set: only http://localhost:${port} is accepted for sign-in; set it to every URL you browse to`);
}

const heap = (name, fallback) => {
  const n = Number(env[name]);
  return Number.isInteger(n) && n > 0 ? n : fallback;
};

const children = new Map();
let stopping = false;
let exitCode = 0;

function stop(signal) {
  if (stopping) return;
  stopping = true;
  for (const child of children.values()) child.kill(signal);
  // A child that ignores SIGTERM is killed after 20 s (docker's default stop timeout is 10 s; compose/k8s give more).
  setTimeout(() => {
    for (const child of children.values()) child.kill('SIGKILL');
  }, 20_000).unref();
}

function run(name, args, cwd, extraEnv) {
  const child = spawn(process.execPath, args, { cwd, env: { ...env, ...extraEnv }, stdio: 'inherit' });
  children.set(name, child);
  child.on('error', (err) => {
    log(`${name} failed to start: ${err.message}`);
    children.delete(name);
    exitCode = 1;
    stop('SIGTERM');
    if (children.size === 0) process.exit(exitCode);
  });
  child.on('exit', (code, signal) => {
    children.delete(name);
    if (!stopping) {
      log(`${name} exited (${signal ?? `code ${code}`}); stopping the other process`);
      exitCode = code ? code : 1;
      stop('SIGTERM');
    }
    if (children.size === 0) process.exit(exitCode);
  });
}

for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    log(`${sig}: stopping`);
    stop(sig);
  });
}

run('ui', [`--max-old-space-size=${heap('MINECLAW_UI_HEAP_MB', 256)}`, 'server.js'],
  join(root, 'apps/web/.next/standalone/apps/web'),
  { PORT: '3000', HOSTNAME: '127.0.0.1', NEXT_TELEMETRY_DISABLED: '1' });
run('api', [`--max-old-space-size=${heap('MINECLAW_API_HEAP_MB', 384)}`, '--disable-warning=ExperimentalWarning', 'dist/main.js'],
  join(root, 'apps/api'), {});
