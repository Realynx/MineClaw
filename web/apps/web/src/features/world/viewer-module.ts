/**
 * ===== THE SINGLE INTEGRATION POINT for @mineclaw/world-viewer =====
 *
 * The world viewer package (three.js, renders the world in the browser) is built in a parallel branch and is not installed
 * here. Everything in the app reaches it only through `loadWorldViewer()` in ./integration.ts, which dynamic-imports THIS
 * module. To wire it after the merge:
 *
 *   1. add "@mineclaw/world-viewer": "*" to apps/web/package.json dependencies;
 *   2. replace the body of this file with:
 *
 *        export * from '@mineclaw/world-viewer';
 *        export const available = true;
 *
 * Expected exports (adapters in ./integration.ts tolerate small naming differences):
 *   - WorldViewerProvider  { source: WorldSource; children }      one shared WebGL renderer for all views
 *   - WorldThumbnail       { source: WorldSource; follow: string } a small live 3D view (multi-view mode)
 *   - WorldViewer          { source: WorldSource; follow?: string; dim?: number; camera?: 'follow' | 'free' }
 */
export const available = false;
