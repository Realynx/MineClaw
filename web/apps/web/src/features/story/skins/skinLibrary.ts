/** What the Skin Browser shows and selects: the library, the default skins and each pawn's current choice. */
import type { DefaultSkin, PawnSkins, SkinLibraryEntry, SkinRef } from '@mineclaw/contracts';

export const DEFAULT_SKINS: ReadonlyArray<{ id: DefaultSkin; label: string; slim: boolean }> = [
  { id: 'steve', label: 'Steve', slim: false },
  { id: 'alex', label: 'Alex', slim: true },
];

/** The skin a pawn wears (null = the normal default appearance). Account names are case-insensitive. */
export function skinOf(skins: PawnSkins | undefined, pawn: string): SkinRef | null {
  return skins?.assignments[pawn.toLowerCase()] ?? null;
}

export function sameRef(a: SkinRef | null | undefined, b: SkinRef | null | undefined): boolean {
  return (a ?? null) === (b ?? null) || (!!a && !!b && a.kind === b.kind && a.id === b.id);
}

/** A ref's library entry (undefined for a default skin or one deleted meanwhile). */
export function entryOf(skins: PawnSkins | undefined, ref: SkinRef | null): SkinLibraryEntry | undefined {
  return ref?.kind === 'library' ? skins?.library.find((s) => s.id === ref.id) : undefined;
}

/** "Steve", the library label, or "Default appearance". */
export function refLabel(skins: PawnSkins | undefined, ref: SkinRef | null): string {
  if (!ref) return 'Default appearance';
  if (ref.kind === 'default') return DEFAULT_SKINS.find((d) => d.id === ref.id)?.label ?? ref.id;
  return entryOf(skins, ref)?.label || 'Removed skin';
}

/** How many pawns wear each library skin. */
export function wearers(skins: PawnSkins | undefined): Map<string, number> {
  const out = new Map<string, number>();
  for (const ref of Object.values(skins?.assignments ?? {}))
    if (ref.kind === 'library') out.set(ref.id, (out.get(ref.id) ?? 0) + 1);
  return out;
}

/**
 * The library as the browser lists it: filtered by `search` (label, case-insensitive), the pawn's current skin first, then
 * the newest.
 */
export function visibleLibrary(skins: PawnSkins | undefined, search: string, current: SkinRef | null): SkinLibraryEntry[] {
  const q = search.trim().toLowerCase();
  return (skins?.library ?? [])
    .filter((s) => !q || s.label.toLowerCase().includes(q))
    .sort((a, b) => {
      const ca = current?.kind === 'library' && current.id === a.id ? 0 : 1;
      const cb = current?.kind === 'library' && current.id === b.id ? 0 : 1;
      return ca - cb || b.createdAt.localeCompare(a.createdAt) || a.label.localeCompare(b.label);
    });
}

/** Whether a skin fits the colony's version: 64×64 skins show in b1.8.1 too (top half), but 64×32 lacks 1.12.2's layers. */
export function versionNote(height: 32 | 64, version: string | undefined): string | null {
  if (height === 32 && version === '1.12.2') return '64×32: no outer layers or separate left limbs in 1.12.2';
  if (height === 64 && version === 'b1.8.1') return 'b1.8.1 draws the top half only (no outer layers)';
  return null;
}
