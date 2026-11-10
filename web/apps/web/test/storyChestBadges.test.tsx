/**
 * The map's chest badge poll (chestBadges.ts `useContainerBadges`): the hive's container summaries are asked for every 30 s,
 * and only while the map is zoomed in far enough to draw badges. The zoom is a getter read in the hook's 5 s timer.
 */
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { setFetch } from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { BADGE_POLL_MS, useContainerBadges } from '@/features/story/chestBadges';

type V3 = [number, number, number];
const CHESTS: Array<{ pos: V3 }> = [{ pos: [2, 74, -4] }, { pos: [10, 70, 6] }];
const cells = () => CHESTS;

let asked: string[] = [];
let qc: QueryClient;

beforeEach(() => {
  // Only the intervals are faked: the 5 s zoom check and the query's refetch interval. Promises and timeouts stay real.
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
  asked = [];
  setFetch((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    asked.push(url);
    return Promise.resolve(new Response(JSON.stringify({ dim: 0, containers: [], capped: false }), { status: 200 }));
  });
  qc = makeQueryClient();
});

afterEach(() => {
  cleanup();
  qc.clear();
  vi.useRealTimers();
  setFetch((...args) => globalThis.fetch(...args));
});

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={qc}>{children}</QueryClientProvider>
);
const boxAsks = () => asked.filter((u) => u.startsWith('/api/world/containers?')).length;
/** Lets the hook's state updates and any request they start settle (real timeouts and promises). */
const settle = () => act(() => new Promise<void>((r) => setTimeout(r, 20)));

it('asks for nothing while the getter says the map is zoomed out, and starts once it is zoomed in', async () => {
  let zoomedIn = false;
  renderHook(() => useContainerBadges(cells, true, () => zoomedIn), { wrapper });
  await settle();
  act(() => void vi.advanceTimersByTime(60_000));
  await settle();
  expect(boxAsks()).toBe(0);

  zoomedIn = true;
  act(() => void vi.advanceTimersByTime(5_000)); // the next zoom check
  await waitFor(() => expect(boxAsks()).toBe(1));
  expect(asked[0]).toContain('x0=0');
});

it('asks every 30 s while zoomed in, and stops after zooming out', async () => {
  let zoomedIn = true;
  renderHook(() => useContainerBadges(cells, true, () => zoomedIn), { wrapper });
  await waitFor(() => expect(boxAsks()).toBe(1));

  act(() => void vi.advanceTimersByTime(BADGE_POLL_MS - 5_000));
  await settle();
  expect(boxAsks()).toBe(1); // the old 10 s poll would have asked twice by now
  act(() => void vi.advanceTimersByTime(5_000));
  await waitFor(() => expect(boxAsks()).toBe(2));

  zoomedIn = false;
  act(() => void vi.advanceTimersByTime(5_000)); // the zoom check turns the poll off
  await settle();
  act(() => void vi.advanceTimersByTime(3 * BADGE_POLL_MS));
  await settle();
  expect(boxAsks()).toBe(2);
});

it('without a zoom getter the poll runs as before (other callers)', async () => {
  renderHook(() => useContainerBadges(cells), { wrapper });
  await waitFor(() => expect(boxAsks()).toBe(1));
});
