import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import * as THREE from 'three';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack, Pack1122, PackBeta } from '../src/assets/bundle';
import { copies, EntityLayer, HELD_POSES } from '../src/render/entityLayer';
import { armorTexture, buildArmor } from '../src/render/entities/armor';
import { BOX_SLOT, buildModel, MAX_PARTS } from '../src/render/entities/builder';
import { isHandheld, itemLookById, itemLookByName, parseHeld } from '../src/render/entities/items';
import { poseBiped } from '../src/render/entities/models/common';
import { attackProgress, deathAngle, newMotion, stepMotion } from '../src/render/entities/motion';
import { apply, rootMatrix, Skeleton } from '../src/render/entities/pose';
import { ALL_MODELS, lookFn, lookKeys, MOB_NAMES, MODEL_BY_ID, mobLook, objectLook, playerLook } from '../src/render/entities/registry';
import type { PoseCtx } from '../src/render/entities/types';
import { Variant } from '../src/render/entities/variant';
import type { BotView, EntityView } from '../src/world/entities';
import type { EntityDto } from '../src/types';

const gen = join(__dirname, '..', 'generated');
const PACK_1122 = JSON.parse(readFileSync(join(gen, '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
const PACK_BETA = JSON.parse(readFileSync(join(gen, 'b1.8.1', 'pack.json'), 'utf8')) as PackBeta;

const ctx = (o: Partial<PoseCtx> = {}): PoseCtx => ({
  swing: 0, amount: 0, age: 0, headYaw: 0, pitch: 0, attack: 0, attackOff: 0, eat: 0, baby: false, v: Variant.NONE, using: false, bow: false, standPose: null, variant: 0, flags: 0, sneaking: false, riding: false, holding: false, aggressive: false, beta: false, seed: 0, speed: 0, ...o,
});

describe('entity model data', () => {
  // Vanilla's own minecart inner floor (uv 44,10, 18×14×1) runs past its 64 px texture; only its top face is ever sampled.
  const UV_EXCEPTIONS = new Set(['minecart']);

  it.each(ALL_MODELS.map((m) => [m.id, m] as const))('%s builds, fits its texture and poses to finite bones', (_id, def) => {
    const b = buildModel(def);
    expect(b.parts.length).toBeGreaterThan(0);
    expect(b.parts.length).toBeLessThanOrEqual(MAX_PARTS);
    expect(b.indices.length).toBeGreaterThan(0);
    expect(b.positions.length / 3).toBe(b.uvs.length / 2);
    if (!UV_EXCEPTIONS.has(def.id))
      for (const [u0, v0, u1, v1] of b.uvRects) {
        expect(u0).toBeGreaterThanOrEqual(0);
        expect(v0).toBeGreaterThanOrEqual(0);
        expect(u1).toBeLessThanOrEqual(def.tex[0]);
        expect(v1).toBeLessThanOrEqual(def.tex[1]);
      }
    const sk = new Skeleton(b);
    const out = new Float32Array(64 * 12);
    const root = rootMatrix(new Float32Array(12), { x: 0, y: 64, z: 0, yaw: 30, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    for (const c of [ctx(), ctx({ swing: 3.3, amount: 1, age: 123, headYaw: 40, pitch: -20, attack: 0.5, sneaking: true, variant: 1 })]) {
      sk.pose(c, root, out, 0);
      expect(out.every(Number.isFinite)).toBe(true);
    }
  });

  it('covers every mob type the wire can report, in both versions, with textures that exist', () => {
    for (const [type, name] of Object.entries(MOB_NAMES)) {
      const look = mobLook('1.12.2', Number(type), null, 0, 7);
      expect(look.model, name).not.toBe('box');
      expect(MODEL_BY_ID[look.model], name).toBeDefined();
      for (const t of [look.texture, ...(look.layers ?? []).map((l) => l.texture)]) expect(PACK_1122.entity.rects[t], `${name}: ${t}`).toBeDefined();
    }
    for (const type of [50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 90, 91, 92, 93, 94, 95]) {
      for (const v of [0, 1, 2, 4, 14, 16]) {
        const look = mobLook('b1.8.1', type, null, v, 3);
        expect(look.model).not.toBe('box');
        for (const t of [look.texture, ...(look.layers ?? []).map((l) => l.texture)]) expect(PACK_BETA.entity.rects[t], `beta ${type}: ${t}`).toBeDefined();
      }
    }
    for (const key of lookKeys()) {
      const l = lookFn(key)!(Variant.of(0), false, 1);
      expect(MODEL_BY_ID[l.model], key).toBeDefined();
    }
    for (const o of ['boat', 'minecart', 'arrow'])
      for (const beta of [false, true]) {
        const l = objectLook(beta ? 'b1.8.1' : '1.12.2', o, 0, 1)!;
        expect((beta ? PACK_BETA : PACK_1122).entity.rects[l.texture], `${o} ${beta}`).toBeDefined();
      }
    expect(PACK_1122.entity.rects[playerLook('1.12.2', 'x', true).texture]).toBeDefined();
    expect(PACK_BETA.entity.rects[playerLook('b1.8.1', 'x', true).texture]).toBeDefined();
  });

  it('layers carry their variants: saddle, wool colour, shearing, slime size, eyes', () => {
    expect(mobLook('1.12.2', 90, null, 1, 0).layers?.[0].layer).toBe('saddle');
    expect(mobLook('1.12.2', 90, null, 0, 0).layers).toEqual([]);
    const red = mobLook('1.12.2', 91, null, 14, 0);
    expect(red.layers?.[0].layer).toBe('wool');
    expect(red.layers?.[0].tint).toBeDefined();
    expect(mobLook('1.12.2', 91, null, 16 | 14, 0).layers).toEqual([]);
    expect(mobLook('b1.8.1', 55, null, 4, 0).scale).toBe(4);
    expect(mobLook('1.12.2', 52, null, 0, 0).layers?.[0].emissive).toBe(true);
    expect(mobLook('1.12.2', 59, null, 0, 0).scale).toBe(0.7);
  });

  it('the root puts the feet at the entity position and the model front toward its yaw', () => {
    const r = rootMatrix(new Float32Array(12), { x: 10, y: 64, z: -3, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    const feet = apply(r, 0, 24, 0);
    expect(feet[0]).toBeCloseTo(10);
    expect(feet[1]).toBeCloseTo(64, 1);
    expect(feet[2]).toBeCloseTo(-3);
    // Model front is −z; yaw 0 faces south (+z).
    expect(apply(r, 0, 24, -8)[2]).toBeGreaterThan(-3);
    const west = rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 90, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] });
    expect(apply(west, 0, 24, -8)[0]).toBeLessThan(-0.4);
    // Head height of a biped: 1.5 + 0.5 block.
    expect(apply(r, 0, -8, 0)[1]).toBeCloseTo(66, 1);
    // Death rolls the model over (90°: the head ends up beside the feet).
    const dead = rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 90, scale: [1, 1, 1], offset: [0, 0, 0] });
    expect(apply(dead, 0, -8, 0)[1]).toBeCloseTo(0, 1);
  });
});

describe('poses', () => {
  const player = buildModel(MODEL_BY_ID.player);

  it('limb swing from distance walked and amount (ModelBiped)', () => {
    const sk = new Skeleton(player);
    const p = sk.byName;
    sk.reset();
    poseBiped(p, ctx({ swing: 2, amount: 0.8 }));
    expect(p.rightLeg.rx).toBeCloseTo(Math.cos(2 * 0.6662) * 1.4 * 0.8);
    expect(p.leftLeg.rx).toBeCloseTo(Math.cos(2 * 0.6662 + Math.PI) * 1.4 * 0.8);
    expect(p.rightArm.rx).toBeCloseTo(Math.cos(2 * 0.6662 + Math.PI) * 0.8 + Math.sin(0) * 0.05);
    sk.reset();
    poseBiped(p, ctx({ swing: 2, amount: 0 }));
    expect(p.rightLeg.rx).toBeCloseTo(0);
  });

  it('head yaw and pitch, hat follows; sneaking bends the body', () => {
    const sk = new Skeleton(player);
    sk.reset();
    poseBiped(sk.byName, ctx({ headYaw: 45, pitch: 30, sneaking: true }));
    expect(sk.byName.head.ry).toBeCloseTo(Math.PI / 4);
    expect(sk.byName.head.rx).toBeCloseTo(Math.PI / 6);
    expect(sk.byName.hat.ry).toBeCloseTo(Math.PI / 4);
    expect(sk.byName.body.rx).toBeCloseTo(0.5);
    expect(sk.byName.head.py).toBeCloseTo(1);
  });

  it('motion: walking fills the limb-swing amount, turns the body to the walk direction, clamps the head', () => {
    const m = newMotion(0, 64, 0, 0, 'k', 0);
    let t = 0;
    // 0.2 blocks/tick toward +x (yaw −90 = east), looking south (yaw 0).
    for (let i = 0; i < 40; i++) {
      t += 50;
      stepMotion(m, ((i + 1) * 0.2), 64, 0, 0, t);
    }
    expect(m.amount).toBeGreaterThan(0.7);
    expect(m.swing).toBeGreaterThan(20);
    // Walking east while looking south: the body turns toward the walk but stays within 75° of the head.
    expect(Math.abs(((m.body + 540) % 360) - 180 - 0)).toBeLessThanOrEqual(75.01);
    expect(m.body).toBeLessThan(-30);
    // Standing still: the amount decays.
    for (let i = 0; i < 40; i++) {
      t += 50;
      stepMotion(m, 8, 64, 0, 0, t);
    }
    expect(m.amount).toBeLessThan(0.05);
  });

  it('hooks: arm swing progress and death fall over time', () => {
    const m = newMotion(0, 0, 0, 0, 'k', 0);
    m.attackAt = 1000;
    expect(attackProgress(m, 1150)).toBeCloseTo(0.5);
    expect(attackProgress(m, 1400)).toBe(0);
    m.deadAt = 1000;
    expect(deathAngle(m, 1000)).toBe(0);
    expect(deathAngle(m, 2000)).toBeCloseTo(90);
    expect(deathAngle(m, 1250)).toBeGreaterThan(30);
  });
});

describe('held and dropped items', () => {
  it('parses held names', () => {
    expect(parseHeld('cobblestone x12')).toEqual({ name: 'cobblestone', count: 12 });
    expect(parseHeld('stone pickaxe')).toEqual({ name: 'stone_pickaxe', count: 1 });
    expect(isHandheld('stone_pickaxe')).toBe(true);
    expect(isHandheld('torch')).toBe(false);
  });

  it('1.12.2: tools and flat items are sprites, blocks are cubes with their faces', () => {
    const pick = itemLookByName('1.12.2', PACK_1122, 'stone pickaxe');
    expect(pick).toMatchObject({ kind: 'sprite', atlas: 'items' });
    const t = PACK_1122.items.tiles['items/stone_pickaxe'];
    expect(pick && pick.kind === 'sprite' && pick.cell).toBe(t[1] * PACK_1122.items.columns + t[0]);
    expect(itemLookByName('1.12.2', PACK_1122, 'torch')).toMatchObject({ kind: 'sprite', atlas: 'blocks' });
    const cobble = itemLookByName('1.12.2', PACK_1122, 'cobblestone x12');
    const c = PACK_1122.blocks.tiles['blocks/cobblestone'];
    expect(cobble).toMatchObject({ kind: 'cube' });
    expect(cobble && cobble.kind === 'cube' && cobble.faces[0]).toBe(c[1] * PACK_1122.blocks.columns + c[0]);
    // Variants by damage: red wool, birch planks; grass tints its top only.
    const wool = itemLookById('1.12.2', PACK_1122, 35, 14);
    const w = PACK_1122.blocks.tiles['blocks/wool_colored_red'];
    expect(wool && wool.kind === 'cube' && wool.faces[1]).toBe(w[1] * PACK_1122.blocks.columns + w[0]);
    const grass = itemLookById('1.12.2', PACK_1122, 2, 0);
    expect(grass && grass.kind === 'cube' && grass.tintMask & 2).toBe(2);
    expect(itemLookById('1.12.2', PACK_1122, 264, 0)).toMatchObject({ kind: 'sprite', atlas: 'items' });
  });

  it('b1.8.1: items.png icons and terrain tiles', () => {
    expect(itemLookByName('b1.8.1', PACK_BETA, 'stone pickaxe')).toEqual({ kind: 'sprite', atlas: 'items', cell: 97, tint: 0 });
    expect(itemLookByName('b1.8.1', PACK_BETA, 'cobblestone')).toMatchObject({ kind: 'cube', faces: [16, 16, 16, 16, 16, 16] });
    expect(itemLookById('b1.8.1', PACK_BETA, 50, 0)).toMatchObject({ kind: 'sprite', atlas: 'blocks', cell: 80 });
    expect(itemLookById('b1.8.1', PACK_BETA, 2, 0)).toMatchObject({ kind: 'cube', faces: [2, 0, 3, 3, 3, 3] });
    expect(itemLookById('b1.8.1', PACK_BETA, 9999, 0)).toBeNull();
  });

  it('stack copies follow each version', () => {
    expect([1, 2, 6, 21].map((n) => copies(n, true))).toEqual([1, 2, 3, 4]);
    expect([1, 2, 17, 33, 49].map((n) => copies(n, false))).toEqual([1, 2, 3, 4, 5]);
    expect(Object.keys(HELD_POSES)).toEqual(['block', 'tool', 'flat']);
  });

  it('armour binds to the wearer\'s bone slots, one layer per piece', () => {
    const player = buildModel(MODEL_BY_ID.player);
    const armor = buildArmor(player)!;
    expect(armor).not.toBeNull();
    const slots = new Set(Array.from(armor.partIds, (t) => t % 256));
    for (const n of ['head', 'body', 'rightArm', 'leftArm', 'rightLeg', 'leftLeg']) expect(slots.has(player.index[n])).toBe(true);
    expect(new Set(Array.from(armor.partIds, (t) => Math.floor(t / 256)))).toEqual(new Set([1, 2, 3, 4]));
    expect(buildArmor(buildModel(MODEL_BY_ID.cow))).toBeNull();
    expect(PACK_1122.entity.rects[armorTexture('iron', 'legs', false)]).toBeDefined();
    expect(PACK_BETA.entity.rects[armorTexture('leather', 'helmet', true)]).toBeDefined();
  });
});

describe('EntityLayer', () => {
  beforeAll(() => {
    // Label sprites draw on a canvas; node has none (a context-less stand-in keeps their sizes).
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });
  const fakeBundle = (pack: Pack): AssetBundle =>
    ({ version: pack.version, pack, blocks: {}, items: {}, entity: {}, anim: null, env: {} }) as unknown as AssetBundle;
  const ent = (id: number, x: number, extra: Partial<EntityDto> = {}): EntityView => {
    const dto = { kind: 'mob', dim: 0, x, y: 64, z: 0, id, type: 54, ...extra } as EntityDto;
    return { key: `0:${id}`, id, dim: 0, kind: dto.kind, name: null, type: dto.type ?? -1, item: dto.item ?? 0, count: dto.count ?? 1, x, y: 64, z: 0, yaw: 0, ageTicks: 0, flags: 0, dto };
  };
  const bot: BotView = { name: 'Alice', dim: 0, x: 2, y: 64, z: 0, yaw: 0, dto: { name: 'Alice', short: 'main', main: true, dim: 0, x: 2, y: 64, z: 0, hp: 20, food: 20, held: 'stone pickaxe' } };

  it('switches to LOD boxes beyond the detail distance, models near the camera, and costs one draw per kind', () => {
    const layer = new EntityLayer();
    layer.setBundle(fakeBundle(PACK_1122));
    layer.detailDistance = 20;
    const ents = [ent(1, 0), ent(2, 3), ent(3, 6, { type: 92 }), ent(4, 100)];
    const cam = new THREE.Vector3(0, 70, 10);
    for (let f = 0; f < 3; f++) {
      layer.update([bot], ents, 1000 + f * 16, new Set());
      layer.showFor(0, cam, { bots: true, entities: true, labels: false });
    }
    expect(layer.detailOf('0:1')).toBe(true);
    expect(layer.detailOf('0:4')).toBe(false);
    expect(layer.instances).toBe(5);
    const meshes: string[] = [];
    layer.group.traverse((o) => {
      if ((o as THREE.Mesh).isMesh && o.visible && o.name) meshes.push(o.name);
    });
    // zombie, cow, player (bot), the far box, the pickaxe sprite, shadows.
    expect(meshes.sort()).toEqual(['entity box', 'entity cow', 'entity player', 'entity shadows', 'entity zombie', 'item sprites'].sort());
    // A narrow (iso) field of view sees far entities large: detail reaches further.
    layer.detailDistance = 20;
    const far = new THREE.Vector3(0, 70, 60);
    for (let f = 0; f < 2; f++) {
      layer.update([bot], ents, 2000 + f * 16, new Set());
      layer.showFor(0, far, { bots: true, entities: true, labels: false, fov: 12 });
    }
    expect(layer.detailOf('0:1')).toBe(true); // 60 blocks away, but a 12° view makes it look 9 away
    layer.showFor(0, far, { bots: true, entities: true, labels: false });
    expect(layer.detailOf('0:1')).toBe(false); // the same distance at 70°
    layer.dispose();
  });

  it('without a bundle every entity is a box; the box slot holds the hitbox', () => {
    const layer = new EntityLayer();
    layer.update([bot], [ent(1, 0)], 1000, new Set());
    layer.showFor(0, new THREE.Vector3(0, 70, 5), { bots: true, entities: true, labels: false });
    expect(layer.detailOf('0:1')).toBe(false);
    const bones = (layer as unknown as { bones: Float32Array }).bones;
    const row = (layer as unknown as { drawn: Map<string, { row: number }> }).drawn.get('0:1')!.row;
    const o = row * 64 * 12 + BOX_SLOT * 12;
    expect(bones[o + 5]).toBeCloseTo(1.95);
    expect(bones[o + 7]).toBeCloseTo(64);
    layer.dispose();
  });

  it('hides only the controlled body in first person and restores it for another shared view', () => {
    const layer = new EntityLayer();
    layer.setBundle(fakeBundle(PACK_1122));
    const other = { ...bot, name: 'Bob', x: 5, dto: { ...bot.dto, name: 'Bob', short: '102' } };
    layer.update([bot, other], [ent(1, 0)], 1000, new Set());
    const camera = new THREE.Vector3(2, 65.62, 0);
    layer.showFor(0, camera, { bots: true, entities: true, labels: true, hiddenBot: 'main' });
    expect(layer.instances).toBe(2);
    expect(layer.detailOf('Alice')).toBeNull();
    expect(layer.detailOf('Bob')).toBe(true);
    layer.showFor(0, camera, { bots: true, entities: true, labels: true });
    expect(layer.instances).toBe(3);
    expect(layer.detailOf('Alice')).toBe(true);
    layer.dispose();
  });

  it('a bot whose hp drops flashes red; hp 0 starts the death fall; botAt finds it', () => {
    const layer = new EntityLayer();
    layer.setBundle(fakeBundle(PACK_1122));
    const cam = new THREE.Vector3(2, 65, 6);
    layer.update([bot], [], 1000, new Set());
    layer.showFor(0, cam, { bots: true, entities: true, labels: false });
    layer.update([{ ...bot, dto: { ...bot.dto, hp: 12 } }], [], 1100, new Set());
    const d = (layer as unknown as { drawn: Map<string, { hurt: number; motion: { deadAt: number } }> }).drawn.get('Alice')!;
    expect(d.hurt).toBe(1);
    layer.update([{ ...bot, dto: { ...bot.dto, hp: 0 } }], [], 1200, new Set());
    expect(d.motion.deadAt).toBe(1200);
    layer.showFor(0, cam, { bots: true, entities: true, labels: false });
    const ray = new THREE.Raycaster(cam, new THREE.Vector3(0, -0.1, -1).normalize());
    expect(layer.botAt(ray)).toBe('Alice');
    layer.dispose();
  });
});
