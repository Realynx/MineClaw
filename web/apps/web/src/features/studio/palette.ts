/**
 * The block palette: the bot's design palette of the session's version (every placeable state, `DesignerSession.palette`),
 * searched by words ("oak st" → oak stairs, "light blue glass", "stained_glass:lime", "251", "251:5"), filtered by category,
 * with the recent and favourite rows kept per version in the browser and the default hotbar the studio starts with.
 */
import type { DesignerPaletteEntry } from '@mineclaw/contracts';

export type PaletteEntry = DesignerPaletteEntry;

/** The creative-tab categories, in the palette's order. */
export const CATEGORIES = ['Building', 'Decoration', 'Colored', 'Nature', 'Redstone', 'Transport', 'Misc'] as const;

const words = (s: string): string[] => s.toLowerCase().replace(/[_:]/g, ' ').split(/\s+/).filter(Boolean);

/**
 * Entries matching every word of the query: a word of the name or spec (whole word first, then a prefix, then inside for 3+
 * letters), the id, or "id:meta"; best first, palette order among equals. An empty query lists everything.
 */
export function searchPalette(entries: readonly PaletteEntry[], query: string): PaletteEntry[] {
  const q = query.trim().toLowerCase();
  if (!q) return [...entries];
  const raw = q.split(/[\s,]+/).filter(Boolean);
  const terms = raw.flatMap((w) => (/^\d+:\d+$/.test(w) ? [w] : words(w)));
  const scored: Array<[number, number, PaletteEntry]> = [];
  entries.forEach((e, i) => {
    const own = words(`${e.name} ${e.spec ?? ''}`);
    const parts = [...own, ...words(e.cat ?? '')];
    const flat = own.join(''); // inside a word: the name and spec only ("red" is not in "Colored")
    let score = 0;
    for (const w of terms) {
      const s = termScore(e, w, parts, flat);
      if (s === 0) return;
      score += s;
    }
    scored.push([score, i, e]);
  });
  return scored.sort((a, b) => b[0] - a[0] || a[1] - b[1]).map((s) => s[2]);
}

/** How well one query word matches an entry: its id or id:meta, a whole word, a word's prefix, inside the name; 0 = not at all. */
function termScore(e: PaletteEntry, w: string, parts: readonly string[], flat: string): number {
  if (String(e.id) === w || `${e.id}:${e.meta}` === w) return 3;
  if (parts.includes(w)) return 2.2;
  if (parts.some((p) => p.startsWith(w))) return e.name.toLowerCase().startsWith(w) ? 2 : 1;
  return w.length >= 3 && flat.includes(w) ? 0.5 : 0;
}

/** The entries of a category (all of them for null). */
export function inCategory(entries: readonly PaletteEntry[], cat: string | null): PaletteEntry[] {
  return cat ? entries.filter((e) => (e.cat ?? 'Misc') === cat) : [...entries];
}

/** The categories the palette has, with their counts, in `CATEGORIES` order. */
export function categoryCounts(entries: readonly PaletteEntry[]): Array<[string, number]> {
  const n = new Map<string, number>();
  for (const e of entries) n.set(e.cat ?? 'Misc', (n.get(e.cat ?? 'Misc') ?? 0) + 1);
  return CATEGORIES.filter((c) => n.has(c)).map((c) => [c, n.get(c)!]);
}

/** The first hotbar: the building basics, in palette order when present. */
export function defaultHotbar(entries: readonly PaletteEntry[]): Array<PaletteEntry | null> {
  const want: Array<[number, number]> = [
    [4, 0],
    [5, 0],
    [17, 0],
    [20, 0],
    [45, 0],
    [98, 0],
    [53, 0],
    [64, 0],
    [50, 5],
  ];
  const out = want.map(([id, meta]) => entries.find((e) => e.id === id && e.meta === meta) ?? null);
  for (let i = 0; i < out.length; i++) if (!out[i]) out[i] = entries[i] ?? null;
  return out;
}

export const sameEntry = (a: PaletteEntry | null | undefined, b: PaletteEntry | null | undefined): boolean =>
  !!a && !!b && a.id === b.id && a.meta === b.meta;

export const entryKey = (e: { id: number; meta: number }): string => `${e.id}:${e.meta}`;

// ------------------------------------------------------------------ recents and favourites (per version, this browser)

const MAX_RECENT = 12;
const storeKey = (kind: 'recent' | 'fav', version: string) => `mineclaw.studio.${kind}.${version}`;

function readKeys(kind: 'recent' | 'fav', version: string): string[] {
  try {
    const v = JSON.parse(globalThis.localStorage?.getItem(storeKey(kind, version)) ?? '[]') as unknown;
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string').slice(0, 64) : [];
  } catch {
    return [];
  }
}

function writeKeys(kind: 'recent' | 'fav', version: string, keys: readonly string[]): void {
  try {
    globalThis.localStorage?.setItem(storeKey(kind, version), JSON.stringify(keys));
  } catch {
    /* private mode or blocked storage: the rows just don't persist */
  }
}

/** The entries for stored keys, in their stored order (unknown keys dropped: another version's or an old palette's). */
export const entriesOf = (entries: readonly PaletteEntry[], keys: readonly string[]): PaletteEntry[] => {
  const by = new Map(entries.map((e) => [entryKey(e), e] as const));
  return keys.map((k) => by.get(k)).filter((e): e is PaletteEntry => !!e);
};

export function recentKeys(version: string): string[] {
  return readKeys('recent', version);
}

/** Puts an entry first in the recent row (at most 12). */
export function pushRecent(version: string, e: PaletteEntry): string[] {
  const k = entryKey(e);
  const next = [k, ...readKeys('recent', version).filter((x) => x !== k)].slice(0, MAX_RECENT);
  writeKeys('recent', version, next);
  return next;
}

export function favouriteKeys(version: string): string[] {
  return readKeys('fav', version);
}

/** Stars or un-stars an entry. */
export function toggleFavourite(version: string, e: PaletteEntry): string[] {
  const k = entryKey(e);
  const cur = readKeys('fav', version);
  const next = cur.includes(k) ? cur.filter((x) => x !== k) : [...cur, k];
  writeKeys('fav', version, next);
  return next;
}
