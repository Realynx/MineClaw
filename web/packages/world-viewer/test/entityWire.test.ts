// The newer entity wire: equipment, variants, babies, hanging entities, events and display mobs.
import * as THREE from 'three';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack } from '../src/assets/bundle';
import { compose, EntityLayer } from '../src/render/entityLayer';
import { armorDyes, armorFromEquipment, armorMaterial, armorPasses, buildArmor, buildElytra, buildSkull, LEATHER_DEFAULT, skullKind } from '../src/render/entities/armor';
import { equipRef } from '../src/wire/normalize';
import { buildModel } from '../src/render/entities/builder';
import { facingYaw, minecartBlock, motive, paintingSheet } from '../src/render/entities/hanging';
import { attackOffProgress, attackProgress, DEATH_GONE_MS, deathAngle, deathScale, eatProgress, hurtAmount, newMotion } from '../src/render/entities/motion';
import { apply, rootMatrix, Skeleton } from '../src/render/entities/pose';
import { MODEL_BY_ID, mobLook, objectLook } from '../src/render/entities/registry';
import { Variant } from '../src/render/entities/variant';
import type { PoseCtx } from '../src/render/entities/types';
import type { EntityDto, EntityEvent } from '../src/types';
import type { BotView, EntityView } from '../src/world/entities';
import { noPacks, pack1122, packBeta } from './pack';

const PACK_1122 = noPacks ? null! : pack1122();
const PACK_BETA = noPacks ? null! : packBeta();
const CTX: PoseCtx = { swing: 0, amount: 0, age: 0, headYaw: 0, pitch: 0, attack: 0, attackOff: 0, eat: 0, baby: false, v: Variant.NONE, variant: 0, flags: 0, sneaking: false, riding: false, holding: false, aggressive: false, using: false, bow: false, standPose: null, beta: false, seed: 0, speed: 0 };

describe('variant parsing', () => {
  it('numbers, tokens, key:value pairs and dyes', () => {
    const v = Variant.of('Creamy, white-dots ,saddle,iron,chest');
    expect(v.tokens).toEqual(['creamy', 'white_dots', 'saddle', 'iron', 'chest']);
    expect(v.has('saddle')).toBe(true);
    expect(v.has('powered')).toBe(false);
    const w = Variant.of('collar:light_blue,angry,tame');
    expect(w.dye('collar')).toBe(3);
    expect(w.get('collar')).toBe('light_blue');
    expect(Variant.of('red,sheared').dye()).toBe(14);
    expect(Variant.of('size:4').numOf('size', 1)).toBe(4);
    expect(Variant.of(3).num).toBe(3);
    expect(Variant.of('2').num).toBe(2);
    expect(Variant.of(null).tokens).toEqual([]);
    expect(Variant.of('light_gray').dye()).toBe(8);
  });

  it('per kind: sheep colour and shearing, wolf collar/anger, pig saddle, creeper aura, slime size, villager profession', () => {
    const red = mobLook('1.12.2', 91, null, 'red', 0);
    expect(red.layers?.[0]).toMatchObject({ layer: 'wool' });
    expect(mobLook('1.12.2', 91, null, 'red,sheared', 0).layers).toEqual([]);
    expect(mobLook('1.12.2', 91, null, 14 | 16, 0).layers).toEqual([]);
    expect(mobLook('1.12.2', 91, null, 14, 0).layers?.[0].tint).toBe(red.layers?.[0].tint);
    const wolf = mobLook('1.12.2', 95, null, 'collar:blue,tame', 0);
    expect(wolf.texture).toBe('entity/wolf/wolf_tame');
    expect(wolf.layers?.[0].texture).toBe('entity/wolf/wolf_collar');
    expect(mobLook('1.12.2', 95, null, 'angry', 0).texture).toBe('entity/wolf/wolf_angry');
    expect(mobLook('1.12.2', 95, null, 2, 0).texture).toBe('entity/wolf/wolf_angry');
    expect(mobLook('1.12.2', 90, null, 'saddle', 0).layers?.[0].layer).toBe('saddle');
    expect(mobLook('b1.8.1', 90, null, 1, 0).layers?.[0].texture).toBe('mob/saddle');
    expect(mobLook('1.12.2', 50, null, 'powered', 0).layers?.[0]).toMatchObject({ layer: 'charge', scroll: true });
    expect(mobLook('1.12.2', 50, null, null, 0).layers).toEqual([]);
    expect(mobLook('1.12.2', 55, null, 'size:4', 0).scale).toBe(4);
    expect(mobLook('1.12.2', 62, null, 2, 0).scale).toBe(2);
    expect(mobLook('1.12.2', 120, null, 1, 0).texture).toBe('entity/villager/librarian');
    expect(mobLook('1.12.2', 120, null, 'smith', 0).texture).toBe('entity/villager/smith');
    expect(mobLook('1.12.2', 27, null, 4, 0).texture).toBe('entity/zombie_villager/zombie_butcher');
    expect(objectLook('1.12.2', 'boat', 'spruce', 0)?.texture).toBe('entity/boat/boat_spruce');
  });

  it.skipIf(noPacks)('per kind (animals): horse coat, markings, armour, saddle; llama carpet; cats; rabbits; parrots', () => {
    const horse = mobLook('1.12.2', 100, null, 'creamy,white_dots,saddle,iron', 0);
    expect(horse.texture).toBe('entity/horse/horse_creamy');
    const layers = (horse.layers ?? []).map((l) => l.layer ?? l.texture);
    expect(layers).toContain('saddle');
    expect(layers.some((t) => /markings_whitedots/.test(t))).toBe(true);
    expect(layers.some((t) => /horse_armor_iron/.test(t))).toBe(true);
    const llama = mobLook('1.12.2', 103, null, 'white,carpet:red,chest', 0);
    expect(llama.texture).toBe('entity/llama/llama_white');
    expect((llama.layers ?? []).some((l) => l.texture === 'entity/llama/decor/decor_red')).toBe(true);
    expect(mobLook('1.12.2', 98, null, 3, 0).texture).toBe('entity/cat/siamese');
    expect(mobLook('1.12.2', 101, null, 3, 0).texture).toBe('entity/rabbit/white_splotched');
    expect(mobLook('1.12.2', 105, null, 1, 0).texture).toBe('entity/parrot/parrot_blue');
    // Every texture a variant asks for exists.
    for (const l of [horse, llama]) for (const t of [l.texture, ...(l.layers ?? []).map((x) => x.texture)]) expect(PACK_1122.entity.rects[t], t).toBeDefined();
  });
});

describe('the final wire: numeric variant + look tokens, names, stand poses', () => {
  it('look tokens decide over the legacy number; key=value pairs', () => {
    const v = Variant.of(7, 'tame,sitting,collar=red');
    expect(v.num).toBe(7);
    expect(v.has('sitting')).toBe(true);
    expect(v.dye('collar')).toBe(14);
    expect(v.isString).toBe(true);
    expect(Variant.of(5).isString).toBe(false);
    expect(mobLook('1.12.2', 95, null, Variant.of(0, 'tame,collar=blue'), 0).layers?.[0].texture).toBe('entity/wolf/wolf_collar');
    expect(mobLook('1.12.2', 91, null, Variant.of(0, 'red,sheared'), 0).layers).toEqual([]);
    expect(mobLook('1.12.2', 55, null, Variant.of(1, 'size=3'), 0).scale).toBe(3);
    expect(mobLook('1.12.2', 120, null, Variant.of(0, 'profession=priest'), 0).texture).toBe('entity/villager/priest');
    expect(mobLook('1.12.2', 50, null, Variant.of(0, 'powered,fusing'), 0).layers?.[0].layer).toBe('charge');
    const horse = mobLook('1.12.2', 100, null, Variant.of(0, 'black,white_field,tame,saddle,armor=diamond'), 0);
    expect(horse.texture).toBe('entity/horse/horse_black');
    expect((horse.layers ?? []).some((l) => /horse_armor_diamond/.test(l.texture))).toBe(true);
  });

  it('wither skeletons and strays by name over the skeleton type; object type names', () => {
    expect(mobLook('1.12.2', 51, 'WitherSkeleton', null, 0).texture).toBe('entity/skeleton/wither_skeleton');
    expect(mobLook('1.12.2', 51, 'Stray', null, 0).texture).toBe('entity/skeleton/stray');
    expect(mobLook('1.12.2', 51, 'Skeleton', null, 0).texture).toBe('entity/skeleton/skeleton');
    expect(mobLook('1.12.2', 54, 'Zombie', null, 0).texture).toBe('entity/zombie/zombie');
  });

  it('armor stands take the wire pose; a drawn bow raises both arms', () => {
    const stand = buildModel(MODEL_BY_ID.armor_stand);
    const sk = new Skeleton(stand);
    sk.reset();
    MODEL_BY_ID.armor_stand.pose!(sk.byName, { ...CTX, standPose: { rightArm: [-90, 0, 0], head: [0, 45, 0] } });
    expect(sk.byName.rightArm.rx).toBeCloseTo(-Math.PI / 2);
    expect(sk.byName.head.ry).toBeCloseTo(Math.PI / 4);
    const player = buildModel(MODEL_BY_ID.player);
    const ps = new Skeleton(player);
    ps.reset();
    MODEL_BY_ID.player.pose!(ps.byName, { ...CTX, using: true, bow: true });
    expect(ps.byName.rightArm.rx).toBeCloseTo(-Math.PI / 2, 1);
    expect(ps.byName.leftArm.rx).toBeCloseTo(-Math.PI / 2, 1);
  });
});

describe('equipment', () => {
  it('armour ids map to materials per slot (both versions share 298..317)', () => {
    expect(armorMaterial(306, 'helmet')).toBe('iron');
    expect(armorMaterial(307, 'chest')).toBe('iron');
    expect(armorMaterial(307, 'helmet')).toBeNull();
    expect(armorMaterial(301, 'boots')).toBe('leather');
    expect(armorMaterial(317, 'boots')).toBe('gold');
    expect(armorFromEquipment({ head: [310, 0], chest: [303, 0], legs: [86, 0], feet: null })).toEqual(['diamond', 'chainmail', null, null]);
    expect(armorFromEquipment({ main: [276, 0] })).toBeNull();
  });

  it.skipIf(noPacks)('dyed leather: the wire’s third element is the dye; LayerArmorBase tints the piece and adds the untinted overlay', () => {
    expect(equipRef([299, 0, 0x3366cc])).toEqual([299, 0, 0x3366cc]);
    expect(equipRef([299, 0])).toEqual([299, 0]);
    expect(equipRef({ id: 301, meta: 0, color: 0x112233 })).toEqual([301, 0, 0x112233]);
    expect(armorDyes({ chest: [299, 0, 0x3366cc], feet: [301, 0] })).toEqual([null, 0x3366cc, null, null]);
    expect(armorDyes({ chest: [299, 0] })).toBeNull();
    expect(armorPasses('leather', 'chest', false, 0x3366cc)).toEqual([
      { texture: 'models/armor/leather_layer_1', tint: 0x3366cc },
      { texture: 'models/armor/leather_layer_1_overlay', tint: null },
    ]);
    expect(armorPasses('leather', 'legs', false, null)[0]).toEqual({ texture: 'models/armor/leather_layer_2', tint: LEATHER_DEFAULT });
    expect(PACK_1122.entity.rects['models/armor/leather_layer_2_overlay']).toBeDefined();
    // b1.8.1's cloth texture is already brown: drawn as it is.
    expect(armorPasses('leather', 'chest', true, null)).toEqual([{ texture: 'armor/cloth_1', tint: null }]);
    expect(armorPasses('iron', 'chest', false, 0x3366cc)).toEqual([{ texture: 'models/armor/iron_layer_1', tint: null }]);
  });

  it.skipIf(noPacks)('skulls (ModelSkeletonHead / ModelHumanoidHead / ModelDragonHead) and the elytra build inside their textures', () => {
    expect([0, 1, 2, 3, 4, 5].map(skullKind)).toEqual(['head', 'head', 'humanoid', 'humanoid', 'head', 'dragon']);
    for (const k of ['head', 'humanoid', 'dragon'] as const) {
      const s = buildSkull(k);
      const [tw, th] = s.def.tex;
      expect(s.uvRects.every(([u0, v0, u1, v1]) => u0 >= 0 && v0 >= 0 && u1 <= tw && v1 <= th)).toBe(true);
    }
    expect(buildSkull('humanoid').indices.length).toBe(2 * buildSkull('head').indices.length); // the hat layer
    expect(new Set(buildSkull('dragon').partIds)).toEqual(new Set([0, 1])); // head + jaw
    expect(new Set(buildElytra().partIds)).toEqual(new Set([0, 1])); // two wings
    expect(PACK_1122.entity.rects['entity/elytra']).toBeDefined();
  });

  it('zombie villagers wear LayerVillagerArmor (helmet 2 px higher, body and legs 0.1 more inflated)', () => {
    const plain = buildArmor(buildModel(MODEL_BY_ID.zombie))!, villager = buildArmor(buildModel(MODEL_BY_ID.zombie_villager))!;
    const minY = (b: typeof plain, layer: number) => {
      let m = Infinity;
      for (let i = 0; i < b.partIds.length; i++) if (Math.floor(b.partIds[i] / 256) === layer) m = Math.min(m, b.positions[i * 3 + 1]);
      return m;
    };
    expect(minY(plain, 1)).toBeCloseTo(-9);
    expect(minY(villager, 1)).toBeCloseTo(-11);
    expect(minY(plain, 3)).toBeCloseTo(-0.5); // leggings: body and legs at 0.5
    expect(minY(villager, 3)).toBeCloseTo(-0.6);
  });
});

describe('animation timing', () => {
  it('swings, off-hand swings, eating, hurt flash decay and the death fall and fade', () => {
    const m = newMotion(0, 0, 0, 0, 'k', 0);
    m.attackAt = 1000;
    m.attackOffAt = 1000;
    m.eatAt = 1000;
    m.hurtAt = 1000;
    expect(attackProgress(m, 1150)).toBeCloseTo(0.5);
    expect(attackOffProgress(m, 1150)).toBeCloseTo(0.5);
    expect(eatProgress(m, 1800)).toBeCloseTo(0.5);
    expect(hurtAmount(m, 1400)).toBe(1);
    expect(hurtAmount(m, 1600)).toBe(0);
    expect(attackOffProgress(m, 1400)).toBe(0);
    m.deadAt = 2000;
    expect(deathAngle(m, 2000)).toBe(0);
    expect(deathAngle(m, 3000)).toBeCloseTo(90);
    expect(deathScale(m, 2500)).toBe(1);
    expect(deathScale(m, 2000 + DEATH_GONE_MS)).toBe(0);
    expect(deathScale(m, 2000 + (1200 + DEATH_GONE_MS) / 2)).toBeCloseTo(0.5);
  });
});

describe('babies', () => {
  it('bipeds keep a big head, animals a full-size head over a half-size body', () => {
    const r = rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    const zombie = buildModel(MODEL_BY_ID.zombie);
    const sk = new Skeleton(zombie);
    const out = new Float32Array(64 * 12);
    sk.pose(null, r, out, 0, true);
    // The head top (model y −8 at the head pivot): 0.75 · (−8 + 16) = 6 px → 1.5 − 6/16 ≈ 1.125 blocks; adult 2.0.
    const headTop = apply(out.subarray(zombie.index.head * 12, zombie.index.head * 12 + 12), 0, -8, 0)[1];
    expect(headTop).toBeCloseTo(1.125, 2);
    // Feet still on the ground: legs end at model y 24 → 0.5 · (12 + 12 + 24) = 24 px.
    const foot = apply(out.subarray(zombie.index.rightLeg * 12, zombie.index.rightLeg * 12 + 12), 0, 12, 0)[1];
    expect(foot).toBeCloseTo(0, 2);
    const cow = buildModel(MODEL_BY_ID.cow);
    const cs = new Skeleton(cow);
    cs.pose(null, r, out, 0, true);
    const legFoot = apply(out.subarray(cow.index.leg1 * 12, cow.index.leg1 * 12 + 12), 0, 12, 0)[1];
    expect(legFoot).toBeCloseTo(0, 2);
  });

  it('foals and baby llamas: ModelHorse / ModelLlama child proportions per part group', () => {
    const r = rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    const out = new Float32Array(64 * 12);
    const at = (m: ReturnType<typeof buildModel>, part: string, x: number, y: number, z: number) => apply(out.subarray(m.index[part] * 12, m.index[part] * 12 + 12), x, y, z);
    const horse = buildModel(MODEL_BY_ID.horse);
    new Skeleton(horse).pose(null, r, out, 0, true);
    // Legs scale(0.5, 0.75, 0.5) · translate(0, 7.6 px): the hooves stay on the ground (model y 24.1 → 0.75 · 31.7 px).
    expect(at(horse, 'backLeftHoof', 0, 8.1, 0)[1]).toBeCloseTo(1.501 - (0.75 * (24.1 + 7.6)) / 16, 3);
    expect(at(horse, 'backLeftHoof', 0, 8.1, 0)[1]).toBeLessThan(0.03);
    // The legs are 3/4 tall but half as thick; the body is half size; the head 0.625.
    const legTop = at(horse, 'backLeftLeg', 0, -2, 0)[1], legBottom = at(horse, 'backLeftLeg', 0, 7, 0)[1];
    expect(legTop - legBottom).toBeCloseTo((9 * 0.75) / 16, 3);
    const bodyLen = Math.abs(at(horse, 'body', 0, 0, -19)[2] - at(horse, 'body', 0, 0, 5)[2]);
    expect(bodyLen).toBeCloseTo((24 * 0.5) / 16, 2);
    const headLen = Math.hypot(...([0, 1, 2].map((k) => at(horse, 'head', 0, -10, -1.5)[k] - at(horse, 'head', 0, -5, -1.5)[k]) as [number, number, number]));
    expect(headLen).toBeCloseTo((5 * 0.625) / 16, 3);
    const llama = buildModel(MODEL_BY_ID.llama);
    new Skeleton(llama).pose(null, r, out, 0, true);
    // Legs scale(0.4545, 0.4132, 0.4545) · translate(0, 33 px): the feet (model y 24) at 0.4132 · 57 px.
    expect(at(llama, 'leg1', 0, 14, 0)[1]).toBeCloseTo(1.501 - (0.41322312 * 57) / 16, 3);
    const neck = Math.abs(at(llama, 'head', 0, -16, -6)[1] - at(llama, 'head', 0, 2, -6)[1]);
    expect(neck).toBeCloseTo((18 * 0.64935064) / 16, 3);
  });
});

describe('hanging entities', () => {
  it.skipIf(noPacks)('painting motives map to their rect of the painting sheet, facings to yaws', () => {
    expect(motive('SkullAndRoses')).toMatchObject({ w: 32, h: 32, u: 128, v: 128 });
    expect(motive('burning_skull')).toMatchObject({ w: 64, h: 64, u: 128, v: 192 });
    expect(motive('minecraft:donkey_kong')).toMatchObject({ w: 64, h: 48, u: 192, v: 112 });
    expect(motive('nonsense').key).toBe('kebab');
    expect(PACK_1122.entity.rects[paintingSheet(false)]).toEqual(expect.arrayContaining([256, 256]));
    expect(PACK_BETA.entity.rects[paintingSheet(true)]).toBeDefined();
    expect([0, 1, 2, 3].map(facingYaw)).toEqual([0, 90, 180, 270]);
    expect(facingYaw('north')).toBe(180);
    expect(minecartBlock('chest')).toBe(54);
    expect(minecartBlock(2)).toBe(61);
    expect(minecartBlock('hopper_minecart')).toBe(154);
    expect(minecartBlock(0)).toBe(0);
  });

  it('compose: translate · rotY · rotZ · scale · translate', () => {
    const m = compose(new Float32Array(12), 1, 2, 3, Math.PI / 2, 0, 2, 2, 2, 0, -0.5, 0);
    const p = apply(m, 0, 0.5, 1);
    expect(p[0]).toBeCloseTo(3);
    expect(p[1]).toBeCloseTo(2);
    expect(p[2]).toBeCloseTo(3);
  });
});

describe('EntityLayer with the newer wire', () => {
  beforeAll(() => {
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });
  const bundle = (pack: Pack): AssetBundle => ({ version: pack.version, pack, blocks: {}, items: {}, entity: {}, anim: null, env: {} }) as unknown as AssetBundle;
  const ent = (id: number, x: number, dto: Partial<EntityDto>): EntityView => {
    const d = { kind: 'mob', dim: 0, x, y: 64, z: 0, id, ...dto } as EntityDto;
    return { key: `0:${id}`, id, dim: 0, kind: d.kind, name: d.name ?? null, type: d.type ?? -1, item: d.item ?? 0, count: d.count ?? 1, x, y: 64, z: 0, yaw: 0, ageTicks: 10, flags: d.flags ?? 0, dto: d };
  };
  const meshes = (layer: EntityLayer) => {
    const out: Record<string, number> = {};
    layer.group.traverse((o) => {
      const m = o as THREE.Mesh;
      if (m.isMesh && o.visible && o.name) out[o.name] = (m.geometry as THREE.InstancedBufferGeometry).instanceCount ?? 1;
    });
    return out;
  };
  const cam = new THREE.Vector3(0, 66, 8);
  const run = (layer: EntityLayer, bots: BotView[], ents: EntityView[], t = 1000) => {
    for (let f = 0; f < 2; f++) {
      layer.update(bots, ents, t + f * 16, new Set());
      layer.showFor(0, cam, { bots: true, entities: true, labels: false });
    }
  };

  it.skipIf(noPacks)('equipment draws armour pieces, both hands and a pumpkin; invisibility hides only the body', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const zombie = ent(1, 0, { type: 54, equipment: { main: [267, 0], off: [50, 0], head: [86, 0], chest: [307, 0], legs: [308, 0], feet: [309, 0] } });
    run(layer, [], [zombie]);
    let m = meshes(layer);
    expect(m['armour zombie']).toBe(3); // chest, legs, boots (the pumpkin is a block)
    expect(m['entity zombie']).toBe(1);
    expect(m['item sprites']).toBe(2); // sword + torch
    expect(m['item cubes']).toBe(1); // the pumpkin
    run(layer, [], [{ ...zombie, flags: 0x20, dto: { ...zombie.dto, flags: 0x20 } }]);
    m = meshes(layer);
    expect(m['entity zombie']).toBeUndefined();
    expect(m['armour zombie']).toBe(3);
    layer.dispose();
  });

  it.skipIf(noPacks)('paintings, item frames (with their item) and loaded minecarts', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [ent(1, 0, { kind: 'painting', motive: 'Kebab', facing: 2 }), ent(2, 2, { kind: 'item_frame', frameItem: [264, 0], rotation: 2, facing: 1 }), ent(3, 4, { kind: 'minecart', minecartType: 'chest' })]);
    const m = meshes(layer);
    expect(m['item sprites']).toBe(2); // the painting and the diamond
    expect(m['item cubes']).toBe(2); // the frame plate and the chest in the cart
    expect(m['entity minecart']).toBe(1);
    expect(layer.detailOf('0:1')).toBe(true);
    layer.dispose();
  });

  it.skipIf(noPacks)('events: swings, hurts, deaths (the body stays until it shrinks away), sparks, teleports and lightning', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const bot: BotView = { name: 'Alice', dim: 0, x: 0, y: 64, z: 2, yaw: 0, dto: { name: 'Alice', short: 'main', main: true, dim: 0, x: 0, y: 64, z: 2, hp: 20, food: 20 } };
    const z = ent(7, 0, { type: 54 });
    run(layer, [bot], [z], 1000);
    const ev = (e: Partial<EntityEvent>): EntityEvent => ({ kind: 'swing', at: 0, dim: 0, ...e });
    layer.onEvents!([ev({ kind: 'swing', name: 'Alice' }), ev({ kind: 'hurt', id: 7 }), ev({ kind: 'crit', id: 7 }), ev({ kind: 'teleport', id: 7, x: 3, y: 64, z: 3 }), ev({ kind: 'lightning', x: 5, y: 64, z: 5 })], 1100);
    const drawn = (layer as unknown as { drawn: Map<string, { motion: { attackAt: number; hurtAt: number; deadAt: number } }> }).drawn;
    expect(drawn.get('Alice')!.motion.attackAt).toBe(1100);
    expect(drawn.get('0:7')!.motion.hurtAt).toBe(1100);
    expect(layer.effects.particles).toBeGreaterThan(30);
    expect(layer.effects.bolts).toBe(1);
    layer.onEvents!([ev({ kind: 'death', id: 7 })], 1200);
    expect(drawn.get('0:7')!.motion.deadAt).toBe(1200);
    // Removed right after its death: still drawn while it falls…
    run(layer, [bot], [], 1300);
    expect(layer.detailOf('0:7')).not.toBeNull();
    // …and gone once it has shrunk away; the bolt is gone after half a second.
    run(layer, [bot], [], 1200 + DEATH_GONE_MS + 50);
    expect(drawn.has('0:7')).toBe(false);
    expect(layer.effects.bolts).toBe(0);
    layer.dispose();
  });

  it.skipIf(noPacks)('display mobs (spawner cages) draw through the shared model draws', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    layer.showDisplayMob('spawner@1,2,3', 0, 0.5, 64, 0.5, 'Zombie', 0.45, 1.2);
    run(layer, [], []);
    expect(meshes(layer)['entity zombie']).toBe(1);
    layer.showDisplayMob('spawner@1,2,3', 0, 0.5, 64, 0.5, 'minecraft:cave_spider', 0.45, 2);
    run(layer, [], []);
    expect(meshes(layer)['entity spider']).toBe(2); // body + eyes
    layer.hideDisplayMob('spawner@1,2,3');
    run(layer, [], []);
    expect(meshes(layer)['entity spider']).toBeUndefined();
    layer.dispose();
  });

  it.skipIf(noPacks)('spawner display mobs cast no blob shadow and lean back 30°', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    layer.showDisplayMob('spawner@1,2,3', 0, 0.5, 64.2, 0.5, 'pig', 0.53125, 0, -30);
    run(layer, [], []);
    let m = meshes(layer);
    expect(m['entity pig']).toBe(1);
    expect(m['entity shadows']).toBeUndefined();
    run(layer, [], [ent(1, 3, { type: 90 })]);
    m = meshes(layer);
    expect(m['entity pig']).toBe(2);
    expect(m['entity shadows']).toBe(1); // the real pig only
    // Leaning back: the pig's snout (front, model −z) rises above its tail.
    const bones = (layer as unknown as { bones: Float32Array }).bones;
    const drawn = (layer as unknown as { drawn: Map<string, { row: number; model: { built: { index: Record<string, number> } } }> }).drawn;
    const d = drawn.get('display:spawner@1,2,3')!;
    const body = d.model.built.index.body, o = (d.row * 64 + body) * 12;
    const front = apply(bones.subarray(o, o + 12), 0, -10, 0), back = apply(bones.subarray(o, o + 12), 0, 8, 0);
    expect(Math.abs(front[1] - back[1])).toBeGreaterThan(0.05);
    // An active spawner: one flame and one smoke a tick.
    const before = layer.effects.particles;
    layer.spawnerParticles(0, 0, 64, 0, 3, 1100);
    expect(layer.effects.particles - before).toBe(6);
    layer.dispose();
  });

  it.skipIf(noPacks)('head items: skulls by type as the skull model, blocks and flat items as items; only on the renderers with the layer', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [
      ent(1, 0, { type: 54, equipment: { head: [397, 4] } }), // creeper head on a zombie
      ent(2, 2, { type: 51, equipment: { head: [397, 5] } }), // dragon head on a skeleton
      ent(3, 4, { kind: 'player', name: 'Fox', equipment: { head: [397, 3] } }),
      ent(4, 6, { type: 54, equipment: { head: [264, 0] } }), // a diamond on the head
      ent(5, 8, { type: 53, equipment: { head: [86, 0], chest: [307, 0] } }), // the giant: armour but no custom head
      ent(6, 10, { type: 35, equipment: { chest: [307, 0] } }), // the vex: no armour layer
      ent(7, 12, { type: 54, equipment: { chest: [443, 0] } }), // elytra on a zombie
    ]);
    const m = meshes(layer);
    expect(m['skull head']).toBe(1);
    expect(m['skull dragon']).toBe(1);
    expect(m['skull humanoid']).toBe(1);
    expect(m['item sprites']).toBe(1); // the diamond
    expect(m['item cubes']).toBeUndefined(); // the giant's pumpkin is not drawn
    expect(m['armour zombie']).toBe(1); // the giant's chestplate (the vex wears none)
    expect(m.elytra).toBe(1);
    layer.dispose();
  });

  it.skipIf(noPacks)('implied layers: the snow golem’s pumpkin, an adult mooshroom’s three crossed mushrooms (none on a calf)', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [ent(1, 0, { type: 97 }), ent(2, 3, { type: 96 }), ent(3, 6, { type: 96, baby: true }), ent(4, 9, { type: 97, look: 'sheared' })]);
    const m = meshes(layer);
    expect(m['item cubes']).toBe(1); // one pumpkin (the sheared golem has none)
    expect(m['item sprites']).toBe(6); // 3 mushrooms × 2 crossed planes, the calf none
    layer.dispose();
  });

  it.skipIf(noPacks)('a fusing creeper swells and flashes white at most 0.2 (RenderCreeper), from when the wire says it began', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const drawn = (layer as unknown as { drawn: Map<string, { flash: number; fuseAt: number }> }).drawn;
    const c = ent(1, 0, { type: 50, look: 'fusing' });
    const flashes: number[] = [];
    for (let t = 1000; t <= 2400; t += 50) {
      layer.update([], [c], t, new Set());
      flashes.push(drawn.get('0:1')!.flash);
    }
    expect(drawn.get('0:1')!.fuseAt).toBe(1000);
    expect(Math.max(...flashes)).toBeGreaterThan(0.1);
    expect(Math.max(...flashes)).toBeLessThanOrEqual(0.2);
    expect(flashes.filter((f) => f === 0).length).toBeGreaterThan(5); // it blinks
    layer.update([], [{ ...c, dto: { ...c.dto, look: undefined } }], 2500, new Set());
    expect(drawn.get('0:1')!.fuseAt).toBe(0);
    layer.dispose();
  });

  it('a screaming enderman opens its jaw: the head rises 5 px off the headwear', () => {
    const enderman = buildModel(MODEL_BY_ID.enderman);
    const sk = new Skeleton(enderman);
    const r = rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    const out = new Float32Array(64 * 12);
    sk.pose({ ...CTX, v: Variant.of('screaming') }, r, out, 0);
    const y = (part: string) => apply(out.subarray(enderman.index[part] * 12, enderman.index[part] * 12 + 12), 0, 0, 0)[1];
    expect(y('head') - y('hat')).toBeCloseTo(5 / 16, 4);
    sk.pose(CTX, r, out, 0);
    expect(y('head') - y('hat')).toBeCloseTo(0, 4);
  });

  it.skipIf(noPacks)('babies are drawn at half the hitbox; glowing entities are lit', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [ent(1, 0, { type: 92, baby: true }), ent(2, 3, { type: 92, flags: 0x40 })]);
    const drawn = (layer as unknown as { drawn: Map<string, { h: number; glowing: boolean }> }).drawn;
    expect(drawn.get('0:1')!.h).toBeCloseTo(0.7);
    expect(drawn.get('0:2')!.glowing).toBe(true);
    layer.dispose();
  });
});
