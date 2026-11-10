import type { EntityEvent } from '@mineclaw/world-viewer';

/**
 * Sounds of the controlled pawn's own actions. Game servers leave the acting player out of its own dig, break, place and
 * step sounds (its game client makes them), so the takeover view makes them here and plays them through the
 * Controller's sound player, which resolves `block.<material>.<kind>` in the imported pack.
 */
export type SoundMaterial = 'stone' | 'wood' | 'grass' | 'sand' | 'gravel' | 'glass' | 'cloth' | 'snow';
export type BlockSoundKind = 'hit' | 'break' | 'place' | 'step';

const WOOD = new Set([5, 17, 25, 47, 53, 54, 58, 64, 68, 85, 96, 107, 125, 126, 134, 135, 136, 146, 162, 163, 164]);
const GRASS = new Set([2, 3, 6, 18, 31, 32, 37, 38, 39, 40, 59, 60, 86, 91, 103, 110, 111, 161, 175]);
const GLASS = new Set([20, 79, 95, 102, 160]);

/** The sound material of a block id: the same table as the break sound the bot side reports (MinecraftClient.Sounds.cs). */
export function soundMaterial(id: number): SoundMaterial {
  if (WOOD.has(id)) return 'wood';
  if (GRASS.has(id)) return 'grass';
  if (id === 12 || id === 88) return 'sand';
  if (id === 13) return 'gravel';
  if (GLASS.has(id)) return 'glass';
  if (id === 35 || id === 171) return 'cloth';
  if (id === 78 || id === 80) return 'snow';
  return 'stone';
}

/** Vanilla's volume and pitch for each kind (the block's sound type at its usual volume 1 and pitch 1). */
const LEVEL: Record<BlockSoundKind, { volume: number; pitch: number }> = {
  hit: { volume: 0.25, pitch: 0.5 },
  break: { volume: 1, pitch: 0.8 },
  place: { volume: 1, pitch: 0.8 },
  step: { volume: 0.15, pitch: 1 },
};

/** One sound event for block `id`, at the middle of cell (x, y, z) (a step: at the feet, x, y, z as given). `at` is
 * Unix ms: the player drops events older than 1.5 s. */
export function blockSound(
  kind: BlockSoundKind,
  id: number,
  dim: number,
  x: number,
  y: number,
  z: number,
  at: number,
): EntityEvent {
  const centre = kind === 'step' ? 0 : 0.5;
  return {
    kind: 'sound',
    sound: `block.${soundMaterial(id)}.${kind}`,
    ...LEVEL[kind],
    dim,
    x: x + centre,
    y: y + centre,
    z: z + centre,
    at,
  };
}

/** Vanilla: a step each time the distance walked, times 0.6, passes the next whole number: every 1.67 blocks. */
export const STEP_DISTANCE = 1 / 0.6;
/** A jump between two frames longer than this is a teleport or a snap, not walking. */
const MAX_STRIDE = 2;

/** Counts the predicted body's walk into footsteps. A sneaking pawn on the ground and a pawn in the air make none. */
export class Footsteps {
  private lastX = NaN;
  private lastZ = NaN;
  private walked = 0;

  reset(): void {
    this.lastX = this.lastZ = NaN;
    this.walked = 0;
  }

  /** The body is now at (x, z): true when this move completes a step. */
  move(x: number, z: number, onGround: boolean, sneaking: boolean): boolean {
    const d = Math.hypot(x - this.lastX, z - this.lastZ);
    this.lastX = x;
    this.lastZ = z;
    if (Number.isNaN(d)) return false;
    if (d > MAX_STRIDE || !onGround || sneaking) return false;
    this.walked += d;
    if (this.walked < STEP_DISTANCE) return false;
    this.walked %= STEP_DISTANCE;
    return true;
  }
}

/** How long the same break or place at one cell counts as one event, from whichever source played it first. */
export const ECHO_MS = 1500;
const ECHO = /^block\.[a-z_]+\.(break|place)$/;

/**
 * One break or place, heard once. The takeover makes its pawn's break and place sounds itself, while every other pawn near
 * it is told of the same event by the game server (a break effect; a named place sound on 1.12.2) and reports it into the
 * world stream with its own time. The two never match on time or material, so they are matched on dimension, kind and cell
 * within {@link ECHO_MS}: whichever comes second is dropped.
 */
export class SoundEchoes {
  private readonly heard = new Map<string, { at: number; own: boolean }>();

  /** The pawn's own sounds about to play, less those the stream already played. */
  own(events: EntityEvent[], now: number): EntityEvent[] {
    return this.pass(events, now, true);
  }

  /** The world stream's sounds about to play, less the echoes of the pawn's own. */
  stream(events: EntityEvent[], now: number): EntityEvent[] {
    return this.pass(events, now, false);
  }

  reset(): void {
    this.heard.clear();
  }

  private pass(events: EntityEvent[], now: number, own: boolean): EntityEvent[] {
    let kept: EntityEvent[] | null = null;
    for (let i = 0; i < events.length; i++) {
      const e = events[i]!;
      const key = echoKey(e);
      let drop = false;
      if (key) {
        const seen = this.heard.get(key);
        if (seen && now - seen.at < ECHO_MS) {
          // The stream may deliver its own event more than once; the player drops those repeats itself.
          drop = seen.own !== own;
        } else {
          this.prune(now);
          this.heard.set(key, { at: now, own });
        }
      }
      if (drop) kept ??= events.slice(0, i);
      else kept?.push(e);
    }
    return kept ?? events;
  }

  private prune(now: number): void {
    if (this.heard.size < 64) return;
    for (const [key, seen] of this.heard) if (now - seen.at >= ECHO_MS) this.heard.delete(key);
  }
}

function echoKey(e: EntityEvent): string | null {
  if (
    e.kind !== 'sound' ||
    typeof e.sound !== 'string' ||
    typeof e.x !== 'number' ||
    typeof e.y !== 'number' ||
    typeof e.z !== 'number'
  )
    return null;
  const kind = ECHO.exec(e.sound)?.[1];
  if (!kind) return null;
  return `${e.dim ?? 0}|${kind}|${Math.floor(e.x)},${Math.floor(e.y)},${Math.floor(e.z)}`;
}
