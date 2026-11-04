/**
 * The universal material selector's logic (MaterialBar.tsx): which materials a structure tool takes (from the Architect's
 * palette for the colony's version: tier, lock state and stock; a static fallback until it loads), search, the recent row,
 * keyboard cycling, and the last material per tool (kept per device). Pure, unit-tested.
 */
import { materialKey, STORY_MATERIALS, type StoryArchitectEntry, type StoryMaterial } from '@mineclaw/contracts';
import type { MapTool } from './StoryMap';

/** The structure tools that take a material. */
export const MATERIAL_TOOLS = ['wall', 'floor', 'roof', 'door', 'stairs', 'fence', 'block'] as const;
export type MaterialToolId = (typeof MATERIAL_TOOLS)[number];

export function materialToolOf(tool: MapTool): MaterialToolId | null {
  return tool.kind === 'build' && (MATERIAL_TOOLS as readonly string[]).includes(tool.id)
    ? (tool.id as MaterialToolId)
    : null;
}

export interface MaterialOption {
  /** "id:meta": the material's identity. */
  key: string;
  /** What a build item carries. */
  material: StoryMaterial;
  label: string;
  /** The icon's item. */
  item: [number, number];
  tier: string | null;
  unlocked: boolean;
  reason: string | null;
  have: number;
  craftable: number;
  short: string[];
}

export const matKey = (m: StoryMaterial | null | undefined): string => materialKey(m).join(':');

/** Static choices until the palette loads (and for a bot without one). */
const FALLBACK_SPECIAL: Partial<Record<MaterialToolId, Array<{ label: string; id: number; meta: number }>>> = {
  door: [{ label: 'Oak door', id: 64, meta: 0 }],
  stairs: [
    { label: 'Oak stairs', id: 53, meta: 0 },
    { label: 'Cobblestone stairs', id: 67, meta: 0 },
  ],
  fence: [{ label: 'Oak fence', id: 85, meta: 0 }],
};
const NOT_BUILDING = new Set(['crafting_table', 'furnace', 'chest', 'bed', 'torch']);

/**
 * The materials `tool` takes: the palette entries that list it (`kinds`, else their default `kind`), each material once,
 * unlocked first (the catalogue's order otherwise). Without a palette, the static list with the stockpile counts.
 */
export function materialsFor(
  tool: MaterialToolId,
  palette?: readonly StoryArchitectEntry[] | null,
  stock?: ReadonlyMap<string, number>,
): MaterialOption[] {
  return palette?.length ? paletteMaterials(tool, palette) : fallbackMaterials(tool, stock);
}

/** The palette entries that list the tool, each material once, unlocked first. */
function paletteMaterials(tool: MaterialToolId, palette: readonly StoryArchitectEntry[]): MaterialOption[] {
  const seen = new Set<string>();
  const out: MaterialOption[] = [];
  for (const e of palette) {
    const kinds = e.kinds?.length ? e.kinds : [e.kind];
    const key = `${e.material[0]}:${e.material[1]}`;
    if (!kinds.includes(tool) || seen.has(key)) continue;
    seen.add(key);
    out.push({
      key,
      material: [e.material[0], e.material[1]],
      label: e.label,
      item: [e.item[0], e.item[1]],
      tier: e.tier,
      unlocked: e.unlocked,
      reason: e.reason ?? null,
      have: e.have,
      craftable: e.craftable,
      short: e.short ?? [],
    });
  }
  return [...out.filter((o) => o.unlocked), ...out.filter((o) => !o.unlocked)];
}

/** Without a palette: the static list with the stockpile counts. */
function fallbackMaterials(tool: MaterialToolId, stock?: ReadonlyMap<string, number>): MaterialOption[] {
  const base =
    FALLBACK_SPECIAL[tool] ??
    STORY_MATERIALS.filter((m) => !NOT_BUILDING.has(m.name)).map((m) => ({ label: m.label, id: m.id, meta: m.meta }));
  return base.map((m) => {
    const key = `${m.id}:${m.meta}`;
    const named = STORY_MATERIALS.find((x) => x.id === m.id && x.meta === m.meta)?.name;
    return {
      key,
      material: named ?? [m.id, m.meta],
      label: m.label,
      item: [m.id, m.meta],
      tier: null,
      unlocked: true,
      reason: null,
      have: stock?.get(key) ?? 0,
      craftable: 0,
      short: [],
    };
  });
}

/** Search: every word in the label (case-insensitive). */
export function searchMaterials(list: readonly MaterialOption[], query: string): MaterialOption[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return [...list];
  return list.filter((o) => words.every((w) => o.label.toLowerCase().includes(w)));
}

/** Tab / Shift+Tab, ] / [: the next (or previous) unlocked material after `current`, wrapping; the first one when unknown. */
export function cycleMaterial(
  list: readonly MaterialOption[],
  current: string | null,
  dir: 1 | -1,
): MaterialOption | null {
  const usable = list.filter((o) => o.unlocked);
  if (!usable.length) return null;
  const i = usable.findIndex((o) => o.key === current);
  if (i < 0) return dir === 1 ? usable[0]! : usable[usable.length - 1]!;
  return usable[(i + dir + usable.length) % usable.length]!;
}

/** The material a tool starts with: its last one while still valid and unlocked, else the first unlocked one. */
export function defaultMaterial(
  list: readonly MaterialOption[],
  last: string | null | undefined,
): MaterialOption | null {
  const hit = last ? list.find((o) => o.key === last && o.unlocked) : undefined;
  return hit ?? list.find((o) => o.unlocked) ?? list[0] ?? null;
}

export const RECENT_MAX = 8;

/** Most recent first, each once. */
export function pushRecent(recent: readonly string[], key: string, max = RECENT_MAX): string[] {
  return [key, ...recent.filter((k) => k !== key)].slice(0, max);
}

/** The recent row for a tool: its materials in the order they were last used (number keys 1–5 pick them). */
export function recentFor(list: readonly MaterialOption[], recent: readonly string[], n = 5): MaterialOption[] {
  const out: MaterialOption[] = [];
  for (const k of recent) {
    const o = list.find((x) => x.key === k);
    if (o) out.push(o);
    if (out.length >= n) break;
  }
  return out;
}

/** What the colony has of it, can make, or is short of (one line under its name). */
export function stockText(o: MaterialOption): string {
  if (!o.unlocked) return o.reason ?? 'locked';
  if (o.have > 0) return `have ${o.have}`;
  if (o.craftable > 0) return `craftable ${o.craftable}`;
  if (o.short.length) return `short: ${o.short.join(', ').toLowerCase()}`;
  return o.tier === 'gathered' || o.tier === null ? 'none stored' : '';
}

// ------------------------------------------------------------------ memory (per device)

export interface MaterialMemory {
  /** Per tool: the last material key. */
  last: Partial<Record<MaterialToolId, string>>;
  recent: string[];
}

const STORE_KEY = 'mcx-story-materials';

export function loadMaterialMemory(): MaterialMemory {
  try {
    const raw = globalThis.localStorage?.getItem(STORE_KEY);
    if (!raw) return { last: {}, recent: [] };
    const v = JSON.parse(raw) as Partial<MaterialMemory>;
    const last: MaterialMemory['last'] = {};
    for (const t of MATERIAL_TOOLS) if (typeof v.last?.[t] === 'string') last[t] = v.last[t];
    const recent = Array.isArray(v.recent) ? v.recent.filter((k): k is string => typeof k === 'string') : [];
    return { last, recent: recent.slice(0, RECENT_MAX) };
  } catch {
    return { last: {}, recent: [] };
  }
}

export function saveMaterialMemory(m: MaterialMemory): void {
  try {
    globalThis.localStorage?.setItem(STORE_KEY, JSON.stringify(m));
  } catch {
    // storage unavailable: the choice holds for this visit
  }
}

/** Remembers `key` as `tool`'s material and the most recent one. */
export function rememberMaterial(m: MaterialMemory, tool: MaterialToolId, key: string): MaterialMemory {
  return { last: { ...m.last, [tool]: key }, recent: pushRecent(m.recent, key) };
}
