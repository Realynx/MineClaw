/**
 * Entry point. `node dist/main.js [--dev] [--mock-bridge] [--config <file>] [--port <n>] [--bind <addr>]`.
 * Prod (CT 114): 0.0.0.0:8080, proxies non-/api to Next on 127.0.0.1:3000. Dev: 127.0.0.1:4000, Next rewrites /api here.
 * In dev, `web/apps/api/.env.local` (gitignored) is loaded first if present (e.g. MINECLAW_BRIDGE_KEY=...).
 */
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { createApp } from './app.js';
import { createLogger } from './common/logger.js';
import { describeConfig, loadConfig } from './config/config.js';

async function main(): Promise<void> {
  const envLocal = join(process.cwd(), '.env.local');
  if (process.env.NODE_ENV !== 'production' && existsSync(envLocal)) process.loadEnvFile(envLocal);

  const { config, dev, files } = loadConfig();
  const log = createLogger(config.web.logLevel);
  log.info({ dev, files, config: describeConfig(config) }, 'starting mineclaw-web-api');
  const api = await createApp(config, { logger: log });
  const url = await api.listen();
  log.info({ url, proxyNext: config.web.proxyNext, mock: config.bridge.mock }, 'listening');

  let stopping = false;
  const stop = (signal: string) => {
    if (stopping) return;
    stopping = true;
    log.info({ signal }, 'shutting down');
    const force = setTimeout(() => process.exit(1), 10_000);
    force.unref();
    api.close().then(
      () => process.exit(0),
      () => process.exit(1),
    );
  };
  process.on('SIGTERM', () => stop('SIGTERM'));
  process.on('SIGINT', () => stop('SIGINT'));
}

main().catch((e: unknown) => {
  console.error(`mineclaw-web-api failed to start: ${e instanceof Error ? e.message : String(e)}`);
  process.exit(1);
});
