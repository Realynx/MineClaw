// The 1.12.2-only animals and bosses: the horse family, llama, polar bear, rabbit, parrot, ocelot/cats, bat, guardians,
// shulker, ender dragon and wither. Dimensions, pivots, rest rotations and texture offsets are the vanilla model ones;
// renderer-level transforms (preRender scales, the dragon's own root offsets, the rabbit's inner 0.6 scale) are folded into
// `scale` or a root part. Facts the wire does not carry (saddles, chests, rearing, eating, perching, peeking, a horse's
// colour) fall back to the plain standing pose and a skin picked from the entity id.

import { box, D2R, look, mbox, part, PI, walkLegs } from '../dsl';
import type { CubeDef, Look, LookFn, ModelDef, PartDef, PartState, PoseFn } from '../types';

const pick = <T>(list: readonly T[], id: number): T => list[((id % list.length) + list.length) % list.length];

// ------------------------------------------------------------------------------------------------------------ horses

function horseModel(id: string, longEars: boolean): ModelDef {
  const headRot = [0.5235988, 0, 0] as const;
  const at = [0, 4, -10] as const;
  const ears: PartDef[] = longEars
    ? [
        part('leftEar', at, [box(-2, -16, 4, 2, 7, 1, 0, 12)], { rot: [0.5235988, 0, 0.2617994] }),
        part('rightEar', at, [box(0, -16, 4, 2, 7, 1, 0, 12)], { rot: [0.5235988, 0, -0.2617994] }),
      ]
    : [part('leftEar', at, [box(0.45, -12, 4, 2, 3, 1, 0, 0)], { rot: headRot }), part('rightEar', at, [box(-2.45, -12, 4, 2, 3, 1, 0, 0)], { rot: headRot })];
  return {
    id,
    tex: [128, 128],
    width: 1.3964844,
    height: 1.6,
    parts: [
      part('body', [0, 11, 9], [box(-5, -8, -19, 10, 10, 24, 0, 34)]),
      part('tailBase', [0, 3, 14], [box(-1, -1, 0, 2, 2, 3, 44, 0)], { rot: [-1.134464, 0, 0] }),
      part('tailMiddle', [0, 3, 14], [box(-1.5, -2, 3, 3, 4, 7, 38, 7)], { rot: [-1.134464, 0, 0] }),
      part('tailTip', [0, 3, 14], [box(-1.5, -4.5, 9, 3, 4, 7, 24, 3)], { rot: [-1.3962634, 0, 0] }),
      part('backLeftLeg', [4, 9, 11], [box(-2.5, -2, -2.5, 4, 9, 5, 78, 29)]),
      part('backLeftShin', [4, 16, 11], [box(-2, 0, -1.5, 3, 5, 3, 78, 43)]),
      part('backLeftHoof', [4, 16, 11], [box(-2.5, 5.1, -2, 4, 3, 4, 78, 51)]),
      part('backRightLeg', [-4, 9, 11], [box(-1.5, -2, -2.5, 4, 9, 5, 96, 29)]),
      part('backRightShin', [-4, 16, 11], [box(-1, 0, -1.5, 3, 5, 3, 96, 43)]),
      part('backRightHoof', [-4, 16, 11], [box(-1.5, 5.1, -2, 4, 3, 4, 96, 51)]),
      part('frontLeftLeg', [4, 9, -8], [box(-1.9, -1, -2.1, 3, 8, 4, 44, 29)]),
      part('frontLeftShin', [4, 16, -8], [box(-1.9, 0, -1.6, 3, 5, 3, 44, 41)]),
      part('frontLeftHoof', [4, 16, -8], [box(-2.4, 5.1, -2.1, 4, 3, 4, 44, 51)]),
      part('frontRightLeg', [-4, 9, -8], [box(-1.1, -1, -2.1, 3, 8, 4, 60, 29)]),
      part('frontRightShin', [-4, 16, -8], [box(-1.1, 0, -1.6, 3, 5, 3, 60, 41)]),
      part('frontRightHoof', [-4, 16, -8], [box(-1.6, 5.1, -2.1, 4, 3, 4, 60, 51)]),
      part('head', at, [box(-2.5, -10, -1.5, 5, 5, 7, 0, 0)], {
        rot: headRot,
        children: [part('upperMouth', [0, 0.02, 0.02], [box(-2, -10, -7, 4, 3, 6, 24, 18)]), part('lowerMouth', [0, 0, 0], [box(-2, -7, -6.5, 4, 2, 5, 24, 27)])],
      }),
      ...ears,
      part('neck', at, [box(-2.05, -9.8, -2, 4, 14, 8, 0, 12)], { rot: headRot }),
      part('mane', at, [box(-1, -11.5, 5, 2, 16, 4, 58, 0)], { rot: headRot }),
    ],
    pose: poseHorse,
  };
}

/** ModelHorse.setLivingAnimations with no rearing, grazing or open mouth. */
const poseHorse: PoseFn = (p, c) => {
  let pitch = c.pitch * D2R;
  const yaw = Math.max(-20, Math.min(20, c.headYaw)) * D2R;
  const a = c.amount;
  if (a > 0.2) pitch += Math.cos(c.swing * 0.4) * 0.15 * a;
  const headRx = 0.5235988 + pitch;
  for (const n of ['head', 'leftEar', 'rightEar', 'neck', 'mane']) {
    const q = p[n];
    if (!q) continue;
    q.rx = headRx;
    q.ry = yaw;
    q.py = 4;
    q.pz = -10;
  }
  const ph = Math.cos(c.swing * 0.6662 + PI);
  const q = ph * 0.8 * a;
  const back = ph * 0.5 * a;
  const fy = 9, fz = -8;
  p.backLeftShin.py = 9 + Math.sin(PI / 2 - back) * 7;
  p.backLeftShin.pz = 11 + Math.cos(-PI / 2 - back) * 7;
  p.backRightShin.py = 9 + Math.sin(PI / 2 + back) * 7;
  p.backRightShin.pz = 11 + Math.cos(-PI / 2 + back) * 7;
  p.frontLeftShin.py = fy + Math.sin(PI / 2 + q) * 7;
  p.frontLeftShin.pz = fz + Math.cos(-PI / 2 + q) * 7;
  p.frontRightShin.py = fy + Math.sin(PI / 2 - q) * 7;
  p.frontRightShin.pz = fz + Math.cos(-PI / 2 - q) * 7;
  p.backLeftLeg.rx = -back;
  p.backLeftShin.rx = p.backLeftHoof.rx = -back - Math.max(0, back);
  p.backRightLeg.rx = back;
  p.backRightShin.rx = p.backRightHoof.rx = back - Math.max(0, -back);
  p.frontLeftLeg.rx = q;
  p.frontLeftShin.rx = p.frontLeftHoof.rx = q + Math.max(0, back);
  p.frontRightLeg.rx = -q;
  p.frontRightShin.rx = p.frontRightHoof.rx = -q + Math.max(0, -back);
  for (const [hoof, shin] of [
    ['backLeftHoof', 'backLeftShin'],
    ['backRightHoof', 'backRightShin'],
    ['frontLeftHoof', 'frontLeftShin'],
    ['frontRightHoof', 'frontRightShin'],
  ]) {
    p[hoof].py = p[shin].py;
    p[hoof].pz = p[shin].pz;
  }
  const tail = Math.min(0, -1.3089969 + a * 1.5);
  p.tailBase.rx = p.tailMiddle.rx = tail;
  p.tailTip.rx = -0.2617994 + tail;
};

export const HORSE = horseModel('horse', false);
export const DONKEY = horseModel('donkey', true);

// ------------------------------------------------------------------------------------------------------------ quadrupeds

/** ModelQuadruped.setRotationAngles (body lying along z, the four legs walking). */
const poseQuadruped: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  p.body.rx = PI / 2;
  walkLegs(p, c.swing, c.amount);
};

export const LLAMA: ModelDef = {
  id: 'llama',
  tex: [128, 64],
  width: 0.9,
  height: 1.87,
  parts: [
    part('head', [0, 7, -6], [box(-2, -14, -10, 4, 4, 9, 0, 0), box(-4, -16, -6, 8, 18, 6, 0, 14), box(-4, -19, -4, 3, 3, 2, 17, 0), box(1, -19, -4, 3, 3, 2, 17, 0)]),
    part('body', [0, 5, 2], [box(-6, -10, -7, 12, 18, 10, 29, 0)], { rot: [PI / 2, 0, 0] }),
    part('leg1', [-3.5, 10, 6], [box(-2, 0, -2, 4, 14, 4, 29, 29)]),
    part('leg2', [3.5, 10, 6], [box(-2, 0, -2, 4, 14, 4, 29, 29)]),
    part('leg3', [-3.5, 10, -5], [box(-2, 0, -2, 4, 14, 4, 29, 29)]),
    part('leg4', [3.5, 10, -5], [box(-2, 0, -2, 4, 14, 4, 29, 29)]),
  ],
  pose: poseQuadruped,
};

export const POLAR_BEAR: ModelDef = {
  id: 'polar_bear',
  tex: [128, 64],
  width: 1.3,
  height: 1.4,
  scale: 1.2,
  parts: [
    part('head', [0, 10, -16], [box(-3.5, -3, -3, 7, 7, 7, 0, 0), box(-2.5, 1, -6, 5, 3, 3, 0, 44), box(-4.5, -4, -1, 2, 2, 1, 26, 0), mbox(2.5, -4, -1, 2, 2, 1, 26, 0)]),
    part('body', [-2, 9, 12], [box(-5, -13, -7, 14, 14, 11, 0, 19), box(-4, -25, -7, 12, 12, 10, 39, 0)], { rot: [PI / 2, 0, 0] }),
    part('leg1', [-4.5, 14, 6], [box(-2, 0, -2, 4, 10, 8, 50, 22)]),
    part('leg2', [4.5, 14, 6], [box(-2, 0, -2, 4, 10, 8, 50, 22)]),
    part('leg3', [-3.5, 14, -8], [box(-2, 0, -2, 4, 10, 6, 50, 40)]),
    part('leg4', [3.5, 14, -8], [box(-2, 0, -2, 4, 10, 6, 50, 40)]),
  ],
  pose: poseQuadruped,
};

// ------------------------------------------------------------------------------------------------------------ rabbit

/** RenderRabbit draws the model at 0.6 scale shifted 16 px down, which is the same as a 0.6 model scale. */
export const RABBIT: ModelDef = {
  id: 'rabbit',
  tex: [64, 32],
  width: 0.4,
  height: 0.5,
  scale: 0.6,
  parts: [
    part('leftFoot', [3, 17.5, 3.7], [box(-1, 5.5, -3.7, 2, 1, 7, 26, 24)]),
    part('rightFoot', [-3, 17.5, 3.7], [box(-1, 5.5, -3.7, 2, 1, 7, 8, 24)]),
    part('leftThigh', [3, 17.5, 3.7], [box(-1, 0, 0, 2, 4, 5, 30, 15)], { rot: [-0.34906584, 0, 0] }),
    part('rightThigh', [-3, 17.5, 3.7], [box(-1, 0, 0, 2, 4, 5, 16, 15)], { rot: [-0.34906584, 0, 0] }),
    part('body', [0, 19, 8], [box(-3, -2, -10, 6, 5, 10, 0, 0)], { rot: [-0.34906584, 0, 0] }),
    part('leftArm', [3, 17, -1], [box(-1, 0, -1, 2, 7, 2, 8, 15)], { rot: [-0.17453292, 0, 0] }),
    part('rightArm', [-3, 17, -1], [box(-1, 0, -1, 2, 7, 2, 0, 15)], { rot: [-0.17453292, 0, 0] }),
    part('head', [0, 16, -1], [box(-2.5, -4, -5, 5, 4, 5, 32, 0)]),
    part('rightEar', [0, 16, -1], [box(-2.5, -9, -1, 2, 5, 1, 52, 0)], { rot: [0, -0.2617994, 0] }),
    part('leftEar', [0, 16, -1], [box(0.5, -9, -1, 2, 5, 1, 58, 0)], { rot: [0, 0.2617994, 0] }),
    part('tail', [0, 20, 7], [box(-1.5, -1.5, 0, 3, 3, 2, 52, 6)], { rot: [-0.3490659, 0, 0] }),
    part('nose', [0, 16, -1], [box(-0.5, -2.5, -5.5, 1, 1, 1, 32, 9)]),
  ],
  pose: (p, c) => {
    const yaw = c.headYaw * D2R, pitch = c.pitch * D2R;
    for (const n of ['nose', 'head', 'rightEar', 'leftEar']) p[n].rx = pitch;
    p.nose.ry = p.head.ry = yaw;
    p.rightEar.ry = yaw - 0.2617994;
    p.leftEar.ry = yaw + 0.2617994;
    // The jump phase is not on the wire: hop with the walk cycle while moving.
    const jump = c.amount > 0.05 ? Math.max(0, Math.sin(c.swing * 0.8)) * Math.min(1, c.amount * 2) : 0;
    p.leftThigh.rx = p.rightThigh.rx = (jump * 50 - 21) * D2R;
    p.leftFoot.rx = p.rightFoot.rx = jump * 50 * D2R;
    p.leftArm.rx = p.rightArm.rx = (jump * -40 - 11) * D2R;
  },
};

// ------------------------------------------------------------------------------------------------------------ parrot

export const PARROT: ModelDef = {
  id: 'parrot',
  tex: [32, 32],
  width: 0.5,
  height: 0.9,
  parts: [
    part('body', [0, 16.5, -3], [box(-1.5, 0, -1.5, 3, 6, 3, 2, 8)], { rot: [0.4937, 0, 0] }),
    part('tail', [0, 21.07, 1.16], [box(-1.5, -1, -1, 3, 4, 1, 22, 1)], { rot: [1.015, 0, 0] }),
    part('wingLeft', [1.5, 16.94, -2.76], [box(-0.5, 0, -1.5, 1, 5, 3, 19, 8)], { rot: [-0.6981, -PI, -0.0873] }),
    part('wingRight', [-1.5, 16.94, -2.76], [box(-0.5, 0, -1.5, 1, 5, 3, 19, 8)], { rot: [-0.6981, -PI, 0.0873] }),
    part('head', [0, 15.69, -2.76], [box(-1, -1.5, -1, 2, 3, 2, 2, 2)], {
      children: [
        part('head2', [0, -2, -1], [box(-1, -0.5, -2, 2, 1, 4, 10, 0)]),
        part('beak1', [0, -0.5, -1.5], [box(-0.5, -1, -0.5, 1, 2, 1, 11, 7)]),
        part('beak2', [0, -1.75, -2.45], [box(-0.5, 0, -0.5, 1, 2, 1, 16, 7)]),
        part('feather', [0, -2.15, 0.15], [box(0, -4, -2, 0, 5, 4, 2, 18)], { rot: [-0.2214, 0, 0] }),
      ],
    }),
    part('legLeft', [1, 22, -1.05], [box(-0.5, 0, -0.5, 1, 2, 1, 14, 18)], { rot: [-0.0299, 0, 0] }),
    part('legRight', [-1, 22, -1.05], [box(-0.5, 0, -0.5, 1, 2, 1, 14, 18)], { rot: [-0.0299, 0, 0] }),
  ],
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    // Standing (perching, flying and dancing are not on the wire): the legs walk, the tail sways with the stride.
    p.legLeft.rx += Math.cos(c.swing * 0.6662) * 1.4 * c.amount;
    p.legRight.rx += Math.cos(c.swing * 0.6662 + PI) * 1.4 * c.amount;
    p.tail.rx = 1.015 + Math.cos(c.swing * 0.6662) * 0.3 * c.amount;
  },
};

// ------------------------------------------------------------------------------------------------------------ ocelot

export const OCELOT: ModelDef = {
  id: 'ocelot',
  tex: [64, 32],
  width: 0.6,
  height: 0.7,
  parts: [
    part('head', [0, 15, -9], [box(-2.5, -2, -3, 5, 4, 5, 0, 0), box(-1.5, 0, -4, 3, 2, 2, 0, 24), box(-2, -3, 0, 1, 1, 2, 0, 10), box(1, -3, 0, 1, 1, 2, 6, 10)]),
    part('body', [0, 12, -10], [box(-2, 3, -8, 4, 16, 6, 20, 0)], { rot: [PI / 2, 0, 0] }),
    part('tail', [0, 15, 8], [box(-0.5, 0, 0, 1, 8, 1, 0, 15)], { rot: [0.9, 0, 0] }),
    part('tail2', [0, 20, 14], [box(-0.5, 0, 0, 1, 8, 1, 4, 15)], { rot: [1.7278761, 0, 0] }),
    part('backLeftLeg', [1.1, 18, 5], [box(-1, 0, 1, 2, 6, 2, 8, 13)]),
    part('backRightLeg', [-1.1, 18, 5], [box(-1, 0, 1, 2, 6, 2, 8, 13)]),
    part('frontLeftLeg', [1.2, 13.8, -5], [box(-1, 0, 0, 2, 10, 2, 40, 0)]),
    part('frontRightLeg', [-1.2, 13.8, -5], [box(-1, 0, 0, 2, 10, 2, 40, 0)]),
  ],
  /** ModelOcelot.setLivingAnimations + setRotationAngles: sneaking, sprinting, sitting (Mob16 bit 1) or walking. */
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    const f = c.swing, a = c.amount;
    const sprint = (c.flags & 0x08) !== 0, sit = (c.variant & 1) !== 0;
    if (c.sneaking) {
      p.body.py += 1;
      p.head.py += 2;
      p.tail.py += 1;
      p.tail2.py += -4;
      p.tail2.pz += 2;
      p.tail.rx = p.tail2.rx = PI / 2;
    } else if (sprint) {
      p.tail2.py = p.tail.py;
      p.tail2.pz += 2;
      p.tail.rx = p.tail2.rx = PI / 2;
    } else if (sit) {
      p.body.rx = PI / 4;
      p.body.py += -4;
      p.body.pz += 5;
      p.head.py += -3.3;
      p.head.pz += 1;
      p.tail.py += 8;
      p.tail.pz += -2;
      p.tail2.py += 2;
      p.tail2.pz += -0.8;
      p.tail.rx = 1.7278761;
      p.tail2.rx = 2.670354;
      for (const n of ['frontLeftLeg', 'frontRightLeg']) {
        p[n].rx = -0.15707964;
        p[n].py = 15.8;
        p[n].pz = -7;
      }
      for (const n of ['backLeftLeg', 'backRightLeg']) {
        p[n].rx = -PI / 2;
        p[n].py = 21;
        p[n].pz = 1;
      }
      return;
    }
    p.body.rx = PI / 2;
    if (sprint) {
      p.backLeftLeg.rx = Math.cos(f * 0.6662) * a;
      p.backRightLeg.rx = Math.cos(f * 0.6662 + 0.3) * a;
      p.frontLeftLeg.rx = Math.cos(f * 0.6662 + PI + 0.3) * a;
      p.frontRightLeg.rx = Math.cos(f * 0.6662 + PI) * a;
      p.tail2.rx = 1.7278761 + 0.31415927 * Math.cos(f) * a;
    } else {
      p.backLeftLeg.rx = Math.cos(f * 0.6662) * a;
      p.backRightLeg.rx = Math.cos(f * 0.6662 + PI) * a;
      p.frontLeftLeg.rx = Math.cos(f * 0.6662 + PI) * a;
      p.frontRightLeg.rx = Math.cos(f * 0.6662) * a;
      p.tail2.rx = 1.7278761 + (c.sneaking ? 0.47123894 : 0.7853982) * Math.cos(f) * a;
    }
  },
};

// ------------------------------------------------------------------------------------------------------------ bat

/** RenderBat bobs the whole bat ±0.1 block (world), i.e. ±0.1 / (0.35 / 16) model pixels. */
const BAT_BOB = 0.1 / (0.35 / 16);

export const BAT: ModelDef = {
  id: 'bat',
  tex: [64, 64],
  width: 0.5,
  height: 0.9,
  scale: 0.35,
  parts: [
    part('head', [0, 0, 0], [box(-3, -3, -3, 6, 6, 6, 0, 0)], {
      children: [part('rightEar', [0, 0, 0], [box(-4, -6, -2, 3, 4, 1, 24, 0)]), part('leftEar', [0, 0, 0], [mbox(1, -6, -2, 3, 4, 1, 24, 0)])],
    }),
    part('body', [0, 0, 0], [box(-3, 4, -3, 6, 12, 6, 0, 16), box(-5, 16, 0, 10, 6, 1, 0, 34)], {
      children: [
        part('rightWing', [0, 0, 0], [box(-12, 1, 1.5, 10, 16, 1, 42, 0)], { children: [part('outerRightWing', [-12, 1, 1.5], [box(-8, 1, 0, 8, 12, 1, 24, 16)])] }),
        part('leftWing', [0, 0, 0], [mbox(2, 1, 1.5, 10, 16, 1, 42, 0)], { children: [part('outerLeftWing', [12, 1, 1.5], [mbox(0, 1, 0, 8, 12, 1, 24, 16)])] }),
      ],
    }),
  ],
  /** ModelBat flying (hanging upside down is not on the wire). */
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    p.body.rx = PI / 4 + Math.cos(c.age * 0.1) * 0.15;
    const w = Math.cos(c.age * 1.3) * PI * 0.25;
    p.rightWing.ry = w;
    p.leftWing.ry = -w;
    p.outerRightWing.ry = w * 0.5;
    p.outerLeftWing.ry = -w * 0.5;
    const bob = -Math.cos(c.age * 0.3) * BAT_BOB;
    p.head.py = p.body.py = bob;
  },
};

// ------------------------------------------------------------------------------------------------------------ guardians

const SPINE_RX = [1.75, 0.25, 0, 0, 0.5, 0.5, 0.5, 0.5, 1.25, 0.75, 0, 0];
const SPINE_RY = [0, 0, 0, 0, 0.25, 1.75, 1.25, 0.75, 0, 0, 0, 0];
const SPINE_RZ = [0, 0, 0.25, 1.75, 0, 0, 0, 0, 0, 0, 0.75, 1.25];
const SPINE_X = [0, 0, 8, -8, -8, 8, 8, -8, 0, 0, 8, -8];
const SPINE_Y = [-8, -8, -8, -8, 0, 0, 0, 0, 8, 8, 8, 8];
const SPINE_Z = [8, -8, 0, 0, -8, -8, 8, 8, 8, -8, 0, 0];

export const GUARDIAN: ModelDef = {
  id: 'guardian',
  tex: [64, 64],
  width: 0.85,
  height: 0.85,
  parts: [
    part(
      'body',
      [0, 0, 0],
      [box(-6, 10, -8, 12, 12, 16, 0, 0), box(-8, 10, -6, 2, 12, 12, 0, 28), mbox(6, 10, -6, 2, 12, 12, 0, 28), box(-6, 8, -6, 12, 2, 12, 16, 40), box(-6, 22, -6, 12, 2, 12, 16, 40)],
      {
        children: [
          ...SPINE_RX.map((_, n) =>
            part(`spine${n}`, [SPINE_X[n], 16 + SPINE_Y[n], SPINE_Z[n]], [box(-1, -4.5, -1, 2, 9, 2, 0, 0)], { rot: [PI * SPINE_RX[n], PI * SPINE_RY[n], PI * SPINE_RZ[n]] }),
          ),
          part('eye', [0, 0, -8.25], [box(-1, 15, 0, 2, 2, 1, 8, 0)]),
          part('tail0', [0, 0, 0], [box(-2, 14, 7, 4, 4, 8, 40, 0)], {
            children: [
              part('tail1', [-1.5, 0.5, 14], [box(0, 14, 0, 3, 3, 7, 0, 54)], {
                children: [part('tail2', [0.5, 0.5, 6], [box(0, 14, 0, 2, 2, 6, 41, 32), box(1, 10.5, 3, 1, 9, 9, 25, 19)])],
              }),
            ],
          }),
        ],
      },
    ),
  ],
  /** ModelGuardian: the whole body turns with the head; spikes breathe; the tail wags (its phase is local). */
  pose: (p, c) => {
    p.body.ry = c.headYaw * D2R;
    p.body.rx = c.pitch * D2R;
    const out = 0.55 * Math.min(1, c.speed * 8); // spikes pull in while it swims
    for (let n = 0; n < 12; n++) {
      const s = p[`spine${n}`];
      const k = 1 + Math.cos(c.age * 1.5 + n) * 0.01 - out;
      s.px = SPINE_X[n] * k;
      s.py = 16 + SPINE_Y[n] * k;
      s.pz = SPINE_Z[n] * k;
    }
    const o = c.age * (0.1 + c.speed * 2) + c.seed * 6.28;
    p.tail0.ry = Math.sin(o) * PI * 0.05;
    p.tail1.ry = Math.sin(o) * PI * 0.1;
    p.tail2.ry = Math.sin(o) * PI * 0.15;
  },
};

// ------------------------------------------------------------------------------------------------------------ shulker

export const SHULKER: ModelDef = {
  id: 'shulker',
  tex: [64, 64],
  width: 1,
  height: 1,
  scale: 0.999,
  parts: [
    part('base', [0, 24, 0], [box(-8, -8, -8, 16, 8, 16, 0, 28)]),
    part('lid', [0, 24, 0], [box(-8, -16, -8, 16, 12, 16, 0, 0)]),
    part('head', [0, 12, 0], [box(-3, 0, -3, 6, 6, 6, 0, 52)]),
  ],
  /** Closed (the peek amount is not on the wire), the head looking about. */
  pose: (p, c) => look(p.head, c.headYaw, c.pitch),
};

// ------------------------------------------------------------------------------------------------------------ ender dragon

/** Mirrors a part across x (the second pass of ModelDragon's wing/leg loop runs under scale(−1, 1, 1)). */
function mirrorX(d: PartDef, suffix: string): PartDef {
  const cubes: CubeDef[] = d.cubes.map((c) => {
    const m: CubeDef = { ...c, o: [-(c.o[0] + c.s[0]), c.o[1], c.o[2]] };
    return c.mirror ? (({ mirror: _m, ...rest }) => rest)(m) : { ...m, mirror: true };
  });
  return {
    ...d,
    name: d.name + suffix,
    pivot: [-d.pivot[0], d.pivot[1], d.pivot[2]],
    rot: d.rot ? [d.rot[0], -d.rot[1], -d.rot[2]] : undefined,
    cubes,
    children: d.children?.map((ch) => mirrorX(ch, suffix)),
  };
}

/** A wing membrane: a zero-height box whose top/bottom texture rectangles are the vanilla box layout ones. */
const membrane = (x: number, z: number, u: number, v: number): CubeDef => ({
  o: [x, 0, z],
  s: [56, 0, 56],
  uv: [u, v],
  faces: { up: [u + 56, v, u + 112, v + 56], down: [u + 112, v, u + 168, v + 56] },
});

const neck = (n: string): PartDef => part(n, [0, 0, 0], [box(-5, -5, -5, 10, 10, 10, 192, 104), box(-1, -9, -3, 2, 4, 6, 48, 0)]);

const DRAGON_RIGHT: PartDef[] = [
  part('wing', [-12, 5, 2], [box(-56, -4, -4, 56, 8, 8, 112, 88), membrane(-56, 2, -56, 88)], {
    children: [part('wingTip', [-56, 0, 0], [box(-56, -2, -2, 56, 4, 4, 112, 136), membrane(-56, 2, -56, 144)])],
  }),
  part('frontLeg', [-12, 20, 2], [box(-4, -4, -4, 8, 24, 8, 112, 104)], {
    children: [part('frontLegTip', [0, 20, -1], [box(-3, -1, -3, 6, 24, 6, 226, 138)], { children: [part('frontFoot', [0, 23, 0], [box(-4, 0, -12, 8, 4, 16, 144, 104)])] })],
  }),
  part('rearLeg', [-16, 16, 42], [box(-8, -4, -8, 16, 32, 16, 0, 0)], {
    children: [part('rearLegTip', [0, 32, -4], [box(-6, -2, 0, 12, 32, 12, 196, 0)], { children: [part('rearFoot', [0, 31, 4], [box(-9, 0, -20, 18, 6, 24, 112, 0)])] })],
  }),
];

/**
 * RenderDragon turns the model by −yaw instead of 180° − yaw and shifts it (0, 0, 1) block; ModelDragon.render shifts it again
 * by (0, −2, −3) blocks: one root part rotated 180° about y at (0, −32, 32) px reproduces both.
 */
export const ENDER_DRAGON: ModelDef = {
  id: 'ender_dragon',
  tex: [256, 256],
  width: 16,
  height: 8,
  parts: [
    part('root', [0, -32, 32], [], {
      rot: [0, PI, 0],
      children: [
        ...[0, 1, 2, 3, 4].map((i) => neck(`neck${i}`)),
        part(
          'head',
          [0, 0, 0],
          [
            box(-6, -1, -24, 12, 5, 16, 176, 44),
            box(-8, -8, -10, 16, 16, 16, 112, 30),
            mbox(-5, -12, -4, 2, 4, 6, 0, 0),
            mbox(-5, -3, -22, 2, 2, 4, 112, 0),
            box(3, -12, -4, 2, 4, 6, 0, 0),
            box(3, -3, -22, 2, 2, 4, 112, 0),
          ],
          { children: [part('jaw', [0, 4, -8], [box(-6, 0, -16, 12, 4, 16, 176, 65)])] },
        ),
        part('body', [0, 4, 8], [box(-12, 0, -16, 24, 24, 64, 0, 0), box(-1, -6, -10, 2, 6, 12, 220, 53), box(-1, -6, 10, 2, 6, 12, 220, 53), box(-1, -6, 30, 2, 6, 12, 220, 53)]),
        ...DRAGON_RIGHT,
        ...DRAGON_RIGHT.map((d) => mirrorX(d, 'L')),
        ...Array.from({ length: 12 }, (_, i) => neck(`tail${i}`)),
      ],
    }),
  ],
  /** ModelDragon.render with a straight body (the movement history is not on the wire): flapping wings, a swaying neck and
   * tail, the jaw working. */
  pose: (p, c) => {
    const f = (c.age * 0.2) / (c.speed * 10 + 1) + c.seed;
    const t = f * PI * 2;
    p.jaw.rx = (Math.sin(t) + 1) * 0.2;
    let g = Math.sin(t - 1) + 1;
    g = (g * g + g * 2) * 0.05;
    p.root.py = -32 + g * 16;
    p.root.rx = g * 2 * D2R;
    let h = 20, z = -12;
    for (let o = 0; o < 5; o++) {
      const s = p[`neck${o}`];
      s.rx = Math.cos(o * 0.45 + t) * 0.15;
      s.px = 0;
      s.py = h;
      s.pz = z;
      h += Math.sin(s.rx) * 10;
      z -= Math.cos(s.rx) * 10;
    }
    p.head.px = 0;
    p.head.py = h;
    p.head.pz = z;
    p.head.ry = c.headYaw * D2R * 0.5;
    p.head.rx = c.pitch * D2R * 0.5;
    for (const side of ['', 'L']) {
      const sign = side ? -1 : 1;
      const wing = p['wing' + side], tip = p['wingTip' + side];
      wing.rx = 0.125 - Math.cos(t) * 0.2;
      wing.ry = 0.25 * sign;
      wing.rz = (Math.sin(t) + 0.125) * 0.8 * sign;
      tip.rz = -(Math.sin(t + 2) + 0.5) * 0.75 * sign;
      p['rearLeg' + side].rx = 1 + g * 0.1;
      p['rearLegTip' + side].rx = 0.5 + g * 0.1;
      p['rearFoot' + side].rx = 0.75 + g * 0.1;
      p['frontLeg' + side].rx = 1.3 + g * 0.1;
      p['frontLegTip' + side].rx = -0.5 - g * 0.1;
      p['frontFoot' + side].rx = 0.75 + g * 0.1;
    }
    let s = 0;
    h = 10;
    z = 60;
    for (let u = 0; u < 12; u++) {
      const q = p[`tail${u}`];
      s += Math.sin(u * 0.45 + t) * 0.05;
      q.ry = PI;
      q.rx = s;
      q.px = 0;
      q.py = h;
      q.pz = z;
      h += Math.sin(q.rx) * 10;
      z += Math.cos(q.rx) * 10;
    }
  },
};

// ------------------------------------------------------------------------------------------------------------ wither

export const WITHER: ModelDef = {
  id: 'wither',
  tex: [64, 64],
  width: 0.9,
  height: 3.5,
  scale: 2,
  parts: [
    part('head0', [0, 0, 0], [box(-4, -4, -4, 8, 8, 8, 0, 0)]),
    part('head1', [-8, 4, 0], [box(-4, -4, -4, 6, 6, 6, 32, 0)]),
    part('head2', [10, 4, 0], [box(-4, -4, -4, 6, 6, 6, 32, 0)]),
    part('shoulders', [0, 0, 0], [box(-10, 3.9, -0.5, 20, 3, 3, 0, 16)]),
    part('ribs', [-2, 6.9, -0.5], [box(0, 0, 0, 3, 10, 3, 0, 22), box(-4, 1.5, 0.5, 11, 2, 2, 24, 22), box(-4, 4, 0.5, 11, 2, 2, 24, 22), box(-4, 6.5, 0.5, 11, 2, 2, 24, 22)]),
    part('tail', [-2, 16.9, -0.5], [box(0, 0, 0, 3, 6, 3, 12, 22)]),
  ],
  /** ModelWither: the ribcage breathes and the tail sways; the side heads look where the main one does (their own targets
   * are not on the wire), a little out of step. */
  pose: (p, c) => {
    const f = Math.cos(c.age * 0.1);
    const rib = (0.065 + 0.05 * f) * PI;
    p.ribs.rx = rib;
    p.tail.py = 6.9 + Math.cos(rib) * 10;
    p.tail.pz = -0.5 + Math.sin(rib) * 10;
    p.tail.rx = (0.265 + 0.1 * f) * PI;
    look(p.head0, c.headYaw, c.pitch);
    p.head1.ry = (c.headYaw + 20 * Math.sin(c.age * 0.05 + c.seed * 6)) * D2R;
    p.head1.rx = c.pitch * D2R;
    p.head2.ry = (c.headYaw - 20 * Math.sin(c.age * 0.04 + c.seed * 4)) * D2R;
    p.head2.rx = c.pitch * D2R;
  },
};

// ------------------------------------------------------------------------------------------------------------ registry

export const MODELS: ModelDef[] = [HORSE, DONKEY, LLAMA, POLAR_BEAR, RABBIT, PARROT, OCELOT, BAT, GUARDIAN, SHULKER, ENDER_DRAGON, WITHER];

const HORSE_COATS = ['white', 'creamy', 'chestnut', 'brown', 'black', 'gray', 'darkbrown'].map((c) => `entity/horse/horse_${c}`);
const HORSE_COLOURS = [0xe8e4dc, 0xc49a62, 0x8a4f26, 0x6b4426, 0x2a2420, 0x7a7670, 0x3f2a1c];
const LLAMAS = ['creamy', 'white', 'brown', 'gray'].map((c) => `entity/llama/llama_${c}`);
const LLAMA_COLOURS = [0xd8c8a0, 0xeeeeee, 0x7a5434, 0x8c8c8c];
const RABBITS = ['brown', 'white', 'black', 'gold', 'salt', 'white_splotched'].map((c) => `entity/rabbit/${c}`);
const RABBIT_COLOURS = [0x8a6a4a, 0xf0f0f0, 0x2a2a2a, 0xd8b060, 0xb09a78, 0xe0e0e0];
const PARROTS = ['red_blue', 'blue', 'green', 'yellow_blue', 'grey'].map((c) => `entity/parrot/parrot_${c}`);
const PARROT_COLOURS = [0xd02020, 0x2050d0, 0x40c020, 0x40b0e0, 0xa0a0a0];
const CATS = ['black', 'red', 'siamese'].map((c) => `entity/cat/${c}`);

const look1 = (model: string, texture: string, colour: number, extra: Partial<Look> = {}): Look => ({ model, texture, colour, ...extra });

export const LOOKS: Record<string, LookFn> = {
  horse: (_v, _b, id) => look1('horse', pick(HORSE_COATS, id), pick(HORSE_COLOURS, id)),
  donkey: () => look1('donkey', 'entity/horse/donkey', 0x8a7a6a, { scale: 0.87 }),
  mule: () => look1('donkey', 'entity/horse/mule', 0x5a3a24, { scale: 0.92 }),
  skeleton_horse: () => look1('horse', 'entity/horse/horse_skeleton', 0xd0d0c8),
  zombie_horse: () => look1('horse', 'entity/horse/horse_zombie', 0x4a7a4a),
  llama: (_v, _b, id) => look1('llama', pick(LLAMAS, id), pick(LLAMA_COLOURS, id)),
  polar_bear: () => look1('polar_bear', 'entity/bear/polarbear', 0xf0f0f0),
  rabbit: (_v, _b, id) => look1('rabbit', pick(RABBITS, id), pick(RABBIT_COLOURS, id)),
  parrot: (_v, _b, id) => look1('parrot', pick(PARROTS, id), pick(PARROT_COLOURS, id)),
  // Mob16 on an ocelot is EntityTameable's flags (1 sitting, 4 tamed): a tamed one is a cat (its skin index is not on the
  // wire: picked from the id) drawn at 0.8.
  ocelot: (v, _b, id) => ((v & 4) !== 0 ? look1('ocelot', pick(CATS, id), 0x3a3a3a, { scale: 0.8 }) : look1('ocelot', 'entity/cat/ocelot', 0xe0c060)),
  bat: () => look1('bat', 'entity/bat', 0x4a3a2a),
  guardian: () => look1('guardian', 'entity/guardian', 0x70a090),
  elder_guardian: () => look1('guardian', 'entity/guardian_elder', 0xc8c0a8, { scale: 2.35 }),
  shulker: () => look1('shulker', 'entity/shulker/shulker_purple', 0x9a6a9a),
  ender_dragon: () => look1('ender_dragon', 'entity/enderdragon/dragon', 0x200a30, { layers: [{ texture: 'entity/enderdragon/dragon_eyes', emissive: true }] }),
  wither: () => look1('wither', 'entity/wither/wither', 0x1a1a1a),
};
