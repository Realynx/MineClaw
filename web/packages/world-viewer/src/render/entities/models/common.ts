// The everyday models of both versions: bipeds (players, bots, zombies, pigmen, skeletons, endermen), creeper, spiders,
// pig, cow, sheep, chicken, wolf, slime, ghast, squid, and the LOD box. Dimensions, pivots and texture offsets are the
// vanilla ones (1.12.2 model classes; b1.8.1 where its layout differs: `_beta` ids).

import { BIPED_BABY, box, D2R, look, mbox, part, PI, quadBaby, walkLegs } from '../dsl';
import type { CubeDef, ModelDef, PartDef, PartState, PoseCtx, PoseFn } from '../types';

// ------------------------------------------------------------------------------------------------------------ bipeds

/** ModelBiped.setRotationAngles (1.12.2; b1.8.1 is the same minus the swing body twist and riding pose details). */
export function poseBiped(p: Record<string, PartState>, c: PoseCtx): void {
  const head = p.head, body = p.body, ra = p.rightArm, la = p.leftArm, rl = p.rightLeg, ll = p.leftLeg;
  look(head, c.headYaw, c.pitch);
  const f = c.swing, a = c.amount;
  ra.rx = Math.cos(f * 0.6662 + PI) * 2 * a * 0.5;
  la.rx = Math.cos(f * 0.6662) * 2 * a * 0.5;
  rl.rx = Math.cos(f * 0.6662) * 1.4 * a;
  ll.rx = Math.cos(f * 0.6662 + PI) * 1.4 * a;
  if (c.riding) {
    ra.rx -= PI / 5;
    la.rx -= PI / 5;
    rl.rx = ll.rx = -1.4137167;
    rl.ry = PI / 10;
    ll.ry = -PI / 10;
    rl.rz = 0.07853982;
    ll.rz = -0.07853982;
  }
  if (c.holding) ra.rx = ra.rx * 0.5 - PI / 10;
  if (c.attack > 0) {
    const g = c.attack;
    body.ry = Math.sin(Math.sqrt(g) * PI * 2) * 0.2;
    ra.pz = Math.sin(body.ry) * 5;
    ra.px = -Math.cos(body.ry) * 5 + (ra.px + 5); // keep the model's own arm offset (slim arms, endermen)
    la.pz = -Math.sin(body.ry) * 5;
    la.px = Math.cos(body.ry) * 5 + (la.px - 5);
    ra.ry += body.ry;
    la.ry += body.ry;
    la.rx += body.ry;
    let h = 1 - g;
    h *= h;
    h *= h;
    h = 1 - h;
    const s = Math.sin(h * PI), i = Math.sin(g * PI) * -(head.rx - 0.7) * 0.75;
    ra.rx -= s * 1.2 + i;
    ra.ry += body.ry * 2;
    ra.rz += Math.sin(g * PI) * -0.4;
  }
  if (c.attackOff > 0) {
    // The off hand (left arm) mirrors the main-hand swing.
    const g = c.attackOff;
    const twist = -Math.sin(Math.sqrt(g) * PI * 2) * 0.2;
    body.ry += twist;
    la.ry += twist;
    let h = 1 - g;
    h *= h;
    h *= h;
    h = 1 - h;
    la.rx -= Math.sin(h * PI) * 1.2 + Math.sin(g * PI) * -(head.rx - 0.7) * 0.75;
    la.ry += twist * 2;
    la.rz -= Math.sin(g * PI) * -0.4;
  }
  if (c.using && c.bow) {
    // Drawing a bow (ArmPose.BOW_AND_ARROW): both arms forward along the look, the left one across.
    ra.ry = -0.1 + head.ry;
    la.ry = 0.1 + head.ry + 0.4;
    ra.rx = -PI / 2 + head.rx;
    la.rx = -PI / 2 + head.rx;
  } else if (c.eat > 0 || c.using) {
    // Eating / drinking: the hand at the mouth, bobbing (third person: the arm raised toward the head).
    ra.rx = -1.2 - Math.abs(Math.cos(c.age * 0.9)) * 0.15 + head.rx * 0.5;
    ra.ry = -0.35 + head.ry;
    head.rx += Math.abs(Math.sin(c.age * 0.9)) * 0.1;
  }
  if (c.sneaking) {
    body.rx = 0.5;
    ra.rx += 0.4;
    la.rx += 0.4;
    rl.pz += 4;
    ll.pz += 4;
    rl.py -= 3;
    ll.py -= 3;
    head.py += 1;
  }
  ra.rz += Math.cos(c.age * 0.09) * 0.05 + 0.05;
  la.rz -= Math.cos(c.age * 0.09) * 0.05 + 0.05;
  ra.rx += Math.sin(c.age * 0.067) * 0.05;
  la.rx -= Math.sin(c.age * 0.067) * 0.05;
  copyAngles(head, p.hat);
}

function copyAngles(from: PartState, to: PartState | undefined): void {
  if (!to) return;
  to.rx = from.rx;
  to.ry = from.ry;
  to.rz = from.rz;
  to.px = from.px;
  to.py = from.py;
  to.pz = from.pz;
}

/** ModelZombie (1.12.2: arms forward, raised higher while aggressive, swing chop) / b1.8.1 ModelZombie (arms straight). */
export const poseZombie: PoseFn = (p, c) => {
  poseBiped(p, { ...c, holding: false });
  const ra = p.rightArm, la = p.leftArm;
  const f = Math.sin(c.attack * PI), g = Math.sin((1 - (1 - c.attack) * (1 - c.attack)) * PI);
  ra.rz = la.rz = 0;
  ra.ry = -(0.1 - f * 0.6);
  la.ry = 0.1 - f * 0.6;
  const up = c.beta ? -PI / 2 : -PI / (c.aggressive ? 1.5 : 2.25);
  ra.rx = la.rx = up;
  ra.rx += f * 1.2 - g * 0.4;
  la.rx += f * 1.2 - g * 0.4;
  ra.rz += Math.cos(c.age * 0.09) * 0.05 + 0.05;
  la.rz -= Math.cos(c.age * 0.09) * 0.05 + 0.05;
  ra.rx += Math.sin(c.age * 0.067) * 0.05;
  la.rx -= Math.sin(c.age * 0.067) * 0.05;
};

/** 1.12.2 skeletons walk like bipeds (aiming only with a bow drawn); b1.8.1 skeletons hold the zombie pose. */
export const poseSkeleton: PoseFn = (p, c) => {
  if (c.beta) return poseZombie(p, c);
  poseBiped(p, c);
  if (c.aggressive) {
    // Bow drawn (ModelSkeleton with an aiming skeleton): both arms forward toward the head.
    p.rightArm.ry = -0.1 + p.head.ry;
    p.leftArm.ry = 0.1 + p.head.ry + 0.4;
    p.rightArm.rx = p.leftArm.rx = -PI / 2 + p.head.rx;
  }
};

/** ModelEnderman: biped limbs at half swing, clamped to ±0.4; head stays on the long neck. */
export const poseEnderman: PoseFn = (p, c) => {
  poseBiped(p, { ...c, sneaking: false, riding: false });
  const clamp = (v: number) => Math.max(-0.4, Math.min(0.4, v));
  p.body.rx = 0;
  p.rightArm.rx = clamp(p.rightArm.rx * 0.5);
  p.leftArm.rx = clamp(p.leftArm.rx * 0.5);
  p.rightLeg.rx = clamp(p.rightLeg.rx * 0.5);
  p.leftLeg.rx = clamp(p.leftLeg.rx * 0.5);
  // Carrying a block: both arms held out forward (ModelEnderman.isCarrying).
  if (c.carrying) {
    p.rightArm.rx = p.leftArm.rx = -0.5;
    p.rightArm.rz = 0.05;
    p.leftArm.rz = -0.05;
  }
  // The jaw (headwear) copies the head first; a screaming enderman then lifts only the head 5 px: its mouth opens.
  copyAngles(p.head, p.hat);
  if (c.aggressive || c.v.has('screaming')) p.head.py -= 5;
};

interface BipedOpts {
  id: string;
  tex: readonly [number, number];
  legX?: number;
  /** Limb thickness (4; skeletons 2). */
  limb?: number;
  /** Wear (second skin) layers of the 1.12.2 player model; `slim` = Alex arms. */
  wear?: boolean;
  slim?: boolean;
  /** An inflated copy of every part tagged with this layer (stray overlay). */
  overlay?: { layer: string; inflate: number };
  pose: PoseFn;
  height?: number;
}

/** The right and left arm cubes: limb thickness t (skeletons 2), arm width aw (Alex 3); the player model's own left arm
 * (64×64 skin), else the right arm mirrored. */
function bipedArms(o: BipedOpts, t: number, aw: number): [CubeDef[], CubeDef[]] {
  const ra = t === 4 ? [box(o.slim ? -2 : -3, -2, -2, aw, 12, 4, 40, 16)] : [box(-1, -2, -1, 2, 12, 2, 40, 16)];
  if (o.wear) return [ra, [box(-1, -2, -2, aw, 12, 4, 32, 48)]];
  return [ra, t === 4 ? [mbox(-1, -2, -2, 4, 12, 4, 40, 16)] : [mbox(-1, -2, -1, 2, 12, 2, 40, 16)]];
}

function biped(o: BipedOpts): ModelDef {
  const t = o.limb ?? 4, h = t / 2, legX = o.legX ?? 1.9;
  const aw = o.slim ? 3 : t; // arm width
  const armY = o.slim ? 2.5 : 2;
  const over = (name: string, cubes: PartDef['cubes']): PartDef[] =>
    o.overlay ? [part(name + 'Overlay', [0, 0, 0], cubes.map((c) => ({ ...c, inflate: (c.inflate ?? 0) + o.overlay!.inflate })), { layer: o.overlay.layer })] : [];
  const wear = (name: string, u: number, v: number, x: number, y: number, z: number, dx: number, dy: number, dz: number): PartDef[] =>
    o.wear ? [part(name, [0, 0, 0], [box(x, y, z, dx, dy, dz, u, v, 0.25)])] : [];
  const headC = [box(-4, -8, -4, 8, 8, 8, 0, 0)];
  const bodyC = [box(-4, 0, -2, 8, 12, 4, 16, 16)];
  const [raC, laC] = bipedArms(o, t, aw);
  const rlC = [box(-h, 0, -h, t, 12, t, 0, 16)];
  const llC = o.wear ? [box(-2, 0, -2, 4, 12, 4, 16, 48)] : [mbox(-h, 0, -h, t, 12, t, 0, 16)];
  return {
    id: o.id,
    tex: o.tex,
    layers: o.overlay ? [o.overlay.layer] : undefined,
    width: 0.6,
    height: o.height ?? 1.95,
    pose: o.pose,
    baby: BIPED_BABY,
    parts: [
      part('head', [0, 0, 0], headC, { children: over('head', headC) }),
      part('hat', [0, 0, 0], [box(-4, -8, -4, 8, 8, 8, 32, 0, 0.5)]),
      part('body', [0, 0, 0], bodyC, { children: [...over('body', bodyC), ...wear('jacket', 16, 32, -4, 0, -2, 8, 12, 4)] }),
      part('rightArm', [-5, armY, 0], raC, { children: [...over('rightArm', raC), ...wear('rightSleeve', 40, 32, o.slim ? -2 : -3, -2, -2, aw, 12, 4)] }),
      part('leftArm', [5, armY, 0], laC, { children: [...over('leftArm', laC), ...wear('leftSleeve', 48, 48, -1, -2, -2, aw, 12, 4)] }),
      part('rightLeg', [-legX, 12, 0], rlC, { children: [...over('rightLeg', rlC), ...wear('rightPants', 0, 32, -2, 0, -2, 4, 12, 4)] }),
      part('leftLeg', [legX, 12, 0], llC, { children: [...over('leftLeg', llC), ...wear('leftPants', 0, 48, -2, 0, -2, 4, 12, 4)] }),
    ],
  };
}

const playerPose: PoseFn = (p, c) => poseBiped(p, c);

export const PLAYER = biped({ id: 'player', tex: [64, 64], wear: true, pose: playerPose, height: 1.8 });
export const PLAYER_SLIM = biped({ id: 'player_slim', tex: [64, 64], wear: true, slim: true, pose: playerPose, height: 1.8 });
/** b1.8.1 players (char.png 64×32, legs at ±2). */
export const PLAYER_BETA = biped({ id: 'player_beta', tex: [64, 32], legX: 2, pose: playerPose, height: 1.8 });
export const ZOMBIE = biped({ id: 'zombie', tex: [64, 64], pose: poseZombie });
export const ZOMBIE_BETA = biped({ id: 'zombie_beta', tex: [64, 32], legX: 2, pose: poseZombie });
export const SKELETON = biped({ id: 'skeleton', tex: [64, 32], limb: 2, legX: 2, pose: poseSkeleton, height: 1.99, overlay: { layer: 'overlay', inflate: 0.25 } });

export const ENDERMAN: ModelDef = {
  id: 'enderman',
  tex: [64, 32],
  width: 0.6,
  height: 2.9,
  pose: poseEnderman,
  parts: [
    part('head', [0, -13, 0], [box(-4, -8, -4, 8, 8, 8, 0, 0)]),
    part('hat', [0, -13, 0], [box(-4, -8, -4, 8, 8, 8, 0, 16, -0.5)]),
    part('body', [0, -14, 0], [box(-4, 0, -2, 8, 12, 4, 32, 16)]),
    part('rightArm', [-3, -12, 0], [box(-1, -2, -1, 2, 30, 2, 56, 0)]),
    part('leftArm', [5, -12, 0], [mbox(-1, -2, -1, 2, 30, 2, 56, 0)]),
    part('rightLeg', [-2, -5, 0], [box(-1, 0, -1, 2, 30, 2, 56, 0)]),
    part('leftLeg', [2, -5, 0], [mbox(-1, 0, -1, 2, 30, 2, 56, 0)]),
  ],
};

// ------------------------------------------------------------------------------------------------------------ creeper

const poseCreeper: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  walkLegs(p, c.swing, c.amount, ['leg1', 'leg2', 'leg3', 'leg4']);
  // Fusing (Mob16 state 1): the swell, as a breathing scale isn't a part transform — flash instead (entity layer).
};

function creeper(id: string, y: number): ModelDef {
  // The charge aura (ModelCreeper(2.0)): every part again, inflated by 2, tagged 'charge'.
  const p = (name: string, pivot: [number, number, number], c: ReturnType<typeof box>) =>
    part(name, pivot, [c], { children: [part(name + 'Charge', [0, 0, 0], [{ ...c, inflate: 2 }], { layer: 'charge' })] });
  return {
    id,
    tex: [64, 32],
    layers: ['charge'],
    width: 0.6,
    height: 1.7,
    pose: poseCreeper,
    parts: [
      p('head', [0, y, 0], box(-4, -8, -4, 8, 8, 8, 0, 0)),
      p('body', [0, y, 0], box(-4, 0, -2, 8, 12, 4, 16, 16)),
      p('leg1', [-2, y + 12, 4], box(-2, 0, -2, 4, 6, 4, 0, 16)),
      p('leg2', [2, y + 12, 4], box(-2, 0, -2, 4, 6, 4, 0, 16)),
      p('leg3', [-2, y + 12, -4], box(-2, 0, -2, 4, 6, 4, 0, 16)),
      p('leg4', [2, y + 12, -4], box(-2, 0, -2, 4, 6, 4, 0, 16)),
    ],
  };
}

export const CREEPER = creeper('creeper', 6);
/** b1.8.1 ModelCreeper (head at 4, legs at 16). */
export const CREEPER_BETA = creeper('creeper_beta', 4);

// ------------------------------------------------------------------------------------------------------------ spider

const SPIDER_RZ = [-PI / 4, PI / 4, -0.58119464, 0.58119464, -0.58119464, 0.58119464, -PI / 4, PI / 4];
const SPIDER_RY = [PI / 4, -PI / 4, 0.3926991, -0.3926991, -0.3926991, 0.3926991, -PI / 4, PI / 4];

const poseSpider: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  const f = c.swing, a = c.amount;
  const sy = [0, PI, PI / 2, PI * 1.5].map((o) => -(Math.cos(f * 0.6662 * 2 + o) * 0.4) * a);
  const sz = [0, PI, PI / 2, PI * 1.5].map((o) => Math.abs(Math.sin(f * 0.6662 + o) * 0.4) * a);
  for (let i = 0; i < 8; i++) {
    const leg = p['leg' + (i + 1)];
    const k = i >> 1, right = (i & 1) === 0;
    leg.rz = SPIDER_RZ[i] + (right ? sz[k] : -sz[k]);
    leg.ry = SPIDER_RY[i] + (right ? sy[k] : -sy[k]);
  }
};

export const SPIDER: ModelDef = {
  id: 'spider',
  tex: [64, 32],
  width: 1.4,
  height: 0.9,
  pose: poseSpider,
  parts: [
    part('head', [0, 15, -3], [box(-4, -4, -8, 8, 8, 8, 32, 4)]),
    part('neck', [0, 15, 0], [box(-3, -3, -3, 6, 6, 6, 0, 0)]),
    part('body', [0, 15, 9], [box(-5, -4, -6, 10, 8, 12, 0, 12)]),
    ...Array.from({ length: 8 }, (_, i) => {
      const right = (i & 1) === 0;
      return part('leg' + (i + 1), [right ? -4 : 4, 15, 2 - (i >> 1)], [box(right ? -15 : -1, -1, -1, 16, 2, 2, 18, 0)], { rot: [0, SPIDER_RY[i], SPIDER_RZ[i]] });
    }),
  ],
};

// ------------------------------------------------------------------------------------------------------------ quadrupeds

const poseQuadruped: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  p.body.rx = PI / 2;
  walkLegs(p, c.swing, c.amount);
};

function legs(h: number, x: number, zBack: number, zFront: number, uv: [number, number] = [0, 16], inflate = 0, layer?: string): PartDef[] {
  const y = 24 - h;
  return [
    part('leg1', [-x, y, zBack], [box(-2, 0, -2, 4, h, 4, uv[0], uv[1], inflate)], { layer }),
    part('leg2', [x, y, zBack], [box(-2, 0, -2, 4, h, 4, uv[0], uv[1], inflate)], { layer }),
    part('leg3', [-x, y, zFront], [box(-2, 0, -2, 4, h, 4, uv[0], uv[1], inflate)], { layer }),
    part('leg4', [x, y, zFront], [box(-2, 0, -2, 4, h, 4, uv[0], uv[1], inflate)], { layer }),
  ];
}

const pigHead = [box(-4, -4, -8, 8, 8, 8, 0, 0), box(-2, 0, -9, 4, 3, 1, 16, 16)];
const pigBody = [box(-5, -10, -7, 10, 16, 8, 28, 8)];

/** ModelPig (+ the saddle: ModelPig(0.5) with pig_saddle.png, as children so they follow the pose). */
export const PIG: ModelDef = {
  id: 'pig',
  tex: [64, 32],
  baby: quadBaby(4, 4),
  layers: ['saddle'],
  width: 0.9,
  height: 0.9,
  pose: poseQuadruped,
  parts: [
    part('head', [0, 12, -6], pigHead, { children: [part('saddleHead', [0, 0, 0], pigHead.map((c) => ({ ...c, inflate: 0.5 })), { layer: 'saddle' })] }),
    part('body', [0, 11, 2], pigBody, { rot: [PI / 2, 0, 0], children: [part('saddleBody', [0, 0, 0], pigBody.map((c) => ({ ...c, inflate: 0.5 })), { layer: 'saddle' })] }),
    ...legs(6, 3, 7, -5).map((l) => ({ ...l, children: [part(l.name + 'Saddle', [0, 0, 0], l.cubes.map((c) => ({ ...c, inflate: 0.5 })), { layer: 'saddle' })] })),
  ],
};

export const COW: ModelDef = {
  id: 'cow',
  tex: [64, 32],
  baby: quadBaby(8, 6),
  width: 0.9,
  height: 1.4,
  pose: poseQuadruped,
  parts: [
    part('head', [0, 4, -8], [box(-4, -4, -6, 8, 8, 6, 0, 0), box(-5, -5, -4, 1, 3, 1, 22, 0), box(4, -5, -4, 1, 3, 1, 22, 0)]),
    part('body', [0, 5, 2], [box(-6, -10, -7, 12, 18, 10, 18, 4), box(-2, 2, -8, 4, 6, 1, 52, 0)], { rot: [PI / 2, 0, 0] }),
    ...legs(12, 4, 7, -6),
  ],
};

const poseSheep: PoseFn = (p, c) => {
  poseQuadruped(p, c);
  if (c.eat > 0) {
    // EntitySheep eating grass: the head down to the ground, chewing.
    p.head.py += 9 * Math.min(1, c.eat * 4, (1 - c.eat) * 4);
    p.head.rx = PI / 5 + Math.sin(c.age * 2.7) * 0.25 * PI / 5;
  }
};

/** ModelSheep2 + the wool (ModelSheep1 with sheep_fur.png) as tagged children. */
export const SHEEP: ModelDef = {
  id: 'sheep',
  tex: [64, 32],
  baby: quadBaby(8, 4),
  layers: ['wool'],
  width: 0.9,
  height: 1.3,
  pose: poseSheep,
  parts: [
    part('head', [0, 6, -8], [box(-3, -4, -6, 6, 6, 8, 0, 0)], { children: [part('woolHead', [0, 0, 0], [box(-3, -4, -4, 6, 6, 6, 0, 0, 0.6)], { layer: 'wool' })] }),
    part('body', [0, 5, 2], [box(-4, -10, -7, 8, 16, 6, 28, 8)], { rot: [PI / 2, 0, 0], children: [part('woolBody', [0, 0, 0], [box(-4, -10, -7, 8, 16, 6, 28, 8, 1.75)], { layer: 'wool' })] }),
    ...legs(12, 3, 7, -5).map((l) => ({ ...l, children: [part(l.name + 'Wool', [0, 0, 0], [box(-2, 0, -2, 4, 6, 4, 0, 16, 0.5)], { layer: 'wool' })] })),
  ],
};

// ------------------------------------------------------------------------------------------------------------ chicken

const poseChicken: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  p.body.rx = PI / 2;
  p.rightLeg.rx = Math.cos(c.swing * 0.6662) * 1.4 * c.amount;
  p.leftLeg.rx = Math.cos(c.swing * 0.6662 + PI) * 1.4 * c.amount;
  // Wing flap (EntityChicken.wingRotation, fast while falling/flying): a small idle flutter.
  const flap = c.speed > 0.15 ? (Math.sin(c.age * 1.6) + 1) * 0.6 : 0;
  p.rightWing.rz = flap;
  p.leftWing.rz = -flap;
};

export const CHICKEN: ModelDef = {
  id: 'chicken',
  tex: [64, 32],
  baby: quadBaby(5, 2),
  width: 0.4,
  height: 0.7,
  pose: poseChicken,
  parts: [
    part('head', [0, 15, -4], [box(-2, -6, -2, 4, 6, 3, 0, 0)], {
      children: [part('bill', [0, 0, 0], [box(-2, -4, -4, 4, 2, 2, 14, 0)]), part('chin', [0, 0, 0], [box(-1, -2, -3, 2, 2, 2, 14, 4)])],
    }),
    part('body', [0, 16, 0], [box(-3, -4, -3, 6, 8, 6, 0, 9)], { rot: [PI / 2, 0, 0] }),
    part('rightLeg', [-2, 19, 1], [box(-1, 0, -3, 3, 5, 3, 26, 0)]),
    part('leftLeg', [1, 19, 1], [box(-1, 0, -3, 3, 5, 3, 26, 0)]),
    part('rightWing', [-4, 13, 0], [box(0, 0, -3, 1, 4, 6, 24, 13)]),
    part('leftWing', [4, 13, 0], [box(-1, 0, -3, 1, 4, 6, 24, 13)]),
  ],
};

// ------------------------------------------------------------------------------------------------------------ wolf

/** ModelWolf: sitting (Mob16 & 1) folds the legs; the tail rises when tamed, angry or wagging. */
const poseWolf: PoseFn = (p, c) => {
  const n = c.v.isString ? 0 : c.variant;
  const sitting = (n & 1) !== 0 || c.v.has('sitting', 'sit'), angry = (n & 2) !== 0 || c.v.has('angry'), tame = (n & 4) !== 0 || c.v.has('tame', 'tamed');
  p.head.ry = c.headYaw * D2R;
  p.head.rx = c.pitch * D2R;
  p.tail.ry = angry ? 0 : Math.cos(c.swing * 0.6662) * 1.4 * c.amount;
  if (sitting) {
    p.mane.px = -1;
    p.mane.py = 16;
    p.mane.pz = -3;
    p.mane.rx = PI * 0.4;
    p.mane.ry = 0;
    p.body.py = 18;
    p.body.pz = 0;
    p.body.rx = PI / 4;
    p.tail.py = 21;
    p.tail.pz = 6;
    p.leg1.py = p.leg2.py = 22;
    p.leg1.pz = p.leg2.pz = 2;
    p.leg1.rx = p.leg2.rx = (PI * 3) / 2;
    p.leg3.py = p.leg4.py = 17;
    p.leg3.rx = p.leg4.rx = 5.811947;
    p.leg3.px -= 0.01;
    p.leg4.px += 0.01;
  } else {
    p.body.rx = PI / 2;
    p.mane.rx = PI / 2;
    walkLegs(p, c.swing, c.amount);
  }
  p.tail.rx = angry ? 1.5393804 : tame ? 0.55 * PI : PI / 5;
};

function wolf(id: string, dx: number): ModelDef {
  return {
    id,
    tex: [64, 32],
    width: 0.6,
    height: 0.85,
    pose: poseWolf,
    baby: quadBaby(5, 2),
    parts: [
      part('head', [-1, 13.5, -7], [box(-2 + dx, -3, -2, 6, 6, 4, 0, 0), box(-2 + dx, -5, 0, 2, 2, 1, 16, 14), box(2 + dx, -5, 0, 2, 2, 1, 16, 14), box(-0.5 + dx, 0, -5, 3, 3, 4, 0, 10)]),
      part('body', [0, 14, 2], [box(-3 + dx, -2, -3, 6, 9, 6, 18, 14)], { rot: [PI / 2, 0, 0] }),
      part('mane', [-1, 14, dx ? 2 : -3], [box(-3 + dx, -3, -3, 8, 6, 7, 21, 0)], { rot: [PI / 2, 0, 0] }),
      part('leg1', [-2.5, 16, 7], [box(0 + dx, 0, -1, 2, 8, 2, 0, 18)]),
      part('leg2', [0.5, 16, 7], [box(0 + dx, 0, -1, 2, 8, 2, 0, 18)]),
      part('leg3', [-2.5, 16, -4], [box(0 + dx, 0, -1, 2, 8, 2, 0, 18)]),
      part('leg4', [0.5, 16, -4], [box(0 + dx, 0, -1, 2, 8, 2, 0, 18)]),
      part('tail', [-1, 12, 8], [box(0 + dx, 0, -1, 2, 8, 2, 9, 18)], { rot: [PI / 5, 0, 0] }),
    ],
  };
}

export const WOLF = wolf('wolf', 0);
/** b1.8.1 ModelWolf: every box one pixel further toward −x, the mane at z 2. */
export const WOLF_BETA = wolf('wolf_beta', -1);

// ------------------------------------------------------------------------------------------------------------ slime, ghast, squid

/** ModelSlime(16) inner body with eyes and mouth, plus the translucent outer cube (ModelSlime(0)) tagged 'outer'. */
export const SLIME: ModelDef = {
  id: 'slime',
  tex: [64, 32],
  layers: ['outer'],
  width: 0.51,
  height: 0.51,
  pose: () => {},
  parts: [
    part('inner', [0, 0, 0], [box(-3, 17, -3, 6, 6, 6, 0, 16)]),
    part('rightEye', [0, 0, 0], [box(-3.25, 18, -3.5, 2, 2, 2, 32, 0)]),
    part('leftEye', [0, 0, 0], [box(1.25, 18, -3.5, 2, 2, 2, 32, 4)]),
    part('mouth', [0, 0, 0], [box(0, 21, -3.5, 1, 1, 1, 32, 8)]),
    part('outer', [0, 0, 0], [box(-4, 16, -4, 8, 8, 8, 0, 0)], { layer: 'outer' }),
  ],
};

/** Tentacle lengths of ModelGhast (its Random(1660).nextInt(7) + 8 sequence). */
const GHAST_TENTACLES = [8, 13, 9, 11, 11, 10, 12, 9, 12];

export const GHAST: ModelDef = {
  id: 'ghast',
  tex: [64, 32],
  width: 4,
  height: 4,
  scale: 4.5,
  // ModelGhast.render translates 0.6 down in model space.
  offset: [0, 9.6, 0],
  pose: (p, c) => {
    for (let i = 0; i < 9; i++) p['tentacle' + i].rx = 0.2 * Math.sin(c.age * 0.3 + i) + 0.4;
  },
  parts: [
    part('body', [0, 8, 0], [box(-8, -8, -8, 16, 16, 16, 0, 0)]),
    ...GHAST_TENTACLES.map((k, j) => {
      const fx = (((j % 3) - (Math.floor(j / 3) % 2) * 0.5 + 0.25) / 2 * 2 - 1) * 5;
      const fz = ((Math.floor(j / 3) / 2) * 2 - 1) * 5;
      return part('tentacle' + j, [fx, 15, fz], [box(-1, 0, -1, 2, k, 2, 0, 0)]);
    }),
  ],
};

/** ModelSquid; RenderSquid's pitch/roll of the body is not on the wire (upright). Tentacles open and close with age. */
export const SQUID: ModelDef = {
  id: 'squid',
  tex: [64, 32],
  width: 0.8,
  height: 0.8,
  // RenderSquid: translate(0, 0.5) … translate(0, -1.2) (y up, before the flip): 0.7 lower than the usual root.
  offset: [0, 11.2, 0],
  pose: (p, c) => {
    const a = (Math.sin(c.age * 0.25 + c.seed * 6.28) * 0.5 + 0.5) * 0.9 + 0.1;
    for (let i = 0; i < 8; i++) p['tentacle' + i].rx = a;
  },
  parts: [
    part('body', [0, 8, 0], [box(-6, -8, -6, 12, 16, 12, 0, 0)]),
    ...Array.from({ length: 8 }, (_, j) => {
      const d = (j * PI * 2) / 8;
      return part('tentacle' + j, [Math.cos(d) * 5, 15, Math.sin(d) * 5], [box(-1, 0, -1, 2, 18, 2, 48, 0)], { rot: [0, (j * PI * -2) / 8 + PI / 2, 0] });
    }),
  ],
};

// ------------------------------------------------------------------------------------------------------------ the LOD box

/** A unit box (one part, untextured): its bone is the entity's hitbox, so far entities draw as plain coloured boxes. */
export const BOX: ModelDef = {
  id: 'box',
  tex: [16, 16],
  width: 1,
  height: 1,
  parts: [part('box', [0, 0, 0], [box(-0.5, 0, -0.5, 1, 1, 1, 0, 0)])],
};

export const MODELS: ModelDef[] = [PLAYER, PLAYER_SLIM, PLAYER_BETA, ZOMBIE, ZOMBIE_BETA, SKELETON, ENDERMAN, CREEPER, CREEPER_BETA, SPIDER, PIG, COW, SHEEP, CHICKEN, WOLF, WOLF_BETA, SLIME, GHAST, SQUID, BOX];
