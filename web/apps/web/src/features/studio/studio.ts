/**
 * The Design Studio's state outside React: the canvas, one undo/redo history for AI and user ops, the user's edits waiting
 * to reach the bot (`POST …/canvas`, debounced), the selection, and the calls to the designer routes. The page renders from
 * `snapshot()` and calls the methods; the world and the 3D overlay listen to `onCells`.
 *
 * Unsaved work (`dirty`): every cell whose state differs from what it was at the last successful save (or when the session
 * opened: a new design's empty plot, a loaded revision). Tracked per cell, so an undo back to the saved state is clean again.
 * A session this browser saved at an older canvas rev than it reopens with starts dirty, until the next save.
 *
 * Sync: the bot's canvas has to equal this one whenever it is saved, talked about or converted, so every request for the
 * session goes out one at a time and in order (`onWire`): the edits in batches sized to the bridge's body cap, then the
 * anchor, a message or a save. A failed batch waits and is tried again (`snapshot().sync`); a save or a message refuses to
 * go ahead of edits that could not be sent (`settle`).
 */
import type {
  DesignerEdit,
  DesignerMessage,
  DesignerMessageRequest,
  DesignerMessageResponse,
  DesignerSaveRequest,
  DesignerSaveResponse,
  DesignerSession,
  DesignerCanvasRequest,
  DesignerCanvasResponse,
} from '@mineclaw/contracts';
import { DESIGNER_CANVAS_BATCH_BYTES } from '@mineclaw/contracts';
import { errorText } from '@/lib/api/client';
import {
  Canvas,
  type Cell,
  type Change,
  EARTH,
  History,
  keyOf,
  type OpGroup,
  posOf,
  type Selection,
  setPlotMode,
  type V3,
} from './canvas';
import { MineTerrain } from './mine/mineTerrain';

/** The routes the studio calls (the app passes the typed client; tests pass fakes and read the bodies). */
export interface StudioApi {
  message(id: number, body: DesignerMessageRequest): Promise<DesignerMessageResponse>;
  canvas(id: number, body: DesignerCanvasRequest): Promise<DesignerCanvasResponse>;
  save(id: number, body: DesignerSaveRequest): Promise<DesignerSaveResponse>;
}

export interface StudioSnapshot {
  rev: number;
  blocks: number;
  /** Dug cells (a mine is mostly these). */
  dug: number;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string | null;
  redoLabel: string | null;
  /** Cells of the user's edits the bot has not confirmed yet: waiting, or in the request on its way. */
  pending: number;
  /**
   * Where those edits stand: nothing waits, they are on their way, the last request failed and is tried again shortly, or
   * the bot refused them and nothing more is sent until `retrySync()`.
   */
  sync: 'idle' | 'sending' | 'retrying' | 'blocked';
  /** Why the last canvas request failed (null once one succeeds). The chat's own failure is `error`. */
  syncError: string | null;
  busy: boolean;
  messages: DesignerMessage[];
  costUsd: number;
  selection: Selection | null;
  anchor: V3 | null;
  designId: number | null;
  name: string;
  error: string | null;
  /** Cells changed since the last save (or since the session opened): leaving now would lose them. */
  dirty: boolean;
}

/** A cell's state as one number (id * 16 + meta; air 0, earth -1), to compare with the saved one. */
const stateOf = (c: Cell): number => (c[3] === EARTH ? -1 : c[3] * 16 + (c[4] & 15));

const MAX_CELLS_PER_EDIT = 20_000;
/** More than any cell takes in a request body (the longest, "[255,127,255,252,15],", is 21 bytes). */
const CELL_BYTES = 22;
/** As small as a 413 makes the requests (about 700 cells); a request refused at this size is not refused for its size. */
const MIN_BATCH_BYTES = 16_000;
/** The waits after failed canvas requests: 2, 4, 8, 16 and then 30 s. */
const RETRY_MS = 2000;
const RETRY_MAX_MS = 30_000;
/**
 * Refusals that pass, tried again like a 5xx: 409 is the bot's "the session is busy" (another request held its session
 * lock past a second), then a timeout, a lock and "too early"; 429 is too many requests.
 */
const PASSING_4XX = new Set([408, 409, 423, 425, 429]);

/**
 * An upper bound on an edit's bytes in the body the bot reads. The bridge writes the body again on its way there, with
 * quotes, plus signs and non-ASCII letters as 6-byte escapes, so every character of the kind and the label counts as 6.
 */
const editBytes = (kind: string, label: string, cells: number): number =>
  CELL_BYTES * cells + 6 * (kind.length + label.length) + 64;

/** A failed request's HTTP status (0 = the server was not reached), when what was thrown has one. */
function statusOf(e: unknown): number | undefined {
  const status = (e as { status?: unknown } | null | undefined)?.status;
  return typeof status === 'number' ? status : undefined;
}

/** How long the server asked to be left alone (0 = it did not say; a minute at most). */
function retryAfterOf(e: unknown): number {
  const ms = (e as { retryAfterMs?: unknown } | null | undefined)?.retryAfterMs;
  return typeof ms === 'number' && ms > 0 ? Math.min(ms, 60_000) : 0;
}

/** What a failed request says to the reader: the client's wording for its own errors, else the message it came with. */
function reasonOf(e: unknown): string {
  if (e instanceof Error) return errorText(e);
  const said = (e as { message?: unknown } | null | undefined)?.message;
  if (typeof said === 'string' && said) return said;
  const status = statusOf(e);
  return status === undefined ? 'the request failed' : `the request failed with status ${status}`;
}

const noop = (): void => undefined;

/** What the bot accepts as a mine's component layout (the save request's `mineAuthoring`). */
export const MINE_AUTHORING_MAX_CHARS = 2_000_000;

/** Browser storage as the studio uses it: reads and writes always, listing and removing where the storage allows. */
export type StorageLike = Pick<Storage, 'getItem' | 'setItem'> &
  Partial<Pick<Storage, 'removeItem' | 'key' | 'length'>>;

export function browserStorage(): StorageLike | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * Removes all but the newest `keep` keys of each group: `rank` says which keys take part (null = leave it alone), their group
 * and how new each is (higher = newer). `except` always stays and is not counted. Does nothing where the storage cannot list
 * or remove keys.
 */
export function keepNewest(
  storage: StorageLike | null,
  keep: number,
  rank: (key: string) => { group: string; order: number } | null,
  except?: string,
): void {
  if (!storage?.removeItem || !storage.key || typeof storage.length !== 'number') return;
  try {
    const groups = new Map<string, Array<{ key: string; order: number }>>();
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      const r = key === null || key === except ? null : rank(key);
      if (key === null || !r) continue;
      const list = groups.get(r.group) ?? [];
      list.push({ key, order: r.order });
      groups.set(r.group, list);
    }
    for (const list of groups.values())
      for (const old of list.sort((a, b) => b.order - a.order).slice(keep)) storage.removeItem(old.key);
  } catch {
    /* Storage that refuses is only left fuller. */
  }
}

/** The key holding the canvas rev a session was last saved at, in this browser. */
const savedRevKey = (owner: string, sessionId: number) => `mineclaw.studio.saved:${owner}:${sessionId}`;
const SAVED_REV_KEY = /^mineclaw\.studio\.saved:(.*):(\d+)$/;

/** A chat message's request: the text, the canvas rev, and the selection and model when given. */
function messageRequest(
  text: string,
  rev: number,
  sel: DesignerMessageRequest['selection'] | null,
  model: string | undefined,
): DesignerMessageRequest {
  return {
    text,
    canvas: { rev },
    ...(sel ? { selection: sel } : {}),
    ...(model ? { model } : {}),
  };
}

export class Studio {
  readonly canvas = new Canvas();
  readonly history = new History();
  private pending: DesignerEdit[] = [];
  /** The debounce after an edit, or the wait before a failed request is tried again. */
  private flushTimer: ReturnType<typeof setTimeout> | null = null;
  /** The tail of the session's requests: the next one starts when this settles (`onWire`). */
  private wire: Promise<unknown> = Promise.resolve();
  /** The drain `flush` started, until it ends. */
  private draining: Promise<void> | null = null;
  /** Cells of the canvas request on its way. */
  private inFlight = 0;
  /** The edits of the canvas request on its way. */
  private sending: DesignerEdit[] = [];
  /** What one canvas request may hold, in bytes of the body the bot reads (a 413 halves it). */
  private budget = DESIGNER_CANVAS_BATCH_BYTES;
  /** Canvas requests that failed in a row. */
  private failures = 0;
  /** The bot refused a canvas request: nothing more is sent until `retrySync()`. */
  private blocked = false;
  /** The page let go of this studio (`dispose`): a failed request is not tried again. A new subscriber takes it back. */
  private disposed = false;
  private syncError: string | null = null;
  /** The anchor changed here and the bot does not have it yet. */
  private anchorUnsent = false;
  private listeners = new Set<() => void>();
  private cellListeners = new Set<(cells: Cell[], by: 'ai' | 'user') => void>();
  private snap: StudioSnapshot;
  rev = 0;
  busy = false;
  messages: DesignerMessage[] = [];
  costUsd = 0;
  selection: Selection | null = null;
  anchor: V3 | null = null;
  designId: number | null = null;
  name = 'new design';
  error: string | null = null;
  private lastGroup: string | null = null;
  /** Cell key → its state at the last save, for every cell touched since. */
  private saved = new Map<number, number>();
  /** Keys of the touched cells whose state differs from the saved one. */
  private diverged = new Set<number>();
  private mineAuthoring: string | undefined;
  private savedMineAuthoring: string | undefined;
  /**
   * The session was saved from this browser at an older canvas rev than it opened with: what changed since is unsaved,
   * though no cell here says which. Cleared by the next save.
   */
  private changedSinceSave = false;
  private redstoneAuthoring: string | undefined;
  private savedRedstoneAuthoring: string | undefined;
  /** The kind of the design revision this session opened or last saved (null: none, or a bot that does not say). */
  private savedKind: string | null = null;
  /** A mine session's generated terrain (null for a build). */
  readonly terrain: MineTerrain | null;

  constructor(
    readonly session: DesignerSession,
    private readonly api: StudioApi,
    readonly flushMs = 400,
  ) {
    // The plot's mode first: a mine's ground is its terrain (the canvas's rules follow it).
    this.terrain = session.kind === 'mine' ? MineTerrain.for(session.seed, session.version) : null;
    setPlotMode(this.terrain ? { kind: 'mine', terrain: this.terrain } : { kind: 'build' });
    this.canvas.load(session.blocks, session.dug ?? []);
    this.rev = session.rev;
    this.messages = [...session.messages];
    this.costUsd = session.costUsd;
    this.anchor = session.anchor ?? null;
    this.designId = session.designId ?? null;
    this.name = session.name;
    this.changedSinceSave = this.savedRevDiffers();
    // A build <-> redstone switch keeps every cell but is still a change of the design until it is saved.
    if (session.designId)
      this.savedKind = session.revisions.find((r) => r.rev === session.designRevision)?.kind ?? null;
    this.snap = this.build();
  }

  /** Whether this browser saved the session's design at another canvas rev than the session has now (unknown = no). */
  private savedRevDiffers(): boolean {
    if (this.session.designId === null || this.session.designId === undefined) return false;
    try {
      const at = browserStorage()?.getItem(savedRevKey(this.session.owner, this.id));
      return at !== null && at !== undefined && Number(at) !== this.session.rev;
    } catch {
      return false;
    }
  }

  /** Remembers the canvas rev a save holds, so a reload of this session knows whether anything changed after it. */
  private rememberSavedRev(): void {
    const storage = browserStorage();
    const key = savedRevKey(this.session.owner, this.id);
    try {
      storage?.setItem(key, String(this.rev));
    } catch {
      return;
    }
    keepNewest(
      storage,
      50,
      (k) => {
        const m = SAVED_REV_KEY.exec(k);
        return m ? { group: m[1]!, order: Number(m[2]) } : null;
      },
      key,
    );
  }

  /**
   * Takes over the unsaved work of the studio this one replaces on the same plot (a build <-> redstone switch remounts the
   * page on the bot's copy of the same cells): what each touched cell held at the last save, so those edits stay unsaved.
   * The undo steps stay behind with the page they belonged to.
   */
  adoptUnsaved(from: Studio): void {
    for (const [k, v] of from.saved) if (!this.saved.has(k)) this.saved.set(k, v);
    if (from.changedSinceSave) this.changedSinceSave = true;
    for (const [k, v] of this.saved) {
      if (this.stateAt(k) === v) this.diverged.delete(k);
      else this.diverged.add(k);
    }
    this.changed();
  }

  get id(): number {
    return this.session.id;
  }

  /** A mine session (dug into generated terrain at absolute y). */
  get mine(): boolean {
    return this.terrain !== null;
  }

  // ------------------------------------------------------------------ subscriptions (useSyncExternalStore)

  subscribe = (fn: () => void): (() => void) => {
    // React's development build disposes a live studio once and subscribes again: it is still in use then.
    this.disposed = false;
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  };

  snapshot = (): StudioSnapshot => this.snap;

  /** Cells as they changed on the canvas (new states), with who did it: the world and the overlay follow these. */
  onCells(fn: (cells: Cell[], by: 'ai' | 'user') => void): () => void {
    this.cellListeners.add(fn);
    return () => this.cellListeners.delete(fn);
  }

  private build(): StudioSnapshot {
    return {
      rev: this.rev,
      blocks: this.canvas.size,
      dug: this.canvas.dug.size,
      canUndo: this.history.canUndo,
      canRedo: this.history.canRedo,
      undoLabel: this.history.peekUndo()?.label ?? null,
      redoLabel: this.history.peekRedo()?.label ?? null,
      pending: this.pending.reduce((n, e) => n + e.cells.length, this.inFlight),
      sync: this.sync,
      syncError: this.syncError,
      busy: this.busy,
      messages: this.messages,
      costUsd: this.costUsd,
      selection: this.selection,
      anchor: this.anchor,
      designId: this.designId,
      name: this.name,
      error: this.error,
      dirty: this.dirty,
    };
  }

  /** Whether leaving now would lose canvas changes. */
  get dirty(): boolean {
    if (this.redstoneAuthoring !== this.savedRedstoneAuthoring) return true;
    if (this.savedKind !== null && this.savedKind !== (this.session.kind ?? 'build')) return true;
    return this.changedSinceSave || this.diverged.size > 0 || this.mineAuthoring !== this.savedMineAuthoring;
  }

  /** The mine layout the bot holds: what the last save sent, or before one, what the session opened with. */
  get savedAuthoring(): string | undefined {
    return this.savedMineAuthoring;
  }

  /** Includes metadata-only edits (grouping, settings and visibility) in Save and the navigation guard. */
  setMineAuthoring(data: string, initialize = false): void {
    if (initialize) this.savedMineAuthoring = data;
    if (this.mineAuthoring === data) return;
    this.mineAuthoring = data;
    this.changed();
  }

  /**
   * A redstone design's own metadata (placed components, probes, breakpoints, simulator settings): an edit of it alone
   * counts as unsaved work and goes out with the next save. `initialize` marks `data` as what the opened revision holds.
   */
  setRedstoneAuthoring(data: string, initialize = false): void {
    if (initialize) this.savedRedstoneAuthoring = data;
    if (this.redstoneAuthoring === data) return;
    this.redstoneAuthoring = data;
    this.changed();
  }

  /** Records an applied change against the saved state (forward[i] and backward[i] are the same cell). */
  private track(ch: Change): void {
    ch.forward.forEach((f, i) => {
      const k = keyOf(f[0], f[1], f[2]);
      if (!this.saved.has(k)) this.saved.set(k, stateOf(ch.backward[i]!));
      if (this.saved.get(k) === stateOf(f)) this.diverged.delete(k);
      else this.diverged.add(k);
    });
  }

  private stateAt(k: number): number {
    return this.canvas.authoredState(k);
  }

  /** The canvas as it is now counts as saved. */
  markSaved(): void {
    this.saved.clear();
    this.diverged.clear();
    this.changedSinceSave = false;
    this.changed();
  }

  private changed(): void {
    this.snap = this.build();
    for (const l of this.listeners) l();
  }

  setSelection(s: Selection | null): void {
    this.selection = s;
    this.changed();
  }

  // ------------------------------------------------------------------ edits

  /** A user edit: applied, recorded for undo, sent to the bot. Returns the cells that changed. */
  edit(cells: readonly Cell[], kind: string, label: string, group?: string): Cell[] {
    return this.recordEdit(cells, kind, label, group);
  }

  /** Layout metadata travels with its canvas operation, including a fully overlapping layout with no changed cells. */
  editWithHistory(cells: readonly Cell[], kind: string, label: string, effect: NonNullable<OpGroup['effect']>, group?: string): Cell[] {
    return this.recordEdit(cells, kind, label, group, effect);
  }

  private recordEdit(cells: readonly Cell[], kind: string, label: string, group?: string, effect?: OpGroup['effect']): Cell[] {
    const ch = this.canvas.apply(cells);
    if (ch.forward.length === 0 && !effect) return [];
    this.track(ch);
    const g: OpGroup = { label, by: 'user', forward: ch.forward, backward: ch.backward, ...(effect ? { effect } : {}) };
    if (group && group === this.lastGroup) {
      const oldEffect = this.history.peekUndo()?.effect;
      this.history.merge(g);
      const merged = this.history.peekUndo();
      if (merged && effect) merged.effect = { undo: () => oldEffect ? oldEffect.undo() : effect.undo(), redo: () => effect.redo() };
    }
    else this.history.push(g);
    this.lastGroup = group ?? null;
    this.emit(ch.forward, 'user');
    this.queue(kind, label, ch.forward);
    this.changed();
    return ch.forward;
  }

  /**
   * The bot's corrections of the user's own cells (a wall torch turned to its solid side): applied without an undo step of
   * their own, counted as changes (dirty) like the edit they belong to.
   */
  correct(cells: readonly Cell[]): void {
    const ch = this.canvas.apply(cells, false);
    if (ch.forward.length === 0) return;
    this.track(ch);
    this.emit(ch.forward, 'ai');
    this.changed();
  }

  /**
   * The bot's corrections that still stand: one for a cell that an edit still waiting to be sent changes again is dropped.
   * The bot decided it from an older canvas than this one, and looks at that cell again when the waiting edit arrives.
   */
  private stillCurrent(fixed: readonly Cell[]): Cell[] {
    const kept = new Map(fixed.map((c) => [keyOf(c[0], c[1], c[2]), c] as const));
    for (const e of this.pending) {
      if (kept.size === 0) break;
      for (const c of e.cells) kept.delete(keyOf(c[0], c[1], c[2]));
    }
    return [...kept.values()];
  }

  /** The AI's ops from a reply (the bot applied them already): applied here, recorded for undo, never sent back. */
  applyAi(ops: DesignerMessageResponse['ops']): Cell[] {
    const all: Cell[] = [];
    this.lastGroup = null;
    for (const op of ops) {
      // A "fix" op holds the bot's corrections of the user's own cells: the same rule as for a canvas reply's.
      const ch = this.canvas.apply(op.kind === 'fix' ? this.stillCurrent(op.cells) : op.cells, false);
      if (ch.forward.length === 0) continue;
      this.track(ch);
      this.history.push({ label: op.label, by: 'ai', forward: ch.forward, backward: ch.backward });
      all.push(...ch.forward);
    }
    if (all.length) this.emit(all, 'ai');
    this.changed();
    return all;
  }

  undo(): OpGroup | null {
    return this.step(this.history.undo(), 'backward', 'undo');
  }

  redo(): OpGroup | null {
    return this.step(this.history.redo(), 'forward', 'redo');
  }

  private step(g: OpGroup | null, dir: 'forward' | 'backward', word: string): OpGroup | null {
    if (!g) return null;
    this.lastGroup = null;
    const ch = this.canvas.apply(g[dir], false);
    this.track(ch);
    if (dir === 'backward') g.effect?.undo();
    else g.effect?.redo();
    if (ch.forward.length) {
      this.emit(ch.forward, 'user');
      this.queue(word, `${word} ${g.label}`, ch.forward);
    }
    this.changed();
    return g;
  }

  private emit(cells: Cell[], by: 'ai' | 'user'): void {
    for (const l of this.cellListeners) l(cells, by);
  }

  private queue(kind: string, label: string, cells: Cell[]): void {
    if (cells.length === 0) return;
    this.pending.push(...this.pieces({ kind: kind.slice(0, 32), label: label.slice(0, 200), cells }));
    this.schedule();
  }

  /** An edit cut into pieces that each fit one request of the current size (and the contract's cells per edit). */
  private pieces(e: DesignerEdit): DesignerEdit[] {
    const room = Math.floor((this.budget - editBytes(e.kind, e.label ?? '', 0)) / CELL_BYTES);
    const size = Math.max(1, Math.min(MAX_CELLS_PER_EDIT, room));
    const out: DesignerEdit[] = [];
    for (let i = 0; i < e.cells.length; i += size) out.push({ ...e, cells: e.cells.slice(i, i + size) });
    return out;
  }

  private schedule(): void {
    if (this.flushTimer || this.busy || this.blocked) return;
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null;
      void this.flush();
    }, this.flushMs);
  }

  /** The next request's edits: the waiting ones in order, as many as fit its size (at most 64, always the first). */
  private take(): DesignerEdit[] {
    const out: DesignerEdit[] = [];
    let cells = 0;
    let bytes = 0;
    while (this.pending.length && out.length < 64) {
      const e = this.pending[0]!;
      const size = editBytes(e.kind, e.label ?? '', e.cells.length);
      if (out.length && (cells + e.cells.length > MAX_CELLS_PER_EDIT * 2 || bytes + size > this.budget)) break;
      this.pending.shift();
      cells += e.cells.length;
      bytes += size;
      out.push(e);
    }
    return out;
  }

  /** Something the bot does not have yet: edits waiting or on their way, or the anchor. */
  private get unsent(): boolean {
    return this.pending.length > 0 || this.inFlight > 0 || this.anchorUnsent;
  }

  private get sync(): StudioSnapshot['sync'] {
    if (this.blocked) return 'blocked';
    if (!this.unsent) return 'idle';
    return this.syncError === null ? 'sending' : 'retrying';
  }

  /**
   * One request at a time for this session, in the order asked: a batch of edits, the anchor, a message, a save. The bot
   * applies cell states as they arrive, so two requests in flight could land out of order and leave older cells on top.
   * A task holds the wire until its promise settles; whatever has to happen before the next request belongs inside it.
   */
  private onWire<T>(task: () => Promise<T>): Promise<T> {
    const run = this.wire.then(task);
    this.wire = run.then(noop, noop);
    return run;
  }

  /**
   * Sends the waiting edits now. Held while a message runs (the bot's session is busy then) and after the bot refused a
   * request (`retrySync`). A second call while one drain is under way gets that one: it never starts another.
   */
  flush(): Promise<void> {
    if (this.busy || this.blocked) return Promise.resolve();
    this.draining ??= this.onWire(() => this.drain());
    return this.draining;
  }

  /**
   * Sends what waits, one request after another, until nothing waits or a request fails (`failed`): the edits in order,
   * then the anchor in a request of its own. Runs with the wire held; a failed request never makes it reject. It does not
   * look at `busy` (`flush` does, for everyone else): a message's own task runs it before the message goes out.
   */
  private async drain(): Promise<void> {
    try {
      if (this.blocked) return;
      this.stopFlushTimer();
      while (this.pending.length || this.anchorUnsent) {
        const ops = this.take();
        const anchor = this.anchor;
        this.inFlight = ops.reduce((n, e) => n + e.cells.length, 0);
        this.sending = ops;
        let r: DesignerCanvasResponse;
        try {
          r = await this.api.canvas(this.id, {
            rev: this.rev,
            ops,
            ...(ops.length ? {} : anchor ? { anchor } : { clearAnchor: true }),
          });
        } catch (e) {
          this.inFlight = 0;
          this.sending = [];
          this.pending.unshift(...ops);
          if (this.failed(e)) continue;
          return;
        }
        this.inFlight = 0;
        this.sending = [];
        this.rev = r.rev;
        this.failures = 0;
        this.syncError = null;
        // An anchor moved again while this one was on its way is still to send.
        if (!ops.length && this.anchor === anchor) this.anchorUnsent = false;
        if (r.fixed?.length) this.correct(this.stillCurrent(r.fixed));
        this.changed();
      }
    } finally {
      // What is queued from here on has not been looked at: `flush` starts another drain for it.
      this.draining = null;
    }
  }

  /**
   * A canvas request failed and its edits wait again. True = go on at once: a 413 halves the request size (the waiting
   * edits are cut to it), down to `MIN_BATCH_BYTES`. Otherwise the drain ends here. A refusal (a 4xx but `PASSING_4XX`)
   * stops the sync until `retrySync()`, because asking again cannot change the answer. Everything else (the bot away or
   * busy, the network) is tried again after 2, 4, 8 … 30 s, or as much later as the server asked; a disposed studio does
   * not try again.
   */
  private failed(e: unknown): boolean {
    const status = statusOf(e);
    if (status === 413 && this.budget > MIN_BATCH_BYTES) {
      this.budget = Math.max(MIN_BATCH_BYTES, Math.floor(this.budget / 2));
      this.pending = this.pending.flatMap((waiting) => this.pieces(waiting));
      return true;
    }
    this.stopFlushTimer();
    this.syncError = reasonOf(e);
    if (status !== undefined && status >= 400 && status < 500 && !PASSING_4XX.has(status)) this.blocked = true;
    else if (!this.disposed) {
      const wait = Math.min(RETRY_MAX_MS, RETRY_MS * 2 ** Math.min(this.failures, 4));
      this.failures++;
      this.flushTimer = setTimeout(
        () => {
          this.flushTimer = null;
          void this.flush();
        },
        Math.max(wait, retryAfterOf(e)),
      );
    }
    this.changed();
    return false;
  }

  /** Tries again now: after the bot refused a request, or instead of waiting for the next retry. */
  retrySync(): Promise<void> {
    this.blocked = false;
    this.failures = 0;
    this.changed();
    return this.flush();
  }

  /**
   * Waits until the bot holds every edit, and throws when it does not: its canvas is then older than this page, and
   * whatever came next (a save, a message, a version change) would work on that older canvas. `then` says what did not
   * happen because of it ("Nothing was saved.").
   */
  async settle(then = ''): Promise<void> {
    await this.flush();
    this.mustBeSettled(then);
  }

  private mustBeSettled(then = ''): void {
    if (!this.unsent && !this.blocked) return;
    const waiting = this.busy ? 'a message to the assistant is still running' : 'they are still on their way';
    const why = (this.syncError ?? waiting).replace(/\.$/, '');
    throw new Error(`The latest edits have not reached the designer (${why}).${then ? ` ${then}` : ''}`);
  }

  /**
   * How many cells of this canvas differ from a copy of the bot's (the session as `designer.get` returns it), leaving out
   * the cells of edits still on their way, which that copy cannot hold yet. Null when the copy is older than what this page
   * has already heard from the bot (a lower rev): it cannot tell then.
   */
  differsFrom(session: Pick<DesignerSession, 'rev' | 'blocks' | 'dug'>): number | null {
    return this.differing(session)?.length ?? null;
  }

  private differing(session: Pick<DesignerSession, 'rev' | 'blocks' | 'dug'>): number[] | null {
    if (session.rev < this.rev) return null;
    const theirs = new Canvas();
    theirs.load(session.blocks, session.dug ?? []);
    const unsent = new Set<number>();
    for (const e of [...this.sending, ...this.pending]) for (const c of e.cells) unsent.add(keyOf(c[0], c[1], c[2]));
    const keys = new Set([...this.canvas.blocks.keys(), ...this.canvas.dug, ...theirs.blocks.keys(), ...theirs.dug]);
    return [...keys].filter((k) => !unsent.has(k) && this.canvas.authoredState(k) !== theirs.authoredState(k));
  }

  /**
   * Sends this page's state of every cell the bot's copy has otherwise (`differsFrom`), as one edit without an undo step,
   * and waits until the bot holds it. Returns how many cells went; throws as `settle` does when they could not be sent.
   */
  async resync(session: Pick<DesignerSession, 'rev' | 'blocks' | 'dug'>): Promise<number> {
    const cells = (this.differing(session) ?? []).map((k): Cell => {
      const v = this.canvas.authoredState(k);
      return [...posOf(k), v < 0 ? EARTH : v >> 4, v < 0 ? 0 : v & 15];
    });
    this.queue('resync', 'resync with the page', cells);
    await this.settle();
    return cells.length;
  }

  /** Runs a request once the bot holds every edit made before it; the wire is held until `task` has dealt with the reply. */
  private afterEdits<T>(then: string, task: () => Promise<T>): Promise<T> {
    return this.onWire(async () => {
      await this.drain();
      this.mustBeSettled(then);
      return task();
    });
  }

  /**
   * Moves (or clears) the anchor. It follows the edits to the bot and, like them, is sent again when that fails; the
   * promise rejects when it could not be sent now.
   */
  async setAnchor(p: V3 | null): Promise<void> {
    this.anchor = p;
    this.anchorUnsent = true;
    this.changed();
    await this.settle();
  }

  // ------------------------------------------------------------------ talking to the model

  /**
   * Sends a message (with the selection, if asked) and applies the AI's ops. The waiting edits go first, through the canvas
   * route, where the bot notes them for the model: the message carries none, so it shares no size limit with them.
   */
  async send(
    text: string,
    opts: { model?: string; withSelection?: boolean } = {},
  ): Promise<DesignerMessageResponse | null> {
    const t = text.trim();
    if (!t || this.busy) return null;
    this.stopFlushTimer();
    const sel = opts.withSelection && this.selection ? { min: this.selection.min, max: this.selection.max } : null;
    const optimistic: DesignerMessage = {
      seq: -1,
      role: 'user',
      text: t,
      at: Date.now(),
      model: null,
      costUsd: null,
      ops: [],
    };
    this.messages = [...this.messages, optimistic];
    this.busy = true;
    this.error = null;
    this.changed();
    try {
      const r = await this.afterEdits('The message was not sent.', async () => {
        const reply = await this.api.message(this.id, messageRequest(t, this.rev, sel, opts.model));
        this.busy = false;
        this.messages = [...this.messages.filter((m) => m !== optimistic), reply.userMessage, reply.message];
        this.costUsd = reply.sessionCostUsd;
        this.applyAi(reply.ops);
        this.rev = reply.rev;
        this.changed();
        return reply;
      });
      void this.flush();
      return r;
    } catch (e) {
      this.busy = false;
      this.error = e instanceof Error ? e.message : String(e);
      this.messages = this.messages.filter((m) => m !== optimistic);
      this.changed();
      // Edits made during the turn go now; a failed batch already has its retry set.
      if (!this.flushTimer) void this.flush();
      throw e;
    }
  }

  private stopFlushTimer() {
    if (!this.flushTimer) return;
    clearTimeout(this.flushTimer);
    this.flushTimer = null;
  }

  /**
   * Saves the canvas as a design (a new one with `asNew`, else the next revision of the loaded one). The bot saves the
   * canvas it holds, so every edit has to be there first: when one could not be sent, nothing is saved and it says why.
   */
  async save(name: string, summary?: string, asNew = false, previewPng?: string): Promise<DesignerSaveResponse> {
    // The bot answers a save during a message "busy": say so now instead of waiting behind the model.
    if (this.busy) throw new Error('The assistant is still answering. Nothing was saved.');
    // The bot would refuse it after the edits went out; say so before anything is sent.
    if ((this.mineAuthoring?.length ?? 0) > MINE_AUTHORING_MAX_CHARS)
      throw new Error(
        'The component layout is too large to save. Remove or combine fewer components. Nothing was saved.',
      );
    return this.afterEdits('Nothing was saved.', () => this.saveNow(name, summary, asNew, previewPng));
  }

  /** The save request and what it leaves saved here; the wire is held throughout, so no edit goes out in between. */
  private async saveNow(
    name: string,
    summary: string | undefined,
    asNew: boolean,
    previewPng: string | undefined,
  ): Promise<DesignerSaveResponse> {
    const body: DesignerSaveRequest = {
      name: name.trim(),
      ...(summary?.trim() ? { summary: summary.trim() } : {}),
      ...(asNew ? { asNew: true } : {}),
      ...(previewPng ? { previewPng } : {}),
      ...(this.mineAuthoring !== undefined ? { mineAuthoring: this.mineAuthoring } : {}),
      ...(this.redstoneAuthoring !== undefined ? { redstoneAuthoring: this.redstoneAuthoring } : {}),
    };
    // What is on the canvas as the save goes out is what the save holds (edits made meanwhile stay unsaved).
    const at = new Map([...this.diverged].map((k) => [k, this.stateAt(k)]));
    const r = await this.api.save(this.id, body);
    this.savedMineAuthoring = body.mineAuthoring;
    this.changedSinceSave = false;
    this.rememberSavedRev();
    this.savedRedstoneAuthoring = body.redstoneAuthoring;
    this.savedKind = this.session.kind ?? 'build';
    for (const [k, v] of at) {
      const now = this.stateAt(k);
      this.saved.set(k, v);
      if (now === v) this.diverged.delete(k);
    }
    this.designId = r.designId;
    this.name = r.name;
    this.messages = [
      ...this.messages,
      {
        seq: this.messages.length + 1,
        role: 'assistant',
        text: r.text,
        at: Date.now(),
        model: null,
        costUsd: null,
        ops: [],
      },
    ];
    this.changed();
    return r;
  }

  /**
   * The page lets go of the studio: edits still waiting for the debounce go now (once: a failure is not tried again), so
   * leaving within the debounce loses nothing a "Resume it" offer would point at.
   */
  dispose(): void {
    this.disposed = true;
    void this.flush();
    this.stopFlushTimer();
    this.listeners.clear();
    this.cellListeners.clear();
  }
}

/**
 * Sends the waiting edits the moment the page is hidden or about to go (a tab switched away, reloaded or closed) instead of
 * after the debounce, which such a page may never reach. Best effort: a request cut off by the page closing is lost.
 * Returns the unbind.
 */
export function flushWhenHidden(
  studio: Pick<Studio, 'flush'>,
  doc: Document = document,
  win: Window = window,
): () => void {
  const hidden = (): void => {
    if (doc.visibilityState === 'hidden') void studio.flush();
  };
  const leaving = (): void => void studio.flush();
  doc.addEventListener('visibilitychange', hidden);
  win.addEventListener('pagehide', leaving);
  return () => {
    doc.removeEventListener('visibilitychange', hidden);
    win.removeEventListener('pagehide', leaving);
  };
}
