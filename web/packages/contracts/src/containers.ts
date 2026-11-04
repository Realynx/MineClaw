/**
 * Container contents for every world viewer (chests, furnaces, hoppers…): what the hive knows of a container, read-only.
 * Public `GET /api/world/container?dim&x&y&z` (one container, any of its blocks) and `GET /api/world/containers?dim&x0&z0&x1&z1`
 * (map badges), both → bot `GET /api/world/containers` (the bridge's `world/*` pass-through).
 *
 * Freshness (the bot's ContainerKnowledge): `live` = a body has the window open now; else the newest of the last look any
 * in-process body took (exact slots: on open, slot changes, close), the world memory's item counts (persisted), and Story Mode's
 * storage ledger; a count-only source wins over an exact look only when it is clearly newer (> 10 s) and says something else.
 * `unknown` = nobody looked yet (no slots). Count-only answers are laid out in slot order (`exact: false`).
 *
 * Slot numbers are the container's own: a chest 0–26 (three rows of nine), a double chest 0–53 (the west/north half first),
 * a furnace 0 input · 1 fuel · 2 output, a dispenser/dropper 0–8 (3×3), a hopper 0–4, a brewing stand 0–2 bottles · 3
 * ingredient · 4 fuel.
 */
import { z } from 'zod';
import { DimId, QInt, UnixMs } from './common.js';
import { InventoryDurability } from './inventory.js';

export const CONTAINER_KINDS = [
  'chest',
  'trapped_chest',
  'double_chest',
  'furnace',
  'hopper',
  'dispenser',
  'dropper',
  'shulker_box',
  'ender_chest',
  'brewing_stand',
] as const;
export const ContainerKind = z.enum(CONTAINER_KINDS);
export type ContainerKind = z.infer<typeof ContainerKind>;

export const ContainerSource = z.enum(['live', 'memory', 'story', 'unknown']);
export type ContainerSource = z.infer<typeof ContainerSource>;

export const BlockXYZ = z.tuple([z.number().int(), z.number().int(), z.number().int()]);

/** One non-empty container slot: the inventory slot shape with the container's own slot number. */
export const ContainerSlot = z.object({
  slot: z.number().int().min(0).max(53).describe("the container's slot (chest 0–26, double chest 0–53, furnace 0 in · 1 fuel · 2 out)"),
  id: z.number().int(),
  meta: z.number().int().describe('damage / variant'),
  count: z.number().int().min(0).max(127),
  name: z.string().describe('snake-case item name keeping the variant'),
  display: z.string().describe('what the game calls it'),
  durability: InventoryDurability.nullish().describe('damageable items only'),
  enchanted: z.boolean().describe('its NBT makes vanilla draw the glint (always false on b1.8.1)'),
});
export type ContainerSlot = z.infer<typeof ContainerSlot>;

/** GET /api/world/container?dim&x&y&z: any block of the container (either half of a double chest). */
export const ContainerQuery = z.object({
  dim: QInt.default(0),
  x: QInt,
  y: QInt,
  z: QInt,
});
export type ContainerQuery = z.infer<typeof ContainerQuery>;

/** One container. */
export const ContainerView = z.object({
  pos: BlockXYZ.describe("the container's own cell (a double chest: its west/north half)"),
  dim: DimId,
  kind: ContainerKind,
  size: z.number().int().min(1).max(54),
  slots: z.array(ContainerSlot).describe('non-empty slots in slot order'),
  seenAt: UnixMs.nullish().describe('when that look was taken (live: now); null when unknown'),
  seenBy: z.string().nullish().describe('the body that looked (story: the colony)'),
  source: ContainerSource,
  version: z.enum(['b1.8.1', '1.12.2']).nullish().describe('the game version (picks the item icons)'),
  exact: z.boolean().describe('false = laid out from item counts (the slot order is not known)'),
  cells: z.array(BlockXYZ).describe("the container's blocks (two for a double chest)"),
  partial: z.boolean().nullish().describe('a double chest of which only one half was seen'),
});
export type ContainerView = z.infer<typeof ContainerView>;

/** GET /api/world/containers?dim&x0&z0&x1&z1[&y0&y1&limit]: a box at most 512 blocks wide. */
export const ContainerBoxQuery = z.object({
  dim: QInt.default(0),
  x0: QInt,
  y0: QInt.optional(),
  z0: QInt,
  x1: QInt,
  y1: QInt.optional(),
  z1: QInt,
  limit: QInt.min(1).max(500).optional(),
});
export type ContainerBoxQuery = z.infer<typeof ContainerBoxQuery>;

export const ContainerItemCount = z.object({
  id: z.number().int(),
  meta: z.number().int(),
  name: z.string(),
  count: z.number().int(),
});
export type ContainerItemCount = z.infer<typeof ContainerItemCount>;

/** A known container for map badges. */
export const ContainerSummary = z.object({
  pos: BlockXYZ,
  dim: DimId,
  kind: ContainerKind,
  size: z.number().int(),
  used: z.number().int().describe('slots used'),
  total: z.number().int().describe('items in it'),
  top: z.array(ContainerItemCount).max(5).describe('the five biggest item totals'),
  seenAt: UnixMs.nullish(),
  seenBy: z.string().nullish(),
  source: ContainerSource,
  cells: z.array(BlockXYZ),
});
export type ContainerSummary = z.infer<typeof ContainerSummary>;

export const ContainerList = z.object({
  dim: DimId.nullish(),
  containers: z.array(ContainerSummary).describe('newest look first'),
  capped: z.boolean().describe('more were known than `limit` (default 200, max 500)'),
});
export type ContainerList = z.infer<typeof ContainerList>;
