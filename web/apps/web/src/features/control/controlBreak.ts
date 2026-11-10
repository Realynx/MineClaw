import type { PawnControlBroke, PawnControlDig } from '@mineclaw/contracts';

/** After a break, the crack stays full until the world shows the cell changed, or this long. */
export const BREAK_HOLD_MS = 600;
/** While digging, a chip (and a hit sound) every this many game ticks. */
export const HIT_EVERY_TICKS = 4;
const TICK_MS = 50;

interface Cell {
  x: number;
  y: number;
  z: number;
}
/** What a frame should draw and play for the pawn's digging. */
export interface BreakFrame {
  /** The crack: the cell and the dig progress 0..1 (1 = the last stage). */
  crack: { x: number; y: number; z: number; progress: number } | null;
  /** A chip off the dug face and a hit sound are due for this dig. */
  hit: PawnControlDig | null;
  /** Broken blocks that left the picture (or whose hold ran out): burst each and play its break sound. */
  bursts: PawnControlBroke[];
}

const at = (c: Cell | null, x: number, y: number, z: number) => !!c && c.x === x && c.y === y && c.z === z;
const same = (a: Cell | null, b: Cell | null) => !!b && at(a, b.x, b.y, b.z);

/**
 * The controlled pawn's dig as the takeover view shows it: the crack, a chip every four ticks and the burst when the block
 * breaks. The server's dig ends as soon as its own world has the block gone, but the picture changes only when the world
 * stream's copy of that change has been meshed, 100 to 500 ms later. So a break holds the full crack until the view
 * reports that cell changed (or 600 ms pass) and then bursts, and the block, its crack and its chips go together. A dig
 * that ends without a break clears at once. Pure: time is passed in.
 */
export class ControlBreak {
  private dim = 0;
  private dig: PawnControlDig | null = null;
  /** The newest dig, kept after it ends, and the progress it reached. */
  private lastDig: Cell | null = null;
  private lastProgress = 0;
  /** The block the server's ray hits (where a block that breaks at once was). */
  private aim: Cell | null = null;
  private hitTick: number | undefined;
  private hitAt = -Infinity;
  private hitDue = false;
  private hold: { broke: PawnControlBroke; since: number; crack: boolean; gone: boolean } | null = null;
  /** The cell of a burst block: a dig the server still reports on it draws no crack. */
  private spent: Cell | null = null;
  /** The newest change the view reported on the dig's or the aim's cell: it can come before the break does. */
  private changed: { x: number; y: number; z: number; at: number } | null = null;
  /** Breaks to burst in the next frame whatever the world says (a newer break took over their hold). */
  private readonly queued: PawnControlBroke[] = [];
  private readonly out: BreakFrame = { crack: null, hit: null, bursts: [] };
  private readonly crack = { x: 0, y: 0, z: 0, progress: 0 };

  reset(): void {
    this.dim = 0;
    this.dig = this.lastDig = this.aim = this.hold = this.changed = this.spent = null;
    this.lastProgress = 0;
    this.hitTick = undefined;
    this.hitAt = -Infinity;
    this.hitDue = false;
    this.queued.length = this.out.bursts.length = 0;
  }

  /** A state frame of the pawn: its dimension, dig, the server's aim and the tick it describes. */
  state(s: { dim: number; dig?: PawnControlDig | null; aim?: Cell | null; tick?: number }, now: number): void {
    if (s.dim !== this.dim) {
      this.reset();
      this.dim = s.dim;
    }
    if (s.aim !== undefined) this.aim = s.aim;
    const dig = s.dig ?? null;
    const fresh = !!dig && !same(dig, this.dig);
    this.dig = dig;
    if (!same(dig, this.spent)) this.spent = null;
    if (!dig) return;
    if (!same(dig, this.lastDig)) this.lastProgress = 0;
    this.lastDig = dig;
    this.lastProgress = Math.max(this.lastProgress, dig.progress);
    // The block is finished and the server's answer is awaited: nothing more is hit.
    if (dig.progress >= 1) return;
    if (fresh) {
      this.hitDue = true;
      this.hitTick = s.tick;
      this.hitAt = now;
    } else if (s.tick !== undefined) {
      const ticks = s.tick - (this.hitTick ?? s.tick);
      if (ticks >= HIT_EVERY_TICKS) this.hitDue = true;
      // Keep the four-tick rhythm when a frame skips ticks; start counting on the first numbered frame.
      this.hitTick = s.tick - (ticks % HIT_EVERY_TICKS);
    } else if (now - this.hitAt >= HIT_EVERY_TICKS * TICK_MS) {
      // A server that does not number its ticks: the same rate on the local clock.
      this.hitDue = true;
      this.hitAt = now;
    }
  }

  /** The pawn broke a block (its counter grew): hold the full crack on it until the view shows it gone. */
  broke(b: PawnControlBroke, now: number): void {
    // Only one block is held at a time: an older one bursts in the next frame.
    if (this.hold) this.queued.push(this.hold.broke);
    // A block that broke at once never had a crack; it still waits to burst with the change.
    const crack = same(b, this.lastDig) && this.lastProgress >= 0.1;
    const c = this.changed;
    const gone = same(b, c) && now - c!.at < BREAK_HOLD_MS;
    this.hold = { broke: b, since: now, crack, gone };
  }

  /** The view's world changed these cells ([x, y, z, ...]) in dimension `dim`. */
  blocksChanged(dim: number, cells: ReadonlyArray<ArrayLike<number>>, now: number): void {
    if (dim !== this.dim) return;
    const hold = this.hold,
      dig = this.lastDig,
      aim = this.aim;
    for (const c of cells) {
      const x = c[0]!,
        y = c[1]!,
        z = c[2]!;
      if (hold && at(hold.broke, x, y, z)) hold.gone = true;
      else if (at(dig, x, y, z) || at(aim, x, y, z)) this.changed = { x, y, z, at: now };
    }
  }

  /** What to draw and play in the frame at `now`. The result is reused: read it before the next call. */
  frame(now: number): BreakFrame {
    const out = this.out;
    out.crack = out.hit = null;
    out.bursts.length = 0;
    for (const b of this.queued) out.bursts.push((this.spent = b));
    this.queued.length = 0;
    const hold = this.hold;
    if (hold && (hold.gone || now - hold.since >= BREAK_HOLD_MS)) {
      this.hold = null;
      out.bursts.push((this.spent = hold.broke));
    }
    const dig = this.dig;
    if (this.hitDue && dig) out.hit = dig;
    this.hitDue = false;
    const shown = this.hold?.crack ? this.hold.broke : same(dig, this.spent) ? null : dig;
    if (shown) {
      const c = this.crack;
      c.x = shown.x;
      c.y = shown.y;
      c.z = shown.z;
      c.progress = shown === dig ? dig.progress : 1;
      out.crack = c;
    }
    return out;
  }
}

/** Blocks a right click uses instead of placing against (as the bot side's list): sneaking places against them. */
const INTERACTABLE = new Set([
  'chest',
  'trapped_chest',
  'ender_chest',
  'locked_chest',
  'furnace',
  'lit_furnace',
  'crafting_table',
  'dispenser',
  'dropper',
  'hopper',
  'noteblock',
  'jukebox',
  'bed',
  'cake',
  'lever',
  'stone_button',
  'wooden_button',
  'trapdoor',
  'iron_trapdoor',
  'anvil',
  'enchanting_table',
  'brewing_stand',
  'beacon',
  'cauldron',
  'flower_pot',
  'unpowered_repeater',
  'powered_repeater',
  'unpowered_comparator',
  'powered_comparator',
  'daylight_detector',
  'daylight_detector_inverted',
  'command_block',
  'repeating_command_block',
  'chain_command_block',
  'structure_block',
  'dragon_egg',
]);
const interactable = (name: string) =>
  INTERACTABLE.has(name) || name.endsWith('_door') || name.endsWith('fence_gate') || name.endsWith('shulker_box');
/** Air, liquids, fire, tall grass, dead bushes, snow layers and vines: a placement takes their own cell. */
const REPLACEABLE = new Set([0, 8, 9, 10, 11, 31, 32, 51, 78, 106]);
/** Outward normal of each face (0 down, 1 up, 2 north, 3 south, 4 west, 5 east). */
const FACES: ReadonlyArray<readonly [number, number, number]> = [
  [0, -1, 0],
  [0, 1, 0],
  [0, 0, -1],
  [0, 0, 1],
  [-1, 0, 0],
  [1, 0, 0],
];

/**
 * The cell a right click would fill with the held block, for the prediction to stand on before the world says so: the
 * aimed block's neighbour across the aimed face (or the aimed cell itself when it is replaceable). Null when the held
 * item is not a block, the aim is not a block face, the click would use the block instead (a door, a chest; unless
 * sneaking), or the cell overlaps the pawn's body (the game refuses that placement).
 */
export function placedCell(
  aim: { x: number; y: number; z: number; id: number; face: number; name: string } | null,
  held: { id: number; meta: number } | null | undefined,
  sneak: boolean,
  body: { x: number; y: number; z: number },
): { x: number; y: number; z: number; id: number; meta: number } | null {
  if (!aim || !held || held.id <= 0 || held.id > 255 || aim.face < 0 || aim.face > 5) return null;
  if (!sneak && interactable(aim.name)) return null;
  const n = REPLACEABLE.has(aim.id) ? [0, 0, 0] : FACES[aim.face]!;
  const x = aim.x + n[0],
    y = aim.y + n[1],
    z = aim.z + n[2];
  const overlaps =
    body.x + 0.3 > x &&
    body.x - 0.3 < x + 1 &&
    body.y + 1.8 > y &&
    body.y < y + 1 &&
    body.z + 0.3 > z &&
    body.z - 0.3 < z + 1;
  return overlaps ? null : { x, y, z, id: held.id, meta: held.meta };
}
