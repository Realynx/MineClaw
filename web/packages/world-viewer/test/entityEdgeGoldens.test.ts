// Behaviour pin for the complexity refactor of render/entityLayer.ts (docs/dev/complexity.md, WP12), next to
// entityGoldens: the paths its crowd rarely reaches. Bots with wire ids, health drops, deaths and respawns, held items, jobs
// and older armour lists; events addressed by bot name, by the older `bot` field and by a bot's entity id; players whose
// shoulder parrots and stuck arrows change; skulls, blocks and flat items worn on (baby, villager) heads; items in both
// hands; experience orbs of every size; ghosts of selected bots by short key; and the labels' canvas calls (text, colours)
// through a recording stand-in canvas. The digests were taken from the code before it was reshaped.

import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import type { AssetBundle, Pack } from '../src/assets/bundle';
import { EntityLayer, type EntityShowOptions } from '../src/render/entityLayer';
import { FLAG_ON_FIRE, FLAG_SNEAKING, type EntityDto, type EntityEvent, type Equipment } from '../src/types';
import type { BotView, EntityView } from '../src/world/entities';
import { Digest, rng } from './digest';
import { noPacks, pack1122, packBeta } from './pack';

const PACK_1122 = noPacks ? null! : pack1122();
const PACK_BETA = noPacks ? null! : packBeta();
const fakeBundle = (pack: Pack): AssetBundle =>
  ({ version: pack.version, pack, blocks: {}, items: {}, entity: {}, anim: null, env: { glint: {} } }) as unknown as AssetBundle;

let canvasLog: string[] = [];
function recorder(): CanvasRenderingContext2D {
  return new Proxy({} as Record<string, unknown>, {
    get: (t, p: string) => {
      if (p === 'measureText') return (s: string) => ({ width: [...s].length * 7 });
      if (p in t) return t[p];
      return (...a: unknown[]) => void canvasLog.push(`${p}(${a.map((x) => (typeof x === 'object' ? typeof x : String(x))).join(',')})`);
    },
    set: (t, p: string, v) => {
      t[p] = v;
      canvasLog.push(`${p}=${typeof v === 'object' ? typeof v : String(v)}`);
      return true;
    },
  }) as unknown as CanvasRenderingContext2D;
}

function digestLayer(d: Digest, layer: EntityLayer): void {
  d.bytes((layer as unknown as { bones: Float32Array }).bones);
  layer.group.traverse((o) => {
    const p = o.position, s = o.scale;
    d.add(`${o.type}|${o.name}|${o.visible}|${o.renderOrder}|${p.x},${p.y},${p.z}|${s.x},${s.y},${s.z}`);
    const mat = (o as THREE.Mesh).material as THREE.MeshBasicMaterial | undefined;
    if (mat && 'opacity' in mat) d.add(`opacity ${mat.opacity}`);
    const g = (o as THREE.Mesh).geometry as THREE.InstancedBufferGeometry | undefined;
    if (!g || !(o as THREE.Mesh).isMesh) return;
    const n = g.instanceCount;
    d.add(`instances ${n}`);
    if (!Number.isFinite(n)) return;
    for (const [k, a] of Object.entries(g.attributes)) {
      if (!(a as THREE.InstancedBufferAttribute).isInstancedBufferAttribute) continue;
      d.add(k);
      d.bytes(((a as THREE.InstancedBufferAttribute).array as Float32Array).subarray(0, n * a.itemSize));
    }
  });
  d.add(`${layer.instances}|${layer.botModels}|${layer.drawCalls}|${JSON.stringify(layer.effects)}`);
  d.add(canvasLog.join(';'));
  canvasLog = [];
}

function scene(seed: number, version: '1.12.2' | 'b1.8.1') {
  const r = rng(seed);
  const ents: EntityView[] = [];
  let id = 1;
  const add = (kind: string, type: number, name: string | null, extra: Partial<EntityDto>, x: number, z: number) => {
    const dto = { kind, dim: 0, x, y: 64, z, id, type, ...extra } as EntityDto;
    ents.push({ key: `0:${id}`, id, dim: 0, kind, name, type, item: dto.item ?? 0, count: dto.count ?? 1, x, y: 64, z, yaw: r() * 360 - 180, ageTicks: Math.floor(r() * 400), flags: dto.flags ?? 0, dto });
    id++;
  };
  const heads: Array<[number, number]> = [[397, 0], [397, 1], [397, 2], [397, 3], [397, 4], [397, 5], [397, 9], [86, 0], [91, 0], [38, 0], [50, 0], [2, 0], [298, 0]];
  let k = 0;
  for (const type of [54, 57, 51, 120, 27, 53, 23, 36])
    for (const [hid, hmeta] of heads) {
      const baby = (k++ % 3) === 0;
      add('mob', type, null, { baby, equipment: { head: [hid, hmeta], main: k % 2 ? [276, 0] : [261, 0], off: k % 4 === 1 ? [2, 0] : undefined, glint: { head: k % 5 === 0 } }, flags: k % 7 === 0 ? FLAG_SNEAKING : 0, armor: k % 3 === 1 ? ['iron', null, 'gold', 'leather'] : undefined } as Partial<EntityDto>, (k % 10) - 5, Math.floor(k / 10) - 5);
    }
  for (let j = 0; j < 6; j++)
    add('player', -1, ['Steve', 'Alex', 'Notch', 'jeb_', 'Dinnerbone', 'x'][j], { equipment: { main: [[276, 0], [1, 0], [261, 0], [346, 0], [50, 0], [357, 0]][j] as [number, number], off: j % 2 ? [[442, 0], [50, 0], [1, 0]][j % 3] as [number, number] : undefined, head: j === 2 ? [397, 3] : undefined }, armor: j === 4 ? ['diamond', 'diamond', null, null] : undefined } as Partial<EntityDto>, j * 2 - 6, 4);
  for (const count of [1, 3, 7, 17, 37, 73, 149, 307, 617, 1237, 2477, 9999]) add('xp_orb', -1, null, { count } as Partial<EntityDto>, count % 7, -6);
  for (const [it, dmg] of [[1, 0], [276, 0], [35, 3], [2, 0], [264, 0], [50, 0], [6, 0]]) add('item', -1, null, { item: it, damage: dmg, count: 3, glint: it === 276 } as Partial<EntityDto>, it % 9 - 4, 7);
  const bots: BotView[] = ['Alice', 'Bob', 'Cara'].map((name, j) => ({
    name, dim: 0, x: j * 2 - 2, y: 64, z: -3, yaw: j * 50,
    dto: { name, short: ['main', '61', '62'][j], main: j === 0, dim: 0, x: j * 2 - 2, y: 64, z: -3, hp: 20, food: 20, held: null, job: null, pitch: 0, id: 500 + j, armor: j === 2 ? ['chainmail', 'iron', null, 'gold'] : undefined } as unknown as BotView['dto'],
  }));
  void version;
  return { ents, bots, r };
}

function run(bundle: AssetBundle | null, version: '1.12.2' | 'b1.8.1', seed: number): string {
  const { ents, bots, r } = scene(seed, version);
  const random = vi.spyOn(Math, 'random').mockImplementation(rng(seed + 100));
  const layer = new EntityLayer();
  layer.setVersion(version);
  layer.setBundle(bundle);
  layer.detailDistance = 60;
  layer.lightAt = (_dim, x, y, z) => [Math.abs(Math.floor(x + y + z)) % 16, 15];
  const d = new Digest();
  const cam = new THREE.Vector3(0, 70, 14);
  const views: EntityShowOptions[] = [
    { bots: true, entities: true, labels: true },
    { bots: true, entities: true, labels: true, selected: 'main', ghosts: new Map([['Alice', 0.7], ['Bob', 0.01], ['Cara', 0.4], ['0:105', 0.5], ['0:106', 0.9]]) },
    { bots: true, entities: true, labels: true, selected: '62', ghosts: new Map([['Alice', 0.3], ['Cara', 1], ['0:1', 0.8]]) },
  ];
  const hps = [20, 20, 15, 15, 0, 0, 0, 20, 12, 20, 20, 3];
  for (let f = 0; f < 12; f++) {
    const now = 1000 + f * 150;
    const bv = bots.map((b, j) => ({
      ...b,
      x: b.x + f * 0.1 * j,
      dto: {
        ...b.dto,
        hp: j === 1 ? hps[f] : j === 2 && f > 8 ? 0 : 20,
        held: [null, 'stone pickaxe', 'torch', 'bow', 'diamond_sword'][(f + j) % 5],
        job: f % 3 === j ? 'mining' : null,
        main: j === 0 && f % 4 !== 3,
        equipment: (j + f) % 4 === 0 ? ({ main: [267, 0], chest: [443, 0], glint: { main: true, chest: f % 2 === 0 } } as Equipment) : null,
        flags: f % 5 === j ? FLAG_ON_FIRE : 0,
      } as BotView['dto'],
    }));
    const moved = ents.map((e) => {
      if (e.kind !== 'player') return e;
      const dto = { ...e.dto, shoulders: (e.id + f) % 3 === 0 ? { left: ['red', 'blue', 'green', 'cyan', 'gray'][f % 5], right: f % 2 ? 'cyan' : null } : f % 4 === 1 ? { left: null, right: 'gray' } : null, arrows: (e.id + f) % 5, flags: f % 3 === 0 ? FLAG_SNEAKING : 0 } as EntityDto;
      return { ...e, dto, flags: dto.flags ?? 0, x: e.x + f * 0.05, yaw: e.yaw + f * 3 };
    });
    if (f % 2 === 0) {
      const evs: EntityEvent[] = [
        { name: 'Bob', kind: 'swing', at: now } as EntityEvent,
        { bot: 'Cara', kind: 'hurt', at: now } as unknown as EntityEvent,
        { id: 501, dim: 0, kind: 'swing_off', at: now } as EntityEvent,
        { id: 502, kind: 'eat', at: now } as EntityEvent,
        { id: 1 + Math.floor(r() * ents.length), dim: 0, kind: ['swing', 'hurt', 'teleport', 'eat', 'crit'][f % 5], at: now, x: 1, y: 64, z: 1 } as EntityEvent,
        { name: 'nobody', id: 3, dim: 0, kind: 'hurt', at: now } as EntityEvent,
        { name: 42, id: 999, kind: 'death', at: now } as unknown as EntityEvent,
      ];
      layer.onEvents?.(evs, now);
    }
    layer.update(bv, moved, now, new Set(f % 2 ? ['61'] : ['Alice', '0:110']));
    for (const v of views) {
      layer.showFor(0, cam, v);
      digestLayer(d, layer);
    }
    d.add(JSON.stringify(layer.ghostCandidates(0, cam, 40)));
    for (const b of bots) d.add(`${layer.detailOf(b.name)}|${layer.labelLift(b.name, true)}`);
    for (const e of ents.slice(100)) d.add(`${layer.detailOf(e.key)}|${layer.labelLift(e.key, false)}`);
  }
  layer.dispose();
  digestLayer(d, layer);
  random.mockRestore();
  return d.hex();
}

describe('entity layer edge goldens (behaviour pin)', () => {
  const g = globalThis as Record<string, unknown>;
  let had: unknown;
  beforeAll(() => {
    had = g.document;
    g.document = { createElement: () => ({ width: 0, height: 0, getContext: () => recorder() }) };
  });
  afterAll(() => {
    g.document = had;
  });

  it.skipIf(noPacks)('1.12.2', () => {
    expect(run(fakeBundle(PACK_1122), '1.12.2', 21)).toBe('153b3f3c10c04bf1b8f06dddad2179c3');
  });

  it.skipIf(noPacks)('b1.8.1', () => {
    expect(run(fakeBundle(PACK_BETA), 'b1.8.1', 22)).toBe('869503cdb842ffd17fde41dec87032d1');
  });

  it('no bundle', () => {
    expect(run(null, '1.12.2', 23)).toBe('abad87fa99eca523c4c6b8a067726def');
  });
});
