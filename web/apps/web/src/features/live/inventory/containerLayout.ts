/**
 * The container panel's pure layer: a `ContainerView` (slots by the container's own index) laid out like the game's screen for
 * that block — rows of nine for chests (27 / 54) and shulker boxes, 3×3 for dispensers and droppers, one row of five for a
 * hopper, the furnace's input / fuel / output and the brewing stand's three bottles, ingredient and fuel — plus the freshness
 * line ("seen 3 min ago by maid_chan_102") and the block ids that count as containers in every viewer.
 */
import type { ContainerKind, ContainerSlot, ContainerView, InventorySlot } from '@mineclaw/contracts';
import type { GridCell } from './grid';

/** Block ids whose contents the bot can know (chest, trapped chest, furnace and lit furnace, dispenser, dropper, hopper, ender
 * chest, brewing stand, the 16 shulker boxes). Ids are the same in b1.8.1 and 1.12.2. */
export const CONTAINER_BLOCK_IDS: ReadonlySet<number> = new Set([
  54,
  146,
  61,
  62,
  23,
  158,
  154,
  130,
  117,
  ...Array.from({ length: 16 }, (_, i) => 219 + i),
]);

export const isContainerBlock = (id: number | null | undefined): boolean =>
  id !== null && id !== undefined && CONTAINER_BLOCK_IDS.has(id);

export const CONTAINER_TITLES: Record<ContainerKind, string> = {
  chest: 'Chest',
  trapped_chest: 'Trapped chest',
  double_chest: 'Large chest',
  furnace: 'Furnace',
  hopper: 'Hopper',
  dispenser: 'Dispenser',
  dropper: 'Dropper',
  shulker_box: 'Shulker box',
  ender_chest: 'Ender chest',
  brewing_stand: 'Brewing stand',
};

export type ContainerLayout =
  | { shape: 'rows'; rows: GridCell[][] }
  | { shape: 'furnace'; input: GridCell; fuel: GridCell; output: GridCell }
  | { shape: 'brewing'; bottles: [GridCell, GridCell, GridCell]; ingredient: GridCell; fuel: GridCell };

/** A container slot in the inventory grid's slot shape (the panel reuses its slot cell). */
export function asInventorySlot(s: ContainerSlot): InventorySlot {
  return { ...s, durability: s.durability ?? null };
}

/** Slots → screen positions. Out-of-range slots are ignored (the last one of a duplicate wins). */
export function containerLayout(view: Pick<ContainerView, 'kind' | 'size' | 'slots'>): ContainerLayout {
  const at = new Map<number, InventorySlot>();
  const size = Math.max(1, Math.min(54, view.size | 0));
  for (const s of view.slots) if (s.slot >= 0 && s.slot < size) at.set(s.slot, asInventorySlot(s));
  const cell = (index: number): GridCell => ({ index, item: at.get(index) ?? null });
  if (view.kind === 'furnace') return { shape: 'furnace', input: cell(0), fuel: cell(1), output: cell(2) };
  if (view.kind === 'brewing_stand')
    return { shape: 'brewing', bottles: [cell(0), cell(1), cell(2)], ingredient: cell(3), fuel: cell(4) };
  const cols = view.kind === 'dispenser' || view.kind === 'dropper' ? 3 : view.kind === 'hopper' ? 5 : 9;
  const rows: GridCell[][] = [];
  for (let i = 0; i < size; i += cols)
    rows.push(Array.from({ length: Math.min(cols, size - i) }, (_, c) => cell(i + c)));
  return { shape: 'rows', rows };
}

/** "just now", "40 s ago", "3 min ago", "2 h ago", "5 d ago". */
export function agoShort(ms: number, now = Date.now()): string {
  const s = Math.max(0, Math.round((now - ms) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return `${s} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

/** The freshness line under the panel's title. */
export function freshnessText(
  view: Pick<ContainerView, 'source' | 'seenAt' | 'seenBy' | 'exact' | 'partial'>,
  now = Date.now(),
): string {
  const by = view.seenBy ? ` by ${view.seenBy}` : '';
  switch (view.source) {
    case 'unknown':
      return 'Not opened yet by any bot';
    case 'live':
      return `Open right now${by}`;
    case 'story':
      return `Story storage ledger · ${view.seenAt ? agoShort(view.seenAt, now) : 'not counted yet'}`;
    default: {
      const when = view.seenAt ? `Seen ${agoShort(view.seenAt, now)}` : 'Seen before';
      const half = view.partial ? ' · one half only' : '';
      return `${when}${by}${half}`;
    }
  }
}

/** Slots used and items held. */
export function containerTotals(view: Pick<ContainerView, 'slots'>): { used: number; items: number } {
  return { used: view.slots.length, items: view.slots.reduce((n, s) => n + s.count, 0) };
}
