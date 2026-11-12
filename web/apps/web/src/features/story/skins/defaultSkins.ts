/**
 * Steve and Alex for the Skin Browser's previews, cut from the user's own imported game assets (Settings → game assets) by
 * the lazy viewer module. Nothing here is bundled or committed: without an import there is no default preview.
 */
import type { DefaultSkin } from '@mineclaw/contracts';
import { loadWorldViewer } from '@/features/world/integration';

export interface DefaultSkinImage {
  image: HTMLCanvasElement;
  height: 32 | 64;
}

type Version = 'b1.8.1' | '1.12.2';
const cache = new Map<string, Promise<DefaultSkinImage | null>>();

/** The texture for `which` in `version`'s imported pack (b1.8.1 has only mob/char, so its Alex is Steve), or null. */
export function loadDefaultSkin(version: Version, which: DefaultSkin): Promise<DefaultSkinImage | null> {
  const key = `${version}|${version === 'b1.8.1' ? 'char' : which}`;
  let p = cache.get(key);
  if (!p) {
    p = (async () => {
      const mod = await loadWorldViewer();
      if (!mod || typeof document === 'undefined') return null;
      if (!(await mod.readLocalPack(version))) return null;
      const bundle = await mod.loadBundle(version);
      const rect = bundle.pack.entity.rects[version === 'b1.8.1' ? 'mob/char' : `entity/${which}`];
      if (!rect) return null;
      const [x, y, w, h] = rect;
      const height: 32 | 64 = (64 * h) / w >= 64 ? 64 : 32;
      const canvas = document.createElement('canvas');
      canvas.width = 64;
      canvas.height = height;
      // The sheet may hold the texture at a larger scale (an HD pack); draw it down to the 64-wide skin layout.
      canvas.getContext('2d')!.drawImage(bundle.entity, x, y, w, (w * height) / 64, 0, 0, 64, height);
      return { image: canvas, height };
    })().catch(() => null);
    cache.set(key, p);
    // A failed or missing import is asked again next time (the user may import the jar meanwhile).
    void p.then((r) => { if (!r) cache.delete(key); });
  }
  return p;
}
