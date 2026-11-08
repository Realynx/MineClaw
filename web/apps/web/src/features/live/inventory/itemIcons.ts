/**
 * Item icons for the inventory panel, drawn once per version, id and meta into a small 2D canvas and cached as data URLs
 * (no WebGL, nothing per frame): a flat sprite from the items or blocks atlas, or a block item as the inventory's isometric
 * cube (top, south and east faces shaded like vanilla's item renderer). The looks come from the world viewer (`itemLookById`,
 * the same tables the 3D view uses for held and dropped items) through the lazy viewer chunk, so a page without the world
 * still never downloads three.js until a panel asks.
 */
import type { AssetBundle, ItemLook } from '@mineclaw/world-viewer';
import { loadWorldViewer } from '@/features/world/integration';

export type GameVersion = 'b1.8.1' | '1.12.2';
export type IconOf = (id: number, meta: number) => string | null;

const bundles = new Map<
  GameVersion,
  Promise<{ bundle: AssetBundle; look: (id: number, meta: number) => ItemLook | null } | null>
>();

function load(version: GameVersion) {
  let p = bundles.get(version);
  if (!p) {
    p = (async () => {
      const mod = await loadWorldViewer();
      if (!mod) return null;
      const bundle = await mod.loadBundle(version);
      return { bundle, look: (id: number, meta: number) => mod.itemLookById(version, bundle.pack, id, meta) };
    })().catch(() => {
      bundles.delete(version);
      return null;
    });
    bundles.set(version, p);
  }
  return p;
}

const SIZE = 32;
const cache = new Map<string, string | null>();
if (typeof window !== 'undefined') window.addEventListener('mineclaw-assets-changed', () => { bundles.clear(); cache.clear(); });

function gridOf(bundle: AssetBundle, atlas: 'items' | 'blocks'): [number, number] {
  const p = bundle.pack;
  if (atlas === 'items') return [p.items.columns, p.items.rows];
  return p.version === '1.12.2' ? [p.blocks.columns, p.blocks.rows] : [p.terrain.columns, p.terrain.rows];
}

function hex(tint: number): string {
  return `#${tint.toString(16).padStart(6, '0')}`;
}

/** One 16×16 cell of an atlas into `g` (0,0)–(16,16), tinted (multiply, kept to the cell's alpha) and shaded. */
function drawCell(
  g: CanvasRenderingContext2D,
  bundle: AssetBundle,
  atlas: 'items' | 'blocks',
  cell: number,
  tint: number,
  shade: number,
): void {
  const img = atlas === 'items' ? bundle.items : bundle.blocks;
  const [cols, rows] = gridOf(bundle, atlas);
  const cw = img.width / cols,
    ch = img.height / rows;
  g.drawImage(img, (cell % cols) * cw, Math.floor(cell / cols) * ch, cw, ch, 0, 0, 16, 16);
  if (tint) {
    g.fillStyle = hex(tint);
    g.globalCompositeOperation = 'multiply';
    g.fillRect(0, 0, 16, 16);
    g.globalCompositeOperation = 'destination-in';
    g.drawImage(img, (cell % cols) * cw, Math.floor(cell / cols) * ch, cw, ch, 0, 0, 16, 16);
    g.globalCompositeOperation = 'source-over';
  }
  if (shade < 1) {
    g.globalCompositeOperation = 'source-atop';
    g.fillStyle = `rgba(0,0,0,${(1 - shade).toFixed(2)})`;
    g.fillRect(0, 0, 16, 16);
    g.globalCompositeOperation = 'source-over';
  }
}

function render(bundle: AssetBundle, look: ItemLook): string | null {
  const c = document.createElement('canvas');
  c.width = c.height = SIZE;
  const g = c.getContext('2d');
  if (!g) return null;
  g.imageSmoothingEnabled = false;
  const s = SIZE / 32;
  const face = (
    atlas: 'items' | 'blocks',
    cell: number,
    tint: number,
    shade: number,
    m: [number, number, number, number, number, number],
  ) => {
    const tmp = document.createElement('canvas');
    tmp.width = tmp.height = 16;
    const tg = tmp.getContext('2d');
    if (!tg) return;
    tg.imageSmoothingEnabled = false;
    drawCell(tg, bundle, atlas, cell, tint, shade);
    g.setTransform(...m);
    g.drawImage(tmp, 0, 0);
  };
  if (look.kind === 'sprite' && look.entityRect) {
    // A builtin/entity item (the shield): its face from the entity sheet, fitted into the slot, centred.
    const [x, y, w, h] = look.entityRect;
    const k = (SIZE - 2) / Math.max(w, h);
    g.drawImage(bundle.entity, x, y, w, h, (SIZE - w * k) / 2, (SIZE - h * k) / 2, w * k, h * k);
  } else if (look.kind === 'sprite') {
    // A layer beneath first (a potion's tinted liquid), then the sprite.
    if (look.under) face(look.under.atlas, look.under.cell, look.under.tint, 1, [s * 2, 0, 0, s * 2, 0, 0]);
    face(look.atlas, look.cell, look.tint, 1, [s * 2, 0, 0, s * 2, 0, 0]);
  } else {
    const t = (f: number) => (look.tintMask & (1 << f) ? look.tint : 0);
    // Faces: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east. Top rhombus, then the left (south) and right (east) sides.
    face('blocks', look.faces[1], t(1), 1, [s * 0.875, s * 0.4375, -s * 0.875, s * 0.4375, s * 16, s * 2]);
    face('blocks', look.faces[3], t(3), 0.8, [s * 0.875, s * 0.4375, 0, s * 0.875, s * 2, s * 9]);
    face('blocks', look.faces[5], t(5), 0.6, [s * 0.875, -s * 0.4375, 0, s * 0.875, s * 16, s * 16]);
  }
  g.setTransform(1, 0, 0, 1, 0, 0);
  return c.toDataURL('image/png');
}

/**
 * Resolves to an icon lookup for a version once its atlases are loaded (null = the viewer could not load: the panel shows
 * labelled squares instead). Each icon is drawn on first use and cached for the page's life.
 */
export async function iconsFor(version: GameVersion): Promise<IconOf | null> {
  const b = await load(version);
  if (!b) return null;
  return (id, meta) => {
    const k = `${version}:${id}:${meta}`;
    if (cache.has(k)) return cache.get(k)!;
    let url: string | null;
    try {
      const look = b.look(id, meta) ?? (meta ? b.look(id, 0) : null);
      url = look ? render(b.bundle, look) : null;
    } catch {
      url = null;
    }
    cache.set(k, url);
    return url;
  };
}
