/**
 * Failed designations on the map (the director keeps them with why they failed): a calm short label instead of the bot's
 * whole sentence, one label per cluster of adjacent designations that failed for the same reason ("12 plants: busy — claimed"),
 * and a screen-space collision skip so labels never stack. The full reason stays in the hover title and the inspector.
 */

/** A short form of the bot's failure reason: `title` for one designation, `tag` inside a cluster's label. */
export interface ShortReason {
  title: string;
  tag: string;
}

const RULES: Array<[RegExp, ShortReason]> = [
  [
    /another hive bot|hive claim|\bclaimed\b|\bis busy\b|\bbusy:/i,
    { title: 'Busy: claimed by another bot', tag: 'busy — claimed' },
  ],
  [
    /out of reach|can'?t reach|cannot reach|unreachable|no path|not reachable|too far/i,
    { title: 'Out of reach', tag: 'out of reach' },
  ],
  [/protect/i, { title: 'Protected block', tag: 'protected' }],
  [/still there|still standing|reported done/i, { title: "Didn't come down", tag: "didn't come down" }],
  [
    /no (?:pick|axe|shovel|hoe|tool)|needs? an? (?:pickaxe|axe|shovel|hoe|tool)|without a tool/i,
    { title: 'Needs a tool', tag: 'needs a tool' },
  ],
  [/inventory (?:is )?full|no room/i, { title: 'Inventory full', tag: 'inventory full' }],
  [/not loaded|out of (?:its )?view|can'?t see/i, { title: 'Out of view', tag: 'out of view' }],
  [/timed? ?out|took too long/i, { title: 'Timed out', tag: 'timed out' }],
  [/hostile|danger|\bmob\b|fled|fleeing/i, { title: 'Unsafe there', tag: 'unsafe' }],
  [/gone|no longer there|vanished|despawn/i, { title: 'Target gone', tag: 'target gone' }],
];

/** Maps the bot's reason to a short phrase (unknown ones: the first clause, trimmed). */
export function shortReason(reason: string | null | undefined): ShortReason {
  const r = (reason ?? '').trim();
  if (!r) return { title: 'Failed', tag: 'failed' };
  for (const [re, s] of RULES) if (re.test(r)) return s;
  let first = r.split(/[;:(]/)[0]!.trim();
  // "double_plant at 5,64,2 failed" → drop the block name and coordinates: the label sits on the block already.
  first = first.replace(/^[a-z_]+ at -?\d+,-?\d+,-?\d+\s*/i, '').trim() || first;
  if (first.length > 26) first = first.slice(0, 25).trimEnd() + '…';
  const title = first.charAt(0).toUpperCase() + first.slice(1);
  return { title, tag: first.toLowerCase() };
}

const NOUNS: Record<string, [string, string]> = {
  cut: ['plant', 'plants'],
  chop: ['tree', 'trees'],
  harvest: ['crop', 'crops'],
  mine: ['block', 'blocks'],
  deconstruct: ['block', 'blocks'],
  haul: ['item', 'items'],
  hunt: ['animal', 'animals'],
};

/** One failed designation as the map sees it. */
export interface FailedMark {
  id: string | number;
  kind: string;
  reason: string;
  cell: [number, number, number];
  /** 'wait': waiting for a tool, not failing (a neutral label). */
  tone?: 'fail' | 'wait';
}

export interface FailedCluster {
  ids: Array<string | number>;
  kind: string;
  /** The full reason (the first designation's), for the hover title. */
  reason: string;
  short: ShortReason;
  /** "12 plants: busy — claimed", or the short title for one. */
  text: string;
  /** The cluster's middle cell (where its label anchors). */
  anchor: [number, number, number];
  tone?: 'fail' | 'wait';
}

/**
 * Groups failed designations that share a short reason and touch (within `reach` blocks horizontally, 2 vertically) into
 * clusters, largest first.
 */
export function clusterFailed(marks: readonly FailedMark[], reach = 2): FailedCluster[] {
  const groups = new Map<string, FailedMark[]>();
  for (const m of marks) {
    const s = shortReason(m.reason);
    const key = `${m.kind}|${m.tone ?? 'fail'}|${s.tag}`;
    (groups.get(key) ?? groups.set(key, []).get(key)!).push(m);
  }
  const out: FailedCluster[] = [];
  for (const list of groups.values()) {
    const seen = new Set<number>();
    for (let i = 0; i < list.length; i++) {
      if (seen.has(i)) continue;
      seen.add(i);
      const members = [list[i]!];
      for (let q = 0; q < members.length; q++) {
        const a = members[q]!.cell;
        for (let j = 0; j < list.length; j++) {
          if (seen.has(j)) continue;
          const b = list[j]!.cell;
          if (Math.abs(a[0] - b[0]) <= reach && Math.abs(a[2] - b[2]) <= reach && Math.abs(a[1] - b[1]) <= 2) {
            seen.add(j);
            members.push(list[j]!);
          }
        }
      }
      const short = shortReason(members[0]!.reason);
      const kind = members[0]!.kind;
      const [one, many] = NOUNS[kind] ?? ['target', 'targets'];
      const n = members.length;
      const mid = (k: 0 | 1 | 2) => members.reduce((s, m) => s + m.cell[k], 0) / n;
      // The member nearest the middle: the label sits on a real designation.
      const cx = mid(0),
        cy = mid(1),
        cz = mid(2);
      const anchor = members.reduce((best, m) =>
        Math.hypot(m.cell[0] - cx, m.cell[1] - cy, m.cell[2] - cz) <
        Math.hypot(best.cell[0] - cx, best.cell[1] - cy, best.cell[2] - cz)
          ? m
          : best,
      ).cell;
      out.push({
        ids: members.map((m) => m.id),
        kind,
        reason: members[0]!.reason,
        tone: members[0]!.tone ?? 'fail',
        short,
        text: n === 1 ? short.title : `${n} ${n === 1 ? one : many}: ${short.tag}`,
        anchor,
      });
    }
  }
  return out.sort((a, b) => b.ids.length - a.ids.length);
}

/** A label placed on screen (top-centre anchored), or skipped when it would overlap one already placed. */
export interface PlacedLabel<T> {
  item: T;
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Rough label width for 11px semibold text plus the pill's padding. */
export const labelWidth = (text: string) => Math.ceil(text.length * 6.3) + 18;
export const LABEL_HEIGHT = 20;

/**
 * Greedy screen-space placement: in the given order (largest cluster first), a label whose box overlaps one already placed (or
 * leaves the view) is skipped. `at` is the label's top-centre.
 */
export function placeLabels<T>(
  items: ReadonlyArray<{ item: T; at: { x: number; y: number } | null; text: string }>,
  view: { w: number; h: number },
  gap = 4,
): Array<PlacedLabel<T>> {
  const placed: Array<PlacedLabel<T>> = [];
  for (const it of items) {
    if (!it.at) continue;
    const w = labelWidth(it.text);
    const h = LABEL_HEIGHT;
    const x = it.at.x - w / 2;
    const y = it.at.y;
    if (x + w < 0 || y + h < 0 || x > view.w || y > view.h) continue;
    const hit = placed.some(
      (p) => x < p.x + p.w + gap && x + w + gap > p.x && y < p.y + p.h + gap && y + h + gap > p.y,
    );
    if (!hit) placed.push({ item: it.item, x, y, w, h });
  }
  return placed;
}

/** "in 7 min" / "in 40 s" / "now" for a designation's next retry. */
export function retryIn(retryAt: string | number | null | undefined, now = Date.now()): string {
  if (!retryAt) return 'now';
  const ms = (typeof retryAt === 'number' ? retryAt : Date.parse(retryAt)) - now;
  if (!Number.isFinite(ms) || ms <= 0) return 'now';
  return ms >= 90_000 ? `in ${Math.round(ms / 60_000)} min` : `in ${Math.max(1, Math.round(ms / 1000))} s`;
}
