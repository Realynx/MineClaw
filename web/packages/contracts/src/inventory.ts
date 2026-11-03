/**
 * A body's inventory (GET /api/bots/:bot/inventory → bot `GET /api/viewer/bodies/{name}/inventory`), read-only. Every in-process
 * body answers (main, hives, subhive bodies, Story pawns), in both game versions.
 *
 * `slot` is vanilla's `InventoryPlayer` index, the same in b1.8.1 and 1.12.2:
 *   0–8 hotbar · 9–35 main (three rows of nine, top row first) · 36 feet · 37 legs · 38 chest · 39 head · 40 off hand (1.12.2).
 * Player-window mapping on the bot: hotbar i ← window 36+i; main 9–35 ← window 9–35; armour (head..feet) ← window 5..8;
 * off hand ← window 45 (1.12.2 only; b1.8.1's window has 45 slots, so `offhand` is always null there).
 */
import { z } from 'zod';

export const InventoryDurability = z.object({
  max: z.number().int().nonnegative(),
  left: z.number().int().nonnegative(),
});
export type InventoryDurability = z.infer<typeof InventoryDurability>;

export const InventorySlot = z.object({
  slot: z
    .number()
    .int()
    .min(0)
    .max(40)
    .describe('InventoryPlayer index: 0–8 hotbar, 9–35 main, 36–39 feet..head, 40 off hand'),
  id: z.number().int(),
  meta: z.number().int().describe('damage / variant'),
  count: z.number().int().min(0).max(127),
  name: z.string().describe('snake-case item name keeping the variant ("red_wool", "diamond_pickaxe")'),
  display: z.string().describe('what the game calls it (a renamed item: its own name)'),
  durability: InventoryDurability.nullable().describe('damageable items only'),
  enchanted: z.boolean().describe('its NBT makes vanilla draw the glint (always false on b1.8.1)'),
});
export type InventorySlot = z.infer<typeof InventorySlot>;

/** GET /api/bots/:bot/inventory. */
export const BodyInventory = z.object({
  name: z.string(),
  version: z.enum(['b1.8.1', '1.12.2']).nullable().describe("the body's game version (picks the item icons)"),
  heldSlot: z.number().int().min(0).max(8),
  slots: z
    .array(InventorySlot)
    .describe('non-empty hotbar and main slots, in slot order (hotbar 0–8 first, then main 9–35)'),
  armor: z
    .tuple([InventorySlot.nullable(), InventorySlot.nullable(), InventorySlot.nullable(), InventorySlot.nullable()])
    .describe('[head, chest, legs, feet]'),
  offhand: InventorySlot.nullable().describe('1.12.2 only'),
  at: z.number().int().describe('unix ms of the snapshot'),
  online: z.boolean().describe('false = the body is not in game; this is its last known inventory'),
});
export type BodyInventory = z.infer<typeof BodyInventory>;
