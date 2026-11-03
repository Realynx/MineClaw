// A brief glow on blocks that just changed (block deltas): one InstancedMesh of slightly oversized boxes, additive, fading out.

import * as THREE from 'three';

const MAX = 512;
const LIFE_MS = 1500;

export class ChangeFlash {
  readonly mesh: THREE.InstancedMesh;
  private readonly items: Array<{ dim: number; x: number; y: number; z: number; t: number }> = [];
  private readonly m4 = new THREE.Matrix4();
  private readonly c = new THREE.Color();

  constructor() {
    const g = new THREE.BoxGeometry(1.04, 1.04, 1.04).translate(0.5, 0.5, 0.5);
    const mat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false });
    this.mesh = new THREE.InstancedMesh(g, mat, MAX);
    this.mesh.count = 0;
    this.mesh.frustumCulled = false;
    this.mesh.renderOrder = 6;
    this.mesh.setColorAt(0, this.c);
  }

  add(dim: number, blocks: Array<[number, number, number]>, now: number): void {
    for (const [x, y, z] of blocks) this.items.push({ dim, x, y, z, t: now });
    if (this.items.length > MAX) this.items.splice(0, this.items.length - MAX);
  }

  /** Before rendering a view of `dim`. */
  showFor(dim: number, now: number, enabled: boolean): void {
    while (this.items.length && now - this.items[0].t > LIFE_MS) this.items.shift();
    let n = 0;
    if (enabled)
      for (const it of this.items) {
        if (it.dim !== dim) continue;
        const k = 1 - (now - it.t) / LIFE_MS;
        this.m4.makeTranslation(it.x - 0.02, it.y - 0.02, it.z - 0.02);
        this.mesh.setMatrixAt(n, this.m4);
        this.mesh.setColorAt(n, this.c.setRGB(1 * k, 0.85 * k, 0.3 * k));
        n++;
      }
    this.mesh.count = n;
    this.mesh.visible = n > 0;
    this.mesh.instanceMatrix.needsUpdate = true;
    if (this.mesh.instanceColor) this.mesh.instanceColor.needsUpdate = true;
  }

  dispose(): void {
    this.mesh.geometry.dispose();
    (this.mesh.material as THREE.Material).dispose();
    this.mesh.dispose();
  }
}
