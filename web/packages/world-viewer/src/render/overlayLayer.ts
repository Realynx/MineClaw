// Toggleable overlay layers from `api/world/overlays`: claims, places, points of interest, build plans as ghost blocks (done vs
// todo), dangers. Boxes are depth-tested outlines with a faint fill; labels float above. Rebuilt only when the reply changes.

import * as THREE from 'three';
import type { LayerToggles, Mark, Overlays } from '../types';
import { LabelFactory } from './labels';
import { flatColour, type GameVersion } from '../world/blocks';

type Kind = 'claims' | 'places' | 'points' | 'plans' | 'dangers';

const COLOURS: Record<Exclude<Kind, 'plans'>, number> = {
  claims: 0x5aa9ff,
  places: 0xf0b84a,
  points: 0x4ee0c8,
  dangers: 0xff4a4a,
};

function ownerColour(owner: string | null | undefined, base: number): number {
  if (!owner) return base;
  let h = 0;
  for (let i = 0; i < owner.length; i++) h = (h * 31 + owner.charCodeAt(i)) | 0;
  return new THREE.Color().setHSL(((h >>> 0) % 360) / 360, 0.7, 0.6).getHex();
}

export class OverlayLayer {
  readonly group = new THREE.Group();
  private readonly layers = new Map<Kind, THREE.Group>();
  private readonly labels = new LabelFactory();
  private readonly box = new THREE.BoxGeometry(1, 1, 1).translate(0.5, 0.5, 0.5);
  private readonly edges = new THREE.EdgesGeometry(this.box);
  private lastJson = '';
  private dangerMats: THREE.MeshBasicMaterial[] = [];

  constructor() {
    this.group.name = 'overlays';
    for (const k of ['claims', 'places', 'points', 'plans', 'dangers'] as Kind[]) {
      const g = new THREE.Group();
      g.name = k;
      this.layers.set(k, g);
      this.group.add(g);
    }
  }

  setVisible(l: LayerToggles): void {
    for (const [k, g] of this.layers) g.visible = l[k];
  }

  /** Rebuilds the layers when the overlays changed. */
  set(ov: Overlays | null, version: GameVersion): void {
    const json = ov ? JSON.stringify([ov.claims, ov.places, ov.points, ov.plans, ov.dangers]) : '';
    if (json === this.lastJson) return;
    this.lastJson = json;
    this.clear();
    if (!ov) return;
    for (const m of ov.claims) this.mark('claims', m, ownerColour(m.owner, COLOURS.claims), 0.05);
    for (const m of ov.places) this.mark('places', m, COLOURS.places, 0.04);
    for (const m of ov.points) this.mark('points', m, COLOURS.points, 0.12);
    for (const m of ov.dangers) this.mark('dangers', m, COLOURS.dangers, 0.18, true);
    for (const p of ov.plans) this.plan(p, version);
  }

  /** Per frame: dangers pulse. */
  tick(timeMs: number): void {
    const k = 0.12 + 0.1 * (0.5 + 0.5 * Math.sin(timeMs / 300));
    for (const m of this.dangerMats) m.opacity = k;
  }

  private mark(kind: Kind, m: Mark, colour: number, fill: number, pulse = false): void {
    const g = this.layers.get(kind)!;
    const x0 = Math.min(m.min[0], m.max[0]), y0 = Math.min(m.min[1], m.max[1]), z0 = Math.min(m.min[2], m.max[2]);
    const x1 = Math.max(m.min[0], m.max[0]) + 1, y1 = Math.max(m.min[1], m.max[1]) + 1, z1 = Math.max(m.min[2], m.max[2]) + 1;
    const pad = 0.02;
    const sx = x1 - x0 + pad * 2, sy = y1 - y0 + pad * 2, sz = z1 - z0 + pad * 2;
    const lineMat = new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity: 0.9 });
    const lines = new THREE.LineSegments(this.edges, lineMat);
    lines.position.set(x0 - pad, y0 - pad, z0 - pad);
    lines.scale.set(sx, sy, sz);
    lines.renderOrder = 3;
    g.add(lines);
    const fillMat = new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: fill, depthWrite: false, side: THREE.DoubleSide });
    if (pulse) this.dangerMats.push(fillMat);
    const mesh = new THREE.Mesh(this.box, fillMat);
    mesh.position.copy(lines.position);
    mesh.scale.copy(lines.scale);
    mesh.renderOrder = 3;
    g.add(mesh);
    if (m.text) {
      const label = this.labels.create(m.text.length > 60 ? m.text.slice(0, 57) + '…' : m.text, { fg: '#ffffff', bg: `rgba(${(colour >> 16) & 255},${(colour >> 8) & 255},${colour & 255},0.55)` }, false, 0.8);
      label.position.set((x0 + x1) / 2, y1 + 0.2, (z0 + z1) / 2);
      g.add(label);
    }
  }

  private plan(p: Overlays['plans'][number], version: GameVersion): void {
    const g = this.layers.get('plans')!;
    const todo = p.blocks.filter((b) => !b[5]);
    const done = p.blocks.filter((b) => b[5]);
    const geo = new THREE.BoxGeometry(0.96, 0.96, 0.96).translate(0.5, 0.5, 0.5);
    const mk = (blocks: typeof p.blocks, opacity: number, tintDone: boolean) => {
      if (!blocks.length) return;
      const mat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity, depthWrite: false });
      const inst = new THREE.InstancedMesh(geo, mat, blocks.length);
      const m = new THREE.Matrix4();
      const c = new THREE.Color();
      blocks.forEach((b, i) => {
        m.makeTranslation(b[0], b[1], b[2]);
        inst.setMatrixAt(i, m);
        const base = flatColour(version, b[3] & 255, b[4] & 15, true);
        c.setHex(base).lerp(new THREE.Color(tintDone ? 0x40ff70 : 0x4a8cff), tintDone ? 0.5 : 0.35);
        inst.setColorAt(i, c);
      });
      inst.renderOrder = 4;
      inst.userData.ownsGeometry = false;
      g.add(inst);
    };
    mk(todo, 0.45, false);
    mk(done, 0.18, true);
    g.userData.geos = [...((g.userData.geos as THREE.BufferGeometry[]) ?? []), geo];
    if (p.blocks.length) {
      let x = 0, y = -Infinity, z = 0;
      for (const b of p.blocks) {
        x += b[0];
        z += b[2];
        y = Math.max(y, b[1]);
      }
      const label = this.labels.create(`${p.name}${p.status ? ` · ${p.status}` : ''} · ${done.length}/${p.blocks.length}`, { fg: '#ffffff', bg: 'rgba(40,90,200,0.6)' }, false, 0.85);
      label.position.set(x / p.blocks.length + 0.5, y + 1.6, z / p.blocks.length + 0.5);
      g.add(label);
    }
  }

  private clear(): void {
    this.dangerMats = [];
    for (const g of this.layers.values()) {
      for (const o of [...g.children]) {
        g.remove(o);
        if (o instanceof THREE.Sprite) this.labels.release(o);
        else if (o instanceof THREE.Mesh || o instanceof THREE.LineSegments) (o.material as THREE.Material).dispose();
        if (o instanceof THREE.InstancedMesh) o.dispose();
      }
      for (const geo of (g.userData.geos as THREE.BufferGeometry[]) ?? []) geo.dispose();
      g.userData.geos = [];
    }
  }

  dispose(): void {
    this.clear();
    this.box.dispose();
    this.edges.dispose();
    this.labels.dispose();
  }
}
