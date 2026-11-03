// Arrows stuck in a player (1.12.2 LayerArrow, RenderPlayer only): `count` arrows placed from java.util.Random(entityId), each
// in a random box of the player model (ModelBase.boxList: every ModelRenderer the model ever made, in construction order,
// the ones ModelPlayer replaced included, frozen at their first rotation point), at a random point of it, aimed back through
// the box's centre. Pure: the layer turns each spot into an arrow model instance on the posed part.

/** java.util.Random (48-bit LCG), enough for LayerArrow: nextInt(bound) and nextFloat. */
export class JavaRandom {
  private seed: bigint;
  private static readonly MUL = 0x5deece66dn;
  private static readonly MASK = (1n << 48n) - 1n;

  constructor(seed: number) {
    this.seed = (BigInt.asIntN(64, BigInt(Math.trunc(seed))) ^ JavaRandom.MUL) & JavaRandom.MASK;
  }

  private next(bits: number): number {
    this.seed = (this.seed * JavaRandom.MUL + 0xbn) & JavaRandom.MASK;
    return Number(BigInt.asIntN(32, this.seed >> BigInt(48 - bits)));
  }

  nextInt(bound: number): number {
    if ((bound & -bound) === bound) return Number((BigInt(bound) * BigInt(this.next(31))) >> 31n);
    let bits: number, val: number;
    do {
      bits = this.next(31);
      val = bits % bound;
    } while (bits - val + (bound - 1) > 0x7fffffff);
    return val;
  }

  /** nextInt(): 32 random bits as a signed int. */
  nextIntRaw(): number {
    return this.next(32);
  }

  nextFloat(): number {
    return this.next(24) / (1 << 24);
  }
}

/** One ModelRenderer of ModelPlayer's boxList: the part whose frame it follows (null = frozen at `pivot`, never posed: a
 * replaced arm or leg, the ears, the cape) and its box's corners (pixels; the uninflated ModelBox.posX1..posZ2). */
interface ListedBox {
  part: string | null;
  pivot: readonly [number, number, number];
  min: readonly [number, number, number];
  max: readonly [number, number, number];
}

const box = (part: string | null, pivot: readonly [number, number, number], x: number, y: number, z: number, dx: number, dy: number, dz: number): ListedBox => ({
  part,
  pivot,
  min: [x, y, z],
  max: [x + dx, y + dy, z + dz],
});
const O: readonly [number, number, number] = [0, 0, 0];

/** ModelPlayer(0, smallArms).boxList: ModelBiped's seven (the replaced arm/leg frozen), the ears and cape, then ModelPlayer's
 * own arms, wear layers and legs. Wear layers copy their base part's angles and points, so they follow it. */
function playerBoxes(slim: boolean): ListedBox[] {
  const head = box('head', O, -4, -8, -4, 8, 8, 8), body = box('body', O, -4, 0, -2, 8, 12, 4);
  const leg = (part: string | null, x: number) => box(part, [x, 12, 0], -2, 0, -2, 4, 12, 4);
  const common = [head, head, body];
  if (!slim)
    return [
      ...common,
      box('rightArm', [-5, 2, 0], -3, -2, -2, 4, 12, 4),
      box(null, [5, 2, 0], -1, -2, -2, 4, 12, 4), // ModelBiped's left arm, replaced
      leg('rightLeg', -1.9),
      leg(null, 1.9), // ModelBiped's left leg, replaced
      box(null, O, -3, -6, -1, 6, 6, 1), // bipedDeadmau5Head
      box(null, O, -5, 0, -1, 10, 16, 1), // bipedCape
      box('leftArm', [5, 2, 0], -1, -2, -2, 4, 12, 4),
      box('leftArm', [5, 2, 0], -1, -2, -2, 4, 12, 4), // left armwear
      box('rightArm', [-5, 2, 0], -3, -2, -2, 4, 12, 4), // right armwear
      leg('leftLeg', 1.9),
      leg('leftLeg', 1.9),
      leg('rightLeg', -1.9),
      body,
    ];
  return [
    ...common,
    box(null, [-5, 2, 0], -3, -2, -2, 4, 12, 4), // ModelBiped's right arm, replaced
    box(null, [5, 2, 0], -1, -2, -2, 4, 12, 4),
    leg('rightLeg', -1.9),
    leg(null, 1.9),
    box(null, O, -3, -6, -1, 6, 6, 1),
    box(null, O, -5, 0, -1, 10, 16, 1),
    box('leftArm', [5, 2.5, 0], -1, -2, -2, 3, 12, 4),
    box('rightArm', [-5, 2.5, 0], -2, -2, -2, 3, 12, 4),
    box('leftArm', [5, 2.5, 0], -1, -2, -2, 3, 12, 4),
    box('rightArm', [-5, 2.5, 0], -2, -2, -2, 3, 12, 4),
    leg('leftLeg', 1.9),
    leg('leftLeg', 1.9),
    leg('rightLeg', -1.9),
    body,
  ];
}

/** Where one stuck arrow sits: the part frame it follows (null = the model root translated to `pivot`), its spot in that frame
 * (blocks: postRender(1/16) then translate) and the arrow entity's yaw and pitch (vanilla degrees: EntityArrow.rotationYaw /
 * rotationPitch). */
export interface ArrowSpot {
  part: string | null;
  pivot: readonly [number, number, number];
  at: [number, number, number];
  yaw: number;
  pitch: number;
}

/** The most arrows drawn on one player (vanilla has no cap; a pin cushion past this adds nothing readable). */
export const MAX_STUCK_ARROWS = 32;

/** LayerArrow.doRenderLayer's placements for a player with `count` arrows (Random(entityId), Steve's or Alex's model). */
export function stuckArrows(entityId: number, count: number, slim: boolean): ArrowSpot[] {
  const boxes = playerBoxes(slim);
  const r = new JavaRandom(entityId);
  const out: ArrowSpot[] = [];
  for (let j = 0; j < Math.min(count, MAX_STUCK_ARROWS); j++) {
    const b = boxes[r.nextInt(boxes.length)];
    r.nextInt(1); // the renderer's cubeList (one box each)
    let f = r.nextFloat(), g = r.nextFloat(), h = r.nextFloat();
    const at: [number, number, number] = [(b.min[0] + (b.max[0] - b.min[0]) * f) / 16, (b.min[1] + (b.max[1] - b.min[1]) * g) / 16, (b.min[2] + (b.max[2] - b.min[2]) * h) / 16];
    f = -(f * 2 - 1);
    g = -(g * 2 - 1);
    h = -(h * 2 - 1);
    const n = Math.sqrt(f * f + h * h);
    out.push({ part: b.part, pivot: b.pivot, at, yaw: (Math.atan2(f, h) * 180) / Math.PI, pitch: (Math.atan2(g, n) * 180) / Math.PI });
  }
  return out;
}
