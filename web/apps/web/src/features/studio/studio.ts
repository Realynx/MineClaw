/**
 * The Design Studio's state outside React: the canvas, one undo/redo history for AI and user ops, the user's edits waiting
 * to reach the bot (`POST …/canvas`, debounced; or carried by the next message), the selection, and the calls to the
 * designer routes. The page renders from `snapshot()` and calls the methods; the world and the 3D overlay listen to `onCells`.
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
import { Canvas, type Cell, History, type OpGroup, type Selection, type V3 } from './canvas';

/** The routes the studio calls (the app passes the typed client; tests pass fakes and read the bodies). */
export interface StudioApi {
  message(id: number, body: DesignerMessageRequest): Promise<DesignerMessageResponse>;
  canvas(id: number, body: DesignerCanvasRequest): Promise<DesignerCanvasResponse>;
  save(id: number, body: DesignerSaveRequest): Promise<DesignerSaveResponse>;
}

export interface StudioSnapshot {
  rev: number;
  blocks: number;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string | null;
  redoLabel: string | null;
  pending: number;
  busy: boolean;
  messages: DesignerMessage[];
  costUsd: number;
  selection: Selection | null;
  anchor: V3 | null;
  designId: number | null;
  name: string;
  error: string | null;
}

const MAX_CELLS_PER_EDIT = 20_000;

export class Studio {
  readonly canvas = new Canvas();
  readonly history = new History();
  private pending: DesignerEdit[] = [];
  private flushTimer: ReturnType<typeof setTimeout> | null = null;
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

  constructor(
    readonly session: DesignerSession,
    private readonly api: StudioApi,
    readonly flushMs = 400,
  ) {
    this.canvas.load(session.blocks);
    this.rev = session.rev;
    this.messages = [...session.messages];
    this.costUsd = session.costUsd;
    this.anchor = session.anchor ?? null;
    this.designId = session.designId ?? null;
    this.name = session.name;
    this.snap = this.build();
  }

  get id(): number {
    return this.session.id;
  }

  // ------------------------------------------------------------------ subscriptions (useSyncExternalStore)

  subscribe = (fn: () => void): (() => void) => {
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
      canUndo: this.history.canUndo,
      canRedo: this.history.canRedo,
      undoLabel: this.history.peekUndo()?.label ?? null,
      redoLabel: this.history.peekRedo()?.label ?? null,
      pending: this.pending.reduce((n, e) => n + e.cells.length, 0),
      busy: this.busy,
      messages: this.messages,
      costUsd: this.costUsd,
      selection: this.selection,
      anchor: this.anchor,
      designId: this.designId,
      name: this.name,
      error: this.error,
    };
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
    const ch = this.canvas.apply(cells);
    if (ch.forward.length === 0) return [];
    const g: OpGroup = { label, by: 'user', forward: ch.forward, backward: ch.backward };
    if (group && group === this.lastGroup) this.history.merge(g);
    else this.history.push(g);
    this.lastGroup = group ?? null;
    this.emit(ch.forward, 'user');
    this.queue(kind, label, ch.forward);
    this.changed();
    return ch.forward;
  }

  /** The AI's ops from a reply (the bot applied them already): applied here, recorded for undo, never sent back. */
  applyAi(ops: DesignerMessageResponse['ops']): Cell[] {
    const all: Cell[] = [];
    this.lastGroup = null;
    for (const op of ops) {
      const ch = this.canvas.apply(op.cells, false);
      if (ch.forward.length === 0) continue;
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
    for (let i = 0; i < cells.length; i += MAX_CELLS_PER_EDIT)
      this.pending.push({
        kind: kind.slice(0, 32),
        label: label.slice(0, 200),
        cells: cells.slice(i, i + MAX_CELLS_PER_EDIT),
      });
    this.schedule();
  }

  private schedule(): void {
    if (this.flushTimer || this.busy) return;
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null;
      void this.flush();
    }, this.flushMs);
  }

  private take(): DesignerEdit[] {
    const out: DesignerEdit[] = [];
    let cells = 0;
    while (this.pending.length && out.length < 64 && cells + this.pending[0]!.cells.length <= MAX_CELLS_PER_EDIT * 2) {
      const e = this.pending.shift()!;
      cells += e.cells.length;
      out.push(e);
    }
    return out;
  }

  /** Sends the waiting edits now (held while a message runs: the bot's session is busy then). */
  async flush(): Promise<void> {
    if (this.busy) return;
    while (this.pending.length) {
      const ops = this.take();
      try {
        const r = await this.api.canvas(this.id, { rev: this.rev, ops });
        this.rev = r.rev;
        this.error = null;
      } catch (e) {
        this.pending.unshift(...ops);
        this.error = e instanceof Error ? e.message : String(e);
        this.changed();
        if (this.flushTimer === null)
          this.flushTimer = setTimeout(() => ((this.flushTimer = null), void this.flush()), 2000);
        return;
      }
    }
    this.changed();
  }

  async setAnchor(p: V3 | null): Promise<void> {
    this.anchor = p;
    this.changed();
    await this.flush();
    const r = await this.api.canvas(
      this.id,
      p ? { rev: this.rev, ops: [], anchor: p } : { rev: this.rev, ops: [], clearAnchor: true },
    );
    this.rev = r.rev;
  }

  // ------------------------------------------------------------------ talking to the model

  /** Sends a message (with the waiting edits and, if asked, the selection) and applies the AI's ops. */
  async send(
    text: string,
    opts: { model?: string; withSelection?: boolean } = {},
  ): Promise<DesignerMessageResponse | null> {
    const t = text.trim();
    if (!t || this.busy) return null;
    if (this.flushTimer) {
      clearTimeout(this.flushTimer);
      this.flushTimer = null;
    }
    const edits = this.take();
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
      const body: DesignerMessageRequest = {
        text: t,
        canvas: { rev: this.rev, ...(edits.length ? { edits } : {}) },
        ...(sel ? { selection: sel } : {}),
        ...(opts.model ? { model: opts.model } : {}),
      };
      const r = await this.api.message(this.id, body);
      this.busy = false;
      this.messages = [...this.messages.filter((m) => m !== optimistic), r.userMessage, r.message];
      this.costUsd = r.sessionCostUsd;
      this.applyAi(r.ops);
      this.rev = r.rev;
      this.changed();
      void this.flush();
      return r;
    } catch (e) {
      this.busy = false;
      this.pending.unshift(...edits);
      this.error = e instanceof Error ? e.message : String(e);
      this.messages = this.messages.filter((m) => m !== optimistic);
      this.changed();
      void this.flush();
      throw e;
    }
  }

  /** Saves the canvas as a design (a new one with `asNew`, else the next revision of the loaded one). */
  async save(name: string, summary?: string, asNew = false): Promise<DesignerSaveResponse> {
    await this.flush();
    const body: DesignerSaveRequest = {
      name: name.trim(),
      ...(summary?.trim() ? { summary: summary.trim() } : {}),
      ...(asNew ? { asNew: true } : {}),
    };
    const r = await this.api.save(this.id, body);
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

  dispose(): void {
    if (this.flushTimer) clearTimeout(this.flushTimer);
    this.listeners.clear();
    this.cellListeners.clear();
  }
}
