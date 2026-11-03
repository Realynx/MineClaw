// The sky over every view: sun and moon (with its phase) from the version's own textures, stars at night and the flat
// cloud layer, placed around each view's camera before it renders. Vanilla (RenderGlobal.renderSky): the sun rises in the
// east and sets in the west, its angle is the celestial angle, the moon is opposite; stars fade in as the day light goes;
// clouds drift west at the cloud height (1.12.2: 128, beta: 108) and are grey under rain.

import * as THREE from 'three';
import type { AssetBundle } from '../assets/bundle';
import { celestialAngle } from '../world/sky';

const STARS = 1500;
const SKY_R = 100;

function texture(img: ImageBitmap | undefined): THREE.Texture | null {
  if (!img) return null;
  const t = new THREE.Texture(img as unknown as HTMLImageElement);
  t.magFilter = THREE.NearestFilter;
  t.minFilter = THREE.NearestFilter;
  t.generateMipmaps = false;
  t.colorSpace = THREE.SRGBColorSpace;
  t.flipY = false;
  t.needsUpdate = true;
  return t;
}

export class SkyLayer {
  readonly group = new THREE.Group();
  private readonly sun: THREE.Mesh;
  private readonly moon: THREE.Mesh;
  private readonly stars: THREE.Points;
  private readonly clouds: THREE.Mesh;
  private readonly sunMat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, fog: false });
  private readonly moonMat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, fog: false });
  private readonly starMat = new THREE.PointsMaterial({ color: 0xffffff, size: 1.6, sizeAttenuation: false, transparent: true, depthWrite: false, fog: false });
  private readonly cloudMat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.8, depthWrite: false, side: THREE.DoubleSide, fog: false });
  private textures: THREE.Texture[] = [];
  private cloudHeight = 128;
  private moonPhases = 8;

  constructor() {
    this.group.name = 'sky';
    const quad = new THREE.PlaneGeometry(30, 30);
    this.sun = new THREE.Mesh(quad, this.sunMat);
    this.moon = new THREE.Mesh(quad.clone(), this.moonMat);
    const pos = new Float32Array(STARS * 3);
    let seed = 10842;
    const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff) * 2 - 1;
    for (let i = 0; i < STARS; i++) {
      let x = rnd(), y = rnd(), z = rnd();
      const l = Math.hypot(x, y, z) || 1;
      x /= l; y /= l; z /= l;
      pos.set([x * SKY_R, y * SKY_R, z * SKY_R], i * 3);
    }
    const sg = new THREE.BufferGeometry();
    sg.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    this.stars = new THREE.Points(sg, this.starMat);
    // Clouds: one big plane, its texture repeating; 12 px of the 256 px clouds.png per block like vanilla's 12-block cells.
    this.clouds = new THREE.Mesh(new THREE.PlaneGeometry(768, 768).rotateX(-Math.PI / 2), this.cloudMat);
    for (const o of [this.sun, this.moon, this.stars, this.clouds]) {
      o.frustumCulled = false;
      o.renderOrder = -10;
      this.group.add(o);
    }
    this.clouds.renderOrder = 7;
    this.sun.visible = this.moon.visible = this.clouds.visible = false;
  }

  /** Takes the version's sun, moon and cloud textures from its bundle. */
  setBundle(b: AssetBundle | null): void {
    for (const t of this.textures) t.dispose();
    this.textures = [];
    const sun = texture(b?.env.sun), moon = texture(b?.env.moon_phases ?? b?.env.moon), clouds = texture(b?.env.clouds);
    this.moonPhases = b?.env.moon_phases ? 8 : 1;
    this.cloudHeight = b?.version === 'b1.8.1' ? 108 : 128;
    this.sunMat.map = sun;
    this.moonMat.map = moon;
    if (clouds) {
      clouds.wrapS = clouds.wrapT = THREE.RepeatWrapping;
      clouds.repeat.set(768 / 12 / 256 * 1, 768 / 12 / 256 * 1);
    }
    this.cloudMat.map = clouds;
    for (const m of [this.sunMat, this.moonMat, this.cloudMat]) m.needsUpdate = true;
    this.textures = [sun, moon, clouds].filter((t): t is THREE.Texture => !!t);
  }

  /** Before rendering a view: place the sky around its camera for the world time (null = unknown: noon, no stars). */
  update(cam: THREE.Vector3, time: number | null, rain: number, nether: boolean, clouds: boolean, nowMs: number, radius = SKY_R): void {
    this.group.visible = !nether;
    if (nether) return;
    const a = time == null ? 0 : celestialAngle(time);
    const th = a * Math.PI * 2;
    const dir = new THREE.Vector3(-Math.sin(th), Math.cos(th), 0);
    const r = 1 - Math.min(1, Math.max(0, rain));
    this.sun.visible = !!this.sunMat.map;
    const k = radius / SKY_R;
    this.sun.scale.setScalar(k);
    this.moon.scale.setScalar(k);
    this.stars.scale.setScalar(k);
    this.sun.position.copy(cam).addScaledVector(dir, radius);
    this.sun.lookAt(cam);
    this.sunMat.opacity = r;
    this.moon.visible = !!this.moonMat.map;
    this.moon.position.copy(cam).addScaledVector(dir, -radius);
    this.moon.lookAt(cam);
    this.moonMat.opacity = r;
    if (this.moonMat.map && this.moonPhases > 1 && time != null) {
      // moon_phases.png: 4 × 2 phases; phase = day % 8.
      const phase = Math.floor(time / 24000) % 8;
      this.moonMat.map.repeat.set(1 / 4, 1 / 2);
      this.moonMat.map.offset.set((phase % 4) / 4, Math.floor(phase / 4) / 2);
    }
    const day = Math.cos(th) * 2 + 0.25;
    const star = Math.max(0, Math.min(1, 1 - day));
    this.starMat.opacity = star * star * 0.5 * r;
    this.stars.visible = this.starMat.opacity > 0.01;
    this.stars.position.copy(cam);
    this.stars.rotation.set(0, 0, th);
    this.clouds.visible = clouds && !!this.cloudMat.map;
    if (this.clouds.visible && this.cloudMat.map) {
      // Clouds drift with world time (vanilla: 0.03 blocks per tick westward); kept centred on the camera.
      const drift = ((time ?? nowMs / 50) * 0.03) / 12 / 256;
      this.clouds.position.set(cam.x, this.cloudHeight + 0.33, cam.z);
      this.cloudMat.map.offset.set(cam.x / 12 / 256 + drift, cam.z / 12 / 256);
      const lit = 0.55 + 0.45 * Math.max(0, Math.min(1, Math.cos(th) * 2 + 0.5));
      this.cloudMat.color.setRGB(lit * (1 - rain * 0.5), lit * (1 - rain * 0.5), lit * (1 - rain * 0.4));
    }
  }

  dispose(): void {
    for (const t of this.textures) t.dispose();
    for (const o of [this.sun, this.moon, this.stars, this.clouds]) o.geometry.dispose();
    for (const m of [this.sunMat, this.moonMat, this.starMat, this.cloudMat]) m.dispose();
  }
}
