/**
 * What a redstone design starts with in hand: the hotbar of circuit parts, and the palette category the block drawer opens
 * on. Both read the session's own palette, so each game version gets the parts it has.
 */
import type { PaletteEntry } from '../palette';

/** The palette category the block drawer opens on in a redstone design. */
export const REDSTONE_CATEGORY = 'Redstone';

const SLOTS = 9;

/**
 * The parts wanted on the hotbar, most used first, as `[id, meta]` of their palette entries: stone to run dust over and
 * hang torches on, dust, torch, repeater, comparator, lever, button, lamp, piston, sticky piston, pressure plate, redstone
 * block, observer. A version takes the first nine it has (b1.8.1 has no comparator, lamp, redstone block or observer).
 */
const WANTED: ReadonlyArray<readonly [id: number, meta: number]> = [
  [1, 0],
  [55, 0],
  [76, 5],
  [93, 0],
  [149, 0],
  [69, 5],
  [77, 1],
  [123, 0],
  [33, 1],
  [29, 1],
  [70, 0],
  [152, 0],
  [218, 3],
];

/** A redstone design's first hotbar; slots the palette cannot fill with a wanted part take its first other entries. */
export function redstoneHotbar(entries: readonly PaletteEntry[]): Array<PaletteEntry | null> {
  const out: Array<PaletteEntry | null> = [];
  for (const [id, meta] of WANTED) {
    const entry = entries.find((e) => e.id === id && e.meta === meta);
    if (entry && out.length < SLOTS) out.push(entry);
  }
  for (const entry of entries) {
    if (out.length >= SLOTS) break;
    if (!out.includes(entry)) out.push(entry);
  }
  while (out.length < SLOTS) out.push(null);
  return out;
}
