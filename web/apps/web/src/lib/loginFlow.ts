import { ApiError } from './api/client';

/**
 * Whether a failed login is the server asking for the TOTP step. The contract keeps auth errors generic, so we accept any of:
 * a validation detail on `totp`, or a 401 whose message mentions the code.
 */
export function needsTotp(e: unknown): boolean {
  if (!(e instanceof ApiError)) return false;
  if (e.details?.some((d) => d.path === 'totp')) return true;
  return e.status === 401 && /totp|two-factor|authenticator|code/i.test(e.message);
}

/** Only same-origin paths may be a post-login destination (no open redirects). */
export function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/login') ? next : '/live';
}
