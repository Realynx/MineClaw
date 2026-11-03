// Armour on bipeds (LayerBipedArmor): armour geometry bound to the wearer's own bone slots, so it follows the same pose.
// Outer layer (helmet, chestplate + arms, boots) inflated 1.0 with <material>_layer_1; inner layer (leggings: body + legs)
// inflated 0.5 with <material>_layer_2. Each worn piece is one instance of the armour geometry, its layer selecting the piece.
// Zombie villagers wear LayerVillagerArmor's boxes (ModelZombieVillager(…, true): the helmet 2 px higher, body and legs
// 0.1 more inflated). 1.12.2 leather is the greyscale texture tinted by the dye (wire `[id, meta, rgb]`, else
// ItemArmor's default 0xA06540) plus the untinted `_overlay`; b1.8.1's cloth texture is already brown and never tinted.

import { CubeSink, type BuiltModel } from './builder';
import { box, mbox } from './dsl';
import type { CubeDef, ModelDef, PartDef } from './types';

export const ARMOR_PIECES = ['helmet', 'chest', 'legs', 'boots'] as const;
export type ArmorPiece = (typeof ARMOR_PIECES)[number];

const BIPED = new Set(['head', 'body', 'rightArm', 'leftArm', 'rightLeg', 'leftLeg']);

/** Whether a model can wear armour (it has the six biped parts). */
export function wearsArmor(def: ModelDef): boolean {
  const names = new Set<string>();
  const walk = (p: PartDef) => {
    names.add(p.name);
    p.children?.forEach(walk);
  };
  def.parts.forEach(walk);
  return [...BIPED].every((n) => names.has(n));
}

function pieceCubes(name: string, villager: boolean): Array<[ArmorPiece, CubeDef]> {
  // LayerVillagerArmor: the head box at y −10, the body and legs 0.1 more inflated than the biped's.
  const g = villager ? 0.1 : 0;
  switch (name) {
    case 'head':
      return [['helmet', box(-4, villager ? -10 : -8, -4, 8, 8, 8, 0, 0, 1)]];
    case 'body':
      return [
        ['chest', box(-4, 0, -2, 8, 12, 4, 16, 16, 1 + g)],
        ['legs', box(-4, 0, -2, 8, 12, 4, 16, 16, 0.5 + g)],
      ];
    case 'rightArm':
      return [['chest', box(-3, -2, -2, 4, 12, 4, 40, 16, 1)]];
    case 'leftArm':
      return [['chest', mbox(-1, -2, -2, 4, 12, 4, 40, 16, 1)]];
    case 'rightLeg':
      return [
        ['legs', box(-2, 0, -2, 4, 12, 4, 0, 16, 0.5 + g)],
        ['boots', box(-2, 0, -2, 4, 12, 4, 0, 16, 1 + g)],
      ];
    case 'leftLeg':
      return [
        ['legs', mbox(-2, 0, -2, 4, 12, 4, 0, 16, 0.5 + g)],
        ['boots', mbox(-2, 0, -2, 4, 12, 4, 0, 16, 1 + g)],
      ];
    default:
      return [];
  }
}

/**
 * The armour geometry of a built biped: the armour pieces' cubes tagged with the wearer's own part slots (so its bones move
 * them) and the piece as the layer (1 helmet, 2 chest, 3 legs, 4 boots). Null when the model is not a biped.
 */
export function buildArmor(base: BuiltModel): (BuiltModel & { pieces: Record<ArmorPiece, number> }) | null {
  if (!wearsArmor(base.def)) return null;
  const sink = new CubeSink([64, 32]);
  const villager = base.def.id === 'zombie_villager';
  for (const name of BIPED) {
    const slot = base.index[name];
    for (const [piece, c] of pieceCubes(name, villager)) sink.cube(c, slot + 256 * (ARMOR_PIECES.indexOf(piece) + 1));
  }
  const layers: Record<string, number> = {};
  ARMOR_PIECES.forEach((p, i) => (layers[p] = i + 1));
  const def: ModelDef = { id: `${base.def.id}~armor`, tex: [64, 32], layers: [...ARMOR_PIECES], width: base.def.width, height: base.def.height, parts: [] };
  return { def, parts: base.parts, index: base.index, layers, pieces: layers as Record<ArmorPiece, number>, ...sink.finish(def.id) };
}

/** Sheet key of an armour piece's texture ("iron", "diamond", "leather"/"cloth", "chainmail"/"chain", "gold"/"golden"). */
export function armorTexture(material: string, piece: ArmorPiece, beta: boolean): string {
  const m = material.toLowerCase().replace(/^golden$/, 'gold');
  const inner = piece === 'legs';
  if (beta) {
    const b = m === 'leather' ? 'cloth' : m === 'chainmail' ? 'chain' : m;
    return `armor/${b}_${inner ? 2 : 1}`;
  }
  const n = m === 'cloth' ? 'leather' : m === 'chain' ? 'chainmail' : m;
  return `models/armor/${n}_layer_${inner ? 2 : 1}`;
}

const MATERIALS = ['leather', 'chainmail', 'iron', 'diamond', 'gold'];

/** The armour material of an item id (298..317, the same ids in both versions) worn in `piece`'s slot, or null. */
export function armorMaterial(id: number, piece: ArmorPiece): string | null {
  if (id < 298 || id > 317) return null;
  const k = id - 298;
  return k % 4 === ARMOR_PIECES.indexOf(piece) ? MATERIALS[Math.floor(k / 4)] : null;
}

type Ref = readonly [number, number] | readonly [number, number, number] | null | undefined;
type Slots = Partial<Record<'main' | 'off' | 'head' | 'chest' | 'legs' | 'feet', Ref>>;

/** [helmet, chestplate, leggings, boots] materials from wire equipment (null where the slot holds no armour). */
export function armorFromEquipment(eq: Slots | null | undefined): Array<string | null> | null {
  if (!eq) return null;
  const slots = [eq.head, eq.chest, eq.legs, eq.feet];
  const out = ARMOR_PIECES.map((p, i) => (slots[i] ? armorMaterial(slots[i]![0], p) : null));
  return out.some(Boolean) ? out : null;
}

/** ItemArmor.getColor's default for undyed leather. */
export const LEATHER_DEFAULT = 0xa06540;

/** [helmet, chestplate, leggings, boots] dye colours from wire equipment (the slot's third element), null where undyed. */
export function armorDyes(eq: Slots | null | undefined): Array<number | null> | null {
  if (!eq) return null;
  const out = [eq.head, eq.chest, eq.legs, eq.feet].map((s) => (s && s.length > 2 && typeof s[2] === 'number' ? s[2] & 0xffffff : null));
  return out.some((c) => c != null) ? out : null;
}

/** LayerArmorBase's passes for one piece: the texture and its tint, then (1.12.2 leather) the untinted overlay. */
export function armorPasses(material: string, piece: ArmorPiece, beta: boolean, dye: number | null | undefined): Array<{ texture: string; tint: number | null }> {
  const texture = armorTexture(material, piece, beta);
  if (beta || !/^(leather|cloth)$/i.test(material)) return [{ texture, tint: null }];
  return [
    { texture, tint: dye ?? LEATHER_DEFAULT },
    { texture: `${texture}_overlay`, tint: null },
  ];
}

/** TileEntitySkullRenderer skull types (item meta 0..5) → their entity sheet texture. */
export const SKULL_TEXTURES = ['entity/skeleton/skeleton', 'entity/skeleton/wither_skeleton', 'entity/zombie/zombie', 'entity/steve', 'entity/creeper/creeper', 'entity/enderdragon/dragon'] as const;

/**
 * The worn skull geometries (LayerCustomHead → TileEntitySkullRenderer.renderSkull), in skull model pixels with slot 0 (the
 * head) and slot 1 (the dragon's jaw): ModelSkeletonHead 64×32 (skeleton, wither, creeper), ModelHumanoidHead 64×64 with
 * its hat layer (zombie, player), ModelDragonHead 256×256.
 */
export function buildSkull(kind: 'head' | 'humanoid' | 'dragon'): BuiltModel {
  const tex: [number, number] = kind === 'head' ? [64, 32] : kind === 'humanoid' ? [64, 64] : [256, 256];
  const sink = new CubeSink(tex);
  if (kind === 'dragon') {
    // ModelDragonHead: upper lip, upper head, the scales and nostrils (the left pair mirrored), and the jaw (child at 0,4,−8).
    for (const c of [
      box(-6, -1, -24, 12, 5, 16, 176, 44),
      box(-8, -8, -10, 16, 16, 16, 112, 30),
      mbox(-5, -12, -4, 2, 4, 6, 0, 0),
      mbox(-5, -3, -22, 2, 2, 4, 112, 0),
      box(3, -12, -4, 2, 4, 6, 0, 0),
      box(3, -3, -22, 2, 2, 4, 112, 0),
    ])
      sink.cube(c, 0);
    sink.cube(box(-6, 0, -16, 12, 4, 16, 176, 65), 1);
  } else {
    sink.cube(box(-4, -8, -4, 8, 8, 8, 0, 0), 0);
    if (kind === 'humanoid') sink.cube(box(-4, -8, -4, 8, 8, 8, 32, 0, 0.25), 0);
  }
  const def: ModelDef = { id: `skull_${kind}`, tex, width: 0.5, height: 0.5, parts: [] };
  return { def, parts: [], index: {}, layers: {}, ...sink.finish(def.id) };
}

/** ModelElytra (64×32, elytra.png): the left wing box(−10, 0, 0, 10, 20, 2, 1.0) at slot 0, the mirrored right wing
 * box(0, 0, 0, 10, 20, 2, 1.0) at slot 1, both at texture (22, 0). */
export function buildElytra(): BuiltModel {
  const sink = new CubeSink([64, 32]);
  sink.cube(box(-10, 0, 0, 10, 20, 2, 22, 0, 1), 0);
  sink.cube(mbox(0, 0, 0, 10, 20, 2, 22, 0, 1), 1);
  const def: ModelDef = { id: 'elytra', tex: [64, 32], width: 0.6, height: 1.8, parts: [] };
  return { def, parts: [], index: {}, layers: {}, ...sink.finish(def.id) };
}

/** Which skull geometry a skull type (item meta) uses. */
export const skullKind = (type: number): 'head' | 'humanoid' | 'dragon' => (type === 5 ? 'dragon' : type === 2 || type === 3 ? 'humanoid' : 'head');
