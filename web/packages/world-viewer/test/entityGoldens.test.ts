// Behaviour pin for the complexity refactor of render/entityLayer.ts (docs/dev/complexity.md, WP12): a seeded crowd of every
// mob type, players, drops, objects, hanging entities, bots and spawner display mobs, with random looks, equipment, flags and
// events, driven through several frames and views (near / far / iso / ghosts / cut / frustum / labels) in both versions and
// without a bundle. Each view's bone texture, every instanced draw's instance data and the scene graph state are hashed; the
// digests were taken from the code before it was reshaped.

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import * as THREE from 'three';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { AssetBundle, Pack, Pack1122, PackBeta } from '../src/assets/bundle';
import { EntityLayer, type EntityShowOptions } from '../src/render/entityLayer';
import { MOB_NAMES } from '../src/render/entities/registry';
import { FLAG_GLOWING, FLAG_INVISIBLE, FLAG_ON_FIRE, FLAG_SNEAKING, FLAG_SPRINTING, type EntityDto, type EntityEvent, type Equipment } from '../src/types';
import type { BotView, EntityView } from '../src/world/entities';
import { Digest, rng } from './digest';

const gen = join(__dirname, '..', 'generated');
const PACK_1122 = JSON.parse(readFileSync(join(gen, '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
const PACK_BETA = JSON.parse(readFileSync(join(gen, 'b1.8.1', 'pack.json'), 'utf8')) as PackBeta;
const fakeBundle = (pack: Pack): AssetBundle =>
  ({ version: pack.version, pack, blocks: {}, items: {}, entity: {}, anim: null, env: { glint: {} } }) as unknown as AssetBundle;

const TOKENS = ['aggressive', 'angry', 'armor', 'arms', 'arms_raised', 'attacking', 'caerbannog', 'carpet', 'charged', 'charging', 'chest', 'chested', 'converting', 'fuse', 'golden', 'holding_rose', 'ignited', 'invulnerable', 'killer_bunny', 'marker', 'no_base_plate', 'no_pumpkin', 'open', 'powered', 'rose', 'saddled', 'screaming', 'sheared', 'shooting', 'show_arms', 'sitting', 'small', 'tamed', 'toast', 'red', 'blue', 'green', 'white', 'black', 'brown', '1', '2', '3', '5', 'tabby', 'creamy'];
const FLAGS = [FLAG_ON_FIRE, FLAG_INVISIBLE, FLAG_GLOWING, FLAG_SNEAKING, FLAG_SPRINTING, 0x04, 0x10];
const ITEMS: Array<[number, number]> = [[1, 0], [4, 0], [17, 2], [35, 14], [50, 0], [86, 0], [91, 0], [38, 0], [6, 1], [256, 0], [261, 0], [267, 0], [276, 0], [283, 0], [298, 0], [302, 0], [306, 0], [310, 0], [311, 0], [314, 0], [299, 0], [300, 0], [301, 0], [345, 0], [397, 0], [397, 3], [397, 5], [322, 1], [384, 0], [403, 0], [443, 0], [442, 0], [262, 0], [264, 0], [331, 0], [2, 0], [81, 0], [54, 0], [12, 0], [46, 0]];
const OBJECT_NAMES = ['FallingSand', 'PrimedTnt', 'MinecartChest', 'minecraft:fireworks_rocket', 'EnderCrystal', 'Snowball', 'ThrownEgg', 'ArmorStand', 'MinecartHopper', 'minecart_rideable', 'XPOrb', 'Boat'];
const MOTIVES = ['Kebab', 'Aztec', 'Alban', 'Pool', 'Courbet', 'Sunset', 'Creebet', 'Wanderer', 'Fighters', 'Pointer', 'Pigscene', 'BurningSkull', 'Skeleton', 'DonkeyKong', null];

function crowd(seed: number) {
  const r = rng(seed);
  const pick = <T,>(a: readonly T[]): T => a[Math.floor(r() * a.length)];
  const chance = (p: number) => r() < p;
  const item = (): [number, number] | [number, number, number] => (chance(0.15) ? [298 + Math.floor(r() * 4), 0, Math.floor(r() * 0xffffff)] : pick(ITEMS));
  const equipment = (): Equipment | null => {
    if (!chance(0.6)) return null;
    const eq: Equipment = {};
    for (const slot of ['main', 'off', 'head', 'chest', 'legs', 'feet'] as const) if (chance(0.45)) eq[slot] = item();
    if (chance(0.3)) eq.glint = { main: chance(0.5), off: chance(0.5), head: chance(0.5), chest: chance(0.5), legs: chance(0.5), feet: chance(0.5) };
    return eq;
  };
  const flags = () => FLAGS.reduce((f, b) => (chance(0.12) ? f | b : f), 0);
  const look = () => (chance(0.5) ? Array.from({ length: 1 + Math.floor(r() * 3) }, () => pick(TOKENS)).join(' ') : null);
  const ents: EntityView[] = [];
  let id = 1;
  const add = (kind: string, extra: Partial<EntityDto>, name: string | null = null, type = -1) => {
    const x = (r() - 0.5) * 60, y = 60 + Math.floor(r() * 10), z = (r() - 0.5) * 60, yaw = r() * 360 - 180;
    const dto = { kind, dim: 0, x, y, z, id, type, ...extra } as EntityDto;
    ents.push({ key: `0:${id}`, id, dim: 0, kind, name, type, item: dto.item ?? 0, count: dto.count ?? 1, x, y, z, yaw, ageTicks: Math.floor(r() * 400), flags: dto.flags ?? 0, dto });
    id++;
  };
  const types = Object.keys(MOB_NAMES).map(Number).concat([-1, 999]);
  for (const type of types)
    for (let k = 0; k < 2; k++)
      add('mob', {
        variant: chance(0.5) ? Math.floor(r() * 6) : null,
        look: look(),
        baby: chance(0.25),
        equipment: equipment(),
        flags: flags(),
        headYaw: chance(0.4) ? r() * 360 - 180 : null,
        carried: chance(0.5) ? pick(ITEMS) : undefined,
        fuse: chance(0.3) ? Math.floor(r() * 80) : null,
        vehicle: chance(0.1) ? 5 : null,
        pose: chance(0.2) ? { head: [10, 20, 0], rightArm: [-30, 0, 10] } : null,
      }, chance(0.2) ? pick(['Zombie', 'minecraft:pig', 'EntityHorse', 'cave_spider', 'nope']) : null, type);
  // Rare combinations, placed near the origin: a golem holding its rose, fusing creepers, elytra and skulls on bipeds, babies.
  const special: Array<[number, Partial<EntityDto>]> = [
    [99, { look: 'holding_rose' }], [99, { variant: 0, look: 'rose' }], [50, { variant: 1 }], [50, { look: 'fuse charged' }], [58, { look: 'screaming', carried: [2, 0] }],
    [58, { carried: [38, 0] }], [54, { equipment: { chest: [443, 0], head: [397, 5], glint: { chest: true } }, baby: true }], [51, { equipment: { chest: [443, 0], head: [397, 2] }, flags: FLAG_SNEAKING }],
    [57, { equipment: { head: [86, 0], main: [261, 0] }, flags: 0x10, look: 'aggressive' }], [120, { equipment: { head: [91, 0] }, baby: true }], [27, { equipment: { head: [397, 4] }, look: 'converting' }],
    [97, { look: 'sheared' }], [97, {}], [96, { baby: false }], [92, { baby: true }], [27, { equipment: { head: [38, 0] } }], [66, { baby: true }], [65, { look: 'sitting' }],
  ];
  for (const [type, extra] of special) {
    add('mob', extra, null, type);
    const e = ents[ents.length - 1];
    e.x = e.dto.x = (ents.length % 7) - 3;
    e.z = e.dto.z = (ents.length % 5) - 2;
  }
  for (let k = 0; k < 8; k++)
    add('player', { equipment: equipment(), flags: flags(), item: chance(0.5) ? pick(ITEMS)[0] : 0, shoulders: chance(0.5) ? { left: chance(0.5) ? pick(['red', 'blue', 'green']) : null, right: chance(0.5) ? 'cyan' : null } : null, arrows: Math.floor(r() * 5), headYaw: r() * 90 }, pick(['Steve', 'Alex', 'Notch', 'jeb_', null]));
  for (let k = 0; k < 14; k++) {
    const [it, dmg] = pick(ITEMS);
    add('item', { item: it, damage: dmg, count: pick([1, 2, 3, 6, 17, 21, 33, 49, 64]), glint: chance(0.2), flags: chance(0.1) ? FLAG_ON_FIRE : 0 });
  }
  for (let k = 0; k < 4; k++) add('xp_orb', { count: pick([1, 3, 7, 17, 37, 73, 149, 307, 617, 1237, 2477]) });
  for (const t of [1, 2, 10, 11, 12, 50, 51, 60, 61, 62, 63, 64, 65, 66, 67, 70, 71, 72, 73, 75, 76, 77, 78, 90, 91, 93, 99])
    add('object', { vx: chance(0.5) ? r() - 0.5 : 0, vy: r() - 0.5, vz: r() - 0.5, block: chance(0.4) ? pick(ITEMS) : undefined, item: chance(0.3) ? 12 : 0, minecartType: chance(0.4) ? Math.floor(r() * 7) : null, fuse: chance(0.5) ? Math.floor(r() * 12) : null, equipment: t === 78 ? equipment() : null, pose: t === 78 ? { leftArm: [20, 0, -10] } : null }, null, t);
  for (const n of OBJECT_NAMES) add('object', { fuse: 5 }, n, -1);
  for (const m of MOTIVES) add('painting', { motive: m, facing: pick([0, 1, 2, 3, 'north', 'east', null]) });
  for (let k = 0; k < 5; k++) add('item_frame', { frameItem: chance(0.8) ? pick(ITEMS) : undefined, rotation: Math.floor(r() * 8), facing: Math.floor(r() * 4), glint: chance(0.3) });
  for (let k = 0; k < 3; k++) add('armor_stand', { equipment: equipment(), look: look(), pose: { body: [0, 10, 0] } });
  const bots: BotView[] = ['Alice', 'Bob', 'Cara', 'Dan'].map((name, k) => {
    const x = k * 3 - 4, z = 3;
    return { name, dim: 0, x, y: 64, z, yaw: k * 40, dto: { name, short: k ? String(60 + k) : 'main', main: k === 0, dim: 0, x, y: 64, z, hp: 20, food: 20, held: pick(['stone pickaxe', 'torch', 'bow', 'diamond_sword', null]), job: k === 3 ? 'mining' : null, pitch: k * 10, equipment: k % 2 ? equipment() : null } };
  });
  return { ents, bots };
}

function digestLayer(d: Digest, layer: EntityLayer): void {
  const bones = (layer as unknown as { bones: Float32Array }).bones;
  d.bytes(bones);
  layer.group.traverse((o) => {
    const p = o.position, s = o.scale;
    d.add(`${o.type}|${o.name}|${o.visible}|${o.renderOrder}|${p.x},${p.y},${p.z}|${s.x},${s.y},${s.z}`);
    const g = (o as THREE.Mesh).geometry as THREE.InstancedBufferGeometry | undefined;
    if (!g || !(o as THREE.Mesh).isMesh) return;
    const n = g.instanceCount;
    d.add(`instances ${n}`);
    if (!Number.isFinite(n)) return;
    for (const [k, a] of Object.entries(g.attributes)) {
      if (!(a as THREE.InstancedBufferAttribute).isInstancedBufferAttribute) continue;
      const arr = (a as THREE.InstancedBufferAttribute).array as Float32Array;
      d.add(k);
      d.bytes(arr.subarray(0, n * a.itemSize));
    }
    const m = (o as THREE.Mesh).material as THREE.MeshBasicMaterial;
    if (m && 'opacity' in m) d.add(`opacity ${m.opacity}`);
  });
  d.add(`${layer.instances}|${layer.botModels}|${layer.drawCalls}|${JSON.stringify(layer.effects)}`);
}

function run(bundle: AssetBundle | null, version: 'b1.8.1' | '1.12.2', seed: number): string {
  const { ents, bots } = crowd(seed);
  const random = vi.spyOn(Math, 'random').mockImplementation(rng(seed + 100));
  const layer = new EntityLayer();
  layer.setVersion(version);
  layer.setBundle(bundle);
  layer.detailDistance = 40;
  layer.lightAt = (_dim, x, y, z) => (Math.floor(x + z) % 3 === 0 ? null : [Math.abs(Math.floor(y)) % 16, Math.abs(Math.floor(x)) % 16]);
  layer.showDisplayMob('cage1', 0, 1, 62, 1, 'Zombie', 0.5, 0.3, -30);
  layer.showDisplayMob('cage2', 0, 4, 62, 1, 'minecraft:cave_spider', 0.4, 1.2);
  layer.showDisplayMob('cage3', 0, 7, 62, 1, 'EntityPig', 0.6, 2.2, 10);
  const d = new Digest();
  const camNear = new THREE.Vector3(0, 70, 8), camFar = new THREE.Vector3(10, 120, 90);
  const pcam = new THREE.PerspectiveCamera(60, 1.5, 0.1, 500);
  pcam.position.set(0, 80, 40);
  pcam.lookAt(0, 64, 0);
  pcam.updateMatrixWorld();
  const frustum = new THREE.Frustum().setFromProjectionMatrix(new THREE.Matrix4().multiplyMatrices(pcam.projectionMatrix, pcam.matrixWorldInverse));
  const ghosts = new Map<string, number>([['Alice', 0.8], ['Bob', 0.01], ['Cara', 0.5], ['0:200', 0.6]]);
  for (const e of ents) if (e.kind === 'player') ghosts.set(e.key, 0.4);
  const views: EntityShowOptions[] = [
    { bots: true, entities: true, labels: false },
    { bots: true, entities: true, labels: true, selected: 'Alice', ghosts, drawDistance: 70 },
    { bots: true, entities: true, labels: true, botLabels: false, fov: 12, centre: { x: 0, z: 0 }, drops: false, selected: '61' },
    { bots: false, entities: true, labels: false, frustum, cut: { x: 0, y: 64, z: 0, r: 20, dimBelow: 62 } },
    { bots: true, entities: false, labels: true, cut: { x: 0, y: 66, z: 0, r: 30, dimBelow: null }, ghosts },
  ];
  const r = rng(seed + 1);
  for (let f = 0; f < 10; f++) {
    const now = 1000 + f * 120;
    const moved = ents.map((e, i) => (i % 3 === f % 3 ? { ...e, x: e.x + f * 0.15, z: e.z - f * 0.1, yaw: e.yaw + f * 7, ageTicks: e.ageTicks + f * 2 } : e));
    const bv = bots.map((b, i) => ({ ...b, x: b.x + f * 0.2 * i, yaw: b.yaw + f * 5, dto: { ...b.dto, hp: i === 1 ? Math.max(0, 20 - f * 4) : i === 2 && f > 5 ? 12 : 20, flags: f % 4 === i ? FLAG_SNEAKING | FLAG_ON_FIRE : 0 } as BotView['dto'] }));
    if (f === 7) bv.pop();
    if (f % 2 === 1) {
      const evs: EntityEvent[] = [];
      for (const kind of ['swing', 'swing_off', 'hurt', 'death', 'crit', 'magic_crit', 'eat', 'teleport']) evs.push({ id: 1 + Math.floor(r() * ents.length), dim: 0, kind, at: now, x: chance(r) ? r() * 10 : null, y: 64, z: 2 });
      evs.push({ name: 'Cara', kind: 'swing', at: now }, { kind: 'lightning', dim: 0, at: now - 50, x: 3, y: 64, z: -2 }, { kind: 'crit', at: now, x: 1, y: 64, z: 1 }, { id: 99999, kind: 'hurt', at: now });
      layer.onEvents?.(evs, now);
    }
    if (f === 3) layer.hideDisplayMob('cage2');
    if (f === 4) {
      layer.swing('Alice', true, now);
      layer.hurt('0:3', now);
      layer.kill('0:4', now);
      layer.spawnerParticles(0, 1, 62, 1, 3, now);
    }
    layer.update(bv, moved, now, new Set(f % 2 ? ['Alice', '0:5'] : ['62']));
    for (const [k, v] of views.entries()) {
      layer.showFor(0, k % 2 ? camFar : camNear, v);
      digestLayer(d, layer);
    }
    layer.showFor(1, camNear, views[0]);
    digestLayer(d, layer);
    d.add(JSON.stringify(layer.ghostCandidates(0, camNear, 30)));
    for (const e of ents.slice(0, 40)) d.add(`${layer.detailOf(e.key)}|${layer.labelLift(e.key, false)}`);
    for (const b of bots) d.add(`${layer.detailOf(b.name)}|${layer.labelLift(b.name, true)}`);
    const ray = new THREE.Raycaster(new THREE.Vector3(0, 65, 20), new THREE.Vector3(0, 0, -1));
    d.add(String(layer.botAt(ray)));
  }
  layer.dispose();
  random.mockRestore();
  return d.hex();
}

const chance = (r: () => number) => r() < 0.5;

describe('entity layer golden (behaviour pin)', () => {
  beforeAll(() => {
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });

  it('1.12.2 crowd', () => {
    expect(run(fakeBundle(PACK_1122), '1.12.2', 11)).toBe('006f29904788eb8ff213270288462988');
  });

  it('b1.8.1 crowd', () => {
    expect(run(fakeBundle(PACK_BETA), 'b1.8.1', 12)).toBe('b27b46600f98a19af3c0962464e95b5e');
  });

  it('no bundle', () => {
    expect(run(null, '1.12.2', 13)).toBe('f500b73f353b2a03cf39778f915e70f3');
  });
});
