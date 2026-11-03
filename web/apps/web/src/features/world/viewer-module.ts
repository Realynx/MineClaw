/**
 * The lazy chunk boundary for @mineclaw/world-viewer (three.js and its mesh worker): the ONLY module that imports the package
 * at runtime. `loadWorldViewer()` in ./integration.tsx dynamic-imports this file, so pages that never show the world do not
 * download the renderer, and a load failure degrades to the 2D placeholder instead of breaking the page.
 */
export { WorldThumbnail, WorldViewer, WorldViewerProvider, useWorldRuntime } from '@mineclaw/world-viewer';

/** Mock mode only: the package's fixture world, re-keyed onto the mock hive's bots (see ./mockSource.ts). */
export async function loadMockSource() {
  const { createMockWorldSource } = await import('./mockSource');
  return createMockWorldSource();
}
