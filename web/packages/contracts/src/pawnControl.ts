/** Direct human control of an in-process colony pawn. Session tokens are never audited. */
import { z } from 'zod';
import { IsoDateTime } from './common.js';

const Session = z.string().min(1).max(128);
const Sequence = z.number().int().min(0).max(Number.MAX_SAFE_INTEGER);

export const PawnControlState = z.object({
  x: z.number(), y: z.number(), z: z.number(), yaw: z.number(), pitch: z.number(),
  dim: z.number().int(), hp: z.number(), food: z.number(), heldSlot: z.number().int(), onGround: z.boolean(),
  windowId: z.number().int().optional(),
});
export type PawnControlState = z.infer<typeof PawnControlState>;

const InventoryItem = z.object({ id: z.number().int(), meta: z.number().int(), count: z.number().int() });
export const PawnControlInventory = z.object({
  windowId: z.number().int(), kind: z.string(), title: z.string(), slotCount: z.number().int(),
  containerSlots: z.number().int(), mainStart: z.number().int(), hotbarStart: z.number().int(),
  slots: z.array(InventoryItem.extend({ slot: z.number().int() })), cursor: InventoryItem.nullish(),
});
export type PawnControlInventory = z.infer<typeof PawnControlInventory>;

export const PawnControlActionStatus = z.object({
  seq: Sequence, pending: z.boolean(), ok: z.boolean().nullish(), message: z.string().nullish(),
});

export const PawnControlSession = z.object({
  session: Session, body: z.string(), expiresAt: IsoDateTime,
  inputTimeoutMs: z.number().int().positive(), leaseTimeoutMs: z.number().int().positive(),
  state: PawnControlState, inventory: PawnControlInventory.nullish(),
  action: PawnControlActionStatus.nullish(),
});
export type PawnControlSession = z.infer<typeof PawnControlSession>;

export const PawnControlAcquireRequest = z.object({ requestId: z.string().min(1).max(128) }).strict();
export type PawnControlAcquireRequest = z.infer<typeof PawnControlAcquireRequest>;

export const PawnControlInputRequest = z.object({
  session: Session, seq: Sequence,
  forward: z.number().min(-1).max(1), strafe: z.number().min(-1).max(1),
  yaw: z.number().min(-1_000_000).max(1_000_000), pitch: z.number().min(-90).max(90),
  jump: z.boolean(), sneak: z.boolean(), sprint: z.boolean(), hotbar: z.number().int().min(0).max(8).optional(),
}).strict();
export type PawnControlInputRequest = z.infer<typeof PawnControlInputRequest>;

export const PawnControlActionRequest = z.object({
  session: Session, seq: Sequence,
  kind: z.enum(['attack', 'dig', 'cancel', 'release', 'use', 'inventory', 'inventory_click', 'close_inventory', 'drop']),
  target: z.object({
    x: z.number().int().min(-30_000_000).max(30_000_000),
    y: z.number().int().min(-64).max(4096),
    z: z.number().int().min(-30_000_000).max(30_000_000),
  }).strict().optional(),
  entityId: z.number().int().positive().max(2_147_483_647).optional(),
  face: z.number().int().min(0).max(5).optional(),
  slot: z.union([z.literal(-999), z.number().int().min(0).max(127)]).optional(),
  button: z.union([z.literal(0), z.literal(1)]).optional(), shift: z.boolean().optional(),
  wholeStack: z.boolean().optional(), windowId: z.number().int().min(0).max(127).optional(),
}).strict();
export type PawnControlActionRequest = z.infer<typeof PawnControlActionRequest>;

export const PawnControlActionResponse = z.object({
  ok: z.boolean(), message: z.string(), state: PawnControlState, inventory: PawnControlInventory.nullish(),
  pending: z.boolean().optional(),
});
export type PawnControlActionResponse = z.infer<typeof PawnControlActionResponse>;
export const PawnControlReleaseRequest = z.object({ session: Session }).strict();
export type PawnControlReleaseRequest = z.infer<typeof PawnControlReleaseRequest>;
export const PawnControlReleaseResponse = z.object({ ok: z.literal(true), state: PawnControlState });
export type PawnControlReleaseResponse = z.infer<typeof PawnControlReleaseResponse>;
