// The x-ray: the cut-away cap pass draws closed solids only (no speckles on leaves and plants), the roof cut ignores a
// canopy, and hidden bodies get a silhouette pass that can only paint what the world covers.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import * as THREE from 'three';
import { beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack1122 } from '../src/assets/bundle';
import { meshSection } from '../src/mesh/mesher';
import { occluded, viewInfo } from '../src/mesh/query';
import { VertexSink } from '../src/mesh/sink';
import { buildStateTable } from '../src/mesh/states';
import { capBackstop, createChunkMaterials, createChunkUniforms, LF_BACKSTOP } from '../src/render/chunkMaterial';
import { EntityLayer } from '../src/render/entityLayer';
import { createGhostMaterial, GHOST_FLAG, GHOST_LEVEL, GHOST_PLAYER, GHOST_SELECTED } from '../src/render/entities/material';
import { ENTITY_ORDER, GHOST_ORDER, InstancedDraw } from '../src/render/entities/meshes';
import { capGeometry } from '../src/render/sections';
import type { BotView, EntityView } from '../src/world/entities';
import type { EntityDto } from '../src/types';
import { storeWith } from './helpers';
import { table1122 } from './pack';

const flat = buildStateTable('b1.8.1', null);
const Y = 64;
const sy = Y >> 4;

/** The cells (section-local x, y, z) of the quads drawn by indices [from, to). */
function cellsOf(m: { index: ArrayLike<number>; cell: Uint8Array }, from: number, to: number): Set<string> {
  const out = new Set<string>();
  for (let i = from; i < to; i++) {
    const v = m.index[i];
    out.add(`${m.cell[v * 4]},${m.cell[v * 4 + 1]},${m.cell[v * 4 + 2]}`);
  }
  return out;
}

describe('cut-away cap: closed solids only', () => {
  it('the sink lists closed quads first; a mirrored (two-sided) quad and its twin are both open', () => {
    const s = new VertexSink();
    const quad = (open: boolean, cell: number) => {
      const v = s.begin(false, open);
      for (let k = 0; k < 4; k++) s.vertex(v + k, k, 0, 0, 0, 0);
      s.quadAttrs(v, 0, 0, 0, 0xffffff, 1, cell, 0, 0);
      return v;
    };
    quad(true, 1);
    quad(false, 2);
    const plant = quad(false, 3);
    s.mirror(plant);
    quad(false, 4);
    const m = s.finish()!;
    expect(m.quads).toBe(5);
    expect(m.capIndex).toBe(12); // cells 2 and 4
    expect([...cellsOf(m, 0, m.capIndex)].sort()).toEqual(['2,0,0', '4,0,0']);
    expect([...cellsOf(m, m.capIndex, m.index.length)].sort()).toEqual(['1,0,0', '3,0,0']);
  });

  it('b1.8.1: the cap range is the stone; leaves, tall grass, flowers and wheat lie after it', async () => {
    const store = await storeWith([[4, Y, 4, 1], [8, Y, 8, 18], [10, Y, 10, 31, 1], [12, Y, 2, 37], [2, Y, 12, 59, 7]]);
    const s = meshSection(store, 0, 0, sy, 0, flat).solid!;
    expect(s.capIndex).toBe(6 * 6);
    expect([...cellsOf(s, 0, s.capIndex)]).toEqual([`4,${Y & 15},4`]);
    const open = cellsOf(s, s.capIndex, s.index.length);
    for (const c of ['8,0,8', '10,0,10', '12,0,2', '2,0,12']) expect(open.has(c), c).toBe(true);
  });

  it('1.12.2 models: cut-out leaves (every face) and cross plants stay out of the cap; a full cube is in it', async () => {
    const store = await storeWith([[4, Y, 4, 1], [8, Y, 8, 18], [10, Y, 10, 31, 1], [2, Y, 12, 59, 7], [12, Y, 12, 2]]);
    const s = meshSection(store, 0, 0, sy, 0, table1122()).solid!;
    const closed = cellsOf(s, 0, s.capIndex);
    expect(closed).toEqual(new Set(['4,0,4', '12,0,12']));
    // The grass block's side fringe (an overlay quad) is open too: the dirt side under it closes the solid.
    expect(s.capIndex).toBe(12 * 6);
  });

  it('the cap mesh draws only that prefix of the very same buffers', () => {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(12), 3));
    g.setIndex(new THREE.BufferAttribute(new Uint16Array([0, 1, 2, 0, 2, 3, 0, 1, 2, 0, 2, 3]), 1));
    const c = capGeometry(g, 6);
    expect(c.getAttribute('position')).toBe(g.getAttribute('position'));
    expect(c.index).toBe(g.index);
    expect(c.drawRange).toMatchObject({ start: 0, count: 6 });
  });
});

describe('section materials per layer', () => {
  const m = createChunkMaterials(createChunkUniforms());
  it('solid (opaque + cut-out) front faces, depth-written, alpha-tested at 0.5', () => {
    expect(m.solid.side).toBe(THREE.FrontSide);
    expect(m.solid.transparent).toBe(false);
    expect(m.solid.depthWrite).toBe(true);
    expect(m.solid.uniforms.uAlphaTest.value).toBe(0.5);
  });
  it('translucent blends without writing depth', () => {
    expect(m.translucent.transparent).toBe(true);
    expect(m.translucent.depthWrite).toBe(false);
    expect(m.translucent.uniforms.uAlphaTest.value).toBeLessThan(0.1);
  });
  it('cap: back faces, depth LESS at their own depth (no gl_FragDepth), hidden until a cut is active', () => {
    expect(m.cap.side).toBe(THREE.BackSide);
    expect(m.cap.depthFunc).toBe(THREE.LessDepth);
    expect(m.cap.defines).toMatchObject({ CAP: 1 });
    expect(m.cap.visible).toBe(false);
    expect(m.cap.fragmentShader).not.toMatch(/gl_FragDepth\s*=/);
  });
  it('the backstop is a downward world-floor quad on the cap material, flagged so it never draws without a cut', () => {
    const b = capBackstop(m.cap, 64);
    expect(b.material).toBe(m.cap);
    expect(b.renderOrder).toBe(1);
    const light = b.geometry.getAttribute('aLight');
    for (let v = 0; v < 4; v++) expect(light.getW(v) * 255).toBeCloseTo(LF_BACKSTOP);
    // Facing down (a back face seen from above): the triangle's normal points to -y.
    const p = b.geometry.getAttribute('position');
    const a = new THREE.Vector3().fromBufferAttribute(p, 0), c1 = new THREE.Vector3().fromBufferAttribute(p, 1), c2 = new THREE.Vector3().fromBufferAttribute(p, 2);
    expect(c1.sub(a).cross(c2.sub(a)).y).toBeLessThan(0);
    expect(m.cap.fragmentShader).toContain('flag(vFlags, 16.0) && (t <= 0.0');
  });
});

describe('x-ray occlusion queries', () => {
  // A wall of the given block between the camera (z = 0) and a body standing at z = 6.
  const wall = async (id: number, meta = 0) => {
    const blocks: Array<[number, number, number, number, number]> = [];
    for (let x = 2; x <= 6; x++) for (let y = Y; y < Y + 3; y++) blocks.push([x, y, 3, id, meta]);
    for (let x = 0; x < 9; x++) for (let z = 0; z < 9; z++) blocks.push([x, Y - 1, z, 1, 0]);
    return storeWith(blocks);
  };
  const cam: [number, number, number] = [4.5, Y + 1.2, 0.5];
  const body = [4.5, Y, 6.5];
  it('stone and leaves hide a body; tall grass, wheat and glass do not', async () => {
    expect(occluded(await wall(1), flat, 0, cam, body)).toEqual([true]);
    expect(occluded(await wall(18), flat, 0, cam, body)).toEqual([true]);
    expect(occluded(await wall(31, 1), flat, 0, cam, body)).toEqual([false]);
    expect(occluded(await wall(59, 7), flat, 0, cam, body)).toEqual([false]);
    expect(occluded(await wall(20), flat, 0, cam, body)).toEqual([false]);
    // Several bodies in one call, one answer each.
    expect(occluded(await wall(1), flat, 0, cam, [...body, 4.5, Y, 1.5])).toEqual([true, false]);
  });
  it('a canopy is not a roof (no roof cut); stone is', async () => {
    const under = (id: number) => storeWith([[4, Y + 4, 4, id], [4, Y - 1, 4, 2]]);
    expect(viewInfo(await under(18), flat, 0, [4.5, Y + 9, -3], [4.5, Y, 4.5]).covered).toBe(false);
    expect(viewInfo(await under(1), flat, 0, [4.5, Y + 9, -3], [4.5, Y, 4.5]).covered).toBe(true);
  });
});

describe('occluded-body silhouettes', () => {
  beforeAll(() => {
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });

  it('the ghost pass paints only behind what is drawn, never writes depth, and runs between the world and the entities', () => {
    const g = createGhostMaterial({});
    expect(g.depthFunc).toBe(THREE.GreaterDepth);
    expect(g.depthWrite).toBe(false);
    expect(g.transparent).toBe(false); // the opaque list: before the entities, after the sections and the cap
    expect(g.blending).toBe(THREE.CustomBlending);
    expect(g.polygonOffset && g.polygonOffsetFactor < 0 && g.polygonOffsetUnits < 0).toBe(true); // touching is not hidden
    expect(GHOST_ORDER).toBeGreaterThan(1); // the cut cap
    expect(GHOST_ORDER).toBeLessThan(ENTITY_ORDER);
    expect(g.vertexShader).toContain('if (!flag(aColor.w, 256.0))');
    expect(GHOST_FLAG).toBe(256);
  });

  it('a draw shows its ghost twin only while some instance is flagged', () => {
    const geo = new THREE.BoxGeometry();
    const d = new InstancedDraw(geo, new THREE.MeshBasicMaterial(), false, 'test');
    expect(d.mesh.renderOrder).toBe(ENTITY_ORDER);
    const { mesh, created } = d.ghostMesh(createGhostMaterial({}));
    expect(created).toBe(true);
    expect(mesh.geometry).toBe(d.geometry);
    expect(mesh.renderOrder).toBe(GHOST_ORDER);
    const inst = { row: 0, slot: 0, layer: 0, tex: [0, 0, 1, 1], r: 1, g: 1, b: 1, flags: 0, hurt: 0, sky: 15, block: 0, flash: 0 };
    d.reset();
    d.push(inst);
    d.commit();
    expect(mesh.visible).toBe(false);
    d.reset();
    d.push({ ...inst, flags: GHOST_FLAG + 15 * GHOST_LEVEL });
    d.push(inst);
    d.commit();
    expect(mesh.visible).toBe(true);
    expect(d.ghostMesh(createGhostMaterial({})).created).toBe(false);
  });

  it('the entity layer flags hidden bots and players per view (tinted by role), and offers only them for the test', () => {
    const pack = JSON.parse(readFileSync(join(__dirname, '..', 'generated', '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
    const layer = new EntityLayer();
    layer.setBundle({ version: '1.12.2', pack, blocks: {}, items: {}, entity: {}, anim: null, env: {} } as unknown as AssetBundle);
    const bot = (name: string, x: number): BotView => ({ name, dim: 0, x, y: 64, z: 0, yaw: 0, dto: { name, short: name[0], main: false, dim: 0, x, y: 64, z: 0, hp: 20, food: 20 } });
    const ent = (id: number, kind: string, x: number, type = -1): EntityView => {
      const dto = { kind, dim: 0, x, y: 64, z: 0, id, type, name: kind === 'player' ? 'fox' : undefined } as EntityDto;
      return { key: `0:${id}`, id, dim: 0, kind, name: dto.name ?? null, type, item: 0, count: 1, x, y: 64, z: 0, yaw: 0, ageTicks: 0, flags: 0, dto };
    };
    const ents = [ent(1, 'player', 4), ent(2, 'mob', 6, 54)];
    const cam = new THREE.Vector3(0, 66, 10);
    const show = (ghosts: Map<string, number> | null) => {
      for (let f = 0; f < 2; f++) {
        layer.update([bot('Alice', 0), bot('Bob', 2)], ents, 1000 + f * 16, new Set());
        layer.showFor(0, cam, { bots: true, entities: true, labels: false, selected: 'Alice', ghosts });
      }
    };
    const ghostsOf = () => {
      const out: Array<{ name: string; visible: boolean }> = [];
      layer.group.traverse((o) => {
        if (o.name.endsWith('(silhouette)')) out.push({ name: o.name, visible: o.visible });
      });
      return out;
    };
    show(null);
    expect(ghostsOf().filter((g) => g.visible)).toEqual([]);
    expect(layer.ghostCandidates(0, cam, 96).map((c) => c.key).sort()).toEqual(['0:1', 'Alice', 'Bob']);
    expect(layer.ghostCandidates(0, cam, 5).map((c) => c.key)).toEqual([]);
    show(new Map([['Alice', 1], ['Bob', 0.5], ['0:1', 1], ['0:2', 1]]));
    const player = ghostsOf().find((g) => g.name === 'entity player (silhouette)');
    expect(player?.visible).toBe(true);
    // Instance flags: Alice selected, Bob a bot at half strength, the player; never the zombie.
    const flags: number[] = [];
    for (const [id, e] of (layer as unknown as { models: Map<string, { opaque: InstancedDraw }> }).models) {
      const col = e.opaque.geometry.getAttribute('aColor').array as Float32Array;
      for (let i = 0; i < e.opaque.count; i++) flags.push(col[i * 4 + 3]);
      if (id === 'zombie') expect(e.opaque.ghosts).toBe(0);
    }
    const ghostFlags = flags.filter((f) => f & GHOST_FLAG);
    expect(ghostFlags.length).toBe(3);
    expect(ghostFlags.some((f) => f & GHOST_SELECTED && Math.floor(f / GHOST_LEVEL) === 15)).toBe(true);
    expect(ghostFlags.some((f) => !(f & (GHOST_SELECTED | GHOST_PLAYER)) && Math.floor(f / GHOST_LEVEL) === 8)).toBe(true);
    expect(ghostFlags.some((f) => f & GHOST_PLAYER)).toBe(true);
    expect(ghostsOf().find((g) => g.name === 'entity zombie (silhouette)')?.visible ?? false).toBe(false);
    show(null);
    expect(ghostsOf().filter((g) => g.visible)).toEqual([]);
    layer.dispose();
  });
});
