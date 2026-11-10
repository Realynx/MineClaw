/**
 * An inspector as the Controller shows it: the colony state it renders is `view(state)`, the cached colony state with the
 * changes `track` is showing laid over it (features/story/optimistic.ts), so a click is drawn back into the inspector before
 * its request is answered. Requests are answered by hand and time moves only when the test moves it.
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { QueryClientProvider, useQuery, type QueryClient } from '@tanstack/react-query';
import { act, render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { vi } from 'vitest';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { colonyKey } from '@/features/story/colonySync';
import { useOptimisticColony, type Track } from '@/features/story/optimistic';

export interface Call {
  url: string;
  method: string;
  body: unknown;
  reply(body: unknown, status?: number): void;
}

/** The page's fetch, answered by hand. */
export function network(): Call[] {
  const calls: Call[] = [];
  setFetch(
    (input, init) =>
      new Promise<Response>((resolve) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        calls.push({
          url,
          method: init?.method ?? 'GET',
          body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
          reply: (body, status = 200) => resolve(new Response(JSON.stringify(body), { status })),
        });
      }),
  );
  return calls;
}

/** The colony writes sent so far, as their bodies. */
export const posted = (calls: Call[]) => calls.filter((c) => c.method === 'POST');

/** Lets answered requests run through the client (real macrotasks: the fake clock does not move). */
export async function settle(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 6; i++) await new Promise<void>((r) => setImmediate(r));
  });
}

type Ui = (page: { state: StoryColonyState | undefined; track: Track }) => ReactElement | null;

const clients: QueryClient[] = [];

/** Fake clock and a CSRF token for the writes (call in `beforeEach`). */
export function harnessUp(): void {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'], now: 1_000_000 });
  setCsrfToken('test');
}

/** Puts the clock, the token, the fetch and the query clients back (call in `afterEach`). */
export function harnessDown(): void {
  vi.useRealTimers();
  for (const qc of clients.splice(0)) qc.clear();
  setCsrfToken(null);
  setFetch((...args) => globalThis.fetch(...args));
}

/**
 * Renders `ui` with colony `id`'s shown state and `track`, starting from `initial`. `server` replaces the cached state (a
 * catch-up or a frame), `show` renders another inspector over the same page (another selection).
 */
export function mountInspector(id: string, initial: StoryColonyState, ui: Ui) {
  const qc = makeQueryClient();
  clients.push(qc);
  qc.setQueryData(colonyKey(id), initial);
  function Page({ ui: draw }: { ui: Ui }) {
    const { data } = useQuery<StoryColonyState>({
      queryKey: colonyKey(id),
      queryFn: () => new Promise<StoryColonyState>(() => undefined),
      enabled: false,
    });
    const { state, track } = useOptimisticColony(id, data);
    return draw({ state, track });
  }
  const tree = (draw: Ui) => (
    <QueryClientProvider client={qc}>
      <Page ui={draw} />
    </QueryClientProvider>
  );
  const view = render(tree(ui));
  return {
    qc,
    view,
    show: (next: Ui) => view.rerender(tree(next)),
    server: async (state: StoryColonyState) => {
      act(() => {
        qc.setQueryData(colonyKey(id), state);
        // The query tells the component on a zero timeout.
        vi.advanceTimersByTime(0);
      });
      await settle();
    },
  };
}
