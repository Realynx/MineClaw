/** Starts the MSW worker (mock mode only: NEXT_PUBLIC_MOCK=1). */
import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

let started: Promise<unknown> | null = null;

export function startMocks(): Promise<unknown> {
  // `onUnhandledRequest` comes from msw's SharedOptions, whose types do not resolve under this tsconfig; passing the options
  // as a variable keeps the (valid) key without an excess-property error. Next's assets and HMR pass through.
  const options = { quiet: true, serviceWorker: { url: '/mockServiceWorker.js' }, onUnhandledRequest: 'bypass' as const };
  started ??= setupWorker(...handlers).start(options);
  return started;
}
