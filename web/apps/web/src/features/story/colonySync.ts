/**
 * Keeps one colony's cached state true to the bot's. The state is read whole once (and again every minute and after a stream
 * resync), then moved on by deltas: the stream's `story` frames and the `since=rev` catch-up. A delta says only what changed
 * after its `since`, so it may only be merged into a state that already holds everything up to there. Three rules follow:
 *
 * - a frame that starts after the cached revision is not merged: one catch-up from the cached revision closes the gap;
 * - a whole read that comes back older than the cache (frames kept merging while it was on its way) is first brought up to
 *   date with a delta from its own revision, so it never puts older rows under the frames that follow;
 * - catch-ups run one at a time per colony, and every caller that arrives meanwhile shares the one that follows.
 *
 * No React here: the hooks (`useStory.ts`) and the stream (`lib/live/dispatch.ts`) call in, and tests drive it directly.
 */
import type { QueryClient } from '@tanstack/react-query';
import type { RouteId, StoryColonyFrame, StoryColonyState } from '@mineclaw/contracts';
import { api } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { mergeColony } from './model';

export const colonyKey = (id: string) => qk('story.get', { id });

/** Frames kept while a catch-up is on the wire (the bridge sends two a second: several seconds' worth). */
const MAX_WAITING = 16;
/** Deltas one catch-up, or one whole read, may ask for before it leaves what is still missing to the next frame or tick. */
const MAX_READS = 3;

interface Follower {
  promise: Promise<void>;
  resolve: () => void;
  reject: (e: unknown) => void;
}

/** One colony's catch-up in one query client. */
interface Sync {
  /** A catch-up is on the wire. */
  running: boolean;
  /** The one catch-up that starts when the running one ends, shared by every caller that arrived meanwhile. */
  follower: Follower | null;
  /** Frames that arrived ahead of the cache while the running catch-up was on the wire, oldest first. */
  waiting: StoryColonyFrame[];
}

const clients = new WeakMap<QueryClient, Map<string, Sync>>();

function syncOf(qc: QueryClient, id: string): Sync {
  let colonies = clients.get(qc);
  if (!colonies) clients.set(qc, (colonies = new Map<string, Sync>()));
  let sync = colonies.get(id);
  if (!sync) colonies.set(id, (sync = { running: false, follower: null, waiting: [] }));
  return sync;
}

function follower(): Follower {
  let resolve!: () => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<void>((ok, fail) => {
    resolve = ok;
    reject = fail;
  });
  return { promise, resolve, reject };
}

/**
 * True when delta `d` starts after the revision `state` holds. It says nothing about what changed in between, so merging it
 * would leave those rows stale under a newer revision. A whole state, or a delta without `since` (an older bot), never does.
 */
export function startsAhead(
  state: Pick<StoryColonyState, 'rev'>,
  d: Pick<StoryColonyState, 'full' | 'since'>,
): boolean {
  return !d.full && typeof d.since === 'number' && d.since > state.rev;
}

/** True while a catch-up for the colony is on the wire. */
export function catchingUp(qc: QueryClient, id: string): boolean {
  return clients.get(qc)?.get(id)?.running ?? false;
}

/**
 * Catches the colony up from the revision the cache holds (merged like a stream frame). One read at a time per colony. A
 * caller that arrives while one is on the wire gets the single read that follows it: the one on the wire may have left
 * before what this caller wants to see (its own write) was applied, so its promise settles only with a read that started
 * after the call. `since` reads from that revision instead: for a state that is about to be cached.
 */
export function catchUp(qc: QueryClient, id: string, since?: number): Promise<void> {
  const sync = syncOf(qc, id);
  if (!sync.running) return run(qc, id, sync, since);
  sync.follower ??= follower();
  return sync.follower.promise;
}

async function run(qc: QueryClient, id: string, sync: Sync, since?: number): Promise<void> {
  const key = colonyKey(id);
  sync.running = true;
  try {
    for (let reads = 0; reads < MAX_READS; reads++) {
      sync.waiting = []; // whatever waited was published before the read that starts now: its reply will have it
      const from = reads === 0 && since !== undefined ? since : qc.getQueryData<StoryColonyState>(key)?.rev;
      if (from === undefined) {
        await readWhole(qc, id);
        return;
      }
      const d = await api('story.get', { params: { id }, query: { since: from } });
      const held = qc.getQueryData<StoryColonyState>(key);
      if (!held) return; // the state went meanwhile: nothing to merge into
      // The cache can step back while the reply is on its way (a whole read replaced it): read again from what it holds.
      if (startsAhead(held, { full: d.full, since: from })) continue;
      qc.setQueryData<StoryColonyState>(key, admit(sync, mergeColony(held, d)));
      if (!sync.waiting.length) return; // else a frame is still ahead: one before it never arrived
    }
  } finally {
    sync.waiting = [];
    sync.running = false;
    const next = sync.follower;
    sync.follower = null;
    if (next) void run(qc, id, sync).then(next.resolve, next.reject);
  }
}

/** Nothing is cached (the first read failed, or is still on its way): the whole state, unless the query is reading it. */
async function readWhole(qc: QueryClient, id: string): Promise<void> {
  const key = colonyKey(id);
  if (qc.isFetching({ queryKey: key, exact: true }) > 0) return;
  const d = await api('story.get', { params: { id } });
  qc.setQueryData<StoryColonyState>(key, (held) => (held && held.rev >= d.rev ? held : { ...d, full: true }));
}

/**
 * The reply's state with the waiting frames it can now take, in order. The reply may be older than a frame that arrived
 * while it was on its way: without that frame the next one would open the same gap again, and every gap would cost one more
 * read. A frame the reply already covers is dropped; one that is still ahead keeps waiting.
 */
function admit(sync: Sync, merged: StoryColonyState): StoryColonyState {
  let state = merged;
  const later: StoryColonyFrame[] = [];
  for (const f of sync.waiting) {
    if (f.rev <= state.rev) continue;
    if (startsAhead(state, f)) later.push(f);
    else state = mergeColony(state, f);
  }
  sync.waiting = later;
  return state;
}

/**
 * One `story` frame from the stream. A frame that starts at or before the cached revision is merged. One that starts after
 * it would leave a hole (frames from before the first state, a restarted bridge, a whole read that came back older), so a
 * catch-up from the cached revision closes the gap instead. While one is on the wire the frame waits for its reply.
 */
export function applyColonyFrame(qc: QueryClient, f: StoryColonyFrame): void {
  const key = colonyKey(f.id);
  const held = qc.getQueryData<StoryColonyState>(key);
  if (!held) return; // nothing to merge into yet: the first read brings its own catch-up
  if (!startsAhead(held, f)) {
    qc.setQueryData<StoryColonyState>(key, mergeColony(held, f));
    return;
  }
  const sync = syncOf(qc, f.id);
  if (!sync.running) void catchUp(qc, f.id).catch(() => undefined);
  else if (sync.waiting.push(f) > MAX_WAITING) sync.waiting.shift();
}

/**
 * The colony query's read: the whole state. Frames keep merging into the cache while it is on its way, so it can come back
 * older than what the cache holds. Returned as it is, it would undo them, and the frames that follow could not bring them
 * back: they are deltas from the bridge's cursor, which is already past the lost change. So an older whole state is brought
 * up to date with a delta from its own revision first (again if frames moved the cache on while that delta was on its way).
 */
export async function readColony(qc: QueryClient, id: string, signal?: AbortSignal): Promise<StoryColonyState> {
  const key = colonyKey(id);
  const first = qc.getQueryData<StoryColonyState>(key) === undefined;
  let state: StoryColonyState = { ...(await api('story.get', { params: { id }, signal })), full: true };
  for (let reads = 0; reads < MAX_READS; reads++) {
    const held = qc.getQueryData<StoryColonyState>(key);
    if (!held || held.rev <= state.rev) break;
    const from = state.rev;
    state = mergeColony(state, await api('story.get', { params: { id }, query: { since: from }, signal }));
    if (state.rev === from) break; // the bot has nothing newer: its revisions are behind the cache's (it restarted)
  }
  // Frames that came before the first state had nothing to merge into: one catch-up from its revision brings their changes.
  if (first) void catchUp(qc, id, state.rev).catch(() => undefined);
  return state;
}

/** Writes that change what the colony can make: its benches, its options, its build queue, its storage. */
const ARCHITECT_WRITES: ReadonlySet<RouteId> = new Set<RouteId>([
  'story.workbenches',
  'story.options',
  'story.builds',
  'story.storage',
]);

/**
 * Whether a write has to refetch the Architect palette itself. A state that carries `architectKey` already fetches the
 * palette again whenever the palette's inputs move (`useArchitect`), so only a bot without the key needs the write to do it.
 */
export function writeRefetchesArchitect(route: RouteId, state: StoryColonyState | undefined): boolean {
  return ARCHITECT_WRITES.has(route) && (state?.architectKey === null || state?.architectKey === undefined);
}
