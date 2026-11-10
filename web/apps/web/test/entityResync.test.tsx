import type { WorldEntities } from '@mineclaw/contracts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api/client';
import type * as ApiClient from '@/lib/api/client';
import { applyLiveEvent, liveKeys } from '@/lib/live/dispatch';
import { useEntities } from '@/lib/live/hooks';

vi.mock('@/lib/api/client', async (original) => ({ ...await original<typeof ApiClient>(), api: vi.fn() }));
const clients: QueryClient[] = [];
afterEach(() => { cleanup(); for (const client of clients.splice(0)) client.clear(); vi.clearAllMocks(); });

const frame = (at: number, x: number, rev = 42): WorldEntities => ({
  at, rev, full: false, removed: [], entities: [],
  bots: [{ name: 'maid_chan_101', short: '101', main: false, dim: 0, x, y: 64, z: 0, hp: 20, food: 20, claims: [], live: true, ageSeconds: 0 }],
});

describe('entity REST resync races', () => {
  it.each([
    { name: 'older pose at the same entity revision', fetched: frame(1100, 1), expected: 2 },
    { name: 'older entity revision', fetched: frame(1100, 1, 41), expected: 2 },
    { name: 'newer pose at the same entity revision', fetched: frame(1300, 3), expected: 3 },
  ])('keeps the newest state when REST returns an $name', async ({ fetched, expected }) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    clients.push(client);
    client.setQueryData(liveKeys.entities, frame(1000, 0));
    let resolve!: (value: WorldEntities) => void;
    vi.mocked(api).mockImplementationOnce(() => new Promise<WorldEntities>((done) => { resolve = done; }));
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
    const hook = renderHook(() => useEntities(), { wrapper });
    let request!: ReturnType<typeof hook.result.current.refetch>;
    act(() => { request = hook.result.current.refetch(); });
    await waitFor(() => expect(api).toHaveBeenCalledOnce());
    act(() => { applyLiveEvent(client, 'entities', frame(1200, 2)); });
    await act(async () => { resolve({ ...fetched, full: true }); await request; });
    expect(client.getQueryData<WorldEntities>(liveKeys.entities)?.bots[0]?.x).toBe(expected);
  });

  it('lays an older snapshot under a newer set that began with a delta instead of keeping that delta alone', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    clients.push(client);
    const mob = (id: number, x: number) => ({ kind: 'mob', dim: 0, id, x, y: 64, z: 0 });
    // The stream got here first: one mob moved, one was removed, nothing about the others.
    client.setQueryData(liveKeys.entities, { ...frame(1200, 2, 43), entities: [mob(1, 7)], removed: [[0, 3]] });
    vi.mocked(api).mockResolvedValueOnce({
      ...frame(1100, 1, 42),
      full: true,
      entities: [mob(1, 0), mob(2, 5), mob(3, 9)],
    });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const hook = renderHook(() => useEntities(), { wrapper });
    await act(async () => {
      await hook.result.current.refetch();
    });
    const set = client.getQueryData<WorldEntities>(liveKeys.entities)!;
    expect(set.entities.map((e) => [e.id, e.x])).toEqual([
      [1, 7],
      [2, 5],
    ]);
    expect(set.full).toBe(true);
    expect(set.rev).toBe(43);
    expect(set.bots[0]?.x).toBe(2);
  });
});
