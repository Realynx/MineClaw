/**
 * Map commands drawn at once (command-lag WP2): the marks of a designation, the designations a Cancel hides, a painted zone
 * and the holograms of a placement are shown from the moment of the release, before the colony answers. The colony state
 * itself is never touched: these ops are drawn over it and go when the state carries them or when the command failed.
 *
 * An op's life follows the colony writes' lifecycle (optimistic.ts):
 * - **sending** while its request is on the wire;
 * - **applied** after a 2xx: dropped once a state reaches the answer's `rev`. An answer without `rev` (an older bot) is
 *   dropped when the catch-up started after the answer has merged (or, without one, by the next state);
 * - **rejected** (a 4xx, a 503 `busy`, offline): removed at once, and the failure goes to the toast;
 * - **unconfirmed** (a lost connection, a 502, another 503): the colony may or may not have it. It counts as applied as soon
 *   as a state shows it (a designation of that kind on one of its cells, a build cell on one of its holograms, the
 *   cancelled designations gone), else it goes after 10 s with a warning. It is never sent again.
 * Whatever its state, no op stays longer than 15 s. Pure (time is passed in), so the map and the tests share it.
 */
import { designationCells, type StoryColonyState, type StoryZone } from '@mineclaw/contracts';
import type { HoloCell } from '@mineclaw/world-viewer/settings';
import { answerRev, storyWriteError, UNCONFIRMED_MS, UNCONFIRMED_TEXT } from './optimistic';

type V3 = [number, number, number];

/** A designation mark to draw: where, and the designate kind whose glyph it takes. */
export interface PendingMark {
  cell: V3;
  kind: string;
}

/** A zone paint as the colony will make it, applied over the server's zones (the `zonePreview` computation). */
export type ZoneStep = (zones: StoryZone[]) => StoryZone[];

/** What one command draws until the colony carries it. Order lines are not part of it. */
export interface PendingOp {
  marks?: readonly PendingMark[];
  /** Designation ids a Cancel removes: not drawn while it is pending. */
  hide?: ReadonlyArray<string | number>;
  zones?: ZoneStep;
  /** Placement holograms (structures, designs, removes). */
  holo?: readonly HoloCell[];
}

export type PendingStatus = 'sending' | 'applied' | 'unconfirmed';

/** How the command's request ended: its answer, or the error it threw. */
export type PendingOutcome =
  | {
      ok: true;
      out: unknown;
      /** For an answer without `rev`: resolves once a state read started after the answer has merged. */
      caughtUp?: PromiseLike<unknown>;
    }
  | { ok: false; error: unknown };

/** A command whose marks went without the colony showing them: what the toast says. */
export interface PendingFailure {
  id: string;
  /** refused: the colony did not take it; unconfirmed: it may still have been applied. */
  kind: 'refused' | 'unconfirmed';
  text: string;
}

/** Everything pending, ready to draw over the colony state. */
export interface PendingView {
  marks: readonly PendingMark[];
  /** Ids of real designations not to draw (as strings). */
  hidden: ReadonlySet<string>;
  /** The zones to draw instead of the server's (null: no zone paint pending). */
  zones: StoryZone[] | null;
  holo: readonly HoloCell[];
}

/** The most an op is drawn, whatever its state. */
export const PENDING_LIMIT_MS = 15_000;

const ID_PREFIX = 'pending:';

/** A pending op's id: local to the page, never a colony id. */
export const isPendingId = (id: unknown): boolean => typeof id === 'string' && id.startsWith(ID_PREFIX);

/** `ids` without any pending id: what a request may carry (pending ids are never sent to the colony). */
export const serverIds = <T>(ids: readonly T[]): T[] => ids.filter((id) => !isPendingId(id));

const EMPTY_VIEW: PendingView = { marks: [], hidden: new Set(), zones: null, holo: [] };
const cellKey = (c: readonly (number | string | null | undefined)[]) => `${c[0]},${c[1]},${c[2]}`;

interface Held {
  id: string;
  op: PendingOp;
  status: PendingStatus;
  addedAt: number;
  /** When it went unconfirmed. */
  since: number;
  /** The answer's revision (applied). */
  rev: number | null;
  /** Applied without a revision and without a catch-up to wait for: the first state other than this one drops it. */
  waitAfter: StoryColonyState | undefined | null;
}

/** Whether a colony state shows the command applied (an unconfirmed op counts as applied then). */
function stateShows(state: StoryColonyState, op: PendingOp): boolean {
  const des = state.designations ?? [];
  if (op.marks?.length) {
    const want = new Set(op.marks.map((m) => `${m.kind}@${cellKey(m.cell)}`));
    if (des.some((d) => designationCells(d).some((c) => want.has(`${d.kind}@${cellKey(c)}`)))) return true;
  }
  if (op.holo?.length) {
    const ghosts = new Set((state.ghosts?.blocks ?? []).map(cellKey));
    if (op.holo.some((h) => ghosts.has(cellKey([h.x, h.y, h.z])))) return true;
  }
  if (op.hide?.length) {
    const ids = new Set(des.map((d) => String(d.id)));
    if (op.hide.every((id) => !ids.has(String(id)))) return true;
  }
  return false;
}

export class PendingCommands {
  private held: Held[] = [];
  private seq = 0;
  private rev = 0;
  /** The last state `reconcile` saw. */
  private last: StoryColonyState | undefined | null = null;
  private memo: { rev: number; zones: readonly StoryZone[] | undefined; out: PendingView } | null = null;

  /** `changed` runs whenever what is drawn changes (also from a catch-up that settles an op later) or `nextDeadline` moves. */
  constructor(private readonly changed: () => void = () => undefined) {}

  /** Moves on with every change of what is drawn. */
  get revision(): number {
    return this.rev;
  }

  get size(): number {
    return this.held.length;
  }

  status(id: string): PendingStatus | null {
    return this.held.find((h) => h.id === id)?.status ?? null;
  }

  /** Draws `op` from now on (before the request is sent). The id stays in the page. */
  add(op: PendingOp, now: number): string {
    const id = `${ID_PREFIX}${++this.seq}`;
    this.held = [...this.held, { id, op, status: 'sending', addedAt: now, since: now, rev: null, waitAfter: null }];
    this.bump();
    return id;
  }

  /** The request ended. A refusal removes the op at once and is returned for the toast; nothing is ever sent again. */
  settle(id: string, outcome: PendingOutcome, now: number): PendingFailure | null {
    const h = this.held.find((x) => x.id === id);
    if (!h) return null;
    if (!outcome.ok) {
      const why = storyWriteError(outcome.error);
      if (why.kind === 'refused') {
        this.drop([h]);
        return { id, kind: 'refused', text: why.text };
      }
      h.status = 'unconfirmed';
      h.since = now;
      // Nothing drawn changes, but its deadline moved to 10 s from now: the caller re-arms its timer.
      this.changed();
      return null;
    }
    h.status = 'applied';
    h.rev = answerRev(outcome.out);
    if (h.rev !== null) {
      if (this.last && this.last.rev >= h.rev) this.drop([h]);
    } else if (outcome.caughtUp) {
      outcome.caughtUp.then(
        () => this.drop([h]),
        // The read failed: whatever state comes next was read after the answer.
        () => (h.waitAfter = this.last ?? undefined),
      );
    } else h.waitAfter = this.last ?? undefined;
    return null;
  }

  /** A colony state arrived (or time passed: call it at `nextDeadline`). Returns the ops that went unconfirmed. */
  reconcile(state: StoryColonyState | undefined, now: number): PendingFailure[] {
    if (state) this.last = state;
    const gone: Held[] = [];
    const failed: PendingFailure[] = [];
    for (const h of this.held) {
      const verdict = this.verdict(h, state, now);
      if (verdict === 'keep') continue;
      gone.push(h);
      if (verdict === 'failed') failed.push({ id: h.id, kind: 'unconfirmed', text: UNCONFIRMED_TEXT });
    }
    this.drop(gone);
    return failed;
  }

  private verdict(h: Held, state: StoryColonyState | undefined, now: number): 'keep' | 'applied' | 'failed' {
    const applied = h.status === 'applied';
    if (now - h.addedAt >= PENDING_LIMIT_MS) return applied ? 'applied' : 'failed';
    if (!state) return h.status === 'unconfirmed' && now - h.since >= UNCONFIRMED_MS ? 'failed' : 'keep';
    if (applied) {
      if (h.rev !== null) return state.rev >= h.rev ? 'applied' : 'keep';
      return h.waitAfter !== null && state !== h.waitAfter ? 'applied' : 'keep';
    }
    if (h.status !== 'unconfirmed') return 'keep';
    if (stateShows(state, h.op)) return 'applied';
    return now - h.since >= UNCONFIRMED_MS ? 'failed' : 'keep';
  }

  /** When `reconcile` must run again even if no state arrives (null: nothing waits on time). */
  nextDeadline(): number | null {
    let at: number | null = null;
    for (const h of this.held) {
      const t = Math.min(
        h.addedAt + PENDING_LIMIT_MS,
        h.status === 'unconfirmed' ? h.since + UNCONFIRMED_MS : Infinity,
      );
      at = at === null ? t : Math.min(at, t);
    }
    return at;
  }

  /** Every pending op, ready to draw over `zones` (the server's): kept while neither the ops nor the zones change. */
  view(zones: readonly StoryZone[] | undefined): PendingView {
    if (!this.held.length) return EMPTY_VIEW;
    const m = this.memo;
    if (m && m.rev === this.rev && m.zones === zones) return m.out;
    if (m && m.rev === this.rev) {
      // Only the server's zones moved: the marks and holograms keep their identity (the map rebuilds nothing for them).
      const out = { ...m.out, zones: this.paint(zones) };
      this.memo = { rev: this.rev, zones, out };
      return out;
    }
    const marks = new Map<string, PendingMark>();
    const hidden = new Set<string>();
    const holo = new Map<string, HoloCell>();
    for (const { op } of this.held) {
      for (const mk of op.marks ?? []) marks.set(`${mk.kind}@${cellKey(mk.cell)}`, mk);
      for (const id of op.hide ?? []) hidden.add(String(id));
      for (const c of op.holo ?? []) holo.set(cellKey([c.x, c.y, c.z]), c);
    }
    const out: PendingView = {
      marks: marks.size ? [...marks.values()] : EMPTY_VIEW.marks,
      hidden,
      zones: this.paint(zones),
      holo: holo.size ? [...holo.values()] : EMPTY_VIEW.holo,
    };
    this.memo = { rev: this.rev, zones, out };
    return out;
  }

  /** The pending zone steps applied in order over the server's zones (null when none is pending). */
  private paint(zones: readonly StoryZone[] | undefined): StoryZone[] | null {
    let painted: StoryZone[] | null = null;
    for (const { op } of this.held) if (op.zones) painted = op.zones(painted ?? [...(zones ?? [])]);
    return painted;
  }

  /** Forgets every op (another colony). */
  clear(): void {
    if (!this.held.length) return;
    this.held = [];
    this.bump();
  }

  private drop(list: readonly Held[]): void {
    const gone = list.filter((h) => this.held.includes(h));
    if (!gone.length) return;
    this.held = this.held.filter((h) => !gone.includes(h));
    this.bump();
  }

  private bump(): void {
    this.rev++;
    this.changed();
  }
}

/** Chop's marks: one per tree, on its top log. Logs group into trees by touching columns, as the preview counts them. */
export function treeTops(logs: readonly V3[]): V3[] {
  const top = new Map<string, V3>();
  for (const l of logs) {
    const k = `${l[0]},${l[2]}`;
    const t = top.get(k);
    if (!t || l[1] > t[1]) top.set(k, l);
  }
  const seen = new Set<string>();
  const out: V3[] = [];
  for (const start of top.keys()) {
    if (seen.has(start)) continue;
    seen.add(start);
    let best = top.get(start)!;
    const stack = [best];
    while (stack.length) {
      const [x, , z] = stack.pop()!;
      for (let dx = -1; dx <= 1; dx++)
        for (let dz = -1; dz <= 1; dz++) {
          const k = `${x + dx},${z + dz}`;
          const next = top.get(k);
          if (!next || seen.has(k)) continue;
          seen.add(k);
          stack.push(next);
          if (next[1] > best[1]) best = next;
        }
    }
    out.push([best[0], best[1], best[2]]);
  }
  return out;
}

/** The most hologram cells one pending placement draws (the placement preview's own cap). */
export const PENDING_HOLO_CAP = 4096;

/** Placement blocks ([x, y, z, id, meta]; id 0 = dig it out) as the hologram cells of a pending build. */
export function holoOf(blocks: ReadonlyArray<readonly number[]>): HoloCell[] {
  return blocks
    .slice(0, PENDING_HOLO_CAP)
    .map(([x, y, z, id, meta]) => ({ x: x!, y: y!, z: z!, id: id!, meta: meta!, build: 'pending', state: 'todo' }));
}

/** The colony's hologram cells with the pending ones added where the colony has none yet (its own cell wins). */
export function withPendingHolo(base: readonly HoloCell[], pending: readonly HoloCell[]): readonly HoloCell[] {
  if (!pending.length) return base;
  const taken = new Set(base.map((c) => cellKey([c.x, c.y, c.z])));
  const extra = pending.filter((c) => !taken.has(cellKey([c.x, c.y, c.z])));
  return extra.length ? [...base, ...extra] : base;
}
