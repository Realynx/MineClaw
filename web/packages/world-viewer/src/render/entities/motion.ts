// Per-entity animation state, advanced once per frame from the interpolated positions: the limb-swing filter
// (EntityLivingBase.limbSwing / limbSwingAmount), the body yaw that turns toward the walk direction and keeps the head
// within 75° (renderYawOffset), arm swings, hurt flashes and the death fall (deathTime → up to 90° roll).

export interface Motion {
  x: number;
  y: number;
  z: number;
  /** limbSwing, limbSwingAmount. */
  swing: number;
  amount: number;
  /** Body yaw (degrees, vanilla convention). */
  body: number;
  /** Horizontal speed, blocks/tick (smoothed). */
  speed: number;
  /** ms timestamps (0 = none). */
  attackAt: number;
  /** Off-hand swing, eating start (ms; 0 = none). */
  attackOffAt: number;
  eatAt: number;
  /** Smoothed head yaw (degrees) when the wire sends one apart from the body yaw; NaN = none. */
  head: number;
  hurtAt: number;
  deadAt: number;
  /** Last known health (bots) to detect damage. */
  hp: number;
  /** Stable 0..1 per entity. */
  seed: number;
  lastMs: number;
  /** Frame the state was last touched (entities that vanish are dropped). */
  seen: number;
}

export function newMotion(x: number, y: number, z: number, yaw: number, key: string, now: number): Motion {
  let h = 2166136261;
  for (let i = 0; i < key.length; i++) h = Math.imul(h ^ key.charCodeAt(i), 16777619);
  return { x, y, z, swing: 0, amount: 0, body: yaw, speed: 0, attackAt: 0, attackOffAt: 0, eatAt: 0, head: NaN, hurtAt: 0, deadAt: 0, hp: NaN, seed: (h >>> 0) / 4294967296, lastMs: now, seen: 0 };
}

export const wrapDeg = (a: number) => ((((a + 180) % 360) + 360) % 360) - 180;

/** Swing duration (ticks) of an arm swing, hurt flash and death fall (vanilla: 6, 10, 20). */
export const SWING_TICKS = 6;
export const HURT_TICKS = 10;
export const DEATH_TICKS = 20;

/**
 * Advances `m` to `now` with the entity at (x, y, z) looking along `look` (degrees). `sprinting` speeds the swing up
 * (vanilla gets that from the larger distance per tick; the factor covers glides that undersample it).
 */
export function stepMotion(m: Motion, x: number, y: number, z: number, look: number, now: number, sprinting = false): void {
  const dtMs = Math.max(0, Math.min(250, now - m.lastMs));
  m.lastMs = now;
  const ticks = dtMs / 50;
  const dx = x - m.x, dz = z - m.z;
  m.x = x;
  m.y = y;
  m.z = z;
  if (ticks <= 0) return;
  const dist = Math.hypot(dx, dz);
  // A teleport / snap is not walking.
  const perTick = dist > 4 ? 0 : dist / ticks;
  m.speed += (perTick - m.speed) * (1 - Math.pow(0.5, ticks));
  const target = Math.min(1, m.speed * 4);
  m.amount += (target - m.amount) * (1 - Math.pow(0.6, ticks));
  m.swing += m.amount * ticks * (sprinting ? 1.25 : 1);
  // Body yaw: turn toward the walk direction (walking backwards keeps facing forward), then keep the head within 75°.
  let goal = m.body;
  if (m.speed > 0.02 && dist > 0) {
    goal = (Math.atan2(dz, dx) * 180) / Math.PI - 90;
    if (Math.abs(wrapDeg(goal - look)) > 95) goal += 180;
  }
  m.body += wrapDeg(goal - m.body) * (1 - Math.pow(0.7, ticks));
  const head = wrapDeg(look - m.body);
  if (head > 75) m.body = look - 75;
  else if (head < -75) m.body = look + 75;
  // Idle: the body slowly follows the head (players turn their body after the head).
  else if (m.speed <= 0.02 && Math.abs(head) > 50) m.body += wrapDeg(look - m.body) * (1 - Math.pow(0.9, ticks));
  m.body = wrapDeg(m.body);
}

/** Eating lasts 32 ticks (EntityLivingBase item use of food). */
export const EAT_TICKS = 32;
/** After the 20-tick fall the body lies there, then shrinks away and is hidden (ms after death). */
export const DEATH_SHRINK_MS = 1200, DEATH_GONE_MS = 1700;

/** Progress 0..1 of something that started at `at` (ms) and lasts `ticks`; 0 when not running. */
export function progress(at: number, ticks: number, now: number): number {
  if (!at) return 0;
  const k = (now - at) / (ticks * 50);
  return k >= 1 || k < 0 ? 0 : Math.max(1e-6, k);
}

/** Off-hand swingProgress 0..1. */
export function attackOffProgress(m: Motion, now: number): number {
  const k = progress(m.attackOffAt, SWING_TICKS, now);
  if (!k && m.attackOffAt && now - m.attackOffAt > SWING_TICKS * 50) m.attackOffAt = 0;
  return k;
}

/** Eating progress 0..1. */
export function eatProgress(m: Motion, now: number): number {
  const k = progress(m.eatAt, EAT_TICKS, now);
  if (!k && m.eatAt && now - m.eatAt > EAT_TICKS * 50) m.eatAt = 0;
  return k;
}

/** Size of a dead body (1 while falling and lying, shrinking to 0, then gone). */
export function deathScale(m: Motion, now: number): number {
  if (!m.deadAt) return 1;
  const t = now - m.deadAt;
  return t <= DEATH_SHRINK_MS ? 1 : Math.max(0, 1 - (t - DEATH_SHRINK_MS) / (DEATH_GONE_MS - DEATH_SHRINK_MS));
}

/** swingProgress 0..1 (0 when not swinging). */
export function attackProgress(m: Motion, now: number): number {
  if (!m.attackAt) return 0;
  const k = (now - m.attackAt) / (SWING_TICKS * 50);
  if (k >= 1) {
    m.attackAt = 0;
    return 0;
  }
  return Math.max(0, k);
}

/** Hurt overlay strength 0..1 (red tint while hurt or dying). */
export function hurtAmount(m: Motion, now: number): number {
  if (m.deadAt) return 1;
  if (!m.hurtAt) return 0;
  const k = (now - m.hurtAt) / (HURT_TICKS * 50);
  if (k >= 1) {
    m.hurtAt = 0;
    return 0;
  }
  return 1;
}

/** Death roll in degrees (RenderLivingBase: sqrt((deathTime − 1) / 20 · 1.6) capped at 1, × 90). */
export function deathAngle(m: Motion, now: number): number {
  if (!m.deadAt) return 0;
  const t = (now - m.deadAt) / 50;
  return Math.min(1, Math.sqrt(Math.max(0, t / DEATH_TICKS) * 1.6)) * 90;
}
