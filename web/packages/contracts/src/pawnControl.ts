/** Direct human control of an in-process colony pawn. Session tokens are never audited. */
import { z } from 'zod';
import { IsoDateTime } from './common.js';

const Session = z.string().min(1).max(128);
const Sequence = z.number().int().min(0).max(Number.MAX_SAFE_INTEGER);

// The supervisor writes all of this in snake_case: test/fixtures/pawn-control-frame.json is one state frame with
// every field, as it travels.

/** A block cell, in world coordinates. */
const cell = { x: z.number().int(), y: z.number().int(), z: z.number().int() };
/** The side of a cell the pawn's ray enters by: 0..5 = -Y, +Y, -Z, +Z, -X, +X; -1 = the ray starts inside the
 * block's box. */
const HitFace = z.number().int().min(-1).max(5);
const blockType = { id: z.number().int(), meta: z.number().int() };

/** Potion amplifiers that change how the pawn moves: 0 = level I, -1 = not active. */
export const PawnControlEffects = z.object({
  speed: z.number().int(), slowness: z.number().int(), jumpBoost: z.number().int(),
});
export type PawnControlEffects = z.infer<typeof PawnControlEffects>;
/** The block the pawn is digging. `progress` runs from 0 to 1 and stays at 1 between the finish packet and the game
 * server's answer; `id` and `meta` are the block's. */
export const PawnControlDig = z.object({ ...cell, face: HitFace, progress: z.number().min(0).max(1), ...blockType });
export type PawnControlDig = z.infer<typeof PawnControlDig>;
/** The block the pawn's own ray hits for the newest input it has applied, within the session's `reach`. */
export const PawnControlAim = z.object({ ...cell, face: HitFace });
export type PawnControlAim = z.infer<typeof PawnControlAim>;
/** Blocks this takeover has broken. `count` only grows; the cell, `id` and `meta` describe the newest one as it was. */
export const PawnControlBroke = z.object({ count: z.number().int(), ...cell, ...blockType });
export type PawnControlBroke = z.infer<typeof PawnControlBroke>;

export const PawnControlState = z.object({
  x: z.number(), y: z.number(), z: z.number(), yaw: z.number(), pitch: z.number(),
  dim: z.number().int(), hp: z.number(), food: z.number(), heldSlot: z.number().int(), onGround: z.boolean(),
  windowId: z.number().int().optional(),
  // Everything below is optional. An older server sends none of it. A state that is not the sample of one fresh game
  // tick (riding, waiting for a teleport, dead) has no tick, ackSeq, ackWaitMs or velocity, and says stopped.
  /** The game client's tick counter (one tick = 50 ms): position, velocity and onGround are as that tick left them. */
  tick: z.number().int().optional(),
  /** `seq` of the newest input frame that tick used (negative = none), and the ms that input waited for the tick. */
  ackSeq: z.number().int().optional(), ackWaitMs: z.number().optional(),
  /** Velocity after that tick, in blocks per tick. */
  vx: z.number().optional(), vy: z.number().optional(), vz: z.number().optional(),
  /** Running counts of server teleports and of knockbacks received. A changed `teleports` means the pawn was moved
   * (snap to it); a changed `impulses` means its velocity was set from outside (adopt vx, vy, vz). */
  teleports: z.number().int().optional(), impulses: z.number().int().optional(),
  /** Eating, drinking, drawing a bow or blocking: 1.12.2 slows the pawn and stops its sprint meanwhile. */
  usingItem: z.boolean().optional(),
  /** The pawn is not following the browser's keys (the input deadman fired, the server stopped it, or no fresh tick
   * exists): prediction pauses and the camera follows the pawn. */
  stopped: z.boolean().optional(),
  effects: PawnControlEffects.nullish(),
  /** Absent or null = not digging. */
  dig: PawnControlDig.nullish(),
  /** null = the ray hits nothing within reach; absent = not reported, so the browser keeps its own pick. */
  aim: PawnControlAim.nullish(),
  /** Absent or null = nothing broken yet. */
  broke: PawnControlBroke.nullish(),
  /** Armour points (0..20), and the air supply in ticks (300 = full, 0 or less = drowning). */
  armor: z.number().optional(), air: z.number().optional(),
});
export type PawnControlState = z.infer<typeof PawnControlState>;

const InventoryItem = z.object({ id: z.number().int(), meta: z.number().int(), count: z.number().int() });
export const PawnControlInventory = z.object({
  windowId: z.number().int(), kind: z.string(), title: z.string(), slotCount: z.number().int(),
  containerSlots: z.number().int(), mainStart: z.number().int(), hotbarStart: z.number().int(),
  slots: z.array(InventoryItem.extend({ slot: z.number().int() })), cursor: InventoryItem.nullish(),
  /** The body's inventory version this snapshot is at least as new as; of two inventories the higher version wins. */
  version: z.number().int().optional(),
});
export type PawnControlInventory = z.infer<typeof PawnControlInventory>;

/** How a finished action ended, as `code` beside the human `message`: `dug` = the block broke; `placed` = the game server
 * filled the cell a held block went into (a right click that only sent the block has no code); `aim_changed` = a dig
 * stopped because the aim, the held item or the input changed; `cancelled` = a newer action or a release replaced it.
 * `code` is a plain string on the wire: a server may send others, and a browser treats one it does not know as none. */
export const PAWN_CONTROL_ACTION_CODES = ['dug', 'aim_changed', 'cancelled', 'placed'] as const;
export type PawnControlActionCode = (typeof PAWN_CONTROL_ACTION_CODES)[number];

export const PawnControlActionStatus = z.object({
  seq: Sequence, pending: z.boolean(), ok: z.boolean().nullish(), message: z.string().nullish(),
  code: z.string().nullish(),
});
export type PawnControlActionStatus = z.infer<typeof PawnControlActionStatus>;

export const PawnControlSession = z.object({
  session: Session, body: z.string(), expiresAt: IsoDateTime,
  inputTimeoutMs: z.number().int().positive(), leaseTimeoutMs: z.number().int().positive(),
  state: PawnControlState, inventory: PawnControlInventory.nullish(),
  action: PawnControlActionStatus.nullish(),
  /** How far the pawn's dig and use ray reaches, in blocks. Absent on an older server. */
  reach: z.number().positive().optional(),
});
export type PawnControlSession = z.infer<typeof PawnControlSession>;

/** Why the server ended a takeover: `reason` beside `message` on the control socket's closing `error` frame. `died` =
 * the pawn died (it respawns by itself); `expired` = the lease ran out without input; `replaced` = another connection
 * took the session; `offline` = the pawn left the game or the colony. Also a plain string on the wire. */
export const PAWN_CONTROL_END_REASONS = ['died', 'expired', 'replaced', 'offline'] as const;
export type PawnControlEndReason = (typeof PAWN_CONTROL_END_REASONS)[number];

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
  /** True on the re-send of a held `attack` (every 250 ms): a dig carries on, an entity is not hit a second time. */
  repeat: z.boolean().optional(),
}).strict();
export type PawnControlActionRequest = z.infer<typeof PawnControlActionRequest>;

export const PawnControlActionResponse = z.object({
  ok: z.boolean(), message: z.string(), state: PawnControlState, inventory: PawnControlInventory.nullish(),
  pending: z.boolean().optional(), code: z.string().nullish(),
});
export type PawnControlActionResponse = z.infer<typeof PawnControlActionResponse>;
export const PawnControlReleaseRequest = z.object({ session: Session }).strict();
export type PawnControlReleaseRequest = z.infer<typeof PawnControlReleaseRequest>;
export const PawnControlReleaseResponse = z.object({ ok: z.literal(true), state: PawnControlState });
export type PawnControlReleaseResponse = z.infer<typeof PawnControlReleaseResponse>;
