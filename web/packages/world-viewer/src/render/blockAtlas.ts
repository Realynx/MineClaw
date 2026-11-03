// The block atlas texture every section pass samples (the bundled 1.12.2 blocks.png with its tile-entity sheets, or the
// b1.8.1 terrain.png), and its animation pass on the main thread: at the game tick rate (20 Hz, or `animHz`), only the
// animated cells are rewritten in place (texSubImage2D through three's copyTextureToTexture from CPU pixels; never a full
// re-upload). 1.12.2 cells step through their anim.png strips (.mcmeta frame times, interpolated blends where flagged);
// b1.8.1 cells take the procedural TextureFX frames (water, lava, fire, portal). `animCellsPerTick` caps the cells written
// per tick (round-robin) and `setVisible` restricts the pass to the cells the visible sections sample (SectionMesh.animCells).

import * as THREE from 'three';
import type { AssetBundle, Pack1122 } from '../assets/bundle';
import { SpriteAnimator, type CellWriter } from '../atlas/anim';
import { betaEffects, type TextureFX } from '../atlas/betaFx';
import type { ChunkUniforms } from './chunkMaterial';

/** RGBA pixels of a bitmap (unpremultiplied), or null where no 2D canvas is available. */
function pixelsOf(img: ImageBitmap): Uint8ClampedArray | null {
  try {
    const canvas: OffscreenCanvas | HTMLCanvasElement =
      typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(img.width, img.height) : Object.assign(document.createElement('canvas'), { width: img.width, height: img.height });
    const ctx = canvas.getContext('2d') as CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D | null;
    if (!ctx) return null;
    ctx.drawImage(img, 0, 0);
    return ctx.getImageData(0, 0, img.width, img.height).data;
  } catch {
    return null;
  }
}

export class BlockAtlas {
  readonly texture: THREE.Texture;
  readonly columns: number;
  readonly rows: number;
  /** Animation updates per second (the game's 20; a software-rendered profile may lower it). */
  animHz = 20;
  /** Most animated cells rewritten per update (0 = all). */
  animCellsPerTick = 0;
  /** Animated cells written so far (diagnostics). */
  writes = 0;
  private visible: Set<number> | null = null;
  private lastTick = -1;
  private readonly sprites: SpriteAnimator | null = null;
  private readonly sheetTex: THREE.DataTexture | null = null;
  private readonly fx: TextureFX[] = [];
  private fxCursor = 0;
  private readonly scratch = new Map<number, THREE.DataTexture>();
  private readonly region = new THREE.Box2(new THREE.Vector2(), new THREE.Vector2(16, 16));
  private readonly at = new THREE.Vector2();

  constructor(readonly bundle: AssetBundle) {
    const tex = new THREE.Texture(bundle.blocks as unknown as TexImageSource);
    tex.flipY = false;
    tex.magFilter = THREE.NearestFilter;
    tex.minFilter = THREE.NearestFilter;
    tex.generateMipmaps = false;
    tex.colorSpace = THREE.NoColorSpace;
    tex.needsUpdate = true;
    this.texture = tex;
    if (bundle.pack.version === '1.12.2') {
      const pack = bundle.pack as Pack1122;
      this.columns = pack.blocks.columns;
      this.rows = pack.blocks.rows;
      const px = bundle.anim ? pixelsOf(bundle.anim) : null;
      if (bundle.anim && px) {
        this.sheetTex = new THREE.DataTexture(new Uint8Array(px.buffer, px.byteOffset, px.byteLength), bundle.anim.width, bundle.anim.height, THREE.RGBAFormat, THREE.UnsignedByteType);
        this.sprites = new SpriteAnimator(
          pack.anims.filter((a) => a.atlas === 'blocks'),
          { data: px, width: bundle.anim.width },
          this.columns,
        );
      }
    } else {
      this.columns = 16;
      this.rows = 16;
      this.fx = betaEffects();
    }
  }

  /** The atlas grid and texture on the section uniforms. */
  bind(u: ChunkUniforms): void {
    this.uniforms = u;
    u.uAtlas.value = this.texture;
    u.uGrid.value.set(this.columns, this.rows);
    u.uInset.value = 0.5 / 16;
    if (this.bundle.pack.version === '1.12.2') {
      const sh = (this.bundle.pack as Pack1122).blocks.sheets ?? {};
      const set = (v: { set(a: number, b: number, c: number, d: number): unknown }, s?: [number, number, number, number]) => s && v.set(s[0], s[1], s[2], s[3]);
      set(u.uStarSky.value, sh['environment/end_sky']);
      set(u.uStarPortal.value, sh['entity/end_portal']);
    }
  }

  private uniforms: ChunkUniforms | null = null;
  /** Starfield layers drawn (the client's 16; fewer under software rendering). */
  starLayers = 16;

  /** Restrict the animation pass to these cells (row * columns + column); null = every animated cell. */
  setVisible(cells: Iterable<number> | null): void {
    this.visible = cells ? new Set(cells) : null;
  }

  /** Cells this atlas animates. */
  animatedCells(): number[] {
    if (this.sprites) return this.sprites.anims.map((a) => a.cell[1] * this.columns + a.cell[0]);
    return this.fx.map((f) => (f.tile >> 4) * 16 + (f.tile & 15));
  }

  /** Called every frame: advances the animations when a new (rate-limited) game tick has begun. */
  update(renderer: THREE.WebGLRenderer, nowMs: number): void {
    if (this.uniforms) {
      this.uniforms.uTime.value = nowMs / 1000;
      this.uniforms.uStarLayers.value = this.starLayers;
    }
    const hz = Math.max(1, Math.min(20, this.animHz || 20));
    const tick = Math.floor((nowMs / 1000) * hz);
    if (tick === this.lastTick) return;
    const steps = this.lastTick < 0 ? 1 : Math.min(4, tick - this.lastTick);
    this.lastTick = tick;
    const gameTick = Math.floor((nowMs / 1000) * 20);
    const writer: CellWriter = {
      fromSheet: (c, r, sx, sy, size) => {
        if (!this.sheetTex) return;
        this.region.min.set(sx, sy);
        this.region.max.set(sx + size, sy + size);
        this.at.set(c * 16, r * 16);
        renderer.copyTextureToTexture(this.sheetTex, this.texture, this.region, this.at);
        this.writes++;
      },
      fromPixels: (c, r, px, size) => {
        let tex = this.scratch.get(size);
        if (!tex) this.scratch.set(size, (tex = new THREE.DataTexture(new Uint8Array(size * size * 4), size, size, THREE.RGBAFormat, THREE.UnsignedByteType)));
        (tex.image.data as Uint8Array).set(px);
        this.at.set(c * 16, r * 16);
        renderer.copyTextureToTexture(tex, this.texture, null, this.at);
        this.writes++;
      },
    };
    if (this.sprites) {
      this.sprites.update(gameTick, writer, { maxCells: this.animCellsPerTick, visible: this.visible });
      return;
    }
    // b1.8.1: every effect advances each tick (its state is a recurrence); uploads honour the budget and visibility.
    for (const f of this.fx) for (let s = 0; s < steps * (20 / hz); s++) f.tick();
    const n = this.fx.length;
    const max = this.animCellsPerTick > 0 ? this.animCellsPerTick : n;
    let done = 0;
    for (let i = 0; i < n && done < max; i++) {
      const f = this.fx[(this.fxCursor + i) % n];
      const cell = (f.tile >> 4) * 16 + (f.tile & 15);
      if (this.visible && !this.visible.has(cell)) continue;
      writer.fromPixels(f.tile & 15, f.tile >> 4, f.pixels, 16);
      done++;
    }
    this.fxCursor = (this.fxCursor + done) % n;
  }

  dispose(): void {
    this.texture.dispose();
    for (const t of this.scratch.values()) t.dispose();
    this.sheetTex?.dispose();
  }
}
