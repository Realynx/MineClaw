import type { PlayerInput } from './playerInput';

export interface ControlBox { minX: number; minY: number; minZ: number; maxX: number; maxY: number; maxZ: number }
export interface ControlTerrain {
  boxes: readonly ControlBox[]; loaded: boolean;
  medium?: 'water' | 'lava' | 'web' | 'ladder' | null;
  slipperiness?: number;
}
export type ControlCollision = (box: ControlBox, dim: number) => ControlTerrain;
export interface ControlBody {
  x: number; y: number; z: number; dim: number;
  vx: number; vy: number; vz: number; onGround: boolean; height: number; jumpCooldown: number;
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

/** Swept player AABB, vertical then horizontal, with vanilla's 0.6-block step and sneak edge protection. */
export function moveControlBody(body: ControlBody, dx: number, dy: number, dz: number, query: ControlCollision,
  options: { step?: boolean; sneak?: boolean } = {}): boolean {
  const start = bodyBox(body), step = options.step ? 0.6 : 0;
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
  return true;
}

/** Fractional form of the client's 50ms velocity/drag recurrence. Keeps rendering independent of display FPS. */
function travel(velocity: number, acceleration: number, drag: number, ticks: number) {
  const steady = acceleration * drag / (1 - drag), decay = drag ** ticks;
  return { distance: (velocity - steady) * (1 - decay) / (1 - drag) + ticks * acceleration / (1 - drag),
    velocity: steady + (velocity - steady) * decay };
}

/** Conservative local visual physics. Server snapshots remain authoritative for effects, impulses and unmodelled mechanics. */
export function advanceControlBody(body: ControlBody, input: PlayerInput, ms: number, query: ControlCollision, food = 20, speedScale = 1): boolean {
  const terrain = query(bodyBox(body), body.dim);
  if (!terrain.loaded) return false;
  const ticks = ms / 50, liquid = terrain.medium === 'water' || terrain.medium === 'lava';
  const wantedHeight = input.sneak ? 1.65 : 1.8;
  if (wantedHeight !== body.height) {
    const taller = { ...bodyBox(body), maxY: body.y + wantedHeight }, space = query(taller, body.dim);
    if (space.loaded && !space.boxes.some((box) => intersects(taller, box))) body.height = wantedHeight;
  }
  body.jumpCooldown = input.jump ? Math.max(0, body.jumpCooldown - ms) : 0;
  const sprint = input.sprint && input.forward >= 0.8 && !input.sneak && food > 6;
  const yaw = input.yaw * Math.PI / 180;
  if (input.jump && body.onGround && !liquid && body.jumpCooldown === 0) {
    body.vy = 0.42; body.jumpCooldown = 500;
    if (sprint) { body.vx -= Math.sin(yaw) * 0.2; body.vz += Math.cos(yaw) * 0.2; }
    body.onGround = false;
  }
  const factor = (input.sneak ? 0.3 : 1) * 0.98 / Math.max(1, Math.hypot(input.forward, input.strafe));
  const forward = input.forward * factor, strafe = input.strafe * factor;
  const drag = liquid ? terrain.medium === 'water' ? 0.8 : 0.5 : body.onGround ? (terrain.slipperiness ?? 0.6) * 0.91 : 0.91;
  const acceleration = liquid ? 0.02 : body.onGround ? (sprint ? 0.13 : 0.1) * speedScale * 0.16277136 / drag ** 3 : sprint ? 0.026 : 0.02;
  const x = travel(body.vx, (strafe * Math.cos(yaw) - forward * Math.sin(yaw)) * acceleration, drag, ticks);
  const z = travel(body.vz, (forward * Math.cos(yaw) + strafe * Math.sin(yaw)) * acceleration, drag, ticks);
  // Vertical recurrence differs: land applies gravity before drag; liquid after drag (jump adds swim acceleration).
  const verticalDrag = liquid ? drag : 0.98, gravity = liquid ? 0.02 / verticalDrag : 0.08;
  const y = travel(body.vy, -gravity + (liquid && input.jump ? 0.04 : 0), verticalDrag, ticks);
  let dy = body.onGround && body.vy <= 0 ? -0.01 : y.distance + gravity * ticks;
  if (terrain.medium === 'ladder') {
    x.distance = Math.max(-0.15 * ticks, Math.min(0.15 * ticks, x.distance));
    z.distance = Math.max(-0.15 * ticks, Math.min(0.15 * ticks, z.distance));
    dy = input.sneak ? Math.max(0, dy) : Math.max(-0.15 * ticks, dy);
    if (input.jump || input.forward) dy = 0.2 * ticks;
  }
  body.vx = x.velocity; body.vy = y.velocity; body.vz = z.velocity;
  const web = terrain.medium === 'web';
  if (!moveControlBody(body, x.distance * (web ? 0.25 : 1), dy * (web ? 0.05 : 1), z.distance * (web ? 0.25 : 1), query,
    { step: true, sneak: input.sneak })) { body.vx = body.vy = body.vz = 0; return false; }
  if (web) body.vx = body.vy = body.vz = 0;
  return true;
}
