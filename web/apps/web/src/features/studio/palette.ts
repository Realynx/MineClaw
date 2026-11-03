/**
 * The block palette: the bot's design palette (the session's `palette`) searched by words ("oak st" → oak stairs), with
 * the default hotbar the studio starts with.
 */
import type { DesignerPaletteEntry } from '@mineclaw/contracts';

export type PaletteEntry = DesignerPaletteEntry;

/** Entries matching every word of the query (prefix of a name word, or the id); name-start matches first. */
export function searchPalette(entries: readonly PaletteEntry[], query: string): PaletteEntry[] {
  const words = query.toLowerCase().replace(/_/g, ' ').split(/\s+/).filter(Boolean);
  if (words.length === 0) return [...entries];
  const scored: Array<[number, number, PaletteEntry]> = [];
  entries.forEach((e, i) => {
    const name = e.name.toLowerCase().replace(/_/g, ' ');
    const parts = name.split(/\s+/);
    let score = 0;
    for (const w of words) {
      if (String(e.id) === w || `${e.id}:${e.meta}` === w) score += 3;
      else if (parts.some((p) => p.startsWith(w))) score += name.startsWith(w) ? 2 : 1;
      else if (name.includes(w)) score += 0.5;
      else return;
    }
    scored.push([score, i, e]);
  });
  return scored.sort((a, b) => b[0] - a[0] || a[1] - b[1]).map((s) => s[2]);
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
