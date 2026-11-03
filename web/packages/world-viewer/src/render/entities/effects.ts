// Short-lived effects driven by entity events: particle bursts (crits, magic crits, teleports, death smoke) drawn as
// camera-facing coloured squares through the entity sprite draw, and lightning bolts (RenderLightningBolt-style jagged
// additive ribbons) with their own small mesh.

import * as THREE from 'three';

export interface Particle {
  dim: number;
  x: number;
  y: number;
  z: number;
  vx: number;
  vy: number;
  vz: number;
  born: number;
  life: number;
  r: number;
  g: number;
  b: number;
  size: number;
  gravity: number;
}

export interface BurstStyle {
  colour: [number, number, number];
  /** Random colour variation 0..1. */
  jitter?: number;
  count: number;
  /** Initial speed (blocks/s) and spread of the start positions (blocks). */
  speed: number;
  spread: number;
  /** Lifetime ms. */
  life: number;
  size: number;
  /** Blocks/s² downward (negative rises). */
  gravity: number;
}

export const BURSTS: Record<string, BurstStyle> = {
  // EntityCrit2FX: pale yellow-white sparks flung out.
  crit: { colour: [0.95, 0.9, 0.7], jitter: 0.1, count: 14, speed: 3.5, spread: 0.3, life: 600, size: 0.09, gravity: 4 },
  // Magic crit: the enchanted-hit cyan-blue.
  magic_crit: { colour: [0.35, 0.6, 1], jitter: 0.15, count: 14, speed: 3.5, spread: 0.3, life: 650, size: 0.09, gravity: 3 },
  // Portal particles of teleports (endermen, pearls): purple, drifting.
  teleport: { colour: [0.75, 0.3, 0.95], jitter: 0.2, count: 22, speed: 1.6, spread: 0.6, life: 900, size: 0.08, gravity: -0.6 },
  // Death "poof": grey-white smoke rising.
  death: { colour: [0.85, 0.85, 0.85], jitter: 0.15, count: 18, speed: 1.2, spread: 0.5, life: 900, size: 0.16, gravity: -1.2 },
};

const MAX_PARTICLES = 1024;

export class Particles {
  readonly list: Particle[] = [];
  private seed = 12345;

  private rnd(): number {
    this.seed = (Math.imul(this.seed, 1103515245) + 12345) | 0;
    return ((this.seed >>> 0) % 100000) / 100000;
  }

  burst(kind: string, dim: number, x: number, y: number, z: number, now: number): void {
    const s = BURSTS[kind];
    if (!s) return;
    for (let i = 0; i < s.count && this.list.length < MAX_PARTICLES; i++) {
      const a = this.rnd() * Math.PI * 2, e = (this.rnd() - 0.3) * Math.PI * 0.8, v = s.speed * (0.4 + this.rnd() * 0.6);
      const j = 1 + (this.rnd() - 0.5) * 2 * (s.jitter ?? 0);
      this.list.push({
        dim,
        x: x + (this.rnd() - 0.5) * s.spread,
        y: y + (this.rnd() - 0.5) * s.spread,
        z: z + (this.rnd() - 0.5) * s.spread,
        vx: Math.cos(a) * Math.cos(e) * v,
        vy: Math.sin(e) * v,
        vz: Math.sin(a) * Math.cos(e) * v,
        born: now,
        life: s.life * (0.6 + this.rnd() * 0.4),
        r: Math.min(1, s.colour[0] * j),
        g: Math.min(1, s.colour[1] * j),
        b: Math.min(1, s.colour[2] * j),
        size: s.size,
        gravity: s.gravity,
      });
    }
  }

  /** Drops expired particles. */
  step(now: number): void {
    let w = 0;
    for (const p of this.list) if (now - p.born < p.life) this.list[w++] = p;
    this.list.length = w;
  }

  /** The particle's affine bone now (centre and size, for the billboard sprite): 12 floats into `out` at `o`. */
  bone(p: Particle, now: number, out: Float32Array, o: number): void {
    const t = (now - p.born) / 1000, k = 1 - (now - p.born) / p.life;
    const s = p.size * (0.4 + 0.6 * k);
    out.fill(0, o, o + 12);
    out[o] = out[o + 5] = out[o + 10] = s;
    out[o + 3] = p.x + p.vx * t;
    out[o + 7] = p.y + p.vy * t - 0.5 * p.gravity * t * t - s * 0.5;
    out[o + 11] = p.z + p.vz * t;
  }
}

interface Bolt {
  dim: number;
  born: number;
  mesh: THREE.Mesh;
}

/** Vanilla-style lightning: a jagged main bolt from high above to the strike point with a couple of branches, drawn as
 * crossed additive ribbons for ~0.5 s, flickering. */
export class Lightning {
  readonly group = new THREE.Group();
  private readonly bolts: Bolt[] = [];
  private readonly mat = new THREE.MeshBasicMaterial({ color: 0xb4bcff, transparent: true, opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide, fog: false });
  static readonly LIFE_MS = 500;

  constructor() {
    this.group.name = 'lightning';
  }

  strike(dim: number, x: number, y: number, z: number, now: number, seed = Math.floor(now)): void {
    let s = seed | 1;
    const rnd = () => ((s = (Math.imul(s, 1103515245) + 12345) | 0) >>> 0) / 4294967296 - 0.5;
    const pos: number[] = [];
    const ribbon = (ax: number, ay: number, az: number, bx: number, by: number, bz: number, w: number) => {
      // Two crossed quads along the segment.
      for (const [dx, dz] of [[w, 0], [0, w]]) {
        pos.push(ax - dx, ay, az - dz, ax + dx, ay, az + dz, bx + dx, by, bz + dz);
        pos.push(ax - dx, ay, az - dz, bx + dx, by, bz + dz, bx - dx, by, bz - dz);
      }
    };
    const chain = (sx: number, sy: number, sz: number, ey: number, segments: number, w: number, wander: number): Array<[number, number, number]> => {
      const pts: Array<[number, number, number]> = [[sx, sy, sz]];
      for (let i = 1; i <= segments; i++) {
        const t = i / segments;
        const last = pts[pts.length - 1];
        pts.push(i === segments && ey === y ? [x, y, z] : [last[0] + rnd() * wander, sy + (ey - sy) * t, last[2] + rnd() * wander]);
      }
      for (let i = 1; i < pts.length; i++) ribbon(pts[i - 1][0], pts[i - 1][1], pts[i - 1][2], pts[i][0], pts[i][1], pts[i][2], w);
      return pts;
    };
    const top = y + 64;
    const main = chain(x + rnd() * 6, top, z + rnd() * 6, y, 16, 0.12, 2.4);
    for (let b = 0; b < 3; b++) {
      const from = main[2 + Math.floor((rnd() + 0.5) * 8)];
      chain(from[0], from[1], from[2], from[1] - 10 - (rnd() + 0.5) * 12, 6, 0.06, 2.8);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    const mesh = new THREE.Mesh(g, this.mat);
    mesh.frustumCulled = false;
    mesh.renderOrder = 5;
    this.group.add(mesh);
    this.bolts.push({ dim, born: now, mesh });
  }

  /** Expires old bolts and flickers live ones. */
  update(now: number): void {
    for (let i = this.bolts.length - 1; i >= 0; i--) {
      const b = this.bolts[i];
      if (now - b.born > Lightning.LIFE_MS) {
        this.group.remove(b.mesh);
        b.mesh.geometry.dispose();
        this.bolts.splice(i, 1);
      }
    }
    this.mat.opacity = 0.35 + 0.3 * (Math.floor(now / 60) % 2);
  }

  showFor(dim: number): void {
    for (const b of this.bolts) b.mesh.visible = b.dim === dim;
  }

  get active(): number {
    return this.bolts.length;
  }

  dispose(): void {
    for (const b of this.bolts) b.mesh.geometry.dispose();
    this.bolts.length = 0;
    this.mat.dispose();
  }
}
