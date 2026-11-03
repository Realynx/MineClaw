// The last wire looks: the enchantment glint, an enderman's carried block, an iron golem's flower, shoulder parrots, arrows
// stuck in players, and styled 1.12.2 sign text.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import * as THREE from 'three';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack, Pack1122, PackBeta } from '../src/assets/bundle';
import { EntityLayer, glintsById } from '../src/render/entityLayer';
import { JavaRandom, MAX_STUCK_ARROWS, stuckArrows } from '../src/render/entities/arrows';
import { buildModel } from '../src/render/entities/builder';
import { createEntityMaterials, defaultWorldUniforms, GHOST_FLAG, GHOST_LEVEL, GLINT_FLAG } from '../src/render/entities/material';
import { apply, rootMatrix, Skeleton } from '../src/render/entities/pose';
import { MODEL_BY_ID } from '../src/render/entities/registry';
import type { PoseCtx } from '../src/render/entities/types';
import { Variant } from '../src/render/entities/variant';
import { fontTable, FORMAT_RGB, layoutLine, richToLegacy } from '../src/render/font';
import type { EntityDto } from '../src/types';
import { equipGlint, normalizeBlockEntities, normalizeEntities } from '../src/wire/normalize';
import type { BotView, EntityView } from '../src/world/entities';

const gen = join(__dirname, '..', 'generated');
const PACK_1122 = JSON.parse(readFileSync(join(gen, '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
const PACK_BETA = JSON.parse(readFileSync(join(gen, 'b1.8.1', 'pack.json'), 'utf8')) as PackBeta;
const FONT = fontTable(PACK_1122.font, false)!;
const CTX: PoseCtx = { swing: 0, amount: 0, age: 0, headYaw: 0, pitch: 0, attack: 0, attackOff: 0, eat: 0, baby: false, v: Variant.NONE, variant: 0, flags: 0, sneaking: false, riding: false, holding: false, aggressive: false, using: false, bow: false, standPose: null, beta: false, seed: 0, speed: 0 };

describe('wire: the last looks', () => {
  it('equipment slots keep [id, meta(, rgb)] and read the trailing {glint} into Equipment.glint', () => {
    const d = normalizeEntities({
      entities: [{ kind: 'mob', dim: 0, x: 0, y: 64, z: 0, id: 1, equipment: { main: [276, 0, { glint: true }], feet: [301, 0, 0x3366cc, { glint: true }], head: [298, 0, 0x112233] } }],
      bots: [{ name: 'Alice', short: 'main', main: true, dim: 0, x: 0, y: 64, z: 0, hp: 20, food: 20, equipment: { main: [278, 0, { glint: true }] } }],
    });
    const eq = d.entities[0].equipment!;
    expect(eq.main).toEqual([276, 0]);
    expect(eq.feet).toEqual([301, 0, 0x3366cc]);
    expect(eq.head).toEqual([298, 0, 0x112233]);
    expect(eq.glint).toEqual({ main: true, feet: true });
    expect(d.bots[0].equipment?.main).toEqual([278, 0]);
    expect(d.bots[0].equipment?.glint).toEqual({ main: true });
    expect(equipGlint([1, 0])).toBe(false);
    expect(equipGlint([1, 0, 5])).toBe(false);
  });

  it('carried blocks, glint, shoulder parrots and stuck arrows pass through (snake or camel case)', () => {
    const d = normalizeEntities({
      entities: [
        { kind: 'mob', dim: 0, x: 0, y: 64, z: 0, id: 1, type: 58, carried: [12, 1] },
        { kind: 'item', dim: 0, x: 0, y: 64, z: 0, id: 2, item: 276, glint: true },
        { kind: 'player', dim: 0, x: 0, y: 64, z: 0, id: 3, name: 'fox', shoulders: { left: 'green' }, arrows: 4 },
      ],
      bots: [],
    });
    expect(d.entities[0].carried).toEqual([12, 1]);
    expect(d.entities[1].glint).toBe(true);
    expect(d.entities[2].shoulders).toEqual({ left: 'green', right: null });
    expect(d.entities[2].arrows).toBe(4);
  });

  it('sign rich runs survive normalisation next to the plain lines', () => {
    const b = normalizeBlockEntities({
      columns: [{ cx: 0, cz: 0, hash: 1, entities: [{ x: 1, y: 2, z: 3, kind: 'sign', lines: ['Foxie', '', '', ''], rich: [[{ text: 'Fox', color: 'red', bold: true }, { text: 'ie' }], [], [], []] }] }],
    });
    const s = b.columns[0].entities[0];
    expect(s.lines).toEqual(['Foxie', '', '', '']);
    expect(s.rich?.[0]).toEqual([{ text: 'Fox', color: 'red', bold: true }, { text: 'ie' }]);
  });
});

describe('sign rich text', () => {
  it('runs become the § text the font renderer reads: colours, styles, the default (§r) between', () => {
    const s = richToLegacy([{ text: 'Fox', color: 'red', bold: true }, { text: 'ie' }, { text: '!', color: 'dark_blue', italic: true, underlined: true, strikethrough: true, obfuscated: true }]);
    expect(s).toBe('§c§lFox§rie§1§o§n§m§k!');
    const l = layoutLine(FONT, s, 0);
    expect(l.glyphs.map((g) => [g.rgb, g.bold, g.italic])).toEqual([
      [FORMAT_RGB[12], true, false],
      [FORMAT_RGB[12], true, false],
      [FORMAT_RGB[12], true, false],
      [0, false, false],
      [0, false, false],
      [FORMAT_RGB[1], false, true],
    ]);
    expect(l.rules.length).toBe(2); // the "!": underline and strikethrough
    expect(richToLegacy(null)).toBe('');
    expect(richToLegacy([{ text: '' }])).toBe('');
  });

  it('obfuscated glyphs change with the seed (the sign redraws them) and keep their width', () => {
    const s = richToLegacy([{ text: 'secret', obfuscated: true }]);
    const a = layoutLine(FONT, s, 0, 0, 1), b = layoutLine(FONT, s, 0, 0, 7919 * 3 + 1);
    expect(a.width).toBe(b.width);
    expect(a.glyphs.map((g) => g.cell)).not.toEqual(b.glyphs.map((g) => g.cell));
  });
});

describe('stuck arrows (LayerArrow)', () => {
  it('java.util.Random matches the JDK', () => {
    expect(new JavaRandom(42).nextIntRaw()).toBe(-1170105035);
    expect(new JavaRandom(0).nextFloat()).toBeCloseTo(0.7309677, 6);
    const r = new JavaRandom(7);
    for (let i = 0; i < 100; i++) {
      const v = r.nextInt(17);
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(17);
    }
  });

  it('spots are deterministic per entity, inside their box, aimed back through it; replaced boxes stay frozen', () => {
    const a = stuckArrows(77, 6, false), b = stuckArrows(77, 6, false);
    expect(a).toEqual(b);
    expect(a.length).toBe(6);
    expect(stuckArrows(78, 6, false)).not.toEqual(a);
    expect(stuckArrows(1, 999, false).length).toBe(MAX_STUCK_ARROWS);
    for (const s of stuckArrows(5, 32, true)) {
      expect(Math.abs(s.yaw)).toBeLessThanOrEqual(180);
      expect(Math.abs(s.pitch)).toBeLessThanOrEqual(90);
      for (const v of s.at) expect(Math.abs(v)).toBeLessThan(1.5);
      if (s.part == null) expect(s.pivot.some((v) => v !== 0) || s.at[1] <= 1).toBe(true);
    }
  });
});

describe('EntityLayer: glint, carried blocks, the golem flower, shoulder parrots, stuck arrows', () => {
  beforeAll(() => {
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });
  const bundle = (pack: Pack, glint = true): AssetBundle =>
    ({ version: pack.version, pack, blocks: {}, items: {}, entity: {}, anim: null, env: glint && pack.version === '1.12.2' ? { glint: {} } : {} }) as unknown as AssetBundle;
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
  type DrawnPeek = { row: number; riders: Array<{ row: number; key: string }> | null; carried: unknown; roseAt: number };
  const drawn = (layer: EntityLayer) => (layer as unknown as { drawn: Map<string, DrawnPeek> }).drawn;
  const bones = (layer: EntityLayer) => (layer as unknown as { bones: Float32Array }).bones;

  it('glinting items and armour get their draw’s glint twin; plain ones and b1.8.1 none', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [ent(1, 0, { type: 54, equipment: { main: [276, 0], chest: [311, 0], glint: { main: true, chest: true } } })]);
    let m = meshes(layer);
    expect(m['item sprites (glint)']).toBeGreaterThan(0);
    expect(m['armour zombie (glint)']).toBeGreaterThan(0);
    run(layer, [], [ent(1, 0, { type: 54, equipment: { main: [276, 0], chest: [311, 0] } })]);
    m = meshes(layer);
    expect(m['item sprites (glint)']).toBeUndefined();
    expect(m['armour zombie (glint)']).toBeUndefined();
    // Items that always glint (Item.hasEffect): a dropped nether star, an enchanted book in a frame.
    run(layer, [], [ent(2, 0, { kind: 'item', item: 399 }), ent(3, 2, { kind: 'item_frame', frameItem: [403, 0], facing: 1 })]);
    expect(meshes(layer)['item sprites (glint)']).toBeGreaterThan(0);
    layer.dispose();
    const beta = new EntityLayer();
    beta.setBundle(bundle(PACK_BETA));
    run(beta, [], [ent(1, 0, { type: 54, equipment: { main: [276, 0], glint: { main: true } } })]);
    expect(meshes(beta)['item sprites (glint)']).toBeUndefined();
    beta.dispose();
    expect(glintsById('1.12.2', 322, 1)).toBe(true);
    expect(glintsById('1.12.2', 322, 0)).toBe(false);
    expect(glintsById('b1.8.1', 399, 0)).toBe(false);
  });

  it('bots wear their own equipment (and glint) when the wire sends it', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const bot: BotView = {
      name: 'Alice', dim: 0, x: 0, y: 64, z: 2, yaw: 0,
      dto: { name: 'Alice', short: 'main', main: true, dim: 0, x: 0, y: 64, z: 2, hp: 20, food: 20, held: 'diamond pickaxe', equipment: { main: [278, 0], head: [310, 0], glint: { head: true } } },
    };
    run(layer, [bot], []);
    const m = meshes(layer);
    expect(m['armour player']).toBe(1);
    expect(m['armour player (glint)']).toBeGreaterThan(0);
    layer.dispose();
  });

  it('the glint pass: its own flag bit above the ghost strength, two materials, additive', () => {
    expect(GLINT_FLAG & (GHOST_FLAG | (15 * GHOST_LEVEL))).toBe(0);
    const mats = createEntityMaterials(defaultWorldUniforms());
    for (const g of [mats.glintModel, mats.glintTile]) {
      expect(g.defines.GLINT).toBe(1);
      expect(g.depthWrite).toBe(false);
      expect(g.blending).toBe(THREE.CustomBlending);
      expect([g.blendSrc, g.blendDst]).toEqual([THREE.OneFactor, THREE.OneFactor]);
      expect(g.fragmentShader).toContain('uGlint');
    }
    expect(mats.glintTile.defines.TILE).toBe(1);
    expect(mats.glintModel.defines.MODEL).toBe(1);
    // The ghost pass reads its strength from four bits only, so a glinting body's silhouette keeps its strength.
    expect(mats.ghost.vertexShader).toContain('mod(floor(aColor.w / 2048.0), 16.0)');
    mats.dispose();
  });

  it('an enderman carries its block (a cube, or crossed planes for a plant) with its arms forward', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    run(layer, [], [ent(1, 0, { type: 58, carried: [12, 0] }), ent(2, 3, { type: 58, carried: [38, 0] }), ent(3, 6, { type: 58 })]);
    const m = meshes(layer);
    expect(m['item cubes']).toBe(1); // the sand
    expect(m['item sprites']).toBe(2); // the poppy's two planes
    // The carried cube sits in front of it at its hands (the long arms held out reach down to ~0.6 blocks up).
    const d = drawn(layer).get('0:1')!;
    const o = d.row * 64 * 12 + 58 * 12;
    const b = bones(layer);
    const p = apply(b.subarray(o, o + 12), 0, 0.5, 0);
    expect(p[1] - 64).toBeGreaterThan(0.5);
    expect(p[1] - 64).toBeLessThan(1.4);
    expect(Math.hypot(p[0] - 0, p[2] - 0)).toBeGreaterThan(0.2);
    layer.dispose();
    // ModelEnderman.isCarrying: both arms −0.5 about x.
    const sk = new Skeleton(buildModel(MODEL_BY_ID.enderman));
    sk.pose({ ...CTX, carrying: true }, rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] }), new Float32Array(64 * 12), 0);
    expect(sk.byName.rightArm.rx).toBeCloseTo(-0.5);
    expect(sk.byName.leftArm.rz).toBeCloseTo(-0.05);
  });

  it('an iron golem holding out its flower raises its right arm and draws the poppy until the 400 ticks run out', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const g = ent(1, 0, { type: 99, look: 'holding_rose' });
    run(layer, [], [g], 1000);
    expect(meshes(layer)['item sprites']).toBe(2);
    expect(drawn(layer).get('0:1')!.roseAt).toBe(1000);
    run(layer, [], [g], 1000 + 20_100);
    expect(meshes(layer)['item sprites']).toBeUndefined();
    run(layer, [], [{ ...g, dto: { ...g.dto, look: null } }], 30_000);
    expect(drawn(layer).get('0:1')!.roseAt).toBe(0);
    layer.dispose();
    const sk = new Skeleton(buildModel(MODEL_BY_ID.iron_golem));
    sk.pose({ ...CTX, rose: 400 }, rootMatrix(new Float32Array(12), { x: 0, y: 0, z: 0, yaw: 0, death: 0, scale: [1, 1, 1], offset: [0, 0, 0] }), new Float32Array(64 * 12), 0);
    expect(sk.byName.rightArm.rx).toBeCloseTo(-0.8 + 0.025 * ((Math.abs((400 % 70) - 35) - 17.5) / 17.5));
    expect(sk.byName.leftArm.rx).toBe(0);
  });

  it('shoulder parrots ride a player as their own rows, at the shoulders; stuck arrows likewise (players only)', () => {
    const layer = new EntityLayer();
    layer.setBundle(bundle(PACK_1122));
    const fox = ent(77, 0, { kind: 'player', name: 'fox', shoulders: { left: 'green', right: 'grey' }, arrows: 3 });
    run(layer, [], [fox, ent(78, 4, { type: 54, arrows: 2 })]);
    const m = meshes(layer);
    expect(m['entity parrot']).toBe(2);
    expect(m['entity arrow']).toBe(3); // the zombie's two are not drawn (LayerArrow is RenderPlayer's)
    const d = drawn(layer).get('0:77')!;
    expect(d.riders?.map((r) => r.key)).toEqual(['0:77#shoulder_left', '0:77#shoulder_right', '0:77#arrow0', '0:77#arrow1', '0:77#arrow2']);
    // The parrot's body sits on the shoulder: ~0.4 to the side, ~1.3–1.7 blocks up.
    const parrot = drawn(layer).get('0:77#shoulder_left')!;
    const body = buildModel(MODEL_BY_ID.parrot).index.body;
    const o = parrot.row * 64 * 12 + body * 12;
    const p = apply(bones(layer).subarray(o, o + 12), 0, 3, 0);
    expect(Math.abs(p[0]) + Math.abs(p[2])).toBeGreaterThan(0.25);
    expect(p[1] - 64).toBeGreaterThan(1.2);
    expect(p[1] - 64).toBeLessThan(1.9);
    // The parrots leave: their rows go.
    run(layer, [], [{ ...fox, dto: { ...fox.dto, shoulders: null, arrows: 0 } }]);
    expect(meshes(layer)['entity parrot']).toBeUndefined();
    expect(drawn(layer).get('0:77')!.riders).toBeNull();
    expect(drawn(layer).has('0:77#shoulder_left')).toBe(false);
    layer.dispose();
  });
});
