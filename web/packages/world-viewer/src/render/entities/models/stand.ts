// The armor stand (ModelArmorStand, entity/armorstand/wood.png 64×64): ModelBiped's six parts at the armor stand's pivots
// (head at y 1, legs at y 11, like ModelArmorStandArmor), so armour pieces and held items attach to them exactly as on a
// player; the stand's own sticks are cubes on those parts or their children, and the base plate is its own part. The arms
// are hidden unless the variant says "arms" (vanilla's default), but only their sticks: armour sleeves and held items stay.
// The default limb angles are EntityArmorStand's default rotations; custom poses are not on the wire.

import { box, D2R, look, part } from '../dsl';
import type { LookFn, ModelDef, PoseFn } from '../types';

const deg = (x: number, y: number, z: number) => [x * D2R, y * D2R, z * D2R] as const;

const poseStand: PoseFn = (p, c) => {
  look(p.head, c.headYaw, c.pitch);
  const arms = c.v.has('arms', 'show_arms');
  p.rightArmStick.visible = p.leftArmStick.visible = arms;
  p.basePlate.visible = !c.v.has('no_base', 'no_base_plate', 'no_baseplate', 'noplate', 'no_plate', 'marker');
  // The wire's pose (EntityArmorStand rotations, degrees) replaces the default angles part by part.
  for (const [name, r] of Object.entries(c.standPose ?? {})) {
    const s = p[name];
    if (!s || r.length < 3) continue;
    s.rx = r[0] * D2R;
    s.ry = r[1] * D2R;
    s.rz = r[2] * D2R;
  }
};

export const ARMOR_STAND: ModelDef = {
  id: 'armor_stand',
  tex: [64, 64],
  width: 0.5,
  height: 1.975,
  pose: poseStand,
  parts: [
    part('head', [0, 1, 0], [box(-1, -7, -1, 2, 7, 2, 0, 0)]),
    part('body', [0, 0, 0], [box(-6, 0, -1.5, 12, 3, 3, 0, 26)], {
      children: [
        part('standRightSide', [0, 0, 0], [box(-3, 3, -1, 2, 7, 2, 16, 0)]),
        part('standLeftSide', [0, 0, 0], [box(1, 3, -1, 2, 7, 2, 48, 16)]),
        part('standWaist', [0, 0, 0], [box(-4, 10, -1, 8, 2, 2, 0, 48)]),
      ],
    }),
    part('rightArm', [-5, 2, 0], [], { rot: deg(-15, 0, 10), children: [part('rightArmStick', [0, 0, 0], [box(-2, -2, -1, 2, 12, 2, 24, 0)])] }),
    part('leftArm', [5, 2, 0], [], { rot: deg(-10, 0, -10), children: [part('leftArmStick', [0, 0, 0], [box(0, -2, -1, 2, 12, 2, 32, 16, 0, true)])] }),
    part('rightLeg', [-1.9, 11, 0], [box(-1, 0, -1, 2, 11, 2, 8, 0)], { rot: deg(1, 0, 1) }),
    part('leftLeg', [1.9, 11, 0], [box(-1, 0, -1, 2, 11, 2, 40, 16, 0, true)], { rot: deg(-1, 0, -1) }),
    part('basePlate', [0, 12, 0], [box(-6, 11, -6, 12, 1, 12, 0, 32)]),
  ],
};

export const MODELS: ModelDef[] = [ARMOR_STAND];

/** "small" (half size), "arms", "no_base_plate", "marker" (no plate). */
const stand: LookFn = (v) => ({ model: 'armor_stand', texture: 'entity/armorstand/wood', colour: 0xa08050, ...(v.has('small') ? { scale: 0.5 } : {}) });

export const LOOKS: Record<string, LookFn> = { armor_stand: stand, 'object:armor_stand': stand };
