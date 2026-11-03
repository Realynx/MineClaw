// Text labels as camera-facing sprites drawn on a canvas (one texture per distinct text/style, reference-counted).

import * as THREE from 'three';

export interface LabelStyle {
  fg: string;
  bg: string;
  /** Second, smaller line (held item, status). */
  sub?: string | null;
  border?: string | null;
}

interface CacheEntry {
  tex: THREE.CanvasTexture;
  w: number;
  h: number;
  refs: number;
}

const PX = 28; // font pixels per line on the canvas
const WORLD_PER_PX = 0.012; // world units per canvas pixel at scale 1

export class LabelFactory {
  private cache = new Map<string, CacheEntry>();

  private key(text: string, s: LabelStyle): string {
    return `${text}\u0000${s.fg}\u0000${s.bg}\u0000${s.sub ?? ''}\u0000${s.border ?? ''}`;
  }

  private draw(text: string, s: LabelStyle): CacheEntry {
    const canvas = typeof OffscreenCanvas !== 'undefined' && typeof document === 'undefined' ? new OffscreenCanvas(4, 4) : document.createElement('canvas');
    const ctx = canvas.getContext('2d') as CanvasRenderingContext2D | null;
    const font = `600 ${PX}px system-ui, -apple-system, "Segoe UI", sans-serif`;
    const subFont = `500 ${Math.round(PX * 0.72)}px system-ui, -apple-system, "Segoe UI", sans-serif`;
    let w = 64;
    if (ctx) {
      ctx.font = font;
      w = Math.ceil(ctx.measureText(text).width);
      if (s.sub) {
        ctx.font = subFont;
        w = Math.max(w, Math.ceil(ctx.measureText(s.sub).width));
      }
    }
    const pad = 10;
    const h = Math.round(PX * 1.35) + (s.sub ? Math.round(PX * 0.95) : 0) + 4;
    canvas.width = w + pad * 2;
    canvas.height = h;
    if (ctx) {
      ctx.fillStyle = s.bg;
      const r = 8;
      ctx.beginPath();
      ctx.roundRect(1, 1, canvas.width - 2, canvas.height - 2, r);
      ctx.fill();
      if (s.border) {
        ctx.strokeStyle = s.border;
        ctx.lineWidth = 3;
        ctx.stroke();
      }
      ctx.fillStyle = s.fg;
      ctx.font = font;
      ctx.textBaseline = 'top';
      ctx.fillText(text, pad, 5);
      if (s.sub) {
        ctx.font = subFont;
        ctx.globalAlpha = 0.85;
        ctx.fillText(s.sub, pad, Math.round(PX * 1.3) + 2);
        ctx.globalAlpha = 1;
      }
    }
    const tex = new THREE.CanvasTexture(canvas as HTMLCanvasElement);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.minFilter = THREE.LinearFilter;
    tex.generateMipmaps = false;
    return { tex, w: canvas.width, h: canvas.height, refs: 0 };
  }

  /** A sprite showing `text`; its bottom-centre sits on the sprite's position. `onTop` = never hidden by terrain. */
  create(text: string, s: LabelStyle, onTop = false, scale = 1): THREE.Sprite {
    const k = this.key(text, s);
    let e = this.cache.get(k);
    if (!e) this.cache.set(k, (e = this.draw(text, s)));
    e.refs++;
    const mat = new THREE.SpriteMaterial({ map: e.tex, transparent: true, depthTest: !onTop, depthWrite: false, sizeAttenuation: true });
    const sp = new THREE.Sprite(mat);
    sp.center.set(0.5, 0);
    sp.scale.set(e.w * WORLD_PER_PX * scale, e.h * WORLD_PER_PX * scale, 1);
    sp.renderOrder = onTop ? 10 : 5;
    sp.userData.labelKey = k;
    return sp;
  }

  release(sp: THREE.Sprite): void {
    const k = sp.userData.labelKey as string | undefined;
    (sp.material as THREE.SpriteMaterial).dispose();
    if (!k) return;
    const e = this.cache.get(k);
    if (!e) return;
    if (--e.refs <= 0) {
      e.tex.dispose();
      this.cache.delete(k);
    }
  }

  dispose(): void {
    for (const e of this.cache.values()) e.tex.dispose();
    this.cache.clear();
  }
}
