/**
 * The mock director's Preserve zones (docs/web/story-mode.md "Preserve zones"; the bot's StoryColony.Preserve.cs): a zone of kind
 * `preserve` (an overlay layer, always on) or any zone but a mine zone with Preserve switched on keeps its blocks from `below`
 * under its painted level to `above` over it. The mock never damages a snapshot: every preserved zone reports itself intact.
 */
import {
  STORY_PRESERVE_MAX_CELLS,
  STORY_PRESERVE_MAX_RANGE,
  type StoryArea,
  type StoryZone,
  type StoryZonePreserve,
  type StoryZoneRequest,
} from './story.js';
import { zoneCellSet } from './storyZones.js';

type PreserveOp = Extract<StoryZoneRequest, { op: 'preserve' }>;
type ZoneAdd = Extract<StoryZoneRequest, { op: 'add' }>;

export const PRESERVE_MINE_REFUSED =
  "a mine zone digs its blocks: it can't preserve them (paint a Preserve zone beside it instead)";
export const PRESERVE_ALWAYS_ON = 'a Preserve zone is always on: erase or remove it instead';
/** The default range: one layer under the painted level, three over it. */
export const PRESERVE_DEFAULT = { below: 1, above: 3 } as const;

const bad = (message: string) => Object.assign(new Error(message), { status: 400 });
const columns = (z: StoryZone) => z.cells ?? zoneCellSet(z).size;
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** "N blocks to keep: at most …" when the range keeps too many blocks for `cols` columns at levels y1..y2; else null. */
export function preserveTooBig(cols: number, y1: number, y2: number, below: number, above: number): string | null {
  const n = cols * (y2 + above - (y1 - below) + 1);
  return n > STORY_PRESERVE_MAX_CELLS
    ? `${n} blocks to keep: at most ${STORY_PRESERVE_MAX_CELLS} (fewer columns, or less depth or height)`
    : null;
}

/** The zone's Preserve as the bot reports it: its y range inside the world, the blocks kept, intact. */
export function preserveOf(
  z: StoryZone,
  below: number,
  above: number,
  takenAt: StoryZonePreserve['takenAt'],
  height = 256,
): StoryZonePreserve {
  const y1 = clamp(z.area[1] - below, 0, height - 1);
  const y2 = clamp(z.area[4] + above, 0, height - 1);
  return {
    on: true,
    below,
    above,
    y1,
    y2,
    cells: columns(z) * (y2 - y1 + 1),
    takenAt: takenAt ?? null,
    build: null,
    unknown: 0,
    held: 0,
    damage: { missing: 0, foreign: 0, turned: 0 },
    toRepair: 0,
    status: 'intact',
  };
}

/** The zone as the state reports it: its Preserve follows its cells and level (a zone without one as it is). */
export function withPreserveState(z: StoryZone): StoryZone {
  const p = z.preserve;
  return p ? { ...z, preserve: preserveOf(z, p.below, p.above, p.takenAt) } : z;
}

/** A newly painted Preserve zone's range (its `below` / `above`, checked against its size); nothing for other kinds. */
export function preserveZoneFields(
  body: ZoneAdd,
  kind: string,
  area: StoryArea,
  cols: number,
  now: string,
): Pick<StoryZone, 'preserve'> | null {
  if (kind !== 'preserve') return null;
  const below = body.below ?? PRESERVE_DEFAULT.below;
  const above = body.above ?? PRESERVE_DEFAULT.above;
  const big = preserveTooBig(cols, area[1], area[4], below, above);
  if (big) throw bad(big);
  return { preserve: preserveOf({ id: '', kind, area, cells: cols }, below, above, now) };
}

/** A range value: a whole number 0..32 (the schema checks it too; the bot's words). */
function rangeOf(key: 'below' | 'above', v: number | undefined, dflt: number): number {
  if (v === undefined) return dflt;
  if (!Number.isInteger(v) || v < 0 || v > STORY_PRESERVE_MAX_RANGE)
    throw bad(`${key}: a whole number 0..${STORY_PRESERVE_MAX_RANGE}`);
  return v;
}

/**
 * zones preserve: the zone with Preserve switched on (a new snapshot when it was off or `retake`; its range from the request, else
 * its own, else 1 / 3) or off (a zone of kind preserve refuses). A mine zone refuses: it digs its blocks.
 */
export function applyPreserveOp(z: StoryZone, body: PreserveOp, now: string): StoryZone {
  if (z.kind === 'mine') throw bad(PRESERVE_MINE_REFUSED);
  const on = body.on ?? true;
  if (!on && z.kind === 'preserve') throw bad(PRESERVE_ALWAYS_ON);
  if (!on) {
    const rest = { ...z };
    delete rest.preserve;
    return rest;
  }
  return { ...z, preserve: preserveOn(z, body, now) };
}

/** The zone's Preserve switched on: the request's range over its own (else 1 / 3), a new snapshot when it was off or retaken. */
function preserveOn(z: StoryZone, body: PreserveOp, now: string): StoryZonePreserve {
  const cur = z.preserve?.on ? z.preserve : null;
  const below = rangeOf('below', body.below, cur ? cur.below : PRESERVE_DEFAULT.below);
  const above = rangeOf('above', body.above, cur ? cur.above : PRESERVE_DEFAULT.above);
  const big = preserveTooBig(columns(z), z.area[1], z.area[4], below, above);
  if (big) throw bad(big);
  const takenAt = !cur || body.retake ? now : (cur.takenAt ?? now);
  return preserveOf(z, below, above, takenAt);
}

/** What a preserved zone shares with quarries (the quarry digs its columns there): said in the answer. */
export function preserveWarnings(z: StoryZone, zones: readonly StoryZone[]): string[] {
  if (!z.preserve?.on) return [];
  const own = zoneCellSet(z);
  const overlaps = (q: StoryZone) => [...zoneCellSet(q)].some((k) => own.has(k));
  return zones
    .filter((q) => q.kind === 'mine' && q.mode === 'quarry' && q.id !== z.id && overlaps(q))
    .map((q) => `overlaps quarry ${q.name || `mine ${q.id}`}: the quarry digs its columns there (not repaired)`);
}
