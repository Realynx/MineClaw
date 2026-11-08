import { afterEach, expect, it, vi } from 'vitest';
import { configureHumanSkins, humanSkins, type HumanSkinFeed } from '../src/assets/humanSkins';
import * as THREE from 'three';
import { PNG } from 'pngjs';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { EntityLayer } from '../src/render/entityLayer';
import type { AssetBundle, Pack1122 } from '../src/assets/bundle';
import type { BotView } from '../src/world/entities';

let stop: (() => void) | undefined;
afterEach(() => { stop?.(); stop = undefined; configureHumanSkins(null); vi.unstubAllGlobals(); vi.useRealTimers(); });

it('shows only configured human skins; resets across disable/re-enable and service initialization epochs', async () => {
  vi.useFakeTimers();
  const drawImage = vi.fn(), clearRect = vi.fn();
  vi.stubGlobal('document', { hidden: false, createElement: () => ({ width: 0, height: 0, getContext: () => ({ drawImage, clearRect }) }) });
  vi.stubGlobal('createImageBitmap', vi.fn(() => Promise.resolve({ width: 64, height: 64, close: vi.fn() })));
  expect(humanSkins.get('player')).toBeUndefined();
  let feed: HumanSkinFeed = { enabled: true, epoch: 'first-start', revision: 1, skins: [{ name: 'player', slim: true, height: 64, png: btoa('fixture') }] };
  const source = vi.fn(() => Promise.resolve(feed));
  configureHumanSkins(source);
  stop = humanSkins.subscribe(() => {});
  await vi.advanceTimersByTimeAsync(0);
  expect(humanSkins.get('PLAYER')?.slim).toBe(true);
  expect(humanSkins.rect('player', false)).toEqual([0, 0, 64 / 1024, 64 / 2048]);
  expect(humanSkins.rect('player', true)).toEqual([0, 0, 64 / 1024, 32 / 2048]);
  feed = { ...feed, enabled: false, skins: [] };
  await vi.advanceTimersByTimeAsync(5000);
  expect(humanSkins.get('player')).toBeUndefined();
  feed = { ...feed, enabled: true, skins: [{ name: 'player', slim: true, height: 64, png: btoa('fixture') }] };
  await vi.advanceTimersByTimeAsync(5000);
  expect(source).toHaveBeenLastCalledWith({ epoch: 'first-start', since: 0 });
  expect(humanSkins.get('player')).toBeDefined();
  feed = { enabled: true, epoch: 'restarted', revision: 0, skins: [] };
  await vi.advanceTimersByTimeAsync(5000);
  expect(humanSkins.get('player')).toBeUndefined();
  configureHumanSkins(null);
  await vi.advanceTimersByTimeAsync(5000);
  expect(humanSkins.get('player')).toBeUndefined();
});

it('draws slim and legacy skins using distinct atlas UVs and correct instanced player models', async () => {
  vi.useFakeTimers();
  vi.stubGlobal('document', { hidden: false, createElement: () => {
    const canvas = { width: 0, height: 0, getContext: () => canvas.width === 1024 ? { drawImage: vi.fn(), clearRect: vi.fn() } : null };
    return canvas;
  } });
  vi.stubGlobal('createImageBitmap', vi.fn(async (blob: Blob) => {
    const decoded = PNG.sync.read(Buffer.from(await blob.arrayBuffer()));
    return { width: decoded.width, height: decoded.height, close: vi.fn() };
  }));
  const skin = (name: string, height: 32 | 64, slim: boolean) => {
    const p = new PNG({ width: 64, height }); p.data.fill(255);
    return { name, height, slim, png: PNG.sync.write(p).toString('base64') };
  };
  configureHumanSkins(() => Promise.resolve({ enabled: true, epoch: 'render', revision: 2, skins: [skin('slim', 64, true), skin('legacy', 32, false)] }));
  const layer = new EntityLayer();
  stop = () => layer.dispose();
  const pack = JSON.parse(readFileSync(join(__dirname, '..', 'generated', '1.12.2', 'pack.json'), 'utf8')) as Pack1122;
  layer.setBundle({ version: '1.12.2', pack, blocks: {}, items: {}, entity: {}, anim: null, env: {} } as unknown as AssetBundle);
  await vi.advanceTimersByTimeAsync(0);
  const bot = (name: string, x: number): BotView => ({ name, dim: 0, x, y: 64, z: 0, yaw: 0, dto: { name, short: name, main: false, dim: 0, x, y: 64, z: 0, hp: 20, food: 20 } });
  const show = () => {
    for (let frame = 0; frame < 2; frame++) {
      layer.update([bot('slim', 0), bot('legacy', 2)], [], 1000 + frame * 16, new Set());
      layer.showFor(0, new THREE.Vector3(0, 65, 5), { bots: true, entities: true, labels: false });
    }
  };
  show();
  const models = layer.group.children.filter((o) => o.name === 'entity player_slim' || o.name === 'entity player_beta') as THREE.Mesh<THREE.InstancedBufferGeometry, THREE.ShaderMaterial>[];
  expect(models.map((m) => m.name).sort()).toEqual(['entity player_beta', 'entity player_slim']);
  for (const mesh of models) {
    const legacy = mesh.name === 'entity player_beta';
    expect(mesh.geometry.instanceCount).toBe(1);
    const uv = mesh.geometry.getAttribute('aTex');
    expect([uv.getX(0), uv.getY(0), uv.getZ(0), uv.getW(0)]).toEqual([legacy ? 64 / 1024 : 0, 0, 64 / 1024, (legacy ? 32 : 64) / 2048]);
    expect(mesh.geometry.getAttribute('aColor').getW(0) & 65536).toBe(65536);
    expect(mesh.material.uniforms.uHumanSkins.value).toBe(humanSkins.texture);
  }
  configureHumanSkins(null); show();
  const visible = layer.group.children.filter((o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh<THREE.InstancedBufferGeometry>).geometry.instanceCount > 0) as THREE.Mesh<THREE.InstancedBufferGeometry>[];
  for (const mesh of visible) {
    const flags = mesh.geometry.getAttribute('aColor');
    if (flags) expect(flags.getW(0) & 65536).toBe(0);
  }
});
