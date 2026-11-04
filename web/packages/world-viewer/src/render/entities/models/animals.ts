// The 1.12.2-only animals and bosses: the horse family, llama, polar bear, rabbit, parrot, ocelot/cats, bat, guardians,
// shulker, ender dragon and wither. Dimensions, pivots, rest rotations and texture offsets are the vanilla model ones;
// renderer-level transforms (preRender scales, the dragon's own root offsets, the rabbit's inner 0.6 scale) are folded into
// `scale` or a root part. Saddles, chest bags and llama carpets are part layers drawn when the variant asks for them;
// coats, markings, horse armour and skins come from the variant (or are picked from the entity id when it says nothing).
// Rearing, grazing, perching and flying poses are not on the wire.

import { box, D2R, look, mbox, part, PI, walkLegs } from '../dsl';
import type { CubeDef, LayerUse, Look, LookFn, ModelDef, PartDef, PartState, PoseFn } from '../types';
import { Variant } from '../variant';


// ------------------------------------------------------------------------------------------------------------ horses

/** ModelHorse's saddle (tagged 'saddle'): the seat and stirrups on the back; the bridle as head children (they share its
 * pivot and turn), the reins drawn only while ridden. */
function saddleParts(): PartDef[] {
  const o = { layer: 'saddle' };
  return [
    part('saddleBottom', [0, 2, 2], [box(-5, 0, -3, 10, 1, 8, 80, 0)], o),
    part('saddleFront', [0, 2, 2], [box(-1.5, -1, -3, 3, 1, 2, 106, 9)], o),
    part('saddleBack', [0, 2, 2], [box(-4, -1, 3, 8, 1, 2, 80, 9)], o),
    part('leftSaddleRope', [5, 2, 2], [box(-0.5, 0, -0.5, 1, 6, 1, 70, 0)], o),
    part('leftSaddleMetal', [5, 2, 2], [box(-0.5, 6, -1, 1, 2, 2, 74, 0)], o),
    part('rightSaddleRope', [-5, 2, 2], [box(-0.5, 0, -0.5, 1, 6, 1, 80, 0)], o),
    part('rightSaddleMetal', [-5, 2, 2], [box(-0.5, 6, -1, 1, 2, 2, 74, 4)], o),
  ];
}

const bridle = (): PartDef[] => [
  part('faceRopes', [0, 0, 0], [box(-2.5, -10.1, -7, 5, 5, 12, 80, 12, 0.2)], { layer: 'saddle' }),
  part('leftFaceMetal', [0, 0, 0], [box(1.5, -8, -4, 1, 2, 2, 74, 13)], { layer: 'saddle' }),
  part('rightFaceMetal', [0, 0, 0], [box(-2.5, -8, -4, 1, 2, 2, 74, 13)], { layer: 'saddle' }),
  // Reins turn with the head's yaw and the pitch only (not its rest tilt).
  part('leftRein', [0, 0, 0], [box(2.6, -6, -6, 0, 3, 16, 44, 10)], { layer: 'saddle', rot: [-0.5235988, 0, 0] }),
  part('rightRein', [0, 0, 0], [box(-2.6, -6, -6, 0, 3, 16, 44, 5)], { layer: 'saddle', rot: [-0.5235988, 0, 0] }),
];

/** Mule / donkey chest bags (tagged 'chest'). */
const chestBags = (): PartDef[] => [
  part('leftChest', [-7.5, 3, 10], [box(-3, 0, 0, 8, 8, 3, 0, 34)], { rot: [0, PI / 2, 0], layer: 'chest' }),
  part('rightChest', [4.5, 3, 10], [box(-3, 0, 0, 8, 8, 3, 0, 47)], { rot: [0, PI / 2, 0], layer: 'chest' }),
];

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
    layers: longEars ? ['saddle', 'chest'] : ['saddle'],
    baby: HORSE_BABY,
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
        children: [part('upperMouth', [0, 0.02, 0.02], [box(-2, -10, -7, 4, 3, 6, 24, 18)]), part('lowerMouth', [0, 0, 0], [box(-2, -7, -6.5, 4, 2, 5, 24, 27)]), ...bridle()],
      }),
      ...ears,
      part('neck', at, [box(-2.05, -9.8, -2, 4, 14, 8, 0, 12)], { rot: headRot }),
      part('mane', at, [box(-1, -11.5, 5, 2, 16, 4, 58, 0)], { rot: headRot }),
      ...saddleParts(),
      ...(longEars ? chestBags() : []),
    ],
    pose: poseHorse,
  };
}

/**
 * ModelHorse.render's child layout with AbstractHorse.getHorseSize() = 0.5 (constant in 1.12.2) and no grazing: the legs
 * scale(g, 0.5 + g/2, g) · translate(0, 0.95(1 − g)), the body, tail, neck and mane scale(g) · translate(0, 1.35(1 − g)),
 * the head and ears scale(0.5 + g²/2) · translate(0, 1.35(1 − g)) (blocks; here pixels). The saddle and chest bags render
 * outside the push/pop (and never on a foal).
 */
const HORSE_G = 0.5;
const HORSE_LEGS = ['back', 'front'].flatMap((a) => ['Left', 'Right'].flatMap((s) => ['Leg', 'Shin', 'Hoof'].map((p) => `${a}${s}${p}`)));
const HORSE_BABY: NonNullable<ModelDef['baby']> = {
  head: ['head', 'leftEar', 'rightEar'],
  headScale: 0.5 + HORSE_G * HORSE_G * 0.5,
  headOffset: [0, 1.35 * (1 - HORSE_G) * 16, 0],
  bodyScale: HORSE_G,
  bodyOffset: [0, 1.35 * (1 - HORSE_G) * 16, 0],
  groups: [{ parts: HORSE_LEGS, scale: [HORSE_G, 0.5 + HORSE_G * 0.5, HORSE_G], offset: [0, 0.95 * (1 - HORSE_G) * 16, 0] }],
  unscaled: ['saddleBottom', 'saddleFront', 'saddleBack', 'leftSaddleRope', 'leftSaddleMetal', 'rightSaddleRope', 'rightSaddleMetal', 'leftChest', 'rightChest'],
};

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
  // Saddle stirrups swing with the stride (held back while ridden); reins only while ridden; chest bags sway.
  const ridden = c.riding;
  for (const [n, side] of [['leftSaddleRope', 1], ['leftSaddleMetal', 1], ['rightSaddleRope', -1], ['rightSaddleMetal', -1]] as const) {
    p[n].rx = ridden ? -1.0471976 : q / 3;
    p[n].rz = ridden ? 0 : (side * q) / 5;
  }
  p.leftRein.visible = p.rightRein.visible = ridden;
  if (p.leftChest) {
    p.leftChest.rx = q / 5;
    p.rightChest.rx = -q / 5;
  }
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

/** An inflated copy of a part's cubes as a child at its pivot, tagged with a layer (ModelLlama(0.5) for the carpet). */
const grown = (name: string, cubes: CubeDef[], inflate: number, layer: string): PartDef =>
  part(name, [0, 0, 0], cubes.map((c) => ({ ...c, inflate: (c.inflate ?? 0) + inflate })), { layer });

const LLAMA_HEAD = [box(-2, -14, -10, 4, 4, 9, 0, 0), box(-4, -16, -6, 8, 18, 6, 0, 14), box(-4, -19, -4, 3, 3, 2, 17, 0), box(1, -19, -4, 3, 3, 2, 17, 0)];
const LLAMA_BODY = [box(-6, -10, -7, 12, 18, 10, 29, 0)];
const LLAMA_LEG = [box(-2, 0, -2, 4, 14, 4, 29, 29)];
const llamaLeg = (n: string, x: number, z: number) => part(n, [x, 10, z], LLAMA_LEG, { children: [grown(n + 'Decor', LLAMA_LEG, 0.5, 'decor')] });

/** ModelLlama; the carpet ('decor': the same boxes grown 0.5 with decor_<colour>.png) and the chest bags ('chest'). */
export const LLAMA: ModelDef = {
  id: 'llama',
  tex: [128, 64],
  width: 0.9,
  height: 1.87,
  layers: ['decor', 'chest'],
  // ModelLlama.render's child layout: head scale(0.714, 0.649, 0.794) · translate(0, 21 px, 0.22), body
  // scale(0.625, 0.4545, 0.4545) · translate(0, 33 px, 0), legs scale(0.4545, 0.4132, 0.4545) · translate(0, 33 px, 0); the
  // chest bags are drawn outside it (and never on a baby).
  baby: {
    head: ['head'],
    headScale: [0.71428573, 0.64935064, 0.7936508],
    headOffset: [0, 21, 0.22 * 16],
    bodyScale: [0.625, 0.45454544, 0.45454544],
    bodyOffset: [0, 33, 0],
    groups: [{ parts: ['leg1', 'leg2', 'leg3', 'leg4'], scale: [0.45454544, 0.41322312, 0.45454544], offset: [0, 33, 0] }],
    unscaled: ['chest1', 'chest2'],
  },
  parts: [
    part('head', [0, 7, -6], LLAMA_HEAD, { children: [grown('headDecor', LLAMA_HEAD, 0.5, 'decor')] }),
    part('body', [0, 5, 2], LLAMA_BODY, { rot: [PI / 2, 0, 0], children: [grown('bodyDecor', LLAMA_BODY, 0.5, 'decor')] }),
    llamaLeg('leg1', -3.5, 6),
    llamaLeg('leg2', 3.5, 6),
    llamaLeg('leg3', -3.5, -5),
    llamaLeg('leg4', 3.5, -5),
    part('chest1', [-8.5, 3, 3], [box(-3, 0, 0, 8, 8, 3, 45, 28)], { rot: [0, PI / 2, 0], layer: 'chest' }),
    part('chest2', [5.5, 3, 3], [box(-3, 0, 0, 8, 8, 3, 45, 41)], { rot: [0, PI / 2, 0], layer: 'chest' }),
  ],
  pose: poseQuadruped,
};

export const POLAR_BEAR: ModelDef = {
  id: 'polar_bear',
  tex: [128, 64],
  width: 1.3,
  height: 1.4,
  scale: 1.2,
  // ModelPolarBear child: head scale(2/3) · translate(0, 16, 4) px; the rest scale(0.5) · translate(0, 24) px.
  baby: { head: ['head'], headScale: 2 / 3, headOffset: [0, 16, 4], bodyScale: 0.5, bodyOffset: [0, 24, 0] },
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
  // ModelRabbit child: head group scale(0.5667) · translate(0, 22, 2) px, body group scale(0.4) · translate(0, 36) px — in
  // vanilla without the adult 0.6; re-expressed inside this model's 0.6 root (and its −1.501 block shift).
  baby: { head: ['head', 'leftEar', 'rightEar', 'nose'], headScale: 0.56666666 / 0.6, headOffset: [0, 5.05, 2], bodyScale: 0.4 / 0.6, bodyOffset: [0, 12, 0] },
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
  // ModelOcelot child: head scale(0.75) · translate(0, 10, 4) px; the rest scale(0.5) · translate(0, 24) px.
  baby: { head: ['head'], headScale: 0.75, headOffset: [0, 10, 4], bodyScale: 0.5, bodyOffset: [0, 24, 0] },
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
  /** ModelOcelot.setLivingAnimations + setRotationAngles: sneaking, sprinting, sitting ("sitting", or the older Mob16 flags
   * ≥ 4 with bit 1) or walking. */
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    const sprint = (c.flags & 0x08) !== 0, sit = ocelotSits(c.v);
    if (c.sneaking) ocelotSneak(p);
    else if (sprint) {
      p.tail2.py = p.tail.py;
      p.tail2.pz += 2;
      p.tail.rx = p.tail2.rx = PI / 2;
    } else if (sit) {
      ocelotSit(p);
      return;
    }
    p.body.rx = PI / 2;
    if (sprint) ocelotSprintLegs(p, c.swing, c.amount);
    else ocelotWalkLegs(p, c.swing, c.amount, c.sneaking);
  },
};

/** Sitting: the "sitting" token, or the older Mob16 flags (≥ 4) with bit 1. */
function ocelotSits(v: Variant): boolean {
  return v.has('sitting', 'sit') || (!v.isString && v.num >= 4 && (v.num & 1) !== 0);
}

function ocelotSneak(p: Record<string, PartState>): void {
  p.body.py += 1;
  p.head.py += 2;
  p.tail.py += 1;
  p.tail2.py += -4;
  p.tail2.pz += 2;
  p.tail.rx = p.tail2.rx = PI / 2;
}

function ocelotSit(p: Record<string, PartState>): void {
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
}

/** The sprint gallop: legs paired front and back, offset 0.3, a lifted tail tip (f = swing, a = amount). */
function ocelotSprintLegs(p: Record<string, PartState>, f: number, a: number): void {
  p.backLeftLeg.rx = Math.cos(f * 0.6662) * a;
  p.backRightLeg.rx = Math.cos(f * 0.6662 + 0.3) * a;
  p.frontLeftLeg.rx = Math.cos(f * 0.6662 + PI + 0.3) * a;
  p.frontRightLeg.rx = Math.cos(f * 0.6662 + PI) * a;
  p.tail2.rx = 1.7278761 + 0.31415927 * Math.cos(f) * a;
}

/** The walk: diagonal legs together; the tail tip swings less when sneaking. */
function ocelotWalkLegs(p: Record<string, PartState>, f: number, a: number, sneaking: boolean): void {
  p.backLeftLeg.rx = Math.cos(f * 0.6662) * a;
  p.backRightLeg.rx = Math.cos(f * 0.6662 + PI) * a;
  p.frontLeftLeg.rx = Math.cos(f * 0.6662 + PI) * a;
  p.frontRightLeg.rx = Math.cos(f * 0.6662) * a;
  p.tail2.rx = 1.7278761 + (sneaking ? 0.47123894 : 0.7853982) * Math.cos(f) * a;
}

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
  /** ModelShulker: the lid rises with the peek amount (and turns once it is open past 0.3), the head looks about. */
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    const peek = shulkerPeek(c.v);
    const g = (0.5 + peek) * PI;
    const h = -1 + Math.sin(g);
    p.lid.py = 16 + Math.sin(g) * 8 + (g > PI ? Math.sin(c.age * 0.1) * 0.7 : 0);
    p.lid.ry = peek > 0.3 ? h * h * h * h * PI * 0.125 : 0;
  },
};

/** Peek 0..1: "open" → 1, "peek:x" (0..1, or 0..100), a bare number 16..100 (the vanilla peek byte; 0..15 are colours). */
export function shulkerPeek(v: Variant): number {
  if (v.has('open')) return 1;
  const k = v.numOf('peek', NaN);
  if (Number.isFinite(k)) return Math.max(0, Math.min(1, k > 1 ? k / 100 : k));
  if (!v.isString && v.num >= 16) return Math.min(1, v.num / 100);
  return 0;
}

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

// ------------------------------------------------------------------------------------------------------------ looks

/** Whether the wire said anything at all (else skins are picked from the entity id). */
const said = (v: Variant) => v.raw != null;

const HORSE_COAT_NAMES = ['white', 'creamy', 'chestnut', 'brown', 'black', 'gray', 'darkbrown'];
const HORSE_COAT_ALIASES = [...HORSE_COAT_NAMES.slice(0, 5), 'grey', 'dark_brown'];
const HORSE_COLOURS = [0xe8e4dc, 0xc49a62, 0x8a4f26, 0x6b4426, 0x2a2420, 0x7a7670, 0x3f2a1c];
/** Markings by vanilla number (1..4) and their texture suffixes. */
const MARKINGS = ['white', 'whitefield', 'whitedots', 'blackdots'];
const MARKING_TOKENS = [
  ['white', 'white_stockings'],
  ['white_field', 'whitefield'],
  ['white_dots', 'whitedots', 'white_spots'],
  ['black_dots', 'blackdots', 'black_spots'],
];
const HORSE_ARMOURS = ['iron', 'gold', 'diamond'];

/** Coat index 0..6: a coat token ("creamy", "dark_brown"…), else vanilla's variant number (colour | markings << 8). */
function horseCoat(v: Variant, id: number): number {
  let i = v.pick(HORSE_COAT_NAMES, 'color');
  if (i < 0) i = v.pick(HORSE_COAT_ALIASES, 'color');
  if (i >= 0) return i;
  if (!said(v)) return ((id % 7) + 7) % 7;
  return Math.min(6, Math.max(0, (v.num & 0xff) % 7));
}

/** Markings 0 none, 1 white, 2 white field, 3 white dots, 4 black dots. */
function horseMarkings(v: Variant): number {
  const k = v.get('markings');
  for (let m = 0; m < MARKING_TOKENS.length; m++) if (MARKING_TOKENS[m].some((t) => v.has(t) || t === k)) return m + 1;
  if (k === 'none') return 0;
  return v.isString ? 0 : (v.num >> 8) & 0xff;
}

function horseArmour(v: Variant): string | null {
  const k = v.get('armor') ?? v.get('armour');
  const name = k ?? HORSE_ARMOURS.find((a) => v.has(a)) ?? (v.has('golden') ? 'gold' : undefined);
  const n = name === 'golden' ? 'gold' : name;
  return n && HORSE_ARMOURS.includes(n) ? n : null;
}

/** A horse-family look: the coat or skin, then saddle and chest layers when the variant has them, markings and armour
 * (horses only) as layers over the base geometry. */
function horseLook(model: string, texture: string, colour: number, v: Variant, opts: { markings?: boolean; armour?: boolean; chest?: boolean; scale?: number } = {}): Look {
  const layers: LayerUse[] = [];
  if (opts.markings) pushMarkings(layers, v);
  if (v.has('saddle', 'saddled')) layers.push({ layer: 'saddle', texture });
  if (opts.chest && v.has('chest', 'chested')) layers.push({ layer: 'chest', texture });
  if (opts.armour) pushArmour(layers, v);
  return look1(model, texture, colour, { layers, ...(opts.scale ? { scale: opts.scale } : {}) });
}

/** The horse's markings layer, when it has markings. */
function pushMarkings(layers: LayerUse[], v: Variant): void {
  const m = horseMarkings(v);
  if (m >= 1 && m <= 4) layers.push({ texture: `entity/horse/horse_markings_${MARKINGS[m - 1]}` });
}

/** The horse's armour layer, when it wears armour. */
function pushArmour(layers: LayerUse[], v: Variant): void {
  const a = horseArmour(v);
  if (a) layers.push({ texture: `entity/horse/armor/horse_armor_${a}` });
}

/** The cat type the variant names (0 wild ocelot .. 3; −1 = none): a type or skin token, else a plain number below 4 (the
 * older Mob16 flags ≥ 4 name no type). */
function catType(v: Variant): number {
  let t = v.pick(CAT_NAMES, 'type');
  if (t < 0) t = v.pick(['wild', 'tuxedo', 'ginger', 'siamese']);
  if (t < 0 && !v.isString && !(v.num >= 4) && said(v)) t = Math.max(0, Math.min(3, v.num));
  return t;
}

const LLAMA_NAMES = ['creamy', 'white', 'brown', 'gray'];
const LLAMA_COLOURS = [0xd8c8a0, 0xeeeeee, 0x7a5434, 0x8c8c8c];
const DYE_FILES = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];

/** Rabbit skins in vanilla's type order (0 brown, 1 white, 2 black, 3 black-and-white, 4 gold, 5 salt). */
const RABBIT_NAMES = ['brown', 'white', 'black', 'white_splotched', 'gold', 'salt'];
const RABBIT_COLOURS = [0x8a6a4a, 0xf0f0f0, 0x2a2a2a, 0xe0e0e0, 0xd8b060, 0xb09a78];

const PARROT_NAMES = ['red_blue', 'blue', 'green', 'yellow_blue', 'grey'];
const PARROT_COLOURS = [0xd02020, 0x2050d0, 0x40c020, 0x40b0e0, 0xa0a0a0];

/** Ocelot skins by vanilla type (0 wild ocelot, 1 black, 2 red/ginger, 3 siamese). */
const CAT_NAMES = ['ocelot', 'black', 'red', 'siamese'];
const CAT_COLOURS = [0xe0c060, 0x2a2a2a, 0xd08030, 0xd8c8a8];

const look1 = (model: string, texture: string, colour: number, extra: Partial<Look> = {}): Look => ({ model, texture, colour, ...extra });

export const LOOKS: Record<string, LookFn> = {
  // Horses: "creamy,white_dots,saddle,iron" or vanilla's number (colour | markings << 8); the coat is picked from the id when
  // the wire says nothing.
  horse: (v, _b, id) => {
    const c = horseCoat(v, id);
    return horseLook('horse', `entity/horse/horse_${HORSE_COAT_NAMES[c]}`, HORSE_COLOURS[c], v, { markings: true, armour: true });
  },
  donkey: (v) => horseLook('donkey', 'entity/horse/donkey', 0x8a7a6a, v, { chest: true, scale: 0.87 }),
  mule: (v) => horseLook('donkey', 'entity/horse/mule', 0x5a3a24, v, { chest: true, scale: 0.92 }),
  skeleton_horse: (v) => horseLook('horse', 'entity/horse/horse_skeleton', 0xd0d0c8, v),
  zombie_horse: (v) => horseLook('horse', 'entity/horse/horse_zombie', 0x4a7a4a, v),
  // Llamas: "creamy,carpet:red,chest" or the colour number 0..3; a bare dye token or "decor:<dye>" also lays a carpet.
  llama: (v, _b, id) => {
    let c = v.pick(LLAMA_NAMES, 'color');
    if (c < 0) c = v.isString || !said(v) ? ((id % 4) + 4) % 4 : Math.max(0, Math.min(3, v.num));
    const texture = `entity/llama/llama_${LLAMA_NAMES[c]}`;
    const layers: LayerUse[] = [];
    let carpet = v.dye('carpet');
    if (carpet < 0) carpet = v.dye('decor');
    if (carpet < 0 && v.has('carpet')) carpet = v.dye();
    if (carpet >= 0) layers.push({ layer: 'decor', texture: `entity/llama/decor/decor_${DYE_FILES[carpet]}` });
    if (v.has('chest', 'chested')) layers.push({ layer: 'chest', texture });
    return look1('llama', texture, LLAMA_COLOURS[c], { layers });
  },
  polar_bear: () => look1('polar_bear', 'entity/bear/polarbear', 0xf0f0f0),
  // Rabbits: the vanilla type number (99 = the killer bunny) or its name; "toast" by name.
  rabbit: (v, _b, id) => {
    if (v.has('toast')) return look1('rabbit', 'entity/rabbit/toast', 0xc0a080);
    if (v.has('caerbannog', 'killer', 'killer_bunny') || v.num === 99) return look1('rabbit', 'entity/rabbit/caerbannog', 0xf0f0f0);
    let t = v.pick(RABBIT_NAMES, 'type');
    if (t < 0) t = v.pick(['brown', 'white', 'black', 'black_and_white', 'gold', 'salt_and_pepper', 'salt']);
    if (t > 5) t = 5;
    if (t < 0) t = v.isString || !said(v) ? ((id % 6) + 6) % 6 : Math.max(0, Math.min(5, v.num));
    return look1('rabbit', `entity/rabbit/${RABBIT_NAMES[t]}`, RABBIT_COLOURS[t]);
  },
  // Parrots: 0 red_blue, 1 blue, 2 green, 3 yellow_blue, 4 grey (or the names).
  parrot: (v, _b, id) => {
    let t = v.pick(PARROT_NAMES, 'color');
    if (t < 0) t = v.pick(['red', 'blue', 'green', 'cyan', 'gray']);
    if (t < 0) t = v.isString || !said(v) ? ((id % 5) + 5) % 5 : Math.max(0, Math.min(4, v.num));
    return look1('parrot', `entity/parrot/parrot_${PARROT_NAMES[t]}`, PARROT_COLOURS[t]);
  },
  // Ocelots and cats: the type 0..3 (0 wild ocelot) or its name; "tame" without a type picks a cat skin from the id. The older
  // Mob16 flags (numbers ≥ 4: bit 4 tamed, bit 1 sitting) still work. Cats draw at 0.8.
  ocelot: (v, _b, id) => {
    let t = catType(v);
    const legacy = !v.isString && v.num >= 4;
    const tame = v.has('tame', 'tamed') || (legacy && (v.num & 4) !== 0) || t > 0;
    if (tame && t <= 0) t = 1 + (((id % 3) + 3) % 3);
    if (t <= 0) return look1('ocelot', 'entity/cat/ocelot', CAT_COLOURS[0]);
    return look1('ocelot', `entity/cat/${CAT_NAMES[t]}`, CAT_COLOURS[t], { scale: 0.8 });
  },
  bat: () => look1('bat', 'entity/bat', 0x4a3a2a),
  guardian: () => look1('guardian', 'entity/guardian', 0x70a090),
  elder_guardian: () => look1('guardian', 'entity/guardian_elder', 0xc8c0a8, { scale: 2.35 }),
  // Shulkers: a dye colour (token, "color:<dye>" or a number 0..15; vanilla's 16 = the default purple); the lid opens in the
  // pose ("open", "peek:x").
  shulker: (v) => {
    let d = v.dye('color');
    if (d < 0) d = v.dye();
    if (d < 0 && !v.isString && said(v) && v.num >= 0 && v.num < 16) d = v.num;
    return look1('shulker', `entity/shulker/shulker_${d >= 0 ? DYE_FILES[d] : 'purple'}`, 0x9a6a9a);
  },
  ender_dragon: () => look1('ender_dragon', 'entity/enderdragon/dragon', 0x200a30, { layers: [{ texture: 'entity/enderdragon/dragon_eyes', emissive: true }] }),
  // Withers: "invulnerable" (spawning: the blue skin), "armored"/"armoured" (below half health: the scrolling aura).
  wither: (v) =>
    look1('wither', v.has('invulnerable') ? 'entity/wither/wither_invulnerable' : 'entity/wither/wither', 0x1a1a1a, {
      layers: v.has('armored', 'armoured', 'armor', 'armour', 'powered') ? [{ texture: 'entity/wither/wither_armor', translucent: true, emissive: true, scroll: true }] : [],
    }),
};
