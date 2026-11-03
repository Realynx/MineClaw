/**
 * The inventory panel's pure layer: a `BodyInventory` (slots by vanilla `InventoryPlayer` index, the same in b1.8.1 and 1.12.2:
 * 0–8 hotbar, 9–35 main, 36 feet … 39 head, 40 off hand) laid out like the game's inventory screen, plus what changed between
 * two snapshots (for the calm "this slot just changed" pulse) and the glint rule.
 */
import type { BodyInventory, InventorySlot } from '@mineclaw/contracts';

export interface GridCell {
  /** `InventoryPlayer` index of this cell. */
  index: number;
  item: InventorySlot | null;
}

export interface InventoryGrid {
  /** Head, chest, legs, feet (top to bottom, as the game's armour column). */
  armor: [GridCell, GridCell, GridCell, GridCell];
  /** Null on b1.8.1 (no off hand). */
  offhand: GridCell | null;
  /** Three rows of nine: indices 9–17, 18–26, 27–35. */
  main: GridCell[][];
  /** Indices 0–8. */
  hotbar: GridCell[];
  /** The held hotbar index (0–8). */
  held: number;
}

export const ARMOR_LABELS = ['Head', 'Chest', 'Legs', 'Feet'] as const;

/** Slots → screen positions. Out-of-range or duplicate slots are ignored (the last one wins). */
export function inventoryGrid(inv: BodyInventory): InventoryGrid {
  const at = new Map<number, InventorySlot>();
  for (const s of inv.slots) if (s.slot >= 0 && s.slot <= 35) at.set(s.slot, s);
  const cell = (index: number): GridCell => ({ index, item: at.get(index) ?? null });
  const armorCell = (k: number): GridCell => ({ index: 39 - k, item: inv.armor[k] ?? null });
  return {
    armor: [armorCell(0), armorCell(1), armorCell(2), armorCell(3)],
    offhand: inv.version === 'b1.8.1' ? null : { index: 40, item: inv.offhand ?? null },
    main: [0, 1, 2].map((r) => Array.from({ length: 9 }, (_, c) => cell(9 + r * 9 + c))),
    hotbar: Array.from({ length: 9 }, (_, c) => cell(c)),
    held: Math.min(8, Math.max(0, inv.heldSlot | 0)),
  };
}

/** Every item of a snapshot by `InventoryPlayer` index (main, armour, off hand). */
export function itemsByIndex(inv: BodyInventory): Map<number, InventorySlot> {
  const m = new Map<number, InventorySlot>();
  for (const s of inv.slots) m.set(s.slot, s);
  inv.armor.forEach((a, k) => {
    if (a) m.set(39 - k, a);
  });
  if (inv.offhand) m.set(40, inv.offhand);
  return m;
}

const sig = (s: InventorySlot | undefined) => (s ? `${s.id}:${s.meta}:${s.count}:${s.enchanted ? 1 : 0}` : '');

/** Indices whose item, count or damage changed from `prev` to `next` (none when there is no `prev` or the body differs). */
export function changedSlots(prev: BodyInventory | null | undefined, next: BodyInventory): Set<number> {
  const out = new Set<number>();
  if (!prev || prev.name !== next.name) return out;
  const a = itemsByIndex(prev),
    b = itemsByIndex(next);
  for (const k of new Set([...a.keys(), ...b.keys()])) if (sig(a.get(k)) !== sig(b.get(k))) out.add(k);
  return out;
}

/** 1.12.2 items vanilla draws with the glint whatever their NBT: bottle o' enchanting, written book, nether star, enchanted
 * book, end crystal; and the enchanted golden apple (322:1). b1.8.1 has no glint at all. */
const ALWAYS_GLINT = new Set([384, 387, 399, 403, 426]);

export function glints(version: BodyInventory['version'], s: InventorySlot): boolean {
  if (version === 'b1.8.1') return false;
  return s.enchanted || ALWAYS_GLINT.has(s.id) || (s.id === 322 && s.meta > 0);
}

/** 0..1 durability left, or null for items without durability. */
export function durabilityFraction(s: InventorySlot): number | null {
  const d = s.durability;
  if (!d || d.max <= 0) return null;
  return Math.min(1, Math.max(0, d.left / d.max));
}

/** Vanilla's bar colour: green at full, through yellow, to red when nearly broken (`ItemStack` damage bar hue). */
export function durabilityColour(f: number): string {
  return `hsl(${Math.round(f * 120)} 85% 45%)`;
}

/** The tooltip's lines: display name, then id:meta (and the snake name), then durability when it has one. */
export function tooltipLines(s: InventorySlot): string[] {
  const lines = [s.count > 1 ? `${s.display} ×${s.count}` : s.display, `${s.name} · ${s.id}:${s.meta}`];
  if (s.durability) lines.push(`Durability ${s.durability.left} / ${s.durability.max}`);
  if (s.enchanted) lines.push('Enchanted');
  return lines;
}
