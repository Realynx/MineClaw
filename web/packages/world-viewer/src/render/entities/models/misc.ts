// Villagers and their kin (villager, witch, zombie villager, illagers, vex), golems, blaze, magma cube, silverfish,
// endermite, and the objects (boat, minecart, arrow, ender crystal). Dimensions, pivots, rest rotations and texture offsets
// are the vanilla 1.12.2 ones (b1.8.1 boat: `boat_beta`); poses port each model's setRotationAngles.
//
// Objects are not drawn by RenderLivingBase: where their renderer differs from the living root transform (types.ts) a
// wrapper part ("root") carries the difference. Our root maps a model point to world y = scale · (1.501 − y / 16), so a
// wrapper at y = 24.016 − 16·h puts its origin h blocks above the entity position; rotZ(π) on it turns the y-down model
// frame into the vanilla y-up frame of renderers that never flip (arrow, ender crystal).

import { BIPED_BABY, box, D2R, look, mbox, part, PI } from '../dsl';
import type { CubeDef, Look, LookFn, ModelDef, PartState, PoseCtx, PoseFn } from '../types';
import type { Variant } from '../variant';

/** Model y (pixels) of the point `h` blocks above the entity position (scale 1). */
const lift = (h: number) => 24.016 - 16 * h;

// ------------------------------------------------------------------------------------------------------------ shared poses

/** ModelBiped.setRotationAngles (head, walk swing, riding, arm swing, idle sway); every part optional. */
function bipedPose(p: Record<string, PartState>, c: PoseCtx): void {
  const head = p.head, body = p.body, ra = p.rightArm, la = p.leftArm, rl = p.rightLeg, ll = p.leftLeg;
  look(head, c.headYaw, c.pitch);
  limbSwing(ra, la, rl, ll, c.swing, c.amount);
  if (c.riding) ridingLimbs(ra, la, rl, ll);
  if (c.attack > 0 && body && ra && la) attackSwing(head, body, ra, la, c.attack);
  armSway(ra, la, c.age);
}

/** The walk swing: arms and legs in opposite phase (f = swing, a = amount). */
function limbSwing(ra: PartState | undefined, la: PartState | undefined, rl: PartState | undefined, ll: PartState | undefined, f: number, a: number): void {
  if (ra) ra.rx = Math.cos(f * 0.6662 + PI) * 2 * a * 0.5;
  if (la) la.rx = Math.cos(f * 0.6662) * 2 * a * 0.5;
  if (rl) rl.rx = Math.cos(f * 0.6662) * 1.4 * a;
  if (ll) ll.rx = Math.cos(f * 0.6662 + PI) * 1.4 * a;
}

/** Riding: arms forward, legs spread out in front. */
function ridingLimbs(ra: PartState | undefined, la: PartState | undefined, rl: PartState | undefined, ll: PartState | undefined): void {
  if (ra) ra.rx -= PI / 5;
  if (la) la.rx -= PI / 5;
  if (rl) Object.assign(rl, { rx: -1.4137167, ry: PI / 10, rz: 0.07853982 });
  if (ll) Object.assign(ll, { rx: -1.4137167, ry: -PI / 10, rz: -0.07853982 });
}

/** The attack swing at progress g: the body turns, the shoulders follow it, the right arm chops down. */
function attackSwing(head: PartState | undefined, body: PartState, ra: PartState, la: PartState, g: number): void {
  body.ry = Math.sin(Math.sqrt(g) * PI * 2) * 0.2;
  ra.pz = Math.sin(body.ry) * 5;
  ra.px = -Math.cos(body.ry) * 5;
  la.pz = -Math.sin(body.ry) * 5;
  la.px = Math.cos(body.ry) * 5;
  ra.ry += body.ry;
  la.ry += body.ry;
  la.rx += body.ry;
  let h = 1 - g;
  h *= h;
  h *= h;
  h = 1 - h;
  ra.rx -= Math.sin(h * PI) * 1.2 + Math.sin(g * PI) * -((head?.rx ?? 0) - 0.7) * 0.75;
  ra.ry += body.ry * 2;
  ra.rz += Math.sin(g * PI) * -0.4;
}

/** The idle arm sway every biped-like model adds. */
function armSway(ra: PartState | undefined, la: PartState | undefined, age: number): void {
  if (ra) {
    ra.rz += Math.cos(age * 0.09) * 0.05 + 0.05;
    ra.rx += Math.sin(age * 0.067) * 0.05;
  }
  if (la) {
    la.rz -= Math.cos(age * 0.09) * 0.05 + 0.05;
    la.rx -= Math.sin(age * 0.067) * 0.05;
  }
}

/** Villager-style legs (half the biped swing) and the folded arms. */
function villagerLegs(p: Record<string, PartState>, c: PoseCtx): void {
  if (p.rightLeg) p.rightLeg.rx = Math.cos(c.swing * 0.6662) * 1.4 * c.amount * 0.5;
  if (p.leftLeg) p.leftLeg.rx = Math.cos(c.swing * 0.6662 + PI) * 1.4 * c.amount * 0.5;
}

function foldedArms(arms: PartState | undefined): void {
  if (!arms) return;
  arms.py = 3;
  arms.pz = -1;
  arms.rx = -0.75;
}

// ------------------------------------------------------------------------------------------------------------ villagers

/** ModelVillager parts (scale = box inflate), texture `tex`; `extraHead` adds children to the head. */
function villagerParts(nose: CubeDef[] = [box(-1, -1, -6, 2, 4, 2, 24, 0)], noseChildren = [] as ReturnType<typeof part>[], headChildren = [] as ReturnType<typeof part>[]) {
  return [
    part('head', [0, 0, 0], [box(-4, -10, -4, 8, 10, 8, 0, 0)], { children: [part('nose', [0, -2, 0], nose, { children: noseChildren }), ...headChildren] }),
    part('body', [0, 0, 0], [box(-4, 0, -3, 8, 12, 6, 16, 20), box(-4, 0, -3, 8, 18, 6, 0, 38, 0.5)]),
    part('arms', [0, 3, -1], [box(-8, -2, -2, 4, 8, 4, 44, 22), box(4, -2, -2, 4, 8, 4, 44, 22), box(-4, 2, -2, 8, 4, 4, 40, 38)], { rot: [-0.75, 0, 0] }),
    part('rightLeg', [-2, 12, 0], [box(-2, 0, -2, 4, 12, 4, 0, 22)]),
    part('leftLeg', [2, 12, 0], [mbox(-2, 0, -2, 4, 12, 4, 0, 22)]),
  ];
}

const poseVillager: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  foldedArms(p.arms);
  villagerLegs(p, c);
};

export const VILLAGER: ModelDef = { id: 'villager', tex: [64, 64], parts: villagerParts(), scale: 0.9375, width: 0.6, height: 1.95, pose: poseVillager };

/** ModelWitch: the villager with a mole on the nose and a four-tier hat (64×128). */
export const WITCH: ModelDef = {
  id: 'witch',
  tex: [64, 128],
  parts: villagerParts(
    undefined,
    [part('mole', [0, -2, 0], [box(0, 3, -6.75, 1, 1, 1, 0, 0, -0.25)])],
    [
      part('hat', [-5, -10.03125, -5], [box(0, 0, 0, 10, 2, 10, 0, 64)], {
        children: [
          part('hat2', [1.75, -4, 2], [box(0, 0, 0, 7, 4, 7, 0, 76)], {
            rot: [-0.05235988, 0, 0.02617994],
            children: [
              part('hat3', [1.75, -4, 2], [box(0, 0, 0, 4, 4, 4, 0, 87)], {
                rot: [-0.10471976, 0, 0.05235988],
                children: [part('hat4', [1.75, -2, 2], [box(0, 0, 0, 1, 2, 1, 0, 95, 0.25)], { rot: [-0.20943952, 0, 0.10471976] })],
              }),
            ],
          }),
        ],
      }),
    ],
  ),
  scale: 0.9375,
  width: 0.6,
  height: 1.95,
  pose: (p, c) => {
    poseVillager(p, c);
    // The nose twitches at a per-witch rate (vanilla: 0.01 · (entity id % 10)).
    const f = 0.01 * Math.floor(c.seed * 10);
    if (p.nose) {
      p.nose.rx = Math.sin(c.age * f) * 4.5 * D2R;
      p.nose.rz = Math.cos(c.age * f) * 2.5 * D2R;
    }
  },
};

/** ModelZombieVillager (64×64): villager head and robe on a biped, zombie arms. */
export const ZOMBIE_VILLAGER: ModelDef = {
  id: 'zombie_villager',
  baby: BIPED_BABY,
  tex: [64, 64],
  parts: [
    part('head', [0, 0, 0], [box(-4, -10, -4, 8, 10, 8, 0, 0), box(-1, -3, -6, 2, 4, 2, 24, 0)], {
      children: [part('hat', [0, 0, 0], [box(-4, -8, -4, 8, 8, 8, 32, 0, 0.5)])],
    }),
    part('body', [0, 0, 0], [box(-4, 0, -3, 8, 12, 6, 16, 20), box(-4, 0, -3, 8, 18, 6, 0, 38, 0.05)]),
    part('rightArm', [-5, 2, 0], [box(-3, -2, -2, 4, 12, 4, 44, 38)]),
    part('leftArm', [5, 2, 0], [mbox(-1, -2, -2, 4, 12, 4, 44, 38)]),
    part('rightLeg', [-2, 12, 0], [box(-2, 0, -2, 4, 12, 4, 0, 22)]),
    part('leftLeg', [2, 12, 0], [mbox(-2, 0, -2, 4, 12, 4, 0, 22)]),
  ],
  width: 0.6,
  height: 1.95,
  pose: (p, c) => {
    bipedPose(p, c);
    const ra = p.rightArm, la = p.leftArm, g = c.attack;
    const f = Math.sin(g * PI), h = Math.sin((1 - (1 - g) * (1 - g)) * PI);
    ra.rz = la.rz = 0;
    ra.ry = -(0.1 - f * 0.6);
    la.ry = 0.1 - f * 0.6;
    ra.rx = la.rx = -PI / (c.aggressive ? 1.5 : 2.25) + f * 1.2 - h * 0.4;
    armSway(ra, la, c.age);
  },
};

// ------------------------------------------------------------------------------------------------------------ illagers

type IllagerPose = 'vindicator' | 'evoker' | 'illusioner';

/** ModelIllager (64×64); the crossed arms or the separate arms show by pose, the hat only on the illusioner. */
function illager(id: string, kind: IllagerPose): ModelDef {
  return {
    id,
    tex: [64, 64],
    parts: [
      part('head', [0, 0, 0], [box(-4, -10, -4, 8, 10, 8, 0, 0)], {
        children: [part('hat', [0, 0, 0], [box(-4, -10, -4, 8, 12, 8, 32, 0, 0.45)]), part('nose', [0, -2, 0], [box(-1, -1, -6, 2, 4, 2, 24, 0)])],
      }),
      part('body', [0, 0, 0], [box(-4, 0, -3, 8, 12, 6, 16, 20), box(-4, 0, -3, 8, 18, 6, 0, 38, 0.5)]),
      part('arms', [0, 3, -1], [box(-8, -2, -2, 4, 8, 4, 44, 22), box(-4, 2, -2, 8, 4, 4, 40, 38)], {
        rot: [-0.75, 0, 0],
        children: [part('armsLeft', [0, 0, 0], [mbox(4, -2, -2, 4, 8, 4, 44, 22)])],
      }),
      part('rightLeg', [-2, 12, 0], [box(-2, 0, -2, 4, 12, 4, 0, 22)]),
      part('leftLeg', [2, 12, 0], [mbox(-2, 0, -2, 4, 12, 4, 0, 22)]),
      part('rightArm', [-5, 2, 0], [box(-3, -2, -2, 4, 12, 4, 40, 46)]),
      part('leftArm', [5, 2, 0], [mbox(-1, -2, -2, 4, 12, 4, 40, 46)]),
    ],
    scale: 0.9375,
    width: 0.6,
    height: 1.95,
    pose: (p, c) => {
      look(p.head, c.headYaw, c.pitch);
      foldedArms(p.arms);
      villagerLegs(p, c);
      p.hat.visible = kind === 'illusioner';
      // Arm pose (AbstractIllager.getArmPose): aggression is the only selector we could ever get; crossed otherwise.
      const pose = !c.aggressive ? 'crossed' : kind === 'vindicator' ? 'attacking' : kind === 'evoker' ? 'spellcasting' : 'bow';
      const crossed = pose === 'crossed';
      p.arms.visible = p.armsLeft.visible = crossed;
      p.rightArm.visible = p.leftArm.visible = !crossed;
      const ra = p.rightArm, la = p.leftArm, head = p.head;
      if (pose === 'attacking') {
        const g = c.attack, f = Math.sin(g * PI), h = Math.sin((1 - (1 - g) * (1 - g)) * PI);
        ra.ry = 0.15707964;
        la.ry = -0.15707964;
        ra.rx = -1.8849558 + Math.cos(c.age * 0.09) * 0.15 + f * 2.2 - h * 0.4;
        la.rx = Math.cos(c.age * 0.19) * 0.5 + f * 1.2 - h * 0.4;
        armSway(ra, la, c.age);
      } else if (pose === 'spellcasting') {
        ra.rx = la.rx = Math.cos(c.age * 0.6662) * 0.25;
        ra.rz = 2.3561945;
        la.rz = -2.3561945;
      } else if (pose === 'bow') {
        ra.ry = -0.1 + head.ry;
        ra.rx = -PI / 2 + head.rx;
        la.rx = -0.9424779 + head.rx;
        la.ry = head.ry - 0.4;
        la.rz = PI / 2;
      }
    },
  };
}

export const VINDICATOR = illager('vindicator', 'vindicator');
export const EVOKER = illager('evoker', 'evoker');
export const ILLUSIONER = illager('illusioner', 'illusioner');

/** ModelVex: a biped (no left leg, no hat) with one tucked leg and two flapping wings; RenderVex scales 0.4. */
export const VEX: ModelDef = {
  id: 'vex',
  tex: [64, 64],
  parts: [
    part('head', [0, 0, 0], [box(-4, -8, -4, 8, 8, 8, 0, 0)]),
    part('body', [0, 0, 0], [box(-4, 0, -2, 8, 12, 4, 16, 16)]),
    part('rightArm', [-5, 2, 0], [box(-3, -2, -2, 4, 12, 4, 40, 16)]),
    part('leftArm', [5, 2, 0], [mbox(-1, -2, -2, 4, 12, 4, 40, 16)]),
    part('rightLeg', [-1.9, 12, 0], [box(-1, -1, -2, 6, 10, 4, 32, 0)]),
    part('rightWing', [0, 1, 2], [box(-20, 0, 0, 20, 12, 1, 0, 32)]),
    part('leftWing', [0, 1, 2], [mbox(0, 0, 0, 20, 12, 1, 0, 32)]),
  ],
  scale: 0.4,
  width: 0.4,
  height: 0.8,
  pose: (p, c) => {
    bipedPose(p, c);
    if (c.aggressive) p.rightArm.rx = 3.7699115; // charging
    p.rightLeg.rx += 0.62831855;
    const w = 0.47123894 + Math.cos(c.age * 0.8) * PI * 0.05;
    Object.assign(p.rightWing, { rx: 0.47123894, ry: w, rz: 0.47123894 });
    Object.assign(p.leftWing, { rx: 0.47123894, ry: -w, rz: -0.47123894 });
  },
};

// ------------------------------------------------------------------------------------------------------------ golems

/** ModelIronGolem.triangleWave. */
const tri = (x: number, period: number) => (Math.abs((x % period) - period * 0.5) - period * 0.25) / (period * 0.25);

/** ModelIronGolem (128×128). Vanilla's "left" leg is the one on the model's right (−x); names kept. */
export const IRON_GOLEM: ModelDef = {
  id: 'iron_golem',
  tex: [128, 128],
  parts: [
    part('head', [0, -7, -2], [box(-4, -12, -5.5, 8, 10, 8, 0, 0), box(-1, -5, -7.5, 2, 4, 2, 24, 0)]),
    part('body', [0, -7, 0], [box(-9, -2, -6, 18, 12, 11, 0, 40), box(-4.5, 10, -3, 9, 5, 6, 0, 70, 0.5)]),
    part('rightArm', [0, -7, 0], [box(-13, -2.5, -3, 4, 30, 6, 60, 21)]),
    part('leftArm', [0, -7, 0], [box(9, -2.5, -3, 4, 30, 6, 60, 58)]),
    part('leftLeg', [-4, 11, 0], [box(-3.5, -3, -3, 6, 16, 5, 37, 0)]),
    part('rightLeg', [5, 11, 0], [mbox(-3.5, -3, -3, 6, 16, 5, 60, 0)]),
  ],
  width: 1.4,
  height: 2.7,
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    const t = tri(c.swing, 13) * c.amount;
    p.leftLeg.rx = -1.5 * t;
    p.rightLeg.rx = 1.5 * t;
    if (c.attack > 0) {
      // The attack timer counts 10 → 0; swingProgress runs 0 → 1.
      p.rightArm.rx = p.leftArm.rx = -2 + 1.5 * tri(10 * (1 - c.attack), 10);
    } else if (c.rose) {
      // setLivingAnimations while holdRoseTick > 0: the right arm held out with the flower, the left one still.
      p.rightArm.rx = -0.8 + 0.025 * tri(c.rose, 70);
      p.leftArm.rx = 0;
    } else {
      p.rightArm.rx = (-0.2 + 1.5 * tri(c.swing, 13)) * c.amount;
      p.leftArm.rx = (-0.2 - 1.5 * tri(c.swing, 13)) * c.amount;
    }
  },
};

/** ModelSnowMan (64×64), without the pumpkin (a block). */
export const SNOW_GOLEM: ModelDef = {
  id: 'snow_golem',
  tex: [64, 64],
  parts: [
    part('head', [0, 4, 0], [box(-4, -8, -4, 8, 8, 8, 0, 0, -0.5)]),
    part('body', [0, 13, 0], [box(-5, -10, -5, 10, 10, 10, 0, 16, -0.5)]),
    part('bottomBody', [0, 24, 0], [box(-6, -12, -6, 12, 12, 12, 0, 36, -0.5)]),
    part('rightHand', [5, 6, 0], [box(-1, 0, -1, 12, 2, 2, 32, 0, -0.5)], { rot: [0, 0, 1] }),
    part('leftHand', [-5, 6, 0], [box(-1, 0, -1, 12, 2, 2, 32, 0, -0.5)], { rot: [0, PI, -1] }),
  ],
  width: 0.7,
  height: 1.9,
  pose: (p, c) => {
    look(p.head, c.headYaw, c.pitch);
    const by = c.headYaw * D2R * 0.25, s = Math.sin(by), k = Math.cos(by);
    p.body.ry = by;
    Object.assign(p.rightHand, { rz: 1, ry: by, px: k * 5, pz: -s * 5 });
    Object.assign(p.leftHand, { rz: -1, ry: PI + by, px: -k * 5, pz: s * 5 });
  },
};

// ------------------------------------------------------------------------------------------------------------ nether, bugs

/** ModelBlaze: a head and three rings of four rods (2×8×2) orbiting at different heights and speeds. */
export const BLAZE: ModelDef = {
  id: 'blaze',
  tex: [64, 32],
  parts: [
    part('head', [0, 0, 0], [box(-4, -4, -4, 8, 8, 8, 0, 0)]),
    ...Array.from({ length: 12 }, (_, i) => part(`rod${i}`, [0, 0, 0], [box(0, 0, 0, 2, 8, 2, 0, 16)])),
  ],
  width: 0.6,
  height: 1.8,
  pose: (p, c) => {
    const t = c.age;
    const ring = (from: number, start: number, r: number, y: (k: number) => number) => {
      let f = start;
      for (let k = from; k < from + 4; k++) {
        const rod = p[`rod${k}`];
        rod.py = y(k);
        rod.px = Math.cos(f) * r;
        rod.pz = Math.sin(f) * r;
        f += PI / 2;
      }
    };
    ring(0, t * PI * -0.1, 9, (k) => -2 + Math.cos((k * 2 + t) * 0.25));
    ring(4, PI / 4 + t * PI * 0.03, 7, (k) => 2 + Math.cos((k * 2 + t) * 0.25));
    ring(8, 0.47123894 + t * PI * -0.05, 5, (k) => 11 + Math.cos((k * 1.5 + t) * 0.5));
    look(p.head, c.headYaw, c.pitch);
  },
};

/** ModelMagmaCube: eight 8×8×1 slices around a 4³ core; Look.scale is the size (squish isn't on the wire: at rest). */
export const MAGMA_CUBE: ModelDef = {
  id: 'magma_cube',
  tex: [64, 32],
  parts: [
    part('core', [0, 0, 0], [box(-2, 18, -2, 4, 4, 4, 0, 16)]),
    ...Array.from({ length: 8 }, (_, i) => part(`slice${i}`, [0, 0, 0], [box(-4, 16 + i, -4, 8, 1, 8, i === 2 || i === 3 ? 24 : 0, i === 2 ? 10 : i === 3 ? 19 : i)])),
  ],
  width: 0.51,
  height: 0.51,
};

/** Segment sizes [x, y, z] and texture offsets of a segmented crawler; segments are centred and laid end to end on z. */
function segments(sizes: number[][], uvs: number[][], name: string) {
  let z = -3.5;
  const zs: number[] = [];
  const parts = sizes.map((s, i) => {
    zs.push(z);
    const p = part(`${name}${i}`, [0, 24 - s[1], z], [box(s[0] * -0.5, 0, s[2] * -0.5, s[0], s[1], s[2], uvs[i][0], uvs[i][1])]);
    if (i < sizes.length - 1) z += (s[2] + sizes[i + 1][2]) * 0.5;
    return p;
  });
  return { parts, zs };
}

function wiggle(p: Record<string, PartState>, n: number, name: string, age: number, yawK: number, xK: number): void {
  for (let i = 0; i < n; i++) {
    const q = p[`${name}${i}`], a = age * 0.9 + i * 0.15 * PI;
    q.ry = Math.cos(a) * PI * yawK * (1 + Math.abs(i - 2));
    q.px = Math.sin(a) * PI * xK * Math.abs(i - 2);
  }
}

const SF = segments(
  [[3, 2, 2], [4, 3, 2], [6, 4, 3], [3, 3, 3], [2, 2, 3], [2, 1, 2], [1, 1, 2]],
  [[0, 0], [0, 4], [0, 9], [0, 16], [0, 22], [11, 0], [13, 4]],
  'seg',
);

/** ModelSilverfish: seven segments and three fur layers riding segments 2, 4 and 1. */
export const SILVERFISH: ModelDef = {
  id: 'silverfish',
  tex: [64, 32],
  parts: [
    ...SF.parts,
    part('fur0', [0, 16, SF.zs[2]], [box(-5, 0, -1.5, 10, 8, 3, 20, 0)]),
    part('fur1', [0, 20, SF.zs[4]], [box(-3, 0, -1.5, 6, 4, 3, 20, 11)]),
    part('fur2', [0, 19, SF.zs[1]], [box(-3, 0, -1.5, 6, 5, 2, 20, 18)]),
  ],
  width: 0.4,
  height: 0.3,
  pose: (p, c) => {
    wiggle(p, 7, 'seg', c.age, 0.05, 0.2);
    p.fur0.ry = p.seg2.ry;
    p.fur1.ry = p.seg4.ry;
    p.fur1.px = p.seg4.px;
    p.fur2.ry = p.seg1.ry;
    p.fur2.px = p.seg1.px;
  },
};

/** ModelEnderMite: four segments. */
export const ENDERMITE: ModelDef = {
  id: 'endermite',
  tex: [64, 32],
  parts: segments([[4, 3, 2], [6, 4, 5], [3, 3, 1], [1, 2, 1]], [[0, 0], [0, 5], [0, 14], [0, 18]], 'seg').parts,
  width: 0.4,
  height: 0.3,
  pose: (p, c) => wiggle(p, 4, 'seg', c.age, 0.01, 0.1),
};

// ------------------------------------------------------------------------------------------------------------ objects

/** ModelBoat (1.12.2, 128×64): bottom, four sides and two paddles; RenderBoat lifts it 0.375 and the model turns 90°. */
export const BOAT: ModelDef = {
  id: 'boat',
  tex: [128, 64],
  parts: [
    part('root', [0, lift(0.375), 0], [], {
      rot: [0, PI / 2, 0],
      children: [
        part('bottom', [0, 3, 1], [box(-14, -9, -3, 28, 16, 3, 0, 0)], { rot: [PI / 2, 0, 0] }),
        part('back', [-15, 4, 4], [box(-13, -7, -1, 18, 6, 2, 0, 19)], { rot: [0, (3 * PI) / 2, 0] }),
        part('front', [15, 4, 0], [box(-8, -7, -1, 16, 6, 2, 0, 27)], { rot: [0, PI / 2, 0] }),
        part('right', [0, 4, -9], [box(-14, -7, -1, 28, 6, 2, 0, 35)], { rot: [0, PI, 0] }),
        part('left', [0, 4, 9], [box(-14, -7, -1, 28, 6, 2, 0, 43)]),
        part('paddleLeft', [3, -5, 9], [box(-1, 0, -5, 2, 2, 18, 62, 0), box(-1.001, -3, 8, 1, 6, 7, 62, 0)]),
        part('paddleRight', [3, -5, -9], [box(-1, 0, -5, 2, 2, 18, 62, 20), box(0.001, -3, 8, 1, 6, 7, 62, 20)]),
      ],
    }),
  ],
  width: 1.375,
  height: 0.5625,
  pose: (p) => {
    // ModelBoat.renderPaddle at rowing time 0 (paddles resting; rowing isn't on the wire).
    const rx = -1.0471976 + (-0.2617994 + 1.0471976) * 0.5;
    const ry = -0.7853982 + 1.5707964 * ((Math.sin(1) + 1) / 2);
    Object.assign(p.paddleLeft, { rx, ry, rz: 0.19634955 });
    Object.assign(p.paddleRight, { rx, ry: PI - ry, rz: 0.19634955 });
  },
};

/** b1.8.1 ModelBoat (64×32): a bottom board and four sides (item/boat.png); its renderer has no lift. */
export const BOAT_BETA: ModelDef = {
  id: 'boat_beta',
  tex: [64, 32],
  parts: [
    part('bottom', [0, 4, 0], [box(-12, -8, -3, 24, 16, 4, 0, 8)], { rot: [PI / 2, 0, 0] }),
    part('back', [-11, 4, 0], [box(-10, -7, -1, 20, 6, 2, 0, 0)], { rot: [0, (3 * PI) / 2, 0] }),
    part('front', [11, 4, 0], [box(-10, -7, -1, 20, 6, 2, 0, 0)], { rot: [0, PI / 2, 0] }),
    part('right', [0, 4, -9], [box(-10, -7, -1, 20, 6, 2, 0, 0)], { rot: [0, PI, 0] }),
    part('left', [0, 4, 9], [box(-10, -7, -1, 20, 6, 2, 0, 0)]),
  ],
  width: 1.5,
  height: 0.6,
};

/** ModelMinecart (64×32): floor, four sides and the inner floor; RenderMinecart lifts it 0.375. */
export const MINECART: ModelDef = {
  id: 'minecart',
  tex: [64, 32],
  parts: [
    part('root', [0, lift(0.375), 0], [], {
      children: [
        part('bottom', [0, 4, 0], [box(-10, -8, -1, 20, 16, 2, 0, 10)], { rot: [PI / 2, 0, 0] }),
        part('back', [-9, 4, 0], [box(-8, -9, -1, 16, 8, 2, 0, 0)], { rot: [0, (3 * PI) / 2, 0] }),
        part('front', [9, 4, 0], [box(-8, -9, -1, 16, 8, 2, 0, 0)], { rot: [0, PI / 2, 0] }),
        part('right', [0, 4, -7], [box(-8, -9, -1, 16, 8, 2, 0, 0)], { rot: [0, PI, 0] }),
        part('left', [0, 4, 7], [box(-8, -9, -1, 16, 8, 2, 0, 0)]),
        // Vanilla's inner floor box runs past the 64 px texture width (only its top face samples inside it).
        part('inner', [0, 4.1, 0], [box(-9, -7, -1, 18, 14, 1, 44, 10)], { rot: [-PI / 2, 0, 0] }),
      ],
    }),
  ],
  width: 0.98,
  height: 0.7,
};

/** A zero-thickness plane cube showing `rect` on both faces of its thin axis (`flip` mirrors u on the far face so both
 * sides read the same way). `faces` rects replace the face's box-layout rectangle with the same corner assignment. */
function plane(o: readonly [number, number, number], s: readonly [number, number, number], a: 'west' | 'north', rect: readonly [number, number, number, number]): CubeDef {
  const b = a === 'west' ? 'east' : 'south';
  const flipped = [rect[2], rect[1], rect[0], rect[3]] as const;
  return { o, s, uv: [0, 0], faces: { [a]: rect, [b]: flipped } };
}

/** RenderArrow (32×32): two crossed shaft planes 16×4 and the fletching cross 4×4, in its 0.05625-block units (0.9 px),
 * pointing at the model front (−z, the facing direction); c.pitch tilts it (positive = nose down). */
export const ARROW: ModelDef = {
  id: 'arrow',
  tex: [32, 32],
  parts: [
    part('root', [0, lift(0), 0], [], {
      children: [
        part('shaftA', [0, 0, 0], [plane([0, -1.8, -3.6], [0, 3.6, 14.4], 'west', [0, 0, 16, 5])], { rot: [0, 0, PI / 4] }),
        part('shaftB', [0, 0, 0], [plane([0, -1.8, -3.6], [0, 3.6, 14.4], 'west', [0, 0, 16, 5])], { rot: [0, 0, (3 * PI) / 4] }),
        part('nock', [0, 0, 0], [plane([-1.8, -1.8, 9.9], [3.6, 3.6, 0], 'north', [0, 5, 5, 10])], { rot: [0, 0, PI / 4] }),
      ],
    }),
  ],
  width: 0.5,
  height: 0.5,
  pose: (p, c) => {
    p.root.rx = c.pitch * D2R;
  },
};

/** ZYX Euler angles [rx, ry, rz] of a 3×3 row-major rotation (M = Rz · Ry · Rx, our part order). */
function euler(m: number[]): [number, number, number] {
  const b = Math.asin(Math.max(-1, Math.min(1, -m[6])));
  return [Math.atan2(m[7], m[8]), b, Math.atan2(m[3], m[0])];
}

function mul3(a: number[], b: number[]): number[] {
  const r = new Array<number>(9);
  for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
  return r;
}

const rotY3 = (t: number) => [Math.cos(t), 0, Math.sin(t), 0, 1, 0, -Math.sin(t), 0, Math.cos(t)];

/** GL rotate(60°, 0.7071, 0, 0.7071). */
const TILT = (() => {
  const t = PI / 3, c = Math.cos(t), s = Math.sin(t), x = Math.SQRT1_2, z = Math.SQRT1_2, k = 1 - c;
  return [c + x * x * k, -z * s, x * z * k, z * s, c, -x * s, x * z * k, x * s, c + z * z * k];
})();

function setRot(q: PartState, m: number[]): void {
  [q.rx, q.ry, q.rz] = euler(m);
}

/** ModelEnderCrystal (64×32, ×2): base slab, two nested glass cubes and the core, each tilted 60° and spinning; the
 * glass bobs. Vanilla's nested 0.875 scales are the inner boxes' negative inflates (the boxes are centred). */
export const ENDER_CRYSTAL: ModelDef = {
  id: 'ender_crystal',
  tex: [64, 32],
  parts: [
    // Vanilla frame (y up) whose origin is 0.5 scaled units (1 block) below the entity position.
    part('root', [0, 32.016, 0], [], {
      rot: [0, 0, PI],
      children: [
        part('base', [0, 0, 0], [box(-6, 0, -6, 12, 4, 12, 0, 16)]),
        part('glass', [0, 12.8, 0], [box(-4, -4, -4, 8, 8, 8, 0, 0)], {
          children: [part('glass2', [0, 0, 0], [box(-4, -4, -4, 8, 8, 8, 0, 0, -0.5)], { children: [part('core', [0, 0, 0], [box(-4, -4, -4, 8, 8, 8, 32, 0, -0.9375)])] })],
        }),
      ],
    }),
  ],
  scale: 2,
  width: 2,
  height: 2,
  pose: (p, c) => {
    const spin = rotY3(c.age * 3 * D2R);
    let g = Math.sin(c.age * 0.2) / 2 + 0.5;
    g = g * g + g;
    p.glass.py = (0.8 + g * 0.2) * 16;
    setRot(p.glass, mul3(spin, TILT));
    const inner = mul3(TILT, spin);
    setRot(p.glass2, inner);
    setRot(p.core, inner);
  },
};

export const MODELS: ModelDef[] = [
  VILLAGER, WITCH, ZOMBIE_VILLAGER, VINDICATOR, EVOKER, ILLUSIONER, VEX, IRON_GOLEM, SNOW_GOLEM, BLAZE, MAGMA_CUBE, SILVERFISH, ENDERMITE,
  BOAT, BOAT_BETA, MINECART, ARROW, ENDER_CRYSTAL,
];

// ------------------------------------------------------------------------------------------------------------ looks

const PROFESSIONS = ['farmer', 'librarian', 'priest', 'smith', 'butcher', 'nitwit'];
const WOODS = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'darkoak'];
const WOOD_NAMES = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'dark_oak'];

/** A villager profession 0..5 from the variant (a number, or a name / "profession:" token), else picked by entity id. */
function profession(v: Variant, id: number): number {
  const byName = v.pick(PROFESSIONS, 'profession');
  if (byName >= 0) return byName;
  if (v.isString ? /^\d+$/.test(v.tokens[0] ?? '') : v.raw != null) return Math.max(0, Math.min(5, v.num));
  return ((id % 5) + 5) % 5;
}

const crystal: LookFn = () => ({ model: 'ender_crystal', texture: 'entity/endercrystal/endercrystal', colour: 0xc080d0 });

export const LOOKS: Record<string, LookFn> = {
  // Nitwits wear the plain villager skin (1.12.2 has no nitwit texture).
  villager: (v, _b, id) => {
    const p = profession(v, id);
    return { model: 'villager', texture: `entity/villager/${p === 5 ? 'villager' : PROFESSIONS[p]}`, colour: 0x9a6a4a };
  },
  witch: () => ({ model: 'witch', texture: 'entity/witch', colour: 0x4a2a5a }),
  zombie_villager: (v, _b, id) => {
    const p = profession(v, id);
    return { model: 'zombie_villager', texture: p === 5 ? 'entity/zombie_villager/zombie_villager' : `entity/zombie_villager/zombie_${PROFESSIONS[p]}`, colour: 0x4a7a4a };
  },
  villager_golem: () => ({ model: 'iron_golem', texture: 'entity/iron_golem', colour: 0xc8c0b0 }),
  snowman: () => ({ model: 'snow_golem', texture: 'entity/snowman', colour: 0xf0f8ff }),
  vindication_illager: () => ({ model: 'vindicator', texture: 'entity/illager/vindicator', colour: 0x5a6a6a }),
  evocation_illager: () => ({ model: 'evoker', texture: 'entity/illager/evoker', colour: 0x3a3a3a }),
  illusion_illager: () => ({ model: 'illusioner', texture: 'entity/illager/illusionist', colour: 0x2a3a7a }),
  vex: (v) => ({ model: 'vex', texture: v.has('charging', 'angry') ? 'entity/illager/vex_charging' : 'entity/illager/vex', colour: 0x8aa0c0 }),
  blaze: () => ({ model: 'blaze', texture: 'entity/blaze', colour: 0xf0b020 }),
  magma_cube: (v) => ({ model: 'magma_cube', texture: 'entity/slime/magmacube', scale: Math.max(1, v.numOf('size', v.num) || 1), colour: 0x8a2a10 }),
  silverfish: (_v, beta) => ({ model: 'silverfish', texture: beta ? 'mob/silverfish' : 'entity/silverfish', colour: 0x9a9a9a }),
  endermite: () => ({ model: 'endermite', texture: 'entity/endermite', colour: 0x2a1a3a }),
  ender_crystal: crystal,
  'object:ender_crystal': crystal,
  'object:boat': (v, beta) => {
    if (beta) return { model: 'boat_beta', texture: 'item/boat', colour: 0x8a6a3a };
    const w = v.pick(WOOD_NAMES, 'wood');
    const k = w >= 0 ? w : v.pick(WOODS) >= 0 ? v.pick(WOODS) : v.num >= 0 && v.num < 6 ? v.num : 0;
    return { model: 'boat', texture: `entity/boat/boat_${WOODS[k]}`, colour: 0x8a6a3a };
  },
  'object:minecart': (_v, beta) => ({ model: 'minecart', texture: beta ? 'item/cart' : 'entity/minecart', colour: 0x6a6a6a }),
  'object:arrow': (_v, beta) => ({ model: 'arrow', texture: beta ? 'item/arrows' : 'entity/projectiles/arrow', colour: 0x9a7a5a }),
  'object:spectral_arrow': () => ({ model: 'arrow', texture: 'entity/projectiles/spectral_arrow', colour: 0xd8c040 }),
};
