// Armour on bipeds (LayerBipedArmor): armour geometry bound to the wearer's own bone slots, so it follows the same pose.
// Outer layer (helmet, chestplate + arms, boots) inflated 1.0 with <material>_layer_1; inner layer (leggings: body + legs) inflated 0.5 with <material>_layer_2. Each worn piece is one
// instance of the armour geometry, its layer selecting the piece.
//
// The wire does not carry equipment today (WorldEntity has no equipment, BotDto neither): the layer reads an optional
// `armor` array [helmet, chestplate, leggings, boots] of material names from the DTO, ready for when it does.

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

function pieceCubes(name: string): Array<[ArmorPiece, CubeDef]> {
  switch (name) {
    case 'head':
      return [['helmet', box(-4, -8, -4, 8, 8, 8, 0, 0, 1)]];
    case 'body':
      return [
        ['chest', box(-4, 0, -2, 8, 12, 4, 16, 16, 1)],
        ['legs', box(-4, 0, -2, 8, 12, 4, 16, 16, 0.5)],
      ];
    case 'rightArm':
      return [['chest', box(-3, -2, -2, 4, 12, 4, 40, 16, 1)]];
    case 'leftArm':
      return [['chest', mbox(-1, -2, -2, 4, 12, 4, 40, 16, 1)]];
    case 'rightLeg':
      return [
        ['legs', box(-2, 0, -2, 4, 12, 4, 0, 16, 0.5)],
        ['boots', box(-2, 0, -2, 4, 12, 4, 0, 16, 1)],
      ];
    case 'leftLeg':
      return [
        ['legs', mbox(-2, 0, -2, 4, 12, 4, 0, 16, 0.5)],
        ['boots', mbox(-2, 0, -2, 4, 12, 4, 0, 16, 1)],
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
  for (const name of BIPED) {
    const slot = base.index[name];
    for (const [piece, c] of pieceCubes(name)) sink.cube(c, slot + 256 * (ARMOR_PIECES.indexOf(piece) + 1));
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

type Ref = readonly [number, number] | null | undefined;

/** [helmet, chestplate, leggings, boots] materials from wire equipment (null where the slot holds no armour). */
export function armorFromEquipment(eq: Partial<Record<'main' | 'off' | 'head' | 'chest' | 'legs' | 'feet', Ref>> | null | undefined): Array<string | null> | null {
  if (!eq) return null;
  const slots = [eq.head, eq.chest, eq.legs, eq.feet];
  const out = ARMOR_PIECES.map((p, i) => (slots[i] ? armorMaterial(slots[i]![0], p) : null));
  return out.some(Boolean) ? out : null;
}
