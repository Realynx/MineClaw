'use client';

/**
 * Colony settings shown at once (docs/web/story-mode.md "Shown at once"). A change the owner makes is drawn over the colony
 * state the page holds, from the moment of the click until the colony's own state carries it. The query cache only ever
 * holds what the bot sent: the changes live beside it, per colony, and `view(server)` lays them over it.
 *
 * Every tracked write goes through one lifecycle:
 * - **sending**: the change is shown while its request is on the wire (per key one request at a time, one more queued);
 * - **acked**: the colony took it. It is shown until a state whose revision reached the answer's `rev` arrives, and again
 *   whenever the state steps back below it (a whole read, a restarted bot), for up to a minute;
 * - **refused** (a 4xx, a 503 `busy`, offline): it goes at once and the error goes to the caller;
 * - **unconfirmed** (a lost connection, a 502, another 503): the colony may or may not have it. It stays shown while the state
 *   is checked, until the state shows it (then it was applied) or 10 s have passed (then the page follows the colony and
 *   says so). It is never sent again.
 *
 * The lifecycle is built once here so pending map marks can reuse it (`add`, `ack`, `remove` without a request). The
 * command ring at the end measures every colony write.
 */
import { hashKey, useQueryClient, type QueryCacheNotifyEvent, type QueryClient } from '@tanstack/react-query';
import type {
  RouteId,
  StoryBed,
  StoryBill,
  StoryColonyState,
  StoryFilter,
  StoryStorage,
  StoryStoragePriority,
  StoryWorkbench,
} from '@mineclaw/contracts';
import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { api, ApiError, errorText, type RouteBody, type RouteResponse } from '@/lib/api/client';
import { qk } from '@/lib/api/query';
import { catchUp, colonyKey, writeRefetchesArchitect } from './colonySync';
import { storyFramesFlowing } from './frames';
import type { ColonyWrite, StoryWrite } from './useStory';

/** A change to the colony state, as the colony will make it. Rows it does not touch keep their identity. */
export type Patch = (state: StoryColonyState) => StoryColonyState;
type Cell = readonly number[];

const same = (a: Cell, b: Cell) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];
const NO_FILTER: StoryFilter = { allow: [], deny: [] };

/** `list` with the rows `edit` changes replaced; the same array when it changes none. */
function mapRows<T>(list: T[], edit: (row: T) => T): T[] {
  let out: T[] | null = null;
  for (let i = 0; i < list.length; i++) {
    const next = edit(list[i]!);
    if (next !== list[i]) (out ??= list.slice())[i] = next;
  }
  return out ?? list;
}

/** `state` with one list replaced; the same state when the list did not change. */
function withList<K extends 'storages' | 'workbenches' | 'beds'>(
  state: StoryColonyState,
  key: K,
  edit: (list: NonNullable<StoryColonyState[K]>) => NonNullable<StoryColonyState[K]>,
): StoryColonyState {
  const list = state[key];
  if (!list) return state;
  const next = edit(list);
  return next === list ? state : { ...state, [key]: next };
}

// ------------------------------------------------------------------ storage (StoryColony.Storage.cs Set, StorageSigns.cs)

/** The storage a chest belongs to (either half of a double chest). */
const storageIndex = (list: readonly StoryStorage[], c: Cell) =>
  list.findIndex((s) => same(s.chest, c) || (!!s.doubleWith && same(s.doubleWith, c)));
const signed = (s: StoryStorage) => (s.signs?.length ?? 0) > 0;

export interface StorageSet {
  filter?: StoryFilter;
  priority?: StoryStoragePriority;
  label?: string;
}

/**
 * One storage after a `set`, as the bot's `Set` makes it. A chest without signs takes the filter and the priority. Signs
 * control a chest whose filter they set: it keeps both and records the request as `pendingFilter` (the new filter, or the
 * one already requested when only the priority changes) and `pendingPriority`, which apply once the signs are gone.
 */
function setStorage(s: StoryStorage, set: StorageSet): StoryStorage {
  const next = { ...s };
  if (set.filter || (set.priority !== undefined && signed(s))) {
    const filter = set.filter ?? s.pendingFilter ?? s.filter;
    if (signed(s)) next.pendingFilter = filter;
    else next.filter = filter;
  }
  if (set.priority !== undefined) {
    if (signed(s)) next.pendingPriority = set.priority;
    else next.priority = set.priority;
  }
  if (set.label !== undefined) next.label = set.label.slice(0, 40);
  return next;
}

/** A chest that is not storage yet, as the colony registers it (its contents follow from the colony's next count). */
const newStorage = (c: Cell, set: StorageSet): StoryStorage =>
  setStorage(
    {
      chest: [c[0]!, c[1]!, c[2]!],
      doubleWith: null,
      label: null,
      priority: 'normal',
      filter: NO_FILTER,
      signs: null,
      pendingFilter: null,
      pendingPriority: null,
      contents: [],
      freeSlots: null,
      knownAt: null,
    },
    set,
  );

/** `storage {op:'set', chests, filter?, priority?, label?}`: each chest's storage takes the fields (registered if new). */
export function patchStorageSet(chests: readonly Cell[], set: StorageSet): Patch {
  return (state) => {
    const list = state.storages ?? [];
    const out = list.slice();
    const done = new Set<number>();
    for (const c of chests) {
      const i = storageIndex(out, c);
      if (i < 0) {
        done.add(out.push(newStorage(c, set)) - 1);
      } else if (!done.has(i)) {
        out[i] = setStorage(out[i]!, set);
        done.add(i);
      }
    }
    return done.size ? { ...state, storages: out } : state;
  };
}

/** What "Use as storage" sends: every item, normal priority. */
export const USE_AS_STORAGE = { priority: 'normal', filter: { allow: ['*'], deny: [] } } satisfies StorageSet;

/** "Use as storage": `set` with `USE_AS_STORAGE`. */
export const patchStorageUse = (chests: readonly Cell[]): Patch => patchStorageSet(chests, USE_AS_STORAGE);

/**
 * `storage {op:'remove', chests}`, as the bot's `RemoveChests`: a chest without signs stops being storage; one that signs
 * control requests an empty filter (it stops being storage when the signs are gone).
 */
export function patchStorageStop(chests: readonly Cell[]): Patch {
  return (state) =>
    withList(state, 'storages', (list) => {
      const hit = (s: StoryStorage) => chests.some((c) => storageIndex([s], c) === 0);
      if (!list.some(hit)) return list;
      return list.flatMap((s) => (!hit(s) ? [s] : signed(s) ? [{ ...s, pendingFilter: NO_FILTER }] : []));
    });
}

// ------------------------------------------------------------------ bills and benches (StoryColony.Bills.cs, Fuel)

const sameId = (a: string | number, b: string | number) => String(a) === String(b);

/** Each bench whose bills `edit` changes. */
const editBills = (state: StoryColonyState, edit: (bills: StoryBill[], bench: StoryWorkbench) => StoryBill[]) =>
  withList(state, 'workbenches', (list) =>
    mapRows(list, (w) => {
      const bills = edit(w.bills, w);
      return bills === w.bills ? w : { ...w, bills };
    }),
  );

/** `bills {op:'update'|'pause'|'resume', id, …}`: the bill takes these fields (as the state shows them). */
export function patchBill(id: string | number, fields: Partial<StoryBill>): Patch {
  return (state) => editBills(state, (bills) => mapRows(bills, (b) => (sameId(b.id, id) ? { ...b, ...fields } : b)));
}

/** `bills {op:'remove', id}`. */
export function patchBillRemove(id: string | number): Patch {
  return (state) =>
    editBills(state, (bills) => (bills.some((b) => sameId(b.id, id)) ? bills.filter((b) => !sameId(b.id, id)) : bills));
}

/** `bills {op:'reorder', bench, ids}`, as the bot's `ReorderBills`: the named bills in that order, then the rest as they were. */
export function patchBillsReorder(bench: Cell, ids: ReadonlyArray<string | number>): Patch {
  return (state) =>
    editBills(state, (bills, w) => {
      if (!same(w.pos, bench)) return bills;
      const named = [...new Set(ids.map(String))].flatMap((id) => bills.filter((b) => String(b.id) === id));
      const out = [...named, ...bills.filter((b) => !named.includes(b))];
      return out.every((b, i) => b === bills[i]) ? bills : out;
    });
}

/** `workbenches {op:'fuel', pos, fuel}`: the furnace's fuel choice. */
export function patchBenchFuel(pos: Cell, fuel: string): Patch {
  return (state) =>
    withList(state, 'workbenches', (list) =>
      mapRows(list, (w) => (same(w.pos, pos) && w.fuel !== fuel ? { ...w, fuel } : w)),
    );
}

// ------------------------------------------------------------------ beds (StoryColony.Beds.cs)

/** `beds {op:'assign', bed, pawn}` (the pawn's other bed is freed) or `{op:'unassign', bed}` (`pawn` null). */
export function patchBedOwner(pos: Cell, pawn: string | null): Patch {
  return (state) =>
    withList(state, 'beds', (list) =>
      mapRows(list, (b: StoryBed) => {
        if (same(b.pos, pos)) return b.pawn === pawn || (!b.pawn && !pawn) ? b : { ...b, pawn };
        return pawn && b.pawn === pawn ? { ...b, pawn: null } : b;
      }),
    );
}

// ------------------------------------------------------------------ why a write failed

/** What the page says for a 503 `busy` on a colony write: the colony refused it within its wait, so nothing changed. */
export const BUSY_TEXT = 'The colony is busy; the change was not saved.';
/** What the page says when a change's outcome stayed unknown for 10 s (it then shows what the colony reports). */
export const UNCONFIRMED_TEXT = 'Could not confirm the change; showing what the colony reports.';
/** What the page says when a queued change waited too long behind the one before it (it was not sent). */
export const STALE_TEXT = 'The change was not sent: the one before it took too long.';

export interface WriteFailure {
  /** refused: nothing changed and nothing will; unknown: the colony may still have applied it. */
  kind: 'refused' | 'unknown';
  text: string;
}

/**
 * Why a colony write failed, and what to say. A 4xx, a 503 `busy` (keyed on the code: Nest replaces the bot's text) and an
 * offline refusal mean the colony did not take it. A lost connection, a 502 or any other 503 say nothing about the outcome:
 * the request may have reached the colony after the bridge gave up on it.
 */
export function storyWriteError(e: unknown): WriteFailure {
  if (e instanceof ApiError) {
    if (e.status === 503 && e.code === 'busy') return { kind: 'refused', text: BUSY_TEXT };
    if (e.code === 'offline' || (e.status >= 400 && e.status < 500)) return { kind: 'refused', text: errorText(e) };
  }
  return { kind: 'unknown', text: errorText(e) };
}

// ------------------------------------------------------------------ the store

export type WriteStatus = 'sending' | 'acked' | 'unconfirmed';

export interface Entry {
  seq: number;
  key: string;
  apply: Patch;
  status: WriteStatus;
  /** The revision that carries the change (set when acked). */
  ackRev: number | null;
  ackAt: number | null;
}

/** How long an acked change is kept, to be shown again if the state steps back below its revision. */
export const ACK_KEEP_MS = 60_000;
/** How long a change of unknown outcome is shown before the page follows the colony; also the most a queued change waits. */
export const UNCONFIRMED_MS = 10_000;
/** With frames flowing, the one read made when the state has still not reached an answer's `rev`. */
export const FALLBACK_CATCH_UP_MS = 1_500;

export interface TrackSpec<K extends ColonyWrite> {
  /** One request in flight per key ("storage:<chests>", "bill:<id>", "bench:<pos>", "bed:<pos>"). */
  key: string;
  apply: Patch;
  /** Combines a queued body with a newer one for the same key (later fields win); without it the newer one replaces it. */
  merge?: (queued: RouteBody<K>, next: RouteBody<K>) => RouteBody<K>;
  /** True when a state shows the change (decides an unknown outcome). Defaults to `shows(apply)`. */
  confirmed?: (state: StoryColonyState) => boolean;
}

/** Equal values, compared by content. Rows a patch leaves alone keep their identity, so the walk stays on what it changed. */
function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((x, i) => sameValue(x, b[i]));
  }
  const x = a as Record<string, unknown>;
  const y = b as Record<string, unknown>;
  for (const k in x) if (!sameValue(x[k], y[k])) return false;
  for (const k in y) if (!(k in x) && y[k] !== undefined) return false;
  return true;
}

/**
 * True when `state` already looks as `apply` would make it: the colony has the change. This decides an unknown outcome
 * for a tracked write that brings no `confirmed` of its own, so a change that did reach the colony is not reported as lost.
 */
export const shows =
  (apply: Patch) =>
  (state: StoryColonyState): boolean =>
    sameValue(apply(state), state);

interface Waiter {
  resolve: (out: unknown) => void;
  reject: (e: unknown) => void;
}

interface Job {
  entry: Entry;
  route: ColonyWrite;
  body: unknown;
  /** When it was queued: a queued job that waited longer than `UNCONFIRMED_MS` is not sent. */
  at: number;
  merge?: (queued: never, next: never) => unknown;
  confirmed?: (state: StoryColonyState) => boolean;
  waiters: Waiter[];
}

interface Lane {
  queued: Job | null;
}

interface Unconfirmed {
  entry: Entry;
  /** The state held when the outcome went unknown: only a later one can confirm it. */
  before: StoryColonyState | undefined;
  confirmed?: (state: StoryColonyState) => boolean;
  waiters: Waiter[];
  timer: ReturnType<typeof setTimeout>;
}

const noop = () => undefined;

/** One colony's shown changes in one query client. */
export class OptimisticStore {
  private entries: Entry[] = [];
  private seq = 0;
  private version = 0;
  private readonly listeners = new Set<() => void>();
  private readonly lanes = new Map<string, Lane>();
  private readonly unconfirmed: Unconfirmed[] = [];
  private memo: { server: StoryColonyState; version: number; out: StoryColonyState } | null = null;

  constructor(
    private readonly qc: QueryClient,
    private readonly id: string,
  ) {}

  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  };

  /** Moves on with every change of what is shown (the hook's snapshot). */
  getVersion = (): number => this.version;

  list(): readonly Entry[] {
    return this.entries;
  }

  /**
   * The server state with the shown changes laid over it, in order: every entry still sending or unconfirmed, and every
   * acked one while the state's revision is below its `ackRev`. With nothing to lay over, the server state itself.
   */
  view(server: StoryColonyState | undefined): StoryColonyState | undefined {
    if (!server) return server;
    const m = this.memo;
    if (m && m.server === server && m.version === this.version) return m.out;
    let out = server;
    for (const e of this.entries) if (e.status !== 'acked' || server.rev < (e.ackRev ?? 0)) out = e.apply(out);
    this.memo = { server, version: this.version, out };
    return out;
  }

  /** Shows `apply` until the colony state carries it (an entry without a request: pending map marks settle it themselves). */
  add(key: string, apply: Patch): Entry {
    const entry: Entry = { seq: ++this.seq, key, apply, status: 'sending', ackRev: null, ackAt: null };
    this.entries = [...this.entries, entry];
    this.changed();
    return entry;
  }

  /** The colony has the change at revision `rev`: shown while the state is below it, dropped a minute later. */
  ack(entry: Entry, rev: number): void {
    if (!this.entries.includes(entry)) return;
    entry.status = 'acked';
    entry.ackRev = rev;
    entry.ackAt = Date.now();
    this.changed();
    setTimeout(() => this.remove(entry), ACK_KEEP_MS);
  }

  remove(entry: Entry): void {
    if (!this.entries.includes(entry)) return;
    this.entries = this.entries.filter((e) => e !== entry);
    this.changed();
  }

  /**
   * Sends `body` on `route` and shows `spec.apply` from now on (see the file's comment for the lifecycle). Resolves with the
   * answer (or nothing when an unknown outcome was confirmed by the state); rejects when the colony refused the change, when
   * its outcome stayed unknown for 10 s, or when it waited too long in the queue.
   */
  track<K extends ColonyWrite>(
    spec: TrackSpec<K>,
    route: K,
    body: RouteBody<K>,
  ): Promise<RouteResponse<K> | undefined> {
    return new Promise((resolve, reject) => {
      const job: Job = {
        entry: this.add(spec.key, spec.apply),
        route,
        body,
        at: Date.now(),
        merge: spec.merge,
        confirmed: spec.confirmed,
        waiters: [{ resolve: resolve as Waiter['resolve'], reject }],
      };
      const lane = this.lanes.get(spec.key);
      if (!lane) {
        this.start(spec.key, job);
        return;
      }
      const queued = lane.queued;
      if (queued) {
        // The newer change replaces the queued one: one request carries both, and both callers get its outcome.
        if (job.merge) {
          job.body = job.merge(queued.body as never, job.body as never);
          const first = queued.entry.apply;
          const then = job.entry.apply;
          job.entry.apply = (s) => then(first(s));
        }
        job.waiters.unshift(...queued.waiters);
        this.remove(queued.entry);
      }
      lane.queued = job;
    });
  }

  private start(key: string, job: Job): void {
    this.lanes.set(key, { queued: null });
    void this.send(job).finally(() => {
      const next = this.lanes.get(key)?.queued ?? null;
      this.lanes.delete(key);
      if (!next) return;
      if (Date.now() - next.at > UNCONFIRMED_MS) {
        // A command never runs long after the click (lib/api/query.ts).
        this.remove(next.entry);
        for (const w of next.waiters) w.reject(new Error(STALE_TEXT));
        return;
      }
      this.start(key, next);
    });
  }

  private async send(job: Job): Promise<void> {
    let out: unknown;
    try {
      out = await sendColonyWrite(this.qc, this.id, job.route, job.body as never);
    } catch (e) {
      if (storyWriteError(e).kind === 'refused') {
        this.remove(job.entry);
        for (const w of job.waiters) w.reject(e);
        // The write queued behind it was built on the refused value (a filter tree sends the whole shown filter): sending it
        // would save the refused change after all. It goes with the same answer; nothing is retried.
        const lane = this.lanes.get(job.entry.key);
        const next = lane?.queued;
        if (lane && next) {
          lane.queued = null;
          this.remove(next.entry);
          for (const w of next.waiters) w.reject(e);
        }
      } else this.unknown(job);
      return;
    }
    this.answered(job.entry, out);
    for (const w of job.waiters) w.resolve(out);
  }

  /** A 2xx: acked by the answer's `rev`, else by the revision of a read that started after the answer. */
  private answered(entry: Entry, out: unknown): void {
    const rev = answerRev(out);
    const read = catchUpAfterWrite(this.qc, this.id, out);
    if (rev !== null) {
      this.ack(entry, rev);
      return;
    }
    void read?.then(
      () => this.ack(entry, this.heldRev() ?? 0),
      // The read failed: whatever the state brings next was read after the answer.
      () => this.ack(entry, (this.heldRev() ?? 0) + 1),
    );
  }

  /** The outcome is unknown: keep showing it while the state is checked (never resend), for at most 10 s. */
  private unknown(job: Job): void {
    const entry = job.entry;
    entry.status = 'unconfirmed';
    this.changed();
    const u: Unconfirmed = {
      entry,
      before: this.held(),
      // Read here, after any merge: a queued change folded into this one is part of what the state must show.
      confirmed: job.confirmed ?? shows(entry.apply),
      waiters: job.waiters,
      timer: setTimeout(() => {
        this.settleUnconfirmed(u);
        this.remove(entry);
        for (const w of u.waiters) w.reject(new Error(UNCONFIRMED_TEXT));
      }, UNCONFIRMED_MS),
    };
    this.unconfirmed.push(u);
    void catchUp(this.qc, this.id).catch(noop);
  }

  private settleUnconfirmed(u: Unconfirmed): void {
    clearTimeout(u.timer);
    const i = this.unconfirmed.indexOf(u);
    if (i >= 0) this.unconfirmed.splice(i, 1);
  }

  /** A new colony state arrived: it decides every unknown outcome it shows applied. */
  serverChanged(state: StoryColonyState | undefined): void {
    if (!state) return;
    for (const u of [...this.unconfirmed]) {
      if (state === u.before || !u.confirmed?.(state)) continue;
      this.settleUnconfirmed(u);
      this.remove(u.entry);
      for (const w of u.waiters) w.resolve(undefined);
    }
  }

  private held(): StoryColonyState | undefined {
    return this.qc.getQueryData<StoryColonyState>(colonyKey(this.id));
  }

  private heldRev(): number | undefined {
    return this.held()?.rev;
  }

  private changed(): void {
    this.version++;
    for (const fn of this.listeners) fn();
  }
}

/** An answer's `rev`, when it may acknowledge the change: never from a `pending` answer, whose revision predates it. */
export function answerRev(out: unknown): number | null {
  if (!out || typeof out !== 'object') return null;
  const o = out as { rev?: unknown; pending?: unknown };
  return o.pending !== true && typeof o.rev === 'number' ? o.rev : null;
}

/** The read each answer started (pending map marks settle by it instead of starting a second one). */
const writeReads = new WeakMap<object, Promise<void>>();

/**
 * The catch-up after a colony write's 2xx. An answer with `rev` while the stream's frames flow needs none: a frame brings the
 * change, and one read follows only if the state has not reached `rev` after `FALLBACK_CATCH_UP_MS`. Otherwise one read at
 * once, returned and kept for `writeCatchUp(out)`; null when there is none.
 */
export function catchUpAfterWrite(qc: QueryClient, id: string, out: unknown): Promise<void> | null {
  const rev = answerRev(out);
  if (rev !== null && storyFramesFlowing(id)) {
    setTimeout(() => {
      if ((qc.getQueryData<StoryColonyState>(colonyKey(id))?.rev ?? -1) < rev) void catchUp(qc, id).catch(noop);
    }, FALLBACK_CATCH_UP_MS);
    return null;
  }
  const read = catchUp(qc, id);
  read.catch(noop);
  if (out && typeof out === 'object') writeReads.set(out, read);
  return read;
}

/** The read `catchUpAfterWrite` started for this answer, if it started one. */
export function writeCatchUp(out: unknown): Promise<void> | undefined {
  return out && typeof out === 'object' ? writeReads.get(out) : undefined;
}

interface Client {
  /** Stores by the colony query's hash. */
  stores: Map<string, OptimisticStore>;
}

const clients = new WeakMap<QueryClient, Client>();

/** Watches the client's colony states: they settle unknown outcomes and confirm the ring's commands. */
function clientOf(qc: QueryClient): Client {
  const c = clients.get(qc);
  if (c) return c;
  const client: Client = { stores: new Map() };
  clients.set(qc, client);
  qc.getQueryCache().subscribe((ev: QueryCacheNotifyEvent) => {
    if (ev.type !== 'updated' || ev.action.type !== 'success') return;
    const data = ev.query.state.data as StoryColonyState | undefined;
    client.stores.get(ev.query.queryHash)?.serverChanged(data);
    const id = colonyIds.get(ev.query.queryHash);
    if (id !== undefined && data) noteColonyState(id, data);
  });
  return client;
}

/** The colony id of each colony query hash a write was sent for. */
const colonyIds = new Map<string, string>();

function watchColony(qc: QueryClient, id: string): string {
  const hash = hashKey(colonyKey(id));
  colonyIds.set(hash, id);
  clientOf(qc);
  return hash;
}

/** The colony's store in this query client. */
export function storeOf(qc: QueryClient, id: string): OptimisticStore {
  const hash = watchColony(qc, id);
  const { stores } = clientOf(qc);
  let s = stores.get(hash);
  if (!s) stores.set(hash, (s = new OptimisticStore(qc, id)));
  return s;
}

const NO_SUBSCRIBE = () => noop;
const NO_VERSION = () => 0;

/**
 * The colony state as the page shows it (the server's with the shown changes over it) and `track`, the tracked write. With
 * no change pending the state is the server's own object.
 */
export function useOptimisticColony(id: string | null, server: StoryColonyState | undefined) {
  const qc = useQueryClient();
  const store = useMemo(() => (id ? storeOf(qc, id) : null), [qc, id]);
  // Renders again whenever the shown changes move; `view` keeps its answer while neither they nor the server state do.
  useSyncExternalStore(
    store?.subscribe ?? NO_SUBSCRIBE,
    store?.getVersion ?? NO_VERSION,
    store?.getVersion ?? NO_VERSION,
  );
  const state = store ? store.view(server) : server;
  const track = useCallback(
    <K extends ColonyWrite>(spec: TrackSpec<K>, route: K, body: RouteBody<K>) =>
      store ? store.track(spec, route, body) : Promise.reject(new Error('no colony')),
    [store],
  );
  return { state, track };
}

export type Track = ReturnType<typeof useOptimisticColony>['track'];

// ------------------------------------------------------------------ the inspector's writes

/** What the page gives an inspector: `track` shows its changes at once, `onError` says why one failed. Both optional. */
export interface TrackedWrites {
  track?: Track;
  onError?: (e: unknown) => void;
}

/** For a write whose failure has nowhere to go (an inspector rendered without `onError`). */
export const ignoreError = (): void => undefined;

/** Through `track` when the page gives one (the change is shown at once), else a plain write (the inspector on its own). */
export function trackOrWrite<K extends ColonyWrite>(
  track: Track | undefined,
  write: StoryWrite,
  spec: TrackSpec<K>,
  route: K,
  body: RouteBody<K>,
): Promise<unknown> {
  return track ? track(spec, route, body) : write(route, body);
}

/** The track keys: one request in flight per chest set, bill, bench or bed. */
export const storageKey = (chests: readonly Cell[]) => `storage:${chests.map((c) => c.join(',')).join(';')}`;
export const billKey = (id: string | number) => `bill:${String(id)}`;
export const benchKey = (pos: Cell) => `bench:${pos.join(',')}`;
export const bedKey = (pos: Cell) => `bed:${pos.join(',')}`;

/** A queued storage `set` and a newer one: one request with both, later fields winning. */
export const mergeFields = <T extends object>(queued: T, next: T): T => ({ ...queued, ...next });

/** Two bill `update`s combine; any other op (a delete) replaces what was queued. */
export const mergeBillBodies = <T extends { op: string }>(queued: T, next: T): T =>
  queued.op === 'update' && next.op === 'update' ? { ...queued, ...next } : next;

// ------------------------------------------------------------------ sending, and the command ring

/**
 * Sends one colony write: the request, its line in the command ring, and the Architect palette when the write changes what
 * the colony can make and the state has no `architectKey` to follow. The caller decides how the state catches up.
 */
export async function sendColonyWrite<K extends ColonyWrite>(
  qc: QueryClient,
  id: string,
  route: K,
  body: RouteBody<K>,
): Promise<RouteResponse<K>> {
  watchColony(qc, id);
  const rec = recordCommand(id, route);
  try {
    const out = await api(route, { params: { id }, body });
    commandAnswered(rec, out);
    // What the colony can make changes with its benches, its storage and its queue: the Architect's tiers follow.
    // A state with `architectKey` fetches the palette again when that moves, so only an older bot needs it done here.
    if (writeRefetchesArchitect(route, qc.getQueryData<StoryColonyState>(colonyKey(id))))
      void qc.invalidateQueries({ queryKey: qk('story.architect', { id }) });
    return out;
  } catch (e) {
    commandFailed(rec, e);
    throw e;
  }
}

/** One colony write in `window.__storyCommands` (times are `Date.now()` milliseconds). */
export interface StoryCommandRecord {
  colony: string;
  route: RouteId;
  sentAt: number;
  repliedAt: number | null;
  /** When the page first held a state with the change: `rev` reached the answer's, or (without one) the next state. */
  confirmedAt: number | null;
  status: 'sending' | 'ok' | 'refused' | 'unknown';
  /** The HTTP status of a failure (0: no answer). */
  http: number | null;
  rev: number | null;
}

/** How many commands the ring keeps. */
export const COMMAND_RING = 50;
/** A command slower than this, from click to confirmation (or to a failure), is logged with `console.debug`. */
export const SLOW_COMMAND_MS = 500;

/** The last `COMMAND_RING` colony writes, oldest first (also `window.__storyCommands`, for measuring in the browser). */
export const storyCommands: StoryCommandRecord[] = [];
if (typeof window !== 'undefined')
  (window as unknown as { __storyCommands?: StoryCommandRecord[] }).__storyCommands = storyCommands;

function recordCommand(colony: string, route: RouteId): StoryCommandRecord {
  const rec: StoryCommandRecord = {
    colony,
    route,
    sentAt: Date.now(),
    repliedAt: null,
    confirmedAt: null,
    status: 'sending',
    http: null,
    rev: null,
  };
  storyCommands.push(rec);
  if (storyCommands.length > COMMAND_RING) storyCommands.splice(0, storyCommands.length - COMMAND_RING);
  return rec;
}

function commandAnswered(rec: StoryCommandRecord, out: unknown): void {
  rec.repliedAt = Date.now();
  rec.status = 'ok';
  rec.rev = answerRev(out);
}

function commandFailed(rec: StoryCommandRecord, e: unknown): void {
  rec.repliedAt = Date.now();
  rec.status = storyWriteError(e).kind;
  rec.http = e instanceof ApiError ? e.status : null;
  if (rec.repliedAt - rec.sentAt > SLOW_COMMAND_MS) logSlow(rec);
}

function noteColonyState(colony: string, state: StoryColonyState): void {
  const now = Date.now();
  for (const rec of storyCommands) {
    if (rec.colony !== colony || rec.status !== 'ok' || rec.confirmedAt !== null || rec.repliedAt === null) continue;
    if (rec.rev !== null && state.rev < rec.rev) continue;
    rec.confirmedAt = now;
    if (now - rec.sentAt > SLOW_COMMAND_MS) logSlow(rec);
  }
}

function logSlow(rec: StoryCommandRecord): void {
  // eslint-disable-next-line no-console -- a measurement the owner reads in the browser's console (command-lag WP0)
  console.debug('[story] slow command', rec);
}
