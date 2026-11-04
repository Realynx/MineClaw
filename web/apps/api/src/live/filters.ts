/**
 * Per-browser filtering of the fan-out (docs/web/api.md §5, Query): `topics`, `bots` (limits `now` and `feed`), `brain`
 * (limits `memoryActivity`) and `dim` (limits `entities` and `blocks`; bots are always sent in full). The upstream carries
 * everything.
 *
 * Generic by design: any event name in the contracts `SSE_EVENTS` is accepted as a topic and passes through unfiltered
 * unless a rule below narrows it, so a new bridge event (e.g. `blocks`) needs a contracts change only.
 */
import { SSE_EVENTS, type LiveStreamQuery, type SseEventName } from '@mineclaw/contracts';

export interface ClientFilter {
  /** Event names this client wants; null = all. `status` is always sent. */
  topics: Set<string> | null;
  /** Lower-cased names/shorts; null = all bots. */
  bots: Set<string> | null;
  brain?: string;
  dim?: number;
}

/** A comma list, each item trimmed (and mapped), empties dropped. */
function listOf(text: string | undefined, map: (s: string) => string = (s) => s): string[] {
  return (text ?? '')
    .split(',')
    .map((s) => map(s.trim()))
    .filter(Boolean);
}

export function filterOf(q: LiveStreamQuery | undefined): ClientFilter {
  const bots = listOf(q?.bots, (s) => s.toLowerCase());
  const known = new Set(Object.keys(SSE_EVENTS));
  const picked = listOf(q?.topics).filter((t) => known.has(t) && t !== 'status');
  return {
    topics: picked.length > 0 ? new Set(picked) : null,
    bots: bots.length === 0 || bots.includes('*') ? null : new Set(bots),
    brain: q?.brain || undefined,
    dim: q?.dim,
  };
}

/** name → short alias table, learned from payloads that carry both (entities' bots, /api/bots tiles). */
export class BotAliases {
  private readonly shortOf = new Map<string, string>();

  learn(payload: unknown): void {
    const bots = (payload as { bots?: unknown } | null)?.bots;
    if (!Array.isArray(bots)) return;
    for (const b of bots as Array<{ name?: unknown; short?: unknown }>) {
      if (typeof b?.name === 'string' && typeof b.short === 'string')
        this.shortOf.set(b.name.toLowerCase(), b.short.toLowerCase());
    }
  }

  matches(filter: Set<string>, bot: unknown): boolean {
    if (typeof bot !== 'string') return false;
    const name = bot.toLowerCase();
    if (filter.has(name)) return true;
    const short = this.shortOf.get(name);
    return short !== undefined && filter.has(short);
  }
}

type Obj = Record<string, unknown>;

/** Narrows one event's payload for a client (null skips the frame). */
type Narrow = (f: ClientFilter, d: Obj, aliases: BotAliases) => unknown;

const listIn = (v: unknown): Obj[] | null => (Array.isArray(v) ? (v as Obj[]) : null);

const narrowNow: Narrow = (f, d, aliases) => (f.bots && !aliases.matches(f.bots, d.bot) ? null : d);

const narrowFeed: Narrow = (f, d, aliases) => {
  if (!f.bots) return d;
  const lines = listIn(d.lines) ?? [];
  const kept = lines.filter((l) => aliases.matches(f.bots!, l.bot));
  if (kept.length === 0) return null;
  return kept.length === lines.length ? d : { ...d, lines: kept };
};

const narrowMemoryActivity: Narrow = (f, d) => {
  if (!f.brain) return d;
  const ofBrain = (e: Obj) => e.brain === f.brain;
  return {
    ...d,
    events: listIn(d.events)?.filter(ofBrain) ?? [],
    inContext: listIn(d.inContext)?.filter(ofBrain) ?? [],
  };
};

const narrowEntities: Narrow = (f, d) => {
  if (f.dim === undefined) return d;
  const entities = listIn(d.entities)?.filter((e) => e.dim === f.dim) ?? [];
  const removed = Array.isArray(d.removed) ? (d.removed as unknown[][]).filter((r) => r[0] === f.dim) : d.removed;
  return { ...d, entities, removed };
};

/**
 * {rev, full, more, blocks:[[x,y,z,id,meta]], columns:[{dim,cx,cz,kind}]}: a frame-level `dim` decides when present;
 * otherwise the column list is narrowed (block tuples carry no dimension).
 */
const narrowBlocks: Narrow = (f, d) => {
  if (f.dim === undefined || d === null || typeof d !== 'object') return d;
  if (typeof d.dim === 'number') return d.dim === f.dim ? d : null;
  return Array.isArray(d.columns) ? narrowColumns(f.dim, d, d.columns as Obj[]) : d;
};

/** The frame's columns of this dimension (or of none); a frame left with nothing and not full is dropped. */
function narrowColumns(dim: number, d: Obj, all: Obj[]): Obj | null {
  const columns = all.filter((c) => c.dim === undefined || c.dim === dim);
  const blocks = Array.isArray(d.blocks) ? d.blocks : [];
  if (columns.length === 0 && blocks.length === 0 && d.full !== true) return null;
  return { ...d, columns };
}

/** The events a filter narrows; every other event passes through unfiltered. */
const NARROW: Partial<Record<string, Narrow>> = {
  now: narrowNow,
  feed: narrowFeed,
  memoryActivity: narrowMemoryActivity,
  entities: narrowEntities,
  blocks: narrowBlocks,
};

/** The payload this client gets for an event, or null to skip the frame. Never mutates `data`. */
export function filterPayload(f: ClientFilter, event: SseEventName, data: unknown, aliases: BotAliases): unknown {
  if (event === 'status') return data;
  if (f.topics && !f.topics.has(event)) return null;
  const narrow = Object.hasOwn(NARROW, event) ? NARROW[event] : undefined;
  return narrow ? narrow(f, data as Obj, aliases) : data;
}

/**
 * For a slow client, frames of state topics are coalesced (latest wins) while it is congested; delta topics are queued.
 * A `full: true` frame of any event (entities, blocks…) supersedes every queued frame of that event.
 */
export function coalesceKey(event: SseEventName, data: unknown): string | undefined {
  switch (event as string) {
    case 'bots':
    case 'hive':
    case 'status':
      return event;
    case 'now':
      return `now:${String((data as Obj).bot)}`;
    default:
      return data !== null && typeof data === 'object' && (data as Obj).full === true ? `${event}:full` : undefined;
  }
}
