/**
 * The lazy chunk boundary for @mineclaw/world-viewer (three.js and its mesh worker): the ONLY module that imports the package
 * at runtime. `loadWorldViewer()` in ./integration.tsx dynamic-imports this file, so pages that never show the world do not
 * download the renderer, and a load failure degrades to the 2D placeholder instead of breaking the page.
 */
import { blockIconUrl, bundleUrls, itemLookById, loadBundle, type GameVersion } from '@mineclaw/world-viewer';

export { WorldThumbnail, WorldViewer, WorldViewerProvider, useWorldRuntime, disposeWorldRuntimes } from '@mineclaw/world-viewer';
/** The body inventory panel's item icons (features/live/inventory): the bundled atlases and the item looks per version. */
export { itemLookById, loadBundle, blockIconAtlas, blockIconAtlasAsync } from '@mineclaw/world-viewer';
export { importGameJar, importSoundFolder, readLocalPack, removeLocalPack, ASSETS_CHANGED } from '@mineclaw/world-viewer';
export { ControllerAudio } from '@mineclaw/world-viewer';

/** Mock mode only: the package's fixture world, re-keyed onto the mock hive's bots (see ./mockSource.ts). */
export async function loadMockSource(player?: () => string | null | undefined) {
  const { createMockWorldSource } = await import('./mockSource');
  return createMockWorldSource(player);
}

/**
 * Item and block icons for Story Mode's panels: a 16×16 cell of the committed b1.8.1 sheets (gui/items.png for items 256+,
 * terrain.png for blocks: the side face of a cube, the sprite of a flat block).
 */
export async function itemIconResolver(version: GameVersion = 'b1.8.1'): Promise<(id: number, meta: number) => { url: string; cell: number; columns?: number; rows?: number } | null> {
  const bundle = await loadBundle(version), urls = bundleUrls(version);
  const cache = new Map<string, { url: string; cell: number; columns?: number; rows?: number } | null>();
  return (id, meta) => {
    const k = `${id}:${meta}`;
    if (cache.has(k)) return cache.get(k)!;
    const look = itemLookById(version, bundle.pack, id, meta);
    let out: { url: string; cell: number; columns?: number; rows?: number } | null = null;
    if (id > 0 && id < 256) {
      const url = blockIconUrl(bundle, id, meta);
      if (url) out = { url, cell: 0, columns: 1, rows: 1 };
    }
    else if (look?.kind === 'sprite' && urls[look.atlas]) {
      const grid = look.atlas === 'items' ? bundle.pack.items : bundle.pack.version === '1.12.2' ? bundle.pack.blocks : bundle.pack.terrain;
      out = { url: urls[look.atlas]!, cell: look.cell, columns: grid.columns, rows: grid.rows };
    }
    cache.set(k, out);
    return out;
  };
}
