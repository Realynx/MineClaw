/**
 * The lazy chunk boundary for @mineclaw/world-viewer (three.js and its mesh worker): the ONLY module that imports the package
 * at runtime. `loadWorldViewer()` in ./integration.tsx dynamic-imports this file, so pages that never show the world do not
 * download the renderer, and a load failure degrades to the 2D placeholder instead of breaking the page.
 */
import { bundleUrls, itemLookById } from '@mineclaw/world-viewer';

export { WorldThumbnail, WorldViewer, WorldViewerProvider, useWorldRuntime, disposeWorldRuntimes } from '@mineclaw/world-viewer';
/** The body inventory panel's item icons (features/live/inventory): the bundled atlases and the item looks per version. */
export { itemLookById, loadBundle, blockIconAtlas } from '@mineclaw/world-viewer';

/** Mock mode only: the package's fixture world, re-keyed onto the mock hive's bots (see ./mockSource.ts). */
export async function loadMockSource(player?: () => string | null | undefined) {
  const { createMockWorldSource } = await import('./mockSource');
  return createMockWorldSource(player);
}

/**
 * Item and block icons for Story Mode's panels: a 16×16 cell of the committed b1.8.1 sheets (gui/items.png for items 256+,
 * terrain.png for blocks: the side face of a cube, the sprite of a flat block).
 */
export function itemIconResolver(): (id: number, meta: number) => { url: string; cell: number } | null {
  const urls = bundleUrls('b1.8.1');
  const cache = new Map<string, { url: string; cell: number } | null>();
  return (id, meta) => {
    const k = `${id}:${meta}`;
    if (cache.has(k)) return cache.get(k)!;
    const look = itemLookById('b1.8.1', null, id, meta);
    let out: { url: string; cell: number } | null = null;
    if (look?.kind === 'sprite') out = { url: look.atlas === 'items' ? urls.items! : urls.blocks!, cell: look.cell };
    else if (look?.kind === 'cube' && look.faces[3] >= 0) out = { url: urls.blocks!, cell: look.faces[3] };
    cache.set(k, out);
    return out;
  };
}
