// The entity model data format: our own description of a Minecraft-style box model (named parts with a pivot and a rest
// rotation, cuboids with origin/size/inflate/mirror and a texture offset, child parts), consumed by the generic builder
// (builder.ts) and posed every frame by a pose function (pose.ts evaluates the part tree into bone matrices).
//
// Model space is the vanilla one: pixels, y DOWN, the feet at y = 24, the model facing −z (north). A part's local
// transform is translate(pivot) · rotZ · rotY · rotX (the vanilla GL order); its cubes are relative to its pivot; children
// are posed inside their parent. The root transform (pose.ts) maps model space to the world like RenderLivingBase:
// translate(position) · rotY(180° − bodyYaw) · rotZ(death) · scale(−1, −1, 1) · scale(model scale) · translate(0, −1.501, 0)
// · scale(1/16).
//
// Texture coordinates are vanilla texture pixels of a `tex`-sized texture (textureWidth/textureHeight); the renderer maps
// them into the texture's rectangle of the packed entity sheet per instance, so one geometry serves every skin of a model
// (zombie / husk / drowned, horse colours, villager professions…).

import type { Variant } from './variant';

export type V3 = readonly [number, number, number];

/** Face keys of a cuboid: −x west, +x east, y0 top (model y is down), y1 bottom, z0 north (front), z1 south (back). */
export type FaceKey = 'west' | 'east' | 'up' | 'down' | 'north' | 'south';

export interface CubeDef {
  /** Min corner in pixels relative to the part pivot (ModelRenderer.addBox x, y, z). */
  o: V3;
  /** Size in pixels (dx, dy, dz). Zero-thickness boxes (wing membranes) are allowed. */
  s: V3;
  /** Texture offset (u, v) in texture pixels (the vanilla box UV layout). */
  uv: readonly [number, number];
  /** Grows the box by this many pixels on every side without changing its UVs (ModelBox delta). */
  inflate?: number;
  /** Mirrored texture (left limbs). */
  mirror?: boolean;
  /** Per-face texture rectangles [u0, v0, u1, v1] instead of the box layout (arrows, flat quads); faces left out are not
   * drawn. */
  faces?: Partial<Record<FaceKey, readonly [number, number, number, number]>>;
}

export interface PartDef {
  name: string;
  /** Rotation point in the parent's space (pixels). */
  pivot: V3;
  /** Rest rotation in radians (applied Z, then Y, then X — the vanilla order). */
  rot?: V3;
  cubes: CubeDef[];
  children?: PartDef[];
  /** A texture layer of the model this part belongs to (an index into `ModelDef.layers` + 1); untagged = the base layer.
   * Children inherit their parent's layer unless they set their own. */
  layer?: string;
}

/** Everything a pose function may read (vanilla setRotationAngles arguments plus the entity facts the wire carries). */
export interface PoseCtx {
  /** limbSwing: distance walked (vanilla units, ≈ blocks · 1.0 at walking speed after the limb-swing filter). */
  swing: number;
  /** limbSwingAmount 0..1. */
  amount: number;
  /** ageInTicks (fractional). */
  age: number;
  /** netHeadYaw: head yaw relative to the body, degrees. */
  headYaw: number;
  /** headPitch, degrees (positive = looking down). */
  pitch: number;
  /** swingProgress 0..1 of a main-hand (right arm) swing (0 = none). */
  attack: number;
  /** swingProgress 0..1 of an off-hand (left arm) swing. */
  attackOff: number;
  /** Eating / drinking progress 0..1 (0 = not eating): arm to the mouth, head bob. */
  eat: number;
  /** A baby (models without a `baby` layout are scaled by the renderer). */
  baby: boolean;
  /** The decoded variant (string tokens and the number); `variant` keeps the numeric form. */
  v: Variant;
  /** Mob16 variant from the wire (slime size, sheep wool, pig saddle, wolf flags, creeper state…), 0 when absent. */
  variant: number;
  /** Entity flags (0x01 burning, 0x02 sneaking, 0x04 riding, 0x08 sprinting, 0x10 using an item). */
  flags: number;
  sneaking: boolean;
  riding: boolean;
  /** Something in the right hand (players/bots: the held item; mobs: never on the wire). */
  holding: boolean;
  /** Zombie arms raised / skeleton aiming / illager aggressive (look tokens such as "aggressive", "attacking"). */
  aggressive: boolean;
  /** Using an item (flag 0x10): drawing a bow (`bow`), eating or drinking, raising a shield. */
  using: boolean;
  bow: boolean;
  /** Armor stands: the wire's pose, part → [x, y, z] degrees (head, body, leftArm, rightArm, leftLeg, rightLeg). */
  standPose: Readonly<Record<string, readonly number[]>> | null;
  /** The b1.8.1 client's pose rules (some setRotationAngles differ between versions). */
  beta: boolean;
  /** Seconds-free per-entity phase 0..1 (desynchronises idle animations of identical mobs). */
  seed: number;
  /** Horizontal speed in blocks/tick (bats/birds flap, ghast tentacles…). */
  speed: number;
}

/** A part's pose this frame (reset to the rest pose before every pose call). */
export interface PartState {
  rx: number;
  ry: number;
  rz: number;
  px: number;
  py: number;
  pz: number;
  /** Hidden parts collapse (zero matrix). */
  visible: boolean;
}

/** Poses a model (vanilla setRotationAngles): `p` holds every part by name (all of them, children too). */
export type PoseFn = (p: Record<string, PartState>, c: PoseCtx) => void;

export interface ModelDef {
  /** Unique id ("biped", "player_slim", "cow"…). */
  id: string;
  /** textureWidth, textureHeight the UVs are in. */
  tex: readonly [number, number];
  parts: PartDef[];
  /** Names of the extra texture layers parts may be tagged with (sheep "wool", pig "saddle"…). */
  layers?: string[];
  /** RenderLiving preRenderCallback scale (uniform or per axis). */
  scale?: number | V3;
  /** Extra translation after the scale, in model pixels (rarely needed). */
  offset?: V3;
  /** Hitbox in blocks: the LOD box and the label height. */
  width: number;
  height: number;
  pose?: PoseFn;
  /**
   * The vanilla child layout (ModelBiped/ModelQuadruped `isChild`): top-level parts named in `head` are drawn with
   * scale(headScale) · translate(headOffset px); every other top-level part with scale(bodyScale) · translate(bodyOffset
   * px). Absent: a baby is the whole model at half size (the renderer's preRender scale).
   */
  baby?: { head: string[]; headScale: number; headOffset: V3; bodyScale: number; bodyOffset: V3 };
}

/** How an entity looks: a model, a skin, extra layers (same geometry or a tagged part layer) and a scale. */
export interface Look {
  model: string;
  /** Entity sheet key ("entity/zombie/zombie", beta "mob/zombie"). */
  texture: string;
  layers?: LayerUse[];
  /** Multiplies the model scale (slime size, baby, giant). */
  scale?: number;
  /** Colour multiplier of the base layer (0xRRGGBB; default white). */
  tint?: number;
  /** The base layer is translucent (blended). */
  translucent?: boolean;
  /** LOD box colour. */
  colour: number;
}

/** The look of one entity kind from the facts the wire carries (the decoded variant: `v.num` is the numeric / Mob16 form;
 * the entity id for stable picks where the wire has no selector). */
export type LookFn = (v: Variant, beta: boolean, id: number) => Look;

export interface LayerUse {
  /** The model layer drawn (a `ModelDef.layers` name); absent = the base geometry again (eyes, collar, markings). */
  layer?: string;
  texture: string;
  tint?: number;
  /** Full bright (spider/enderman eyes). */
  emissive?: boolean;
  translucent?: boolean;
  /** Texture scrolls with time (creeper charge, wither armour). */
  scroll?: boolean;
}
