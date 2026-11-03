// Entity model gallery (`npm run dev`, then /models.html): every model the entity layer has, on a flat floor, walking back
// and forth, for checking geometry, textures, layers and animation without a world. Drag = orbit, right-drag = pan,
// wheel = zoom.
//   ?version=1.12.2|b1.8.1  ?walk=0 (stand still)  ?spin=1 (turn around)  ?cols=10  ?detail=48  ?labels=0
//   ?n=20 (perf: n walking zombies and nothing else)  ?set=looks (only the equipment / baby showcase)

import * as THREE from 'three';
import { loadBundle } from '../src/assets/bundle';
import { EntityLayer } from '../src/render/entityLayer';
import { MOB_NAMES } from '../src/render/entities/registry';
import type { EntityDto, EntityEvent } from '../src/types';
import type { BotView, EntityView } from '../src/world/entities';

const q = new URLSearchParams(location.search);
const version = q.get('version') === 'b1.8.1' ? 'b1.8.1' : '1.12.2';
const walk = q.get('walk') !== '0';
const cols = Number(q.get('cols') ?? 10);
const perf = Number(q.get('n') ?? 0);

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(Math.min(2, devicePixelRatio));
renderer.setSize(innerWidth, innerHeight);
document.body.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x78a7ff);
scene.add(new THREE.Mesh(new THREE.PlaneGeometry(400, 400).rotateX(-Math.PI / 2), new THREE.MeshBasicMaterial({ color: 0x6a8a4a })));
const grid = new THREE.GridHelper(400, 400, 0x557040, 0x557040);
grid.position.y = 0.001;
scene.add(grid);
const camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.1, 500);
const layer = new EntityLayer();
if (q.get('detail')) layer.detailDistance = Number(q.get('detail'));
scene.add(layer.group);

const BETA_TYPES = [50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 90, 91, 92, 93, 94, 95];
const types = perf ? Array<number>(perf).fill(54) : version === 'b1.8.1' ? BETA_TYPES : Object.keys(MOB_NAMES).map(Number);
const variants: Record<number, number[]> = { 55: [2], 62: [2], 90: [1], 91: [14, 16], 95: [4, 2], 98: [4] };
const entries: Array<Partial<EntityDto>> = [];
const looksOnly = q.get('set') === 'looks';
if (!looksOnly) for (const t of types) for (const v of perf ? [0] : (variants[t] ?? [0])) entries.push({ kind: 'mob', type: t, variant: v });
// Dyed leather, worn skulls and head items, elytra, foals and baby llamas next to their adults.
const LOOKS: Array<Partial<EntityDto>> = [
  { kind: 'mob', type: 54, equipment: { head: [298, 0, 0x3366cc], chest: [299, 0, 0xb02e26], legs: [300, 0], feet: [301, 0, 0x80c71f] } },
  { kind: 'player', name: 'Steve', equipment: { head: [298, 0], chest: [299, 0], legs: [300, 0, 0xfed83d], feet: [301, 0] } },
  { kind: 'mob', type: 51, equipment: { head: [397, 4] } },
  { kind: 'mob', type: 54, equipment: { head: [397, 5] } },
  { kind: 'mob', type: 27, variant: 2, equipment: { head: [397, 3], chest: [303, 0] } },
  { kind: 'mob', type: 120, variant: 1, equipment: { head: [397, 2] } },
  { kind: 'mob', type: 54, baby: true, equipment: { head: [397, 1] } },
  { kind: 'armor_stand', look: 'arms', equipment: { head: [397, 0], chest: [443, 0] } },
  { kind: 'player', name: 'Glider', equipment: { chest: [443, 0], head: [264, 0] } },
  { kind: 'player', name: 'Sneak', flags: 0x02, equipment: { chest: [443, 0], head: [86, 0] } },
  { kind: 'mob', type: 100, look: 'chestnut', baby: true },
  { kind: 'mob', type: 100, look: 'chestnut' },
  { kind: 'mob', type: 103, look: 'brown', baby: true },
  { kind: 'mob', type: 103, look: 'brown' },
  { kind: 'mob', type: 31, baby: true },
  { kind: 'mob', type: 120, equipment: { head: [86, 0] } },
  // Layers the wire implies: the snow golem's pumpkin, the mooshroom's mushrooms, a fusing creeper, a screaming enderman.
  { kind: 'mob', type: 97 },
  { kind: 'mob', type: 96 },
  { kind: 'mob', type: 50, look: 'fusing' },
  { kind: 'mob', type: 58, look: 'screaming' },
];
if (looksOnly) entries.push(...LOOKS);
if (!perf && !looksOnly) {
  for (const [item, count] of [[1, 64], [278, 1], [264, 5], [38, 1], [50, 3], [17, 2], [2, 1], [35, 14]]) entries.push({ kind: 'item', item, count });
  entries.push(
    { kind: 'object', type: 1 },
    { kind: 'object', type: 10 },
    { kind: 'object', type: 60 },
    { kind: 'object', type: 70, item: 12 },
    { kind: 'object', type: 50 },
    { kind: 'xp_orb', count: 20 },
    { kind: 'player', name: 'Steve' },
    { kind: 'player', name: 'alexa', item: 276 },
    { kind: 'mob', type: 54, flags: 1 },
    // The newer wire: equipment, variants, babies, hanging things, loaded carts.
    { kind: 'player', name: 'Knight', equipment: { main: [276, 0], off: [50, 0], head: [306, 0], chest: [311, 0], legs: [316, 0], feet: [301, 0] } },
    { kind: 'mob', type: 51, equipment: { main: [261, 0], head: [302, 0], feet: [309, 0] } },
    { kind: 'armor_stand', look: 'arms', equipment: { main: [267, 0], head: [314, 0], chest: [315, 0] } },
    { kind: 'mob', type: 54, baby: true, equipment: { head: [86, 0] } },
    { kind: 'mob', type: 92, baby: true },
    { kind: 'mob', type: 91, look: 'lime', baby: true },
    { kind: 'mob', type: 100, look: 'creamy,white_dots,saddle,iron' },
    { kind: 'mob', type: 31, look: 'saddle,chest' },
    { kind: 'mob', type: 103, look: 'gray,carpet:blue,chest' },
    { kind: 'mob', type: 50, look: 'powered' },
    { kind: 'mob', type: 95, look: 'collar:lime,tame' },
    { kind: 'mob', type: 69, look: 'cyan,open' },
    { kind: 'painting', motive: 'BurningSkull', facing: 0 },
    { kind: 'item_frame', frameItem: [264, 0], rotation: 1, facing: 0 },
    { kind: 'minecart', minecartType: 'tnt' },
    { kind: 'mob', type: 54, flags: 0x40 },
    { kind: 'mob', type: 54, flags: 0x20, equipment: { head: [310, 0], chest: [311, 0] } },
  );
}
const spacing = 4;
const hud = document.getElementById('hud')!;
const t0 = performance.now();
let frames = 0, fpsAt = t0, lastSec = -1;

function views(now: number): { bots: BotView[]; ents: EntityView[] } {
  const s = (now - t0) / 1000;
  const ents: EntityView[] = entries.map((e, i) => {
    const gx = (i % cols) * spacing, gz = Math.floor(i / cols) * spacing;
    const phase = walk ? Math.sin(s * 1.2 + i) * 0.6 : 0;
    const dto = { dim: 0, x: gx, y: 0, z: gz, id: i, ...e } as EntityDto;
    const flying = dto.kind === 'mob' && [56, 63, 64, 65].includes(dto.type ?? 0) ? 2 : 0;
    const yaw = q.get('spin') === '1' ? (s * 40) % 360 : walk ? (Math.cos(s * 1.2 + i) >= 0 ? 0 : 180) : 0;
    return { key: `0:${i}`, id: i, dim: 0, kind: dto.kind, name: dto.name ?? null, type: dto.type ?? -1, item: dto.item ?? 0, count: dto.count ?? 1, x: gx, y: flying, z: gz + phase, yaw, ageTicks: s * 20, flags: dto.flags ?? 0, dto };
  });
  const bot = (name: string, z: number, yaw: number, held: string, hp: number, pitch = 0): BotView => ({
    name, dim: 0, x: -spacing, y: 0, z, yaw, dto: { name, short: name, main: name === 'Alice', dim: 0, x: 0, y: 0, z, hp, food: 20, held, pitch },
  });
  const bots = perf ? [] : [bot('Alice', 0, (s * 30) % 360, 'stone pickaxe', 20, 20 * Math.sin(s)), bot('Bob', spacing, 0, 'cobblestone x12', Math.floor(s) % 6 === 0 ? 4 : 10), bot('Cleo', spacing * 2, 90, 'torch', 20)];
  return { bots, ents };
}

const rows = Math.ceil(entries.length / cols);
const centre = new THREE.Vector3(((cols - 1) * spacing) / 2, 0, ((rows - 1) * spacing) / 2);
let camYaw = 0.6, camDist = Math.max(20, rows * spacing * 1.1), camPitch = 0.6;
addEventListener('mousemove', (e) => {
  if (e.buttons & 1) {
    camYaw -= e.movementX * 0.005;
    camPitch = Math.max(0.05, Math.min(1.5, camPitch + e.movementY * 0.005));
  }
  if (e.buttons & 2) centre.x -= e.movementX * 0.05;
  if (e.buttons & 2) centre.z -= e.movementY * 0.05;
});
addEventListener('contextmenu', (e) => e.preventDefault());
addEventListener('wheel', (e) => (camDist = Math.max(2, camDist * (e.deltaY > 0 ? 1.1 : 0.9))));
addEventListener('resize', () => {
  renderer.setSize(innerWidth, innerHeight);
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
});
(window as unknown as Record<string, unknown>).__gallery = {
  layer,
  camera,
  /** Times `n` full frames (update + showFor + render + gl.finish), ms per frame. */
  bench(n = 60) {
    const gl = renderer.getContext();
    const px = new Uint8Array(4);
    const t = performance.now();
    for (let k = 0; k < n; k++) {
      const now = t + k * 16;
      const { bots, ents } = views(now);
      layer.update(bots, ents, now, new Set());
      layer.showFor(0, camera.position, { bots: true, entities: true, labels: false, fov: camera.fov });
      renderer.render(scene, camera);
      gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px); // waits for the frame (finish alone may not)
    }
    return (performance.now() - t) / n;
  },
  /** Look at (x, z) from yaw/pitch (radians) and distance. */
  set(yaw: number, pitch: number, dist: number, x?: number, z?: number, y = 0) {
    camYaw = yaw;
    camPitch = pitch;
    camDist = dist;
    if (x != null) centre.set(x, y, z ?? 0);
  },
  cell: (i: number) => [(i % cols) * spacing, Math.floor(i / cols) * spacing],
  entries,
};

void loadBundle(version).then((b) => layer.setBundle(b));
function frame(now: number) {
  requestAnimationFrame(frame);
  const { bots, ents } = views(now);
  layer.update(bots, ents, now, new Set(['Alice']));
  // Events: Alice swings every second; the first zombie is hurt, sparks and dies every 6 s; lightning every 5 s.
  const sec = Math.floor((now - t0) / 1000);
  if (sec !== lastSec) {
    lastSec = sec;
    const zi = entries.findIndex((e) => e.type === 54);
    const ev: EntityEvent[] = [{ kind: 'swing', name: 'Alice', at: now }];
    if (sec % 6 === 1) ev.push({ kind: 'hurt', id: zi, dim: 0, at: now }, { kind: 'crit', id: zi, dim: 0, at: now });
    if (sec % 6 === 2) ev.push({ kind: 'magic_crit', id: zi, dim: 0, at: now }, { kind: 'eat', name: 'Cleo', at: now });
    if (sec % 6 === 3) ev.push({ kind: 'death', id: zi, dim: 0, at: now });
    if (sec % 5 === 0) ev.push({ kind: 'lightning', dim: 0, at: now, x: centre.x + 6, y: 0, z: centre.z - 6 });
    if (q.get('events') !== '0') layer.onEvents?.(ev, now);
  }
  camera.position.set(centre.x + Math.sin(camYaw) * Math.cos(camPitch) * camDist, centre.y + Math.sin(camPitch) * camDist, centre.z + Math.cos(camYaw) * Math.cos(camPitch) * camDist);
  camera.lookAt(centre);
  layer.showFor(0, camera.position, { bots: true, entities: true, labels: q.get('labels') !== '0', selected: 'Alice', fov: camera.fov });
  renderer.render(scene, camera);
  frames++;
  if (now - fpsAt > 1000) {
    const fps = (frames * 1000) / (now - fpsAt);
    frames = 0;
    fpsAt = now;
    hud.textContent = `${version} · ${entries.length} entities · ${fps.toFixed(0)} fps · ${renderer.info.render.calls} calls · ${renderer.info.render.triangles} tris`;
  }
}
requestAnimationFrame(frame);
