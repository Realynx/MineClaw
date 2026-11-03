/**
 * Structured logs (pino). Request logs carry only method, path, status, client IP, user and request id: headers (cookies,
 * CSRF tokens, Access JWTs) and bodies are never logged, and `redact` is a second net for anything that slips into a log call.
 */
import type { LoggerService } from '@nestjs/common';
import { pino, type Logger } from 'pino';

export type { Logger } from 'pino';

export const REDACT_PATHS = [
  'password',
  '*.password',
  'key',
  '*.key',
  'secret',
  '*.secret',
  'totp',
  '*.totp',
  'csrfToken',
  '*.csrfToken',
  'cookie',
  'headers.cookie',
  'headers.authorization',
  'headers["x-csrf-token"]',
  'headers["cf-access-jwt-assertion"]',
  'headers["x-mcx-sig"]',
  'req.headers',
  'res.headers',
];

export function createLogger(level: string, destination?: NodeJS.WritableStream): Logger {
  return pino(
    {
      level,
      base: { svc: 'mineclaw-web-api' },
      redact: { paths: REDACT_PATHS, censor: '[redacted]' },
      timestamp: pino.stdTimeFunctions.isoTime,
    },
    destination as never,
  );
}

/** Routes Nest's own logs (bootstrap, router) through pino. */
export class PinoNestLogger implements LoggerService {
  constructor(private readonly logger: Logger) {}
  log(message: unknown, ...rest: unknown[]): void {
    this.logger.info({ ctx: rest.at(-1) }, String(message));
  }
  error(message: unknown, ...rest: unknown[]): void {
    this.logger.error({ ctx: rest.at(-1) }, String(message));
  }
  warn(message: unknown, ...rest: unknown[]): void {
    this.logger.warn({ ctx: rest.at(-1) }, String(message));
  }
  debug(message: unknown, ...rest: unknown[]): void {
    this.logger.debug({ ctx: rest.at(-1) }, String(message));
  }
  verbose(message: unknown, ...rest: unknown[]): void {
    this.logger.trace({ ctx: rest.at(-1) }, String(message));
  }
}
