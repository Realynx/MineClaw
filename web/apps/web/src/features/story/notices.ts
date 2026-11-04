/**
 * Story Mode's notifications, the genre's way (pure rules; `useNotices` runs them, `StoryNotices.tsx` draws them):
 * - **Messages**: transient events (the colony's alerts as they arrive). They expire by themselves (info 6 s, warn 12 s; a
 *   danger message stays until dismissed), repeats of the same kind collapse into one entry with a count that refreshes its
 *   timer, and at most `STACK_CAP` show at once ("+N more" opens the log).
 * - **Conditions**: ongoing problems derived from the colony state (pawns offline, no bed, no food or seeds in storage, main
 *   not op). Listed while they hold, gone when they clear; never clicked away.
 * - **Log**: the last `LOG_CAP` messages (and the page's own toasts), filterable, each a jump to its pawn or place.
 */
import type { StoryAlert, StoryColonyState, StoryPawn } from '@mineclaw/contracts';

export type NoticeLevel = 'info' | 'ok' | 'warn' | 'danger';

export interface Notice {
  /** The coalescing key (level + the text with its numbers and positions taken out). */
  key: string;
  level: NoticeLevel;
  /** The latest text of the kind. */
  text: string;
  count: number;
  /** ms (Date.now()). */
  first: number;
  last: number;
  /** ms; Infinity = until dismissed. */
  expires: number;
  pawn: string | null;
  pos: [number, number, number] | null;
  alert: StoryAlert | null;
}

export interface LogEntry {
  id: number;
  key: string;
  level: NoticeLevel;
  text: string;
  count: number;
  at: number;
  pawn: string | null;
  pos: [number, number, number] | null;
  alert: StoryAlert | null;
  /** From the colony (an alert) or the page itself (a toast). */
  source: 'colony' | 'page';
}

export interface NoticeState {
  stack: Notice[];
  log: LogEntry[];
  /** Alert ids (or at|text) already taken in: a state poll never shows them again. */
  seen: Set<string>;
  unread: number;
  nextId: number;
}

export const STACK_CAP = 4;
export const LOG_CAP = 200;
/** How long a message shows (ms). */
export const TTL: Record<NoticeLevel, number> = { info: 6000, ok: 6000, warn: 12_000, danger: Infinity };
/** Alerts older than this when the page first sees them go to the log only. */
export const FRESH_MS = 60_000;
/** A repeat merges into the log's entry of its kind when that entry is this recent. */
const LOG_MERGE_MS = 120_000;

export function emptyNotices(): NoticeState {
  return { stack: [], log: [], seen: new Set(), unread: 0, nextId: 1 };
}

const POS = /(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)/;

/** The text of a kind: positions and numbers taken out (`cut at 5,64,2 failed 3 times` → `cut at # failed # times`). */
export function normalize(text: string): string {
  return text
    .replace(/-?\d+\s*,\s*-?\d+\s*,\s*-?\d+/g, '#')
    .replace(/-?\d+(\.\d+)?/g, '#')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

export function noticeKey(level: string, text: string): string {
  return `${level}|${normalize(text)}`;
}

/** The first x, y, z in a text (a place to jump to), or null. */
export function posIn(text: string): [number, number, number] | null {
  const m = POS.exec(text);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

const levelOf = (l: string | null | undefined): NoticeLevel => (l === 'warn' || l === 'danger' || l === 'ok' ? l : 'info');
const alertId = (a: StoryAlert) => String(a.id ?? `${a.at}|${a.text}`);
const msOf = (at: StoryAlert['at'] | undefined, fallback: number): number => {
  if (typeof at === 'number') return at > 1e12 ? at : at * 1000;
  const t = at ? Date.parse(at) : NaN;
  return Number.isFinite(t) ? t : fallback;
};

export interface MessageIn {
  level: string;
  text: string;
  pawn?: string | null;
  pos?: [number, number, number] | null;
  alert?: StoryAlert | null;
  /** Shown in the stack (colony alerts) or only logged (the page's own toasts). */
  stack?: boolean;
  source?: LogEntry['source'];
}

/** One message as it lands: its kind, level, text, pawn, place and alert. */
interface Incoming {
  key: string;
  level: NoticeLevel;
  text: string;
  pawn: string | null;
  pos: [number, number, number] | null;
  alert: StoryAlert | null;
  now: number;
}

function incoming(m: MessageIn, now: number): Incoming {
  const level = levelOf(m.level);
  return {
    key: noticeKey(level, m.text),
    level,
    text: m.text,
    pos: m.pos ?? m.alert?.fix?.workbench ?? posIn(m.text),
    pawn: m.pawn ?? m.alert?.pawn ?? null,
    alert: m.alert ?? null,
    now,
  };
}

/** The stack with `m`: merged into the entry of its kind (moved to the top, count and timer refreshed), else added on top. */
function stackWith(stack: Notice[], m: Incoming, expires: number): Notice[] {
  const i = stack.findIndex((n) => n.key === m.key);
  if (i < 0) {
    const { key, level, text, now, pawn, pos, alert } = m;
    return [...stack, { key, level, text, count: 1, first: now, last: now, expires, pawn, pos, alert }];
  }
  const n = stack[i]!;
  const merged: Notice = {
    ...n,
    text: m.text,
    count: n.count + 1,
    last: m.now,
    expires,
    pawn: m.pawn ?? n.pawn,
    pos: m.pos ?? n.pos,
    alert: m.alert ?? n.alert,
  };
  return [...stack.slice(0, i), ...stack.slice(i + 1), merged];
}

/** The log with `m`: merged into a recent entry of its kind (moved to the end), else a new entry (the oldest dropped past the cap). */
function logWith(
  log: LogEntry[],
  nextId: number,
  m: Incoming,
  source: LogEntry['source'],
): { log: LogEntry[]; nextId: number; merged: boolean } {
  const j = log.findIndex((e) => e.key === m.key && m.now - e.at < LOG_MERGE_MS);
  if (j >= 0) {
    const e = log[j]!;
    const entry = { ...e, text: m.text, count: e.count + 1, at: m.now, pawn: m.pawn ?? e.pawn, pos: m.pos ?? e.pos };
    return { log: [...log.slice(0, j), ...log.slice(j + 1), entry], nextId, merged: true };
  }
  const { key, level, text, now, pawn, pos, alert } = m;
  const out = [...log, { id: nextId, key, level, text, count: 1, at: now, pawn, pos, alert, source }];
  return { log: out.length > LOG_CAP ? out.slice(out.length - LOG_CAP) : out, nextId: nextId + 1, merged: false };
}

/** A message: into the log, and (unless `stack: false`) onto the stack, merged with one of its kind when there is one. */
export function addMessage(s: NoticeState, m: MessageIn, now: number, ttlScale = 1, logOpen = false): NoticeState {
  const msg = incoming(m, now);
  const stack = m.stack === false ? s.stack : stackWith(s.stack, msg, now + TTL[msg.level] * ttlScale);
  const { log, nextId, merged } = logWith(s.log, s.nextId, msg, m.source ?? 'colony');
  return { ...s, stack, log, nextId, unread: logOpen ? 0 : s.unread + (merged ? 0 : 1) };
}

/**
 * The colony's alerts after a state poll: the ones not seen before become messages; old ones (from before the page opened,
 * `FRESH_MS`) go to the log only. Returns the same state when nothing is new.
 */
export function ingestAlerts(s: NoticeState, alerts: readonly StoryAlert[], now: number, ttlScale = 1, logOpen = false): NoticeState {
  let out = s;
  for (const a of alerts) {
    const id = alertId(a);
    if (out.seen.has(id)) continue;
    if (out === s) out = { ...s, seen: new Set(s.seen) };
    out.seen.add(id);
    const fresh = now - msOf(a.at, now) < FRESH_MS;
    out = { ...addMessage(out, { level: a.level, text: a.text, pawn: a.pawn, alert: a, stack: fresh }, now, ttlScale, logOpen), seen: out.seen };
  }
  return out;
}

/** Drops expired messages (not while `hold`: the pointer is over the stack). */
export function expire(s: NoticeState, now: number, hold = false): NoticeState {
  if (hold || !s.stack.some((n) => n.expires <= now)) return s;
  return { ...s, stack: s.stack.filter((n) => n.expires > now) };
}

/** After a hover: every message gets at least `ms` more. */
export function extend(s: NoticeState, now: number, ms = 3000): NoticeState {
  return { ...s, stack: s.stack.map((n) => (n.expires < now + ms ? { ...n, expires: now + ms } : n)) };
}

export function dismiss(s: NoticeState, key: string): NoticeState {
  return { ...s, stack: s.stack.filter((n) => n.key !== key) };
}

export function dismissAll(s: NoticeState): NoticeState {
  return s.stack.length ? { ...s, stack: [] } : s;
}

/** What the stack shows: the newest `cap` (newest first) and how many more there are. */
export function visible(s: NoticeState, cap = STACK_CAP): { shown: Notice[]; more: number } {
  const shown = s.stack.slice(-cap).reverse();
  return { shown, more: Math.max(0, s.stack.length - cap) };
}

// ------------------------------------------------------------------ conditions

export interface Condition {
  key: string;
  level: 'warn' | 'danger';
  text: string;
  /** What it is about: pawns to select, or a place. */
  pawns: string[];
  tab?: 'colony' | 'storage' | 'work';
}

/** Foods (both versions' ids): apple, stew, bread, pork, golden apple, fish, cookie, melon, beef, chicken, carrot, potato,
 * pie, rabbit, mutton, beetroot. */
const FOOD = new Set([260, 282, 297, 319, 320, 322, 349, 350, 357, 360, 363, 364, 365, 366, 391, 392, 393, 400, 411, 412, 413, 423, 424, 434, 436]);
/** Seeds: wheat, pumpkin, melon, beetroot (and carrots and potatoes, which plant themselves). */
const SEEDS = new Set([295, 361, 362, 435, 391, 392]);

type ConditionRule = (
  st: StoryColonyState,
  pawns: readonly StoryPawn[],
  opts: { mainOp?: boolean | null },
) => Condition | Condition[] | null;

/** "Dara is offline" for one pawn, "3 pawns offline" for more. */
function pawnsText(list: readonly StoryPawn[], one: (display: string) => string, many: (n: number) => string): string {
  return list.length === 1 ? one(list[0]!.display) : many(list.length);
}

/** A condition about some pawns (none: no condition). */
function aboutPawns(
  key: string,
  list: readonly StoryPawn[],
  one: (display: string) => string,
  many: (n: number) => string,
  tab?: Condition['tab'],
): Condition | null {
  if (!list.length) return null;
  return {
    key,
    level: 'warn',
    text: pawnsText(list, one, many),
    pawns: list.map((p) => p.name),
    ...(tab ? { tab } : {}),
  };
}

const offlineRule: ConditionRule = (_st, pawns) =>
  aboutPawns(
    'offline',
    pawns.filter((p) => p.online === false),
    (d) => `${d} is offline`,
    (n) => `${n} pawns offline`,
  );

const bedRule: ConditionRule = (st, pawns) => {
  if (!st.beds) return null;
  const owners = new Set(st.beds.map((b) => b.pawn).filter(Boolean));
  return aboutPawns(
    'bed',
    pawns.filter((p) => p.online !== false && !owners.has(p.name)),
    (d) => `${d} has no bed`,
    (n) => `${n} pawns have no bed`,
    'colony',
  );
};

/** No food, no seeds for a growing zone: only once a chest's contents are known. */
const supplyRule: ConditionRule = (st, pawns) => {
  const stores = st.storages ?? [];
  if (!stores.some((s) => s.knownAt !== null && s.knownAt !== undefined)) return null;
  const has = (set: Set<number>) =>
    stores.some((s) => s.contents.some(([id, , n]) => set.has(id) && n > 0)) ||
    pawns.some((p) => p.inventory.some(([id, , n]) => set.has(id) && n > 0));
  const out: Condition[] = [];
  if (!has(FOOD)) out.push({ key: 'food', level: 'danger', text: 'No food in storage', pawns: [], tab: 'storage' });
  if ((st.zones ?? []).some((z) => z.kind === 'farm') && !has(SEEDS))
    out.push({ key: 'seeds', level: 'warn', text: 'No seeds for the growing zone', pawns: [], tab: 'storage' });
  return out;
};

const unstoredRule: ConditionRule = (st) => {
  const unstored = st.unstoredKinds ?? [];
  if (!unstored.length) return null;
  return { key: 'unstored', level: 'warn', text: `No storage for: ${unstored.join(', ')}`, pawns: [], tab: 'storage' };
};

const stuckRule: ConditionRule = (_st, pawns) =>
  aboutPawns(
    'stuck',
    pawns.filter((p) => p.online !== false && p.stuck),
    (d) => `${d} is stuck`,
    (n) => `${n} pawns are stuck`,
  );

/** Idle pawns with work about (the idle audit), by why: stuck ones have their own condition, "not taken yet" passes by itself. */
const idleWorkRule: ConditionRule = (st) => {
  const blocked = (st.blockedWork ?? []).filter(
    (b) => b.reason !== 'stuck' && b.reason !== 'work not taken yet' && b.count > 0,
  );
  if (!blocked.length) return null;
  const n = blocked.reduce((a, b) => a + b.count, 0);
  const why = blocked[0]!.reason.replace(/^waiting: /, '');
  const more = blocked.length > 1 ? ` (+${blocked.length - 1} more reason${blocked.length > 2 ? 's' : ''})` : '';
  return {
    key: 'idle-work',
    level: 'warn',
    text: `${n === 1 ? '1 pawn' : `${n} pawns`} idle: ${why}${more}`,
    pawns: blocked.flatMap((b) => b.pawns),
    tab: 'work',
  };
};

const opRule: ConditionRule = (st, _pawns, opts) =>
  (opts.mainOp ?? st.timeControl?.available) === false
    ? { key: 'op', level: 'warn', text: "Main isn't op: time of day and commands are off", pawns: [] }
    : null;

/** The conditions, in the order the page lists them. */
const CONDITION_RULES: readonly ConditionRule[] = [
  offlineRule,
  bedRule,
  supplyRule,
  unstoredRule,
  stuckRule,
  idleWorkRule,
  opRule,
];

/** The colony's ongoing problems, from its state; each clears itself when its condition does. */
export function conditions(st: StoryColonyState | undefined, opts: { mainOp?: boolean | null } = {}): Condition[] {
  if (!st) return [];
  const pawns = st.pawns ?? [];
  return CONDITION_RULES.flatMap((rule) => rule(st, pawns, opts) ?? []);
}
