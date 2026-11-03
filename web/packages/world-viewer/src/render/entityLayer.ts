// Bots, players, mobs and drops at the EntityStore's interpolated positions, shared by every view: bots are small posed models
// with labels; every other entity is one instance of a single InstancedMesh (unit box scaled per kind). Positions update once
// per frame; each view then picks its dimension and whether labels show (`showFor`), so nothing is rebuilt per view.

import * as THREE from 'three';
import type { BotView, EntityView } from '../world/entities';
import { LabelFactory } from './labels';

const MOBS: Record<number, [string, number, number, number]> = {
  // type: [name, colour, width, height]
  4: ['elder guardian', 0xc8c0a8, 2, 2],
  5: ['wither skeleton', 0x2a2a2a, 0.7, 2.4],
  6: ['stray', 0x8aa0a0, 0.6, 1.99],
  23: ['husk', 0x8a7a50, 0.6, 1.95],
  27: ['zombie villager', 0x4a7a4a, 0.6, 1.95],
  50: ['creeper', 0x4caf50, 0.6, 1.7],
  51: ['skeleton', 0xd0d0d0, 0.6, 1.99],
  52: ['spider', 0x3a2e28, 1.4, 0.9],
  53: ['giant', 0x3a7a5a, 3.6, 10.8],
  54: ['zombie', 0x3a7a5a, 0.6, 1.95],
  55: ['slime', 0x78c868, 0.51, 0.51],
  56: ['ghast', 0xf0f0f0, 4, 4],
  57: ['zombie pigman', 0xe0a0a0, 0.6, 1.95],
  58: ['enderman', 0x1a1020, 0.6, 2.9],
  59: ['cave spider', 0x1c3a40, 0.7, 0.5],
  60: ['silverfish', 0x9a9a9a, 0.4, 0.3],
  61: ['blaze', 0xf0b020, 0.6, 1.8],
  62: ['magma cube', 0x8a2a10, 0.51, 0.51],
  63: ['ender dragon', 0x200a30, 16, 8],
  64: ['wither', 0x1a1a1a, 0.9, 3.5],
  65: ['bat', 0x4a3a2a, 0.5, 0.9],
  66: ['witch', 0x4a2a5a, 0.6, 1.95],
  67: ['endermite', 0x2a1a3a, 0.4, 0.3],
  68: ['guardian', 0x70a090, 0.85, 0.85],
  69: ['shulker', 0x9a6a9a, 1, 1],
  90: ['pig', 0xf0a0a0, 0.9, 0.9],
  91: ['sheep', 0xe8e8e8, 0.9, 1.3],
  92: ['cow', 0x5a3a24, 0.9, 1.4],
  93: ['chicken', 0xf8f8f8, 0.4, 0.7],
  94: ['squid', 0x2a3a5a, 0.8, 0.8],
  95: ['wolf', 0xd8d8d8, 0.6, 0.85],
  96: ['mooshroom', 0xb02020, 0.9, 1.4],
  97: ['snow golem', 0xf0f8ff, 0.7, 1.9],
  98: ['ocelot', 0xe0c060, 0.6, 0.7],
  99: ['iron golem', 0xc8c0b0, 1.4, 2.7],
  100: ['horse', 0x8a5a30, 1.4, 1.6],
  101: ['rabbit', 0xb08a60, 0.4, 0.5],
  102: ['polar bear', 0xf0f0f0, 1.3, 1.4],
  103: ['llama', 0xd8c8a0, 0.9, 1.87],
  105: ['parrot', 0x20c020, 0.5, 0.9],
  120: ['villager', 0x9a6a4a, 0.6, 1.95],
};

export function mobInfo(type: number): [string, number, number, number] {
  return MOBS[type] ?? ['mob', 0xb05050, 0.6, 1.8];
}

function hashHue(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return ((h >>> 0) % 360) / 360;
}

export function botColour(b: { name: string; dto: { main: boolean } }): THREE.Color {
  return b.dto.main ? new THREE.Color(0xf2c14e) : new THREE.Color().setHSL(hashHue(b.name), 0.65, 0.55);
}

/** Box size and colour of a non-bot entity. */
export function entityLook(e: EntityView): { colour: number; w: number; h: number } {
  if (e.kind === 'player') return { colour: 0x4a7ad8, w: 0.6, h: 1.8 };
  if (e.kind === 'item') return { colour: 0xd8b040, w: 0.25, h: 0.25 };
  if (e.kind === 'mob') {
    const [, c, w, h] = mobInfo(e.type);
    const size = (e.type === 55 || e.type === 62) && e.dto.variant ? Math.max(1, e.dto.variant) : 1;
    return { colour: c, w: w * size, h: h * size };
  }
  return { colour: 0x9a9a9a, w: 0.5, h: 0.5 };
}

export function entityText(e: EntityView): string {
  if (e.kind === 'item') return `${e.name ?? `item ${e.item}`}${e.count > 1 ? ` x${e.count}` : ''}`;
  if (e.kind === 'player') return e.name ?? 'player';
  if (e.kind === 'mob') return e.name ?? mobInfo(e.type)[0];
  return e.name ?? e.kind;
}

interface BotObj {
  dim: number;
  root: THREE.Group;
  body: THREE.Mesh;
  head: THREE.Mesh;
  ring: THREE.Mesh;
  mat: THREE.MeshLambertMaterial;
  label: THREE.Sprite | null;
  labelText: string;
}

interface Label {
  sprite: THREE.Sprite;
  text: string;
}

const MAX_INSTANCES = 2048;

export class EntityLayer {
  readonly group = new THREE.Group();
  private readonly labels = new LabelFactory();
  private readonly bots = new Map<string, BotObj>();
  private readonly entLabels = new Map<string, Label>();
  private readonly unit = new THREE.BoxGeometry(1, 1, 1).translate(0, 0.5, 0);
  private readonly instMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
  private readonly inst = new THREE.InstancedMesh(this.unit, this.instMat, MAX_INSTANCES);
  private readonly bodyGeo = new THREE.BoxGeometry(0.6, 1.4, 0.36).translate(0, 0.7, 0);
  private readonly headGeo = new THREE.BoxGeometry(0.5, 0.5, 0.5).translate(0, 1.65, 0);
  private readonly faceGeo = new THREE.PlaneGeometry(0.3, 0.12).translate(0, 1.68, 0.251);
  private readonly faceMat = new THREE.MeshBasicMaterial({ color: 0x1a1a22 });
  private readonly ringGeo = new THREE.RingGeometry(0.55, 0.75, 32).rotateX(-Math.PI / 2).translate(0, 0.03, 0);
  private readonly ringMat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.9, depthTest: false });
  private ents: EntityView[] = [];
  private readonly m4 = new THREE.Matrix4();
  private readonly q = new THREE.Quaternion();
  private readonly c = new THREE.Color();
  private readonly p = new THREE.Vector3();
  private readonly s = new THREE.Vector3();

  constructor() {
    this.group.name = 'entities';
    this.inst.count = 0;
    this.inst.frustumCulled = false;
    this.inst.setColorAt(0, new THREE.Color());
    this.group.add(this.inst);
  }

  /** Once per frame: every bot and entity (every dimension) at its interpolated position. */
  update(bots: BotView[], ents: EntityView[], timeMs: number, selected: ReadonlySet<string>): void {
    const seen = new Set<string>();
    for (const b of bots) {
      seen.add(b.name);
      let o = this.bots.get(b.name);
      if (!o) this.bots.set(b.name, (o = this.makeBot(b)));
      o.dim = b.dim;
      o.root.position.set(b.x, b.y, b.z);
      o.body.rotation.y = o.head.rotation.y = (-b.yaw * Math.PI) / 180;
      const sel = selected.has(b.name) || selected.has(b.dto.short);
      (o.ring.material as THREE.MeshBasicMaterial).opacity = 0.6 + 0.3 * Math.sin(timeMs / 250);
      o.mat.emissive.setScalar(sel ? 0.25 : 0);
      const held = b.dto.held ? `holding ${b.dto.held}` : b.dto.job ? b.dto.job : null;
      const text = `${b.name}${b.dto.main ? ' · main' : ''}`;
      const lt = `${text}|${held ?? ''}|${sel}`;
      if (o.labelText !== lt) {
        if (o.label) {
          o.root.remove(o.label);
          this.labels.release(o.label);
        }
        o.label = this.labels.create(text, { fg: '#ffffff', bg: sel ? 'rgba(30,60,120,0.85)' : 'rgba(0,0,0,0.6)', sub: held, border: sel ? '#9ec5ff' : null }, true);
        o.label.position.set(0, 2.2, 0);
        o.root.add(o.label);
        o.labelText = lt;
      }
    }
    for (const [k, o] of this.bots) if (!seen.has(k)) this.dropBot(k, o);
    this.ents = ents;
  }

  /**
   * Before rendering one view: its dimension only, the instances for it, its selection ring, and labels (bot labels always
   * when `labels`; mob/drop labels near its camera) — the layer toggles come from the view.
   */
  showFor(dim: number, cam: THREE.Vector3, opts: { bots: boolean; entities: boolean; labels: boolean; selected?: string | null }): void {
    for (const o of this.bots.values()) {
      o.root.visible = opts.bots && o.dim === dim;
      if (o.label) o.label.visible = opts.labels;
      o.ring.visible = !!opts.selected && (o.root.userData.name === opts.selected || o.root.userData.short === opts.selected);
    }
    let n = 0;
    const want = new Set<string>();
    if (opts.entities)
      for (const e of this.ents) {
        if (e.dim !== dim || n >= MAX_INSTANCES) continue;
        const look = entityLook(e);
        const bob = e.kind === 'item' ? Math.sin(e.ageTicks / 10) * 0.1 + 0.15 : 0;
        const yaw = e.kind === 'item' ? e.ageTicks / 20 : (-e.yaw * Math.PI) / 180;
        this.q.setFromAxisAngle(THREE.Object3D.DEFAULT_UP, yaw);
        this.m4.compose(this.p.set(e.x, e.y + bob, e.z), this.q, this.s.set(look.w, look.h, look.w));
        this.inst.setMatrixAt(n, this.m4);
        this.inst.setColorAt(n, this.c.setHex(look.colour));
        n++;
        if (!opts.labels) continue;
        const near = e.kind === 'player' ? 64 : e.kind === 'item' ? 12 : 24;
        if (Math.hypot(e.x - cam.x, e.y - cam.y, e.z - cam.z) > near) continue;
        want.add(e.key);
        const text = entityText(e);
        let l = this.entLabels.get(e.key);
        if (l && l.text !== text) {
          this.group.remove(l.sprite);
          this.labels.release(l.sprite);
          this.entLabels.delete(e.key);
          l = undefined;
        }
        if (!l) {
          const sprite = this.labels.create(text, { fg: e.kind === 'player' ? '#cfe0ff' : '#ffffff', bg: 'rgba(0,0,0,0.45)' }, e.kind === 'player', e.kind === 'item' ? 0.7 : 0.85);
          this.group.add(sprite);
          this.entLabels.set(e.key, (l = { sprite, text }));
        }
        l.sprite.position.set(e.x, e.y + look.h + 0.25 + bob, e.z);
      }
    this.inst.count = n;
    this.inst.instanceMatrix.needsUpdate = true;
    if (this.inst.instanceColor) this.inst.instanceColor.needsUpdate = true;
    for (const [k, l] of this.entLabels) {
      if (want.has(k)) l.sprite.visible = true;
      else if (opts.labels) {
        this.group.remove(l.sprite);
        this.labels.release(l.sprite);
        this.entLabels.delete(k);
      } else l.sprite.visible = false;
    }
  }

  /** The bot under a ray (selection), nearest first; only bots visible in the last `showFor`. */
  botAt(ray: THREE.Raycaster): string | null {
    const objs: THREE.Object3D[] = [];
    for (const o of this.bots.values()) if (o.root.visible) objs.push(o.body, o.head);
    const hits = ray.intersectObjects(objs, false);
    if (!hits.length) return null;
    for (const [k, o] of this.bots) if (o.body === hits[0].object || o.head === hits[0].object) return k;
    return null;
  }

  get instances(): number {
    return this.inst.count;
  }

  private makeBot(b: BotView): BotObj {
    const mat = new THREE.MeshLambertMaterial({ color: botColour(b) });
    const body = new THREE.Mesh(this.bodyGeo, mat);
    const head = new THREE.Mesh(this.headGeo, mat);
    head.add(new THREE.Mesh(this.faceGeo, this.faceMat));
    const ring = new THREE.Mesh(this.ringGeo, this.ringMat.clone());
    ring.renderOrder = 9;
    const root = new THREE.Group();
    root.userData = { name: b.name, short: b.dto.short };
    root.add(body, head, ring);
    this.group.add(root);
    return { dim: b.dim, root, body, head, ring, mat, label: null, labelText: '' };
  }

  private dropBot(k: string, o: BotObj): void {
    this.group.remove(o.root);
    if (o.label) this.labels.release(o.label);
    o.mat.dispose();
    (o.ring.material as THREE.Material).dispose();
    this.bots.delete(k);
  }

  dispose(): void {
    for (const [k, o] of this.bots) this.dropBot(k, o);
    for (const l of this.entLabels.values()) this.labels.release(l.sprite);
    this.entLabels.clear();
    this.inst.dispose();
    for (const g of [this.unit, this.bodyGeo, this.headGeo, this.faceGeo, this.ringGeo]) g.dispose();
    for (const m of [this.instMat, this.faceMat, this.ringMat]) m.dispose();
    this.labels.dispose();
  }
}
