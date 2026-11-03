// Reading aids, as the C# ReadingAids draws them over the AI's 3D panels: a faint ground grid on the followed bot's floor
// (every 5th line stronger and labelled with its absolute x/z at the edge, like a ruler), distance rings at 2, 4 and 8
// blocks, and an N/E/S/W compass. Optional (`layers.aids`), drawn for the interactive view only.

import * as THREE from 'three';
import { LabelFactory } from './labels';

const R = 10;

export class AidsLayer {
  readonly group = new THREE.Group();
  private readonly labels = new LabelFactory();
  private readonly grid: THREE.LineSegments;
  private readonly major: THREE.LineSegments;
  private readonly rings: THREE.LineSegments;
  private readonly rulers: THREE.Sprite[] = [];
  private lastKey = '';

  constructor() {
    this.group.name = 'aids';
    const minor: number[] = [], majorPts: number[] = [];
    for (let i = -R; i <= R; i++) {
      const target = i % 5 === 0 ? majorPts : minor;
      target.push(i, 0, -R, i, 0, R, -R, 0, i, R, 0, i);
    }
    const mk = (pts: number[], colour: number, opacity: number) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      const l = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity, depthWrite: false }));
      l.renderOrder = 4;
      return l;
    };
    this.grid = mk(minor, 0xffffff, 0.18);
    this.major = mk(majorPts, 0xffffff, 0.45);
    const ring: number[] = [];
    for (const r of [2, 4, 8])
      for (let k = 0; k < 64; k++) {
        const a = (k / 64) * Math.PI * 2, b = ((k + 1) / 64) * Math.PI * 2;
        ring.push(Math.cos(a) * r, 0, Math.sin(a) * r, Math.cos(b) * r, 0, Math.sin(b) * r);
      }
    this.rings = mk(ring, 0x9ec5ff, 0.55);
    this.group.add(this.grid, this.major, this.rings);
    for (const [t, x, z] of [['N', 0, -R - 1], ['S', 0, R + 1], ['W', -R - 1, 0], ['E', R + 1, 0]] as const) {
      const s = this.labels.create(t, { fg: '#ffe9a0', bg: 'rgba(0,0,0,0.55)' }, true, 1.1);
      s.position.set(x, 0.2, z);
      s.userData.fixed = true;
      this.group.add(s);
    }
    this.group.visible = false;
  }

  /** Before a view renders: show around (x, y, z) — the followed bot's feet — or hide (null). */
  showAt(p: { x: number; y: number; z: number } | null): void {
    this.group.visible = !!p;
    if (!p) return;
    const bx = Math.floor(p.x), by = Math.floor(p.y), bz = Math.floor(p.z);
    this.group.position.set(bx, by + 0.02, bz);
    const key = `${bx},${bz}`;
    if (key === this.lastKey) return;
    this.lastKey = key;
    // Rulers: absolute coordinates of the strong lines along the north and west edges.
    for (const s of this.rulers) {
      this.group.remove(s);
      this.labels.release(s);
    }
    this.rulers.length = 0;
    for (let i = -R; i <= R; i++) {
      if ((bx + i) % 5 !== 0 && (bz + i) % 5 !== 0) continue;
      if ((bx + i) % 5 === 0) this.ruler(String(bx + i), i, -R - 0.3);
      if ((bz + i) % 5 === 0) this.ruler(String(bz + i), -R - 0.6, i);
    }
  }

  private ruler(text: string, x: number, z: number): void {
    const s = this.labels.create(text, { fg: '#ffffff', bg: 'rgba(0,0,0,0.4)' }, false, 0.55);
    s.position.set(x, 0.05, z);
    this.rulers.push(s);
    this.group.add(s);
  }

  dispose(): void {
    for (const o of [this.grid, this.major, this.rings]) {
      o.geometry.dispose();
      (o.material as THREE.Material).dispose();
    }
    for (const c of this.group.children) if (c instanceof THREE.Sprite) this.labels.release(c);
    this.labels.dispose();
  }
}
