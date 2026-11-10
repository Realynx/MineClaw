import type { PlayerInput } from './playerInput';

export interface ControlBox { minX: number; minY: number; minZ: number; maxX: number; maxY: number; maxZ: number }
export interface ControlTerrain {
  boxes: readonly ControlBox[]; loaded: boolean;
  medium?: 'water' | 'lava' | 'web' | 'ladder' | null;
  slipperiness?: number;
  /** The cell at the centre of the box's bottom holds a ladder (or another block the version climbs). */
  climbable?: boolean;
}
export type ControlCollision = (box: ControlBox, dim: number) => ControlTerrain;
export interface ControlBody {
  x: number; y: number; z: number; dim: number;
  vx: number; vy: number; vz: number; onGround: boolean; height: number; jumpCooldown: number;
  /** The last move was stopped sideways: no sprint, and against a ladder a climb. */
  collidedHorizontally?: boolean;
  /** 1.12.2 keeps the sprint as a state, and moves with the speeds the previous tick left. */
  sprinting?: boolean;
}
/** What the pawn's own state says about how it moves, beside the keys. */
export interface ControlRules {
  /** The game version's rules: 'b1.8.1', or anything else (absent too) for 1.12.2. */
  version?: string | null;
  food?: number;
  /** 1.12.2 potion amplifiers (0 = level I, below 0 = none). */
  effects?: { speed: number; slowness: number; jumpBoost: number } | null;
  /** 1.12.2: eating, drinking, drawing a bow or blocking. */
  usingItem?: boolean;
}
const EPS = 1e-7;
export const bodyBox = (body: ControlBody): ControlBox => ({
  minX: body.x - 0.3, minY: body.y, minZ: body.z - 0.3, maxX: body.x + 0.3, maxY: body.y + body.height, maxZ: body.z + 0.3,
});
const offset = (box: ControlBox, x: number, y: number, z: number): ControlBox => ({
  minX: box.minX + x, minY: box.minY + y, minZ: box.minZ + z, maxX: box.maxX + x, maxY: box.maxY + y, maxZ: box.maxZ + z,
});
const intersects = (a: ControlBox, b: ControlBox) => a.maxX > b.minX + EPS && a.minX < b.maxX - EPS
  && a.maxY > b.minY + EPS && a.minY < b.maxY - EPS && a.maxZ > b.minZ + EPS && a.minZ < b.maxZ - EPS;

function clip(box: ControlBox, boxes: readonly ControlBox[], amount: number, axis: 'X' | 'Y' | 'Z'): number {
  const [a, b] = axis === 'X' ? ['Y', 'Z'] as const : axis === 'Y' ? ['X', 'Z'] as const : ['X', 'Y'] as const;
  for (const solid of boxes) {
    if (box[`max${a}`] <= solid[`min${a}`] + EPS || box[`min${a}`] >= solid[`max${a}`] - EPS
      || box[`max${b}`] <= solid[`min${b}`] + EPS || box[`min${b}`] >= solid[`max${b}`] - EPS) continue;
    if (amount > 0 && box[`max${axis}`] <= solid[`min${axis}`] + EPS) amount = Math.min(amount, solid[`min${axis}`] - box[`max${axis}`]);
    else if (amount < 0 && box[`min${axis}`] >= solid[`max${axis}`] - EPS) amount = Math.max(amount, solid[`max${axis}`] - box[`min${axis}`]);
  }
  return amount;
}

function slide(start: ControlBox, boxes: readonly ControlBox[], x: number, y: number, z: number) {
  y = clip(start, boxes, y, 'Y');
  let box = offset(start, 0, y, 0);
  x = clip(box, boxes, x, 'X'); box = offset(box, x, 0, 0);
  z = clip(box, boxes, z, 'Z'); box = offset(box, 0, 0, z);
  return { box, x, y, z };
}

/** Swept player AABB, vertical then horizontal, with vanilla's step (0.6 blocks, or the height given) and sneak edge
 * protection. */
export function moveControlBody(body: ControlBody, dx: number, dy: number, dz: number, query: ControlCollision,
  options: { step?: boolean | number; sneak?: boolean } = {}): boolean {
  const start = bodyBox(body), step = options.step === true ? 0.6 : options.step || 0;
  const swept = { minX: start.minX + Math.min(0, dx), minY: start.minY + Math.min(-step, dy), minZ: start.minZ + Math.min(0, dz),
    maxX: start.maxX + Math.max(0, dx), maxY: start.maxY + Math.max(step, dy), maxZ: start.maxZ + Math.max(0, dz) };
  const terrain = query(swept, body.dim);
  if (!terrain.loaded) return false;
  const support = (x: number, z: number) => terrain.boxes.some((b) => intersects(offset(start, x, -0.6, z), b));
  const shrink = (n: number) => Math.abs(n) < 0.05 ? 0 : n - Math.sign(n) * 0.05;
  if (options.sneak && body.onGround) {
    while (dx && !support(dx, 0)) dx = shrink(dx);
    while (dz && !support(0, dz)) dz = shrink(dz);
    while (dx && dz && !support(dx, dz)) { dx = shrink(dx); dz = shrink(dz); }
  }
  let moved = slide(start, terrain.boxes, dx, dy, dz);
  if (step && (body.onGround || (dy < 0 && moved.y !== dy)) && (moved.x !== dx || moved.z !== dz)) {
    const up = clip(start, terrain.boxes, step, 'Y');
    const raised = slide(offset(start, 0, up, 0), terrain.boxes, dx, 0, dz);
    const down = clip(raised.box, terrain.boxes, dy - up, 'Y');
    if (raised.x ** 2 + raised.z ** 2 > moved.x ** 2 + moved.z ** 2 + EPS)
      moved = { box: offset(raised.box, 0, down, 0), x: raised.x, y: up + down, z: raised.z };
  }
  body.x = (moved.box.minX + moved.box.maxX) / 2; body.y = moved.box.minY; body.z = (moved.box.minZ + moved.box.maxZ) / 2;
  if (moved.x !== dx) body.vx = 0;
  if (moved.z !== dz) body.vz = 0;
  if (moved.y !== dy) body.vy = 0;
  body.onGround = dy < 0 && moved.y > dy + EPS;
  body.collidedHorizontally = moved.x !== dx || moved.z !== dz;
  return true;
}

/** Fractional form of the client's 50ms velocity/drag recurrence. Keeps rendering independent of display FPS. */
function travel(velocity: number, acceleration: number, drag: number, ticks: number) {
  const steady = acceleration * drag / (1 - drag), decay = drag ** ticks;
  return { distance: (velocity - steady) * (1 - decay) / (1 - drag) + ticks * acceleration / (1 - drag),
    velocity: steady + (velocity - steady) * decay };
}

/** The vertical speed a body at rest keeps: one tick of gravity after the floor stopped it, (0 - 0.08) × 0.98 on land
 * and -0.02 in a liquid. A walk off an edge therefore falls 0.0784 in its first tick, as the game client does. */
const REST_VY = -0.0784, LIQUID_REST_VY = -0.02;
/** On a ladder: sideways and falling speed limits, and the speed after a tick of climbing, (0.2 - 0.08) × 0.98. */
const LADDER_SPEED = 0.15, CLIMB_VY = 0.1176;
const clamp = (v: number, limit: number) => Math.max(-limit, Math.min(limit, v));

/**
 * Local visual physics, the game client's tick (MineClaw.World's PlayerPhysics for b1.8.1, Physics1122 for 1.12.2) in a
 * form that also takes parts of a tick: exact for whole ticks, so the parity fixtures in tests/fixtures/control-parity
 * hold it to the C# client. Server samples stay authoritative for currents, impulses and what is not modelled.
 */
export function advanceControlBody(body: ControlBody, input: PlayerInput, ms: number, query: ControlCollision,
  rules: ControlRules = {}): boolean {
  const terrain = query(bodyBox(body), body.dim);
  if (!terrain.loaded) return false;
  const release = rules.version !== 'b1.8.1';
  const ticks = ms / 50, liquid = terrain.medium === 'water' || terrain.medium === 'lava';
  // b1.8.1 neither shrinks a sneaking player nor waits between jumps.
  const wantedHeight = input.sneak && release ? 1.65 : 1.8;
  if (wantedHeight !== body.height) {
    const taller = { ...bodyBox(body), maxY: body.y + wantedHeight }, space = query(taller, body.dim);
    if (space.loaded && !space.boxes.some((box) => intersects(taller, box))) body.height = wantedHeight;
  }
  body.jumpCooldown = input.jump && release ? Math.max(0, body.jumpCooldown - ms) : 0;
  if (release) {
    // 1.12.2 drops motion below 0.003 before each tick. A part of a tick starting from rest has not reached a tick's
    // speed yet, so the limit shrinks with it (or slow moves, an item in use, would never start).
    const still = 0.003 * Math.min(1, ticks);
    if (Math.abs(body.vx) < still) body.vx = 0;
    if (Math.abs(body.vy) < still) body.vy = 0;
    if (Math.abs(body.vz) < still) body.vz = 0;
  }
  const usingItem = release && rules.usingItem === true, food = rules.food ?? 20;
  const scale = (input.sneak ? 0.3 : 1) * (usingItem ? 0.2 : 1), intent = input.forward * scale;
  let sprint: boolean, sprintSpeed: boolean;
  if (release) {
    const was = body.sprinting === true;
    sprint = was || (input.sprint && intent >= 0.8 && food > 6 && !usingItem);
    if (sprint && (intent < 0.8 || body.collidedHorizontally || food <= 6 || !input.sprint)) sprint = false;
    sprintSpeed = was;
  } else {
    // b1.8.1 ignores hunger here.
    sprint = sprintSpeed = input.sprint && intent >= 0.8 && !body.collidedHorizontally && !input.sneak;
  }
  body.sprinting = sprint;
  const effects = release ? rules.effects : null;
  const potion = effects
    ? (effects.speed >= 0 ? 1 + 0.2 * (effects.speed + 1) : 1) * (effects.slowness >= 0 ? 1 - 0.15 * (effects.slowness + 1) : 1)
    : 1;
  const yaw = input.yaw * Math.PI / 180;
  // The jump tick still moves with the ground's friction and acceleration: the body leaves the ground in the move.
  if (input.jump && body.onGround && !liquid && body.jumpCooldown === 0) {
    body.vy = 0.42 + (effects && effects.jumpBoost >= 0 ? 0.1 * (effects.jumpBoost + 1) : 0);
    body.jumpCooldown = release ? 500 : 0;
    if (sprint) { body.vx -= Math.sin(yaw) * 0.2; body.vz += Math.cos(yaw) * 0.2; }
  }
  // Scaled first (0.98, sneak 0.3, item 0.2), normalised only if still longer than 1: W+A is 1/0.98 faster than W, and
  // sneaking diagonally √2 faster than sneaking straight.
  const k = 0.98 * scale;
  const norm = Math.max(1, Math.hypot(input.forward * k, input.strafe * k));
  const forward = input.forward * k / norm, strafe = input.strafe * k / norm;
  const drag = liquid ? terrain.medium === 'water' ? 0.8 : 0.5 : body.onGround ? (terrain.slipperiness ?? 0.6) * 0.91 : 0.91;
  const land = Math.max(0, (sprintSpeed ? 0.13 : 0.1) * potion);
  const acceleration = liquid ? 0.02 : body.onGround ? land * 0.16277136 / drag ** 3 : sprintSpeed ? 0.026 : 0.02;
  const x = travel(body.vx, (strafe * Math.cos(yaw) - forward * Math.sin(yaw)) * acceleration, drag, ticks);
  const z = travel(body.vz, (forward * Math.cos(yaw) + strafe * Math.sin(yaw)) * acceleration, drag, ticks);
  // Vertical recurrence differs: land applies gravity before drag; liquid after drag (jump adds swim acceleration).
  const verticalDrag = liquid ? drag : 0.98, gravity = liquid ? 0.02 / verticalDrag : 0.08;
  let vy = body.onGround && body.vy <= 0 ? liquid ? LIQUID_REST_VY : REST_VY : body.vy;
  const ladder = !liquid && terrain.climbable === true;
  if (ladder) {
    const sideways = LADDER_SPEED * drag ** ticks;
    x.distance = clamp(x.distance, LADDER_SPEED * ticks); x.velocity = clamp(x.velocity, sideways);
    z.distance = clamp(z.distance, LADDER_SPEED * ticks); z.velocity = clamp(z.velocity, sideways);
    vy = Math.max(vy, -LADDER_SPEED);
    if (input.sneak && vy < 0) vy = 0;
  }
  const y = travel(vy, -gravity + (liquid && input.jump ? 0.04 : 0), verticalDrag, ticks);
  // On a ladder the body moves at its limited speed all tick long (the same for a whole tick).
  const dy = ladder ? vy * ticks : y.distance + gravity * ticks;
  body.vx = x.velocity; body.vy = y.velocity; body.vz = z.velocity;
  const web = terrain.medium === 'web';
  if (!moveControlBody(body, x.distance * (web ? 0.25 : 1), dy * (web ? 0.05 : 1), z.distance * (web ? 0.25 : 1), query,
    { step: release ? 0.6 : 0.5, sneak: input.sneak })) { body.vx = body.vy = body.vz = 0; return false; }
  if (web) body.vx = body.vy = body.vz = 0;
  // Pushing into the ladder's own block climbs, judged where the move ended. A body that touches no ladder cell cannot
  // stand in one, so the second query is only made beside one.
  else if (!liquid && body.collidedHorizontally && (ladder || terrain.medium === 'ladder') && query(bodyBox(body), body.dim).climbable)
    body.vy = CLIMB_VY;
  return true;
}
