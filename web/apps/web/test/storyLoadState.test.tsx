import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Me } from '@mineclaw/contracts';
import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TooltipProvider } from '@/components/ui/overlays';
import { noteStoryFrame } from '@/features/story/frames';
import { colonyLoadState, defaultColony, useStoryModeState, type ColonyLoad } from '@/features/story/mode/state';
import type { StoryMapProps } from '@/features/story/StoryMap';
import { StoryMode } from '@/features/story/StoryMode';
import { colonyKey, refetchMissing, useColony } from '@/features/story/useStory';
import { clearView, loadView, saveView } from '@/features/story/viewMemory';
import { api, setFetch } from '@/lib/api/client';
import { StaticAuthProvider } from '@/lib/auth';
import type * as Integration from '@/features/world/integration';

const nav = vi.hoisted(() => ({ params: new URLSearchParams(), replace: vi.fn() }));
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace: nav.replace, push: vi.fn() }),
  useSearchParams: () => nav.params,
}));
// The page is rendered around a stand-in map that keeps the props it is handed (the map itself: storyCamera.test.tsx).
const map = vi.hoisted(() => ({ props: null as StoryMapProps | null }));
vi.mock('@/features/story/StoryMap', () => ({
  StoryMap: (props: StoryMapProps) => {
    map.props = props;
    return null;
  },
}));
vi.mock('@/features/world/integration', async (importOriginal) => ({
  ...(await importOriginal<typeof Integration>()),
  WorldHost: ({ children }: { children: ReactNode }) => children,
  loadWorldViewer: () => Promise.resolve(null),
}));

const at = '2026-11-09T10:00:00+00:00';
const me: Me = {
  username: 'fox',
  player: 'poofimafox',
  roles: ['viewer', 'story'],
  totpEnrolled: false,
  sessionCreatedAt: at,
  idleExpiresAt: at,
  absoluteExpiresAt: at,
};
const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const busy = () => json(503, { error: 'the bot is busy', code: 'busy' });
const missing = () => json(404, { error: 'no such colony' });
const colonyState = (id: string, home: [number, number, number] = [100, 63, 200]) =>
  json(200, { id, rev: 7, at, home });
const summary = (id: string, active = true) => ({ id, name: id, pawns: [], active });
const colonyList = (...colonies: Array<ReturnType<typeof summary>>) => json(200, { colonies });

/** Every request the page made, and what answers each path. */
let calls: string[];
let respond: (path: string) => Response | Promise<Response>;

/** A reply that waits until the test lets it go. */
function held() {
  let release!: (r: Response) => void;
  const reply = new Promise<Response>((resolve) => {
    release = resolve;
  });
  return { reply, release };
}

function setup() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>
      <StaticAuthProvider me={me}>{children}</StaticAuthProvider>
    </QueryClientProvider>
  );
  return { qc, wrapper };
}

beforeEach(() => {
  calls = [];
  map.props = null;
  nav.params = new URLSearchParams();
  localStorage.clear();
  clearView('fox');
  setFetch((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    calls.push(url);
    return Promise.resolve(respond(url));
  });
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('no colony, or a colony that cannot be reached', () => {
  const load = (over: Partial<ColonyLoad>): ColonyLoad => ({
    explicit: false,
    listLoaded: false,
    listCount: 0,
    listFailed: false,
    hasState: false,
    stateFailed: false,
    stateMissing: false,
    ...over,
  });
  const loading = { noColony: false, loadError: false };

  it('is still loading while the list is on its way', () => {
    expect(colonyLoadState(load({}))).toEqual(loading);
  });

  it('is a load error when the list failed and none was ever read', () => {
    expect(colonyLoadState(load({ listFailed: true }))).toEqual({ noColony: false, loadError: true });
  });

  it('is no colony when the list came back empty', () => {
    expect(colonyLoadState(load({ listLoaded: true }))).toEqual({ noColony: true, loadError: false });
  });

  it('is still loading while the state of a listed colony is on its way', () => {
    expect(colonyLoadState(load({ listLoaded: true, listCount: 1 }))).toEqual(loading);
  });

  it('is a load error, never "no colony", when the state request failed', () => {
    expect(colonyLoadState(load({ listLoaded: true, listCount: 1, stateFailed: true }))).toEqual({
      noColony: false,
      loadError: true,
    });
  });

  it('is no colony when the address names one the server does not have', () => {
    const gone = load({ explicit: true, listLoaded: true, listCount: 1, stateFailed: true, stateMissing: true });
    expect(colonyLoadState(gone)).toEqual({ noColony: true, loadError: false });
  });

  it('waits for the list when the remembered colony answers 404 before the list is in', () => {
    expect(colonyLoadState(load({ stateFailed: true, stateMissing: true }))).toEqual(loading);
  });

  it('waits for a named colony’s own answer even when the list is empty', () => {
    expect(colonyLoadState(load({ explicit: true, listLoaded: true }))).toEqual(loading);
    expect(colonyLoadState(load({ explicit: true, listLoaded: true, stateFailed: true }))).toEqual({
      noColony: false,
      loadError: true,
    });
  });

  it('is neither once the state is here, whatever happened to the list', () => {
    expect(colonyLoadState(load({ hasState: true, listFailed: true }))).toEqual(loading);
  });

  it('does not mind a list refresh that failed while an earlier list is held', () => {
    expect(colonyLoadState(load({ listLoaded: true, listCount: 1, listFailed: true }))).toEqual(loading);
  });
});

describe('the colony that opens with none named in the address', () => {
  const list = [summary('story-0', false), summary('story-1'), summary('story-2')];

  it('is the one this browser opened last, asked for before the list is in', () => {
    expect(defaultColony(undefined, 'story-2')).toBe('story-2');
    expect(defaultColony(undefined, null)).toBeNull();
  });

  it('stays the remembered one while the list has it, released or not', () => {
    expect(defaultColony(list, 'story-2')).toBe('story-2');
    expect(defaultColony(list, 'story-0')).toBe('story-0');
  });

  it('is the first active one when nothing is remembered or the remembered one is not listed', () => {
    expect(defaultColony(list, null)).toBe('story-1');
    expect(defaultColony(list, 'story-9')).toBe('story-1');
  });

  it('is the first one when none is active, and none when there is no colony', () => {
    expect(defaultColony([summary('story-0', false), summary('story-3', false)], null)).toBe('story-0');
    expect(defaultColony([], 'story-2')).toBeNull();
  });
});

describe('a colony whose first fetch failed', () => {
  it('is asked for again within three seconds although stream frames keep flowing', async () => {
    vi.useFakeTimers();
    respond = () => (calls.length === 1 ? busy() : colonyState('story-1'));
    const { qc, wrapper } = setup();
    noteStoryFrame('story-1');
    const frames = setInterval(() => noteStoryFrame('story-1'), 1000);
    const { result, unmount } = renderHook(() => useColony('story-1'), { wrapper });
    await act(() => vi.advanceTimersByTimeAsync(2999));
    expect(calls).toEqual(['/api/story/colonies/story-1']);
    expect(result.current.isError).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(calls).toEqual(['/api/story/colonies/story-1', '/api/story/colonies/story-1']);
    await act(() => vi.advanceTimersByTimeAsync(50)); // the reply is read
    expect(result.current.data?.home).toEqual([100, 63, 200]);
    expect(result.current.isError).toBe(false);
    // With a state to merge frames into, the catch-up stands down again while they flow.
    await act(() => vi.advanceTimersByTimeAsync(6000));
    expect(calls).toHaveLength(2);
    expect(qc.getQueryState(colonyKey('story-1'))?.status).toBe('success');
    clearInterval(frames);
    unmount();
  });

  it('keeps saying it failed while the next attempt is out, and makes one attempt at a time', async () => {
    vi.useFakeTimers();
    const second = held();
    respond = () => (calls.length === 1 ? busy() : second.reply);
    const { wrapper } = setup();
    noteStoryFrame('story-1');
    const { result, unmount } = renderHook(() => useColony('story-1'), { wrapper });
    await act(() => vi.advanceTimersByTimeAsync(3000));
    expect(calls).toHaveLength(2); // asked again, and that request is still out
    expect(result.current.isError).toBe(true);
    expect(result.current.isLoading).toBe(false);
    await act(() => vi.advanceTimersByTimeAsync(6000));
    expect(calls).toHaveLength(2); // one request at a time: it may be waiting on the colony
    expect(result.current.isError).toBe(true);
    await act(async () => {
      second.release(colonyState('story-1'));
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(result.current.isError).toBe(false);
    expect(result.current.data?.id).toBe('story-1');
    unmount();
  });

  it('is not asked for again when the server does not have it (404)', async () => {
    vi.useFakeTimers();
    respond = () => missing();
    const { wrapper } = setup();
    const { result, unmount } = renderHook(() => useColony('story-9'), { wrapper });
    await act(() => vi.advanceTimersByTimeAsync(9000));
    expect(result.current.isError).toBe(true);
    expect(calls).toEqual(['/api/story/colonies/story-9']);
    unmount();
  });

  it('is left alone while its first fetch is out, in a hidden tab and once it has a state', async () => {
    const { qc } = setup();
    const key = colonyKey('story-1');
    expect(refetchMissing(qc, 'story-1')).toBe(false); // never asked for: nothing to repeat
    const first = held();
    respond = () => first.reply;
    const fetched = qc
      .fetchQuery({ queryKey: key, queryFn: () => api('story.get', { params: { id: 'story-1' } }) })
      .catch(() => undefined);
    expect(refetchMissing(qc, 'story-1')).toBe(false); // the first fetch is still out
    first.release(busy());
    await fetched;
    expect(calls).toHaveLength(1);
    const hidden = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    expect(refetchMissing(qc, 'story-1')).toBe(false);
    hidden.mockRestore();
    const second = held();
    respond = () => second.reply;
    expect(refetchMissing(qc, 'story-1')).toBe(true);
    expect(refetchMissing(qc, 'story-1')).toBe(true); // being asked for: not asked twice
    expect(calls).toHaveLength(2);
    second.release(busy());
    respond = () => colonyState('story-1');
    await waitFor(() => {
      refetchMissing(qc, 'story-1'); // the failed attempt is over: the next ask goes out
      expect(calls).toHaveLength(3);
    });
    await waitFor(() => expect(qc.getQueryData(key)).toMatchObject({ id: 'story-1', rev: 7 }));
    expect(refetchMissing(qc, 'story-1')).toBe(false);
    expect(calls).toHaveLength(3);
  });
});

describe('the Controller’s colony data', () => {
  it('treats a failed state request as a load error: no found-a-colony card, no settling elsewhere', async () => {
    respond = (path) => (path === '/api/story' ? colonyList(summary('story-1')) : busy());
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    await waitFor(() => expect(result.current.loadError).toBe(true));
    expect(result.current.noColony).toBe(false);
    expect(result.current.id).toBe('story-1');
  });

  it('treats a failed list request as a load error too', async () => {
    respond = () => busy();
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    await waitFor(() => expect(result.current.loadError).toBe(true));
    expect(result.current.noColony).toBe(false);
    expect(result.current.id).toBeNull();
  });

  it('says there is no colony only when the list came back empty', async () => {
    respond = () => colonyList();
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    expect(result.current.noColony).toBe(false); // still loading
    await waitFor(() => expect(result.current.noColony).toBe(true));
    expect(result.current.loadError).toBe(false);
  });

  it('says there is no colony when the address names one that is gone', async () => {
    nav.params = new URLSearchParams('colony=story-7');
    respond = (path) => (path === '/api/story' ? colonyList(summary('story-1')) : missing());
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    await waitFor(() => expect(result.current.noColony).toBe(true));
    expect(result.current.loadError).toBe(false);
    expect(result.current.id).toBe('story-7');
  });

  it('opens the colony this browser showed last and asks for its state before the list is in', async () => {
    const view = { colony: 'story-2', x: 216.5, z: 400.5, zoom: 60 };
    saveView('fox', view);
    const list = held();
    respond = (path) => (path === '/api/story' ? list.reply : colonyState('story-2', [216, 64, 400]));
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    expect(result.current.id).toBe('story-2');
    expect(result.current.initialView).toEqual(view);
    await waitFor(() => expect(result.current.state?.home).toEqual([216, 64, 400]));
    expect(calls).toContain('/api/story/colonies/story-2');
    expect(result.current.colonies.data).toBeUndefined(); // the list has not answered yet
    await act(async () => {
      list.release(colonyList(summary('story-1'), summary('story-2')));
      await Promise.resolve();
    });
    await waitFor(() => expect(result.current.list).toHaveLength(2));
    expect(result.current.id).toBe('story-2'); // not the first listed
  });

  it('forgets a remembered colony that answers 404 and opens the first active one', async () => {
    saveView('fox', { colony: 'story-9', x: 0.5, z: 0.5, zoom: 44 });
    const list = held();
    respond = (path) => {
      if (path === '/api/story') return list.reply;
      return path.endsWith('/story-9') ? missing() : colonyState('story-1');
    };
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    expect(result.current.id).toBe('story-9');
    await waitFor(() => expect(loadView('fox')).toBeNull());
    await waitFor(() => expect(result.current.id).toBeNull());
    expect(result.current.initialView).toBeNull();
    expect(result.current.noColony).toBe(false); // the list decides, and it is not in yet
    expect(result.current.loadError).toBe(false);
    await act(async () => {
      list.release(colonyList(summary('story-0', false), summary('story-1')));
      await Promise.resolve();
    });
    await waitFor(() => expect(result.current.state?.id).toBe('story-1'));
    expect(result.current.id).toBe('story-1');
  });

  it('remembers the map’s view for the colony shown, and only once that colony’s state is here', async () => {
    const state = held();
    respond = (path) => (path === '/api/story' ? colonyList(summary('story-1')) : state.reply);
    const { result } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    await waitFor(() => expect(result.current.id).toBe('story-1'));
    act(() => result.current.rememberView({ x: 1.5, z: 2.5, zoom: 44 }));
    expect(loadView('fox')).toBeNull();
    await act(async () => {
      state.release(colonyState('story-1'));
      await Promise.resolve();
    });
    await waitFor(() => expect(result.current.state).toBeDefined());
    act(() => result.current.rememberView({ x: 120.5, z: 180.5, zoom: 80 }));
    expect(loadView('fox')).toEqual({ colony: 'story-1', x: 120.5, z: 180.5, zoom: 80 });
    expect(result.current.initialView).toBeNull(); // read once per visit: a later save is not where this visit opened
  });

  it('stays on the colony last shown when the address stops naming one', async () => {
    nav.params = new URLSearchParams('colony=story-2');
    respond = (path) =>
      path === '/api/story' ? colonyList(summary('story-1'), summary('story-2')) : colonyState(path.split('/').pop()!);
    const { result, rerender } = renderHook(() => useStoryModeState(), { wrapper: setup().wrapper });
    await waitFor(() => expect(result.current.state?.id).toBe('story-2'));
    await waitFor(() => expect(result.current.list).toHaveLength(2));
    act(() => result.current.rememberView({ x: 1.5, z: 2.5, zoom: 44 }));
    nav.params = new URLSearchParams(); // the Controller link again: no `?colony=`
    rerender();
    expect(result.current.id).toBe('story-2'); // not the first active one
  });
});

describe('the Controller page', () => {
  const FOUND_CARD = /Run a colony of pawns/;
  const ERROR_CARD = "Can't reach the colony. Retrying…";

  function open() {
    const Wrapper = setup().wrapper;
    return render(
      <Wrapper>
        <TooltipProvider>
          <StoryMode />
        </TooltipProvider>
      </Wrapper>,
    );
  }

  it('says it cannot reach the colony when a request failed: no found-a-colony card, no settling', async () => {
    respond = (path) => (path === '/api/story' ? colonyList(summary('story-1')) : busy());
    open();
    expect(await screen.findByText(ERROR_CARD)).toHaveAttribute('role', 'status');
    expect(screen.queryByText(FOUND_CARD)).not.toBeInTheDocument();
    expect(map.props).toMatchObject({ colony: 'story-1', home: null, settle: false, loading: false });
  });

  it('offers to found a colony, and lets the map settle over the main bot, only when there is none', async () => {
    respond = () => colonyList();
    open();
    expect(await screen.findByText(FOUND_CARD)).toBeInTheDocument();
    expect(screen.queryByText(ERROR_CARD)).not.toBeInTheDocument();
    expect(map.props).toMatchObject({ colony: null, home: null, settle: true, loading: false });
  });

  it('tells the map the colony is loading while its requests are out, and shows neither card', async () => {
    const waiting = held();
    respond = () => waiting.reply;
    open();
    await waitFor(() => expect(calls).toContain('/api/story'));
    expect(map.props).toMatchObject({ colony: null, home: null, settle: false, loading: true });
    expect(screen.queryByText(FOUND_CARD)).not.toBeInTheDocument();
    expect(screen.queryByText(ERROR_CARD)).not.toBeInTheDocument();
  });

  it('hands the map the colony, its home and the remembered view, and saves the view the map reports', async () => {
    const view = { colony: 'story-1', x: 216.5, z: 400.5, zoom: 60 };
    saveView('fox', view);
    respond = (path) => (path === '/api/story' ? colonyList(summary('story-1')) : colonyState('story-1'));
    open();
    await waitFor(() => expect(map.props?.home).toEqual([100, 63, 200]));
    expect(map.props).toMatchObject({ colony: 'story-1', settle: false, loading: false, initialView: view });
    act(() => map.props!.onView!({ x: 120.5, z: 180.5, zoom: 80 }));
    expect(loadView('fox')).toEqual({ colony: 'story-1', x: 120.5, z: 180.5, zoom: 80 });
  });

  it('lets the map settle over the main bot for a colony that has no home', async () => {
    respond = (path) =>
      path === '/api/story' ? colonyList(summary('story-1')) : json(200, { id: 'story-1', rev: 7, at, home: null });
    open();
    await waitFor(() => expect(map.props?.state?.id).toBe('story-1'));
    expect(map.props).toMatchObject({ colony: 'story-1', home: null, settle: true, loading: false });
    expect(screen.queryByText(FOUND_CARD)).not.toBeInTheDocument();
  });
});
