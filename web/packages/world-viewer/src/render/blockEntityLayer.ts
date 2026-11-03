// The main-thread block-entity layer, shared by every view: what the static section meshes cannot carry.
// - Sign text: the four lines in the sign font's metrics on the board's front (TileEntitySignRenderer's transform), one small
//   canvas texture per sign (LRU-capped), drawn only within `signDistance` of the view.
// - Banners with their block entity: base colour + pattern layers composited into a per-banner texture
//   (LayeredColorMaskTexture), the slate swaying like TileEntityBannerRenderer (the section mesh leaves these banners out).
// - Spawners: the display mob, spinning, through the entity layer's `showDisplayMob` hook when it has one.
// Rebuilt only when the block-entity store's revision or the dimension changes (block id/meta and light come from the worker
// once per rebuild, light again every few seconds); per frame only distances, the sway and the spin are updated.

import * as THREE from 'three';
import type { AssetBundle } from '../assets/bundle';
import type { MeshHost } from '../mesh/host';
import { partQuads, type Part, type Sheet } from '../mesh/models/builtin';
import { chain, rotate, scale, translate, type M4, type Quad } from '../mesh/quad';
import type { BlockEntityDto, Dim } from '../types';
import { dyeIndex } from '../wire/normalize';
import type { GameVersion } from '../world/blocks';
import { bannerPatternName, charWidth as charWidthOf, compositeBanner, DYE_RGB, signLayout, spawnerMobScale } from './blockEntityTex';
import type { ChunkUniforms } from './chunkMaterial';

const vertex = /* glsl */ `
attribute float aShade;
varying vec2 vUv;
varying float vShade;
varying vec3 vWorld;
void main() {
  vUv = uv;
  vShade = aShade;
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const fragment = /* glsl */ `
uniform sampler2D map;
uniform vec2 uLight;
uniform float uSkySub;
uniform float uBase;
uniform float uMinBright;
uniform float uFlash;
uniform vec3 uFogColor;
uniform vec3 uFogCentre;
uniform vec2 uFogRange;
varying vec2 vUv;
varying float vShade;
varying vec3 vWorld;
float bright(float level) {
  float f1 = 1.0 - clamp(level, 0.0, 15.0) / 15.0;
  return (1.0 - f1) / (f1 * 3.0 + 1.0) * (1.0 - uBase) + uBase;
}
void main() {
  vec4 c = texture2D(map, vUv);
  if (c.a < 0.5) discard;
  float b = max(bright(max(uLight.y, uLight.x - uSkySub)), uMinBright) * vShade;
  float fog = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
  gl_FragColor = vec4(mix(c.rgb * b + vec3(uFlash), uFogColor, fog), 1.0);
}
`;

interface Obj {
  key: string;
  be: BlockEntityDto;
  id: number;
  meta: number;
  /** Sky and block light of the cell. */
  light: THREE.Vector2;
  group: THREE.Group;
  /** Banner slate (sways). */
  slate?: THREE.Mesh;
  /** Sign text: built lazily within the sign distance. */
  text?: THREE.Mesh;
  material?: THREE.ShaderMaterial;
  texture?: THREE.Texture;
  usedAt: number;
  kind: 'sign' | 'banner' | 'spawner';
}

/** Model-space quads (px) → a BufferGeometry with uv in a w×h px texture and a per-vertex face shade. */
function geometryOf(quads: Quad[], texW: number, texH: number): THREE.BufferGeometry {
  const pos: number[] = [], uv: number[] = [], shade: number[] = [], index: number[] = [];
  for (const q of quads) {
    const b = pos.length / 3;
    for (let k = 0; k < 4; k++) {
      pos.push(q.p[k * 3], q.p[k * 3 + 1], q.p[k * 3 + 2]);
      uv.push((q.uv[k * 2] * 16) / texW, (q.uv[k * 2 + 1] * 16) / texH);
      shade.push(q.shade);
    }
    index.push(b, b + 1, b + 2, b, b + 2, b + 3);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setAttribute('aShade', new THREE.Float32BufferAttribute(shade, 1));
  g.setIndex(index);
  return g;
}

const SHEET: Sheet = { c: 0, r: 0, wc: 4, hc: 4 };
const MODEL_PX = scale(16, 16, 16); // partQuads scales by 1/16: keep model pixels
const k23 = 0.6666667;

/** The sign/banner frame (TileEntitySignRenderer / TileEntityBannerRenderer) before the model's own scale: standing ones turn
 * by meta·22.5°, wall ones by their facing (2 N 180°, 4 W 90°, 5 E −90°) and step back against the wall. */
function frameOf(standing: boolean, meta: number, banner: boolean): M4 {
  if (standing) return chain(translate(0.5, 0.5, 0.5), rotate((-(meta & 15) * 360) / 16, 'y'));
  const a = meta === 2 ? 180 : meta === 4 ? 90 : meta === 5 ? -90 : 0;
  return chain(translate(0.5, banner ? -0.16666667 : 0.5, 0.5), rotate(-a, 'y'), translate(0, -0.3125, -0.4375));
}

const m4 = (m: M4) => new THREE.Matrix4().set(...(m as [number, number, number, number, number, number, number, number, number, number, number, number, number, number, number, number]));

export interface BlockEntityLayerUpdate {
  dim: Dim;
  /** The primary view's camera position (distances). */
  centre: THREE.Vector3;
  software: boolean;
  now: number;
  /** Ticks of world time (banner sway). */
  ticks: number;
  /** The entity layer's display-mob hook (spawners), when present. */
  displayMobs?: {
    showDisplayMob?: (key: string, dim: Dim, x: number, y: number, z: number, mob: string, scale: number, spinRad: number) => void;
    hideDisplayMob?: (key: string) => void;
  };
}

export class BlockEntityLayer {
  readonly group = new THREE.Group();
  private readonly objs = new Map<string, Obj>();
  private revision = -1;
  private dim: Dim | null = null;
  private building = false;
  private lightAt = 0;
  private bundle: AssetBundle | null = null;
  private pixels: { data: Uint8ClampedArray; width: number } | null = null;
  private readonly shown = new Set<string>();
  /** Signs with text within this distance (blocks) get their text; banners and spawner mobs within `detailDistance`. */
  signDistance = 32;
  detailDistance = 64;
  /** Most sign text textures kept (least recently drawn dropped). */
  maxTextures = 64;

  constructor(
    private readonly host: MeshHost,
    private readonly uniforms: ChunkUniforms,
  ) {
    this.group.name = 'block-entities';
  }

  setBundle(b: AssetBundle | null): void {
    if (b === this.bundle) return;
    this.bundle = b;
    this.pixels = null;
    this.revision = -1; // banner textures come from the bundle: rebuild
  }

  get version(): GameVersion | null {
    return this.bundle?.version ?? null;
  }

  private material(map: THREE.Texture, light: THREE.Vector2): THREE.ShaderMaterial {
    const u = this.uniforms;
    return new THREE.ShaderMaterial({
      vertexShader: vertex,
      fragmentShader: fragment,
      uniforms: {
        map: { value: map },
        uLight: { value: light },
        uSkySub: u.uSkySub,
        uBase: u.uBase,
        uMinBright: u.uMinBright,
        uFlash: u.uFlash,
        uFogColor: u.uFogColor,
        uFogCentre: u.uFogCentre,
        uFogRange: u.uFogRange,
      },
      side: THREE.DoubleSide,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -1,
    });
  }

  /** Called once per frame. */
  update(blockEntities: { revision: number; all(dim: Dim): Iterable<BlockEntityDto> }, p: BlockEntityLayerUpdate): void {
    this.signDistance = p.software ? 24 : 48;
    this.detailDistance = p.software ? 40 : 96;
    this.maxTextures = p.software ? 24 : 96;
    if ((blockEntities.revision !== this.revision || p.dim !== this.dim) && !this.building) void this.rebuild(blockEntities, p.dim);
    if (p.now - this.lightAt > 3000 && !this.building && this.objs.size) this.refreshLight(p.dim, p.now);
    const c = p.centre;
    let texts = 0;
    const seen = new Set<string>();
    for (const o of this.objs.values()) {
      const x = o.be.x + 0.5, y = o.be.y + 0.5, z = o.be.z + 0.5;
      const d = Math.hypot(x - c.x, y - c.y, z - c.z);
      if (o.kind === 'spawner') {
        if (d <= this.detailDistance && p.displayMobs?.showDisplayMob && o.be.mob) {
          // MobSpawnerBaseLogic: the mob turns ~(1000 / (delay + 200))° a tick, drawn ×10: about 4 rad/s.
          p.displayMobs.showDisplayMob(o.key, p.dim, x, o.be.y + 0.2, z, String(o.be.mob), spawnerMobScale(String(o.be.mob)), (p.now / 1000) * 4);
          seen.add(o.key);
        }
        continue;
      }
      const vis = d <= (o.kind === 'sign' ? this.signDistance : this.detailDistance);
      o.group.visible = vis;
      if (!vis) continue;
      o.usedAt = p.now;
      if (o.kind === 'sign' && !o.text) {
        if (texts++ < 4) this.buildText(o); // a few new text textures a frame
      }
      if (o.slate) {
        // TileEntityBannerRenderer: (−0.0125 + 0.01·cos(f·π·0.02))·π, f = x·7 + y·9 + z·13 + world time.
        const f = o.be.x * 7 + o.be.y * 9 + o.be.z * 13 + p.ticks;
        o.slate.rotation.x = (-0.0125 + 0.01 * Math.cos(f * Math.PI * 0.02)) * Math.PI;
      }
    }
    // Spawner mobs out of range or gone.
    for (const k of this.shown) if (!seen.has(k)) p.displayMobs?.hideDisplayMob?.(k);
    this.shown.clear();
    for (const k of seen) this.shown.add(k);
    this.trimTextures();
  }

  private async rebuild(store: { revision: number; all(dim: Dim): Iterable<BlockEntityDto> }, dim: Dim): Promise<void> {
    this.building = true;
    const rev = store.revision;
    try {
      const want = [...store.all(dim)].filter((b) => b.kind === 'sign' || b.kind === 'banner' || b.kind === 'mob_spawner');
      const pts = want.flatMap((b) => [b.x, b.y, b.z]);
      const info = pts.length ? await this.host.blocksAt(dim, pts) : [];
      const keep = new Set<string>();
      want.forEach((be, i) => {
        const id = info[i * 3], meta = info[i * 3 + 1], light = info[i * 3 + 2];
        const kind = be.kind === 'sign' ? 'sign' : be.kind === 'banner' ? 'banner' : 'spawner';
        const ok = kind === 'sign' ? id === 63 || id === 68 : kind === 'banner' ? id === 176 || id === 177 : id === 52;
        if (!ok) return;
        const key = `${dim},${be.x},${be.y},${be.z}`;
        keep.add(key);
        const old = this.objs.get(key);
        if (old && old.id === id && old.meta === meta && JSON.stringify(old.be) === JSON.stringify(be)) return;
        if (old) this.remove(old);
        const o = this.create(key, be, id, meta, kind);
        if (light >= 0) o.light.set(light >> 4, light & 15);
        this.objs.set(key, o);
      });
      for (const o of [...this.objs.values()]) if (!keep.has(o.key)) this.remove(o);
      this.revision = rev;
      this.dim = dim;
    } catch {
      /* the next frame retries */
    } finally {
      this.building = false;
    }
  }

  private refreshLight(dim: Dim, now: number): void {
    this.lightAt = now;
    const list = [...this.objs.values()].filter((o) => o.group.visible);
    if (!list.length) return;
    void this.host
      .blocksAt(dim, list.flatMap((o) => [o.be.x, o.be.y, o.be.z]))
      .then((v) => list.forEach((o, i) => v[i * 3 + 2] >= 0 && o.light.set(v[i * 3 + 2] >> 4, v[i * 3 + 2] & 15)))
      .catch(() => undefined);
  }

  private create(key: string, be: BlockEntityDto, id: number, meta: number, kind: Obj['kind']): Obj {
    const group = new THREE.Group();
    group.matrixAutoUpdate = false;
    group.matrix.makeTranslation(be.x, be.y, be.z);
    group.visible = false;
    const o: Obj = { key, be, id, meta, kind, group, light: new THREE.Vector2(15, 0), usedAt: 0 };
    if (kind === 'banner') this.buildBanner(o);
    if (kind !== 'spawner') this.group.add(group);
    return o;
  }

  // ------------------------------------------------------------------------------------------------------------ signs

  private buildText(o: Obj): void {
    const lines = o.be.lines ?? [];
    if (!lines.some((l) => l)) {
      o.text = new THREE.Mesh(); // nothing to draw: remember that
      return;
    }
    // One canvas pixel per font pixel: glyphs drawn at the font's 8 px height, centred in their vanilla advances, then cut
    // to opaque/clear (no anti-aliasing) and magnified with nearest sampling — the blocky sign-font look.
    const S = 1;
    const W = 96, H = 42;
    const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(W * S, H * S) : Object.assign(document.createElement('canvas'), { width: W * S, height: H * S });
    const ctx = canvas.getContext('2d') as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
    if (!ctx) return;
    ctx.clearRect(0, 0, W * S, H * S);
    ctx.fillStyle = '#000';
    ctx.textBaseline = 'top';
    ctx.font = `bold ${8 * S}px "Lucida Console", "DejaVu Sans Mono", monospace`;
    for (const l of signLayout(lines)) {
      let x = l.x + W / 2;
      for (const ch of l.text) {
        const w = charWidthOf(ch);
        ctx.fillText(ch, (x + (w - 1) / 2) * S - ctx.measureText(ch).width / 2, (l.y + H / 2) * S);
        x += w;
      }
    }
    const img = ctx.getImageData(0, 0, W * S, H * S);
    for (let i = 3; i < img.data.length; i += 4) img.data[i] = img.data[i] >= 96 ? 255 : 0;
    ctx.putImageData(img, 0, 0);
    const tex = new THREE.CanvasTexture(canvas as HTMLCanvasElement);
    tex.flipY = false;
    tex.magFilter = THREE.NearestFilter;
    tex.minFilter = THREE.NearestFilter;
    tex.generateMipmaps = false;
    tex.colorSpace = THREE.NoColorSpace;
    // TileEntitySignRenderer: the frame, then translate(0, 1/3, 0.0467) and scale(1/96, −1/96, 1/96) in font pixels.
    const m = chain(frameOf(o.id === 63, o.meta, false), translate(0, 0.33333334, 0.046666667 + 0.002), scale(0.010416667, -0.010416667, 0.010416667));
    const g = new THREE.BufferGeometry();
    const corners = [[-W / 2, -H / 2], [-W / 2, H / 2], [W / 2, H / 2], [W / 2, -H / 2]];
    g.setAttribute('position', new THREE.Float32BufferAttribute(corners.flatMap(([x, y]) => [x, y, 0]), 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute(corners.flatMap(([x, y]) => [(x + W / 2) / W, (y + H / 2) / H]), 2));
    g.setAttribute('aShade', new THREE.Float32BufferAttribute([1, 1, 1, 1], 1));
    g.setIndex([0, 1, 2, 0, 2, 3]);
    const mat = this.material(tex, o.light);
    mat.transparent = false;
    const mesh = new THREE.Mesh(g, mat);
    mesh.matrixAutoUpdate = false;
    mesh.matrix.copy(m4(m));
    o.group.add(mesh);
    o.text = mesh;
    o.texture = tex;
    o.material = mat;
  }

  private trimTextures(): void {
    const withTex = [...this.objs.values()].filter((o) => o.kind === 'sign' && o.texture);
    if (withTex.length <= this.maxTextures) return;
    withTex.sort((a, b) => a.usedAt - b.usedAt);
    for (const o of withTex.slice(0, withTex.length - this.maxTextures)) this.dropText(o);
  }

  private dropText(o: Obj): void {
    if (o.text) {
      o.group.remove(o.text);
      o.text.geometry.dispose();
    }
    o.material?.dispose();
    o.texture?.dispose();
    o.text = o.material = o.texture = undefined;
  }

  // ------------------------------------------------------------------------------------------------------------ banners

  private sheetPixels(): { data: Uint8ClampedArray; width: number } | null {
    if (this.pixels || !this.bundle) return this.pixels;
    try {
      const img = this.bundle.entity;
      const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(img.width, img.height) : Object.assign(document.createElement('canvas'), { width: img.width, height: img.height });
      const ctx = canvas.getContext('2d') as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
      if (!ctx) return null;
      ctx.drawImage(img, 0, 0);
      this.pixels = { data: ctx.getImageData(0, 0, img.width, img.height).data, width: img.width };
    } catch {
      this.pixels = null;
    }
    return this.pixels;
  }

  /** A 64×64 region of the entity sheet (null when the pack lacks it). */
  private region(name: string): Uint8ClampedArray | null {
    const px = this.sheetPixels();
    const r = this.bundle?.pack.entity.rects[name];
    if (!px || !r || r[2] < 64 || r[3] < 64) return null;
    const out = new Uint8ClampedArray(64 * 64 * 4);
    for (let y = 0; y < 64; y++) out.set(px.data.subarray(((r[1] + y) * px.width + r[0]) * 4, ((r[1] + y) * px.width + r[0] + 64) * 4), y * 256);
    return out;
  }

  private buildBanner(o: Obj): void {
    const base = this.region('entity/banner_base');
    if (!base) return;
    const baseColour = dyeIndex(o.be.base);
    const layers: Array<{ mask: Uint8ClampedArray; rgb: number }> = [];
    const add = (code: string, colour: unknown) => {
      const name = bannerPatternName(code);
      const mask = name ? this.region(`entity/banner/${name}`) : null;
      const c = dyeIndex(colour);
      if (mask) layers.push({ mask, rgb: DYE_RGB[c >= 0 ? c : 0] });
    };
    add('b', baseColour >= 0 ? baseColour : 0);
    for (const [code, colour] of o.be.patterns ?? []) add(code, colour);
    const data = compositeBanner(base, layers);
    const tex = new THREE.DataTexture(new Uint8Array(data.buffer, data.byteOffset, data.byteLength), 64, 64, THREE.RGBAFormat, THREE.UnsignedByteType);
    tex.magFilter = THREE.NearestFilter;
    tex.minFilter = THREE.NearestFilter;
    tex.colorSpace = THREE.NoColorSpace;
    tex.needsUpdate = true;
    const mat = this.material(tex, o.light);
    const standing = o.id === 176;
    // ModelBanner: slate 20×40×1 hanging from (0, −32, 0), the bar on top, the pole when standing (2/3 scale, y and z flipped).
    const frame = m4(chain(frameOf(standing, o.meta, true), scale(k23, -k23, -k23), scale(1 / 16, 1 / 16, 1 / 16)));
    const fixed: Quad[] = [];
    const parts: Part[] = [{ u: 0, v: 42, box: [-10, -32, -1, 20, 2, 2] }];
    if (standing) parts.push({ u: 44, v: 0, box: [-1, -30, -1, 2, 42, 2] });
    for (const part of parts) partQuads(SHEET, part, MODEL_PX, fixed);
    const body = new THREE.Group();
    body.matrixAutoUpdate = false;
    body.matrix.copy(frame);
    const pole = new THREE.Mesh(geometryOf(fixed, 64, 64), mat);
    const slateQuads: Quad[] = [];
    partQuads(SHEET, { u: 0, v: 0, box: [-10, 0, -2, 20, 40, 1] }, MODEL_PX, slateQuads);
    const slate = new THREE.Mesh(geometryOf(slateQuads, 64, 64), mat);
    slate.position.set(0, -32, 0);
    body.add(pole, slate);
    o.group.add(body);
    o.slate = slate;
    o.material = mat;
    o.texture = tex;
  }

  // ------------------------------------------------------------------------------------------------------------ upkeep

  private remove(o: Obj): void {
    this.group.remove(o.group);
    o.group.traverse((c) => {
      if ((c as THREE.Mesh).isMesh) (c as THREE.Mesh).geometry.dispose();
    });
    o.material?.dispose();
    o.texture?.dispose();
    this.objs.delete(o.key);
  }

  /** Only `dim`'s objects show in a view of `dim`. */
  showFor(dim: Dim): void {
    this.group.visible = dim === this.dim;
  }

  get count(): number {
    return this.objs.size;
  }

  clear(): void {
    for (const o of [...this.objs.values()]) this.remove(o);
    this.revision = -1;
  }

  dispose(): void {
    this.clear();
  }
}
