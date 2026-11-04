/**
 * TanStack Query glue: one key shape for every route (`[routeId, params, query]`), sensible stale times per route family,
 * and `useApi` / `useApiMutation` hooks. Live data (status, now, feed, events, entities, hive, memory activity) is pushed by
 * the shared SSE client into these same keys, so those queries never poll (staleTime Infinity).
 */
import {
  QueryClient,
  useMutation,
  useQuery,
  type QueryKey,
  type UseMutationOptions,
  type UseQueryOptions,
} from '@tanstack/react-query';
import type { RouteId } from '@mineclaw/contracts';
import { api, ApiError, type RouteBody, type RouteQueryInput, type RouteResponse } from './client';

export type ParamMap = Record<string, string | number>;

export function qk<K extends RouteId>(id: K, params?: ParamMap, query?: RouteQueryInput<K> | object): QueryKey {
  return [id, params ?? {}, query ?? {}];
}

/** Routes whose data arrives through SSE: fetched once (and on resync), never polled. */
export const LIVE_ROUTES: ReadonlySet<RouteId> = new Set<RouteId>([
  'status',
  'bots.now',
  'feed',
  'events',
  'world.entities',
  'hive.tree',
  'memoryActivity.list',
]);

const MIN = 60_000;

/** Stale time by route family. Db reads are a live read-only view of the bot's db, so they go stale after a short while. */
export function staleTimeFor(id: RouteId): number {
  if (LIVE_ROUTES.has(id)) return Infinity;
  if (id.startsWith('scripts.') && id !== 'scripts.list') return 5 * MIN; // versions are immutable once saved
  return STALE_TIME.get(id) ?? 30_000;
}

/** The other routes that do not go stale after the default 30 s. */
const STALE_TIME = new Map<RouteId, number>([
  ['auth.me', 5 * MIN],
  ['admin.schema', Infinity],
  ['designs.diff', 10 * MIN],
  ['memoryActivity.heat', 15_000],
  ['brains.context', 0],
  ['bodies.script', 0],
]);

export function makeQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: (count, err) => {
          if (err instanceof ApiError && [400, 401, 403, 404, 501].includes(err.status)) return false;
          return count < 2;
        },
        refetchOnWindowFocus: false,
        staleTime: 30_000,
        gcTime: 5 * MIN,
      },
      mutations: { retry: false },
    },
  });
}

type ApiQueryOptions<K extends RouteId> = Omit<
  UseQueryOptions<RouteResponse<K>, ApiError, RouteResponse<K>, QueryKey>,
  'queryKey' | 'queryFn'
>;

/** `useQuery` over a route id. */
export function useApi<K extends RouteId>(
  id: K,
  call: { params?: ParamMap; query?: RouteQueryInput<K> } = {},
  options: ApiQueryOptions<K> = {},
) {
  return useQuery<RouteResponse<K>, ApiError, RouteResponse<K>, QueryKey>({
    queryKey: qk(id, call.params, call.query),
    queryFn: ({ signal }) => api(id, { params: call.params, query: call.query, signal }),
    staleTime: staleTimeFor(id),
    ...options,
  });
}

export interface MutationVars<K extends RouteId> {
  params?: ParamMap;
  body?: RouteBody<K>;
  file?: Blob;
}

/** `useMutation` over a route id. */
export function useApiMutation<K extends RouteId>(
  id: K,
  options: Omit<UseMutationOptions<RouteResponse<K>, ApiError, MutationVars<K>>, 'mutationFn'> = {},
) {
  return useMutation<RouteResponse<K>, ApiError, MutationVars<K>>({
    mutationFn: (vars) => api(id, { params: vars.params, body: vars.body, file: vars.file }),
    ...options,
  });
}
