/**
 * The Design Studio's lazy chunk: three.js, the world viewer, the studio world and its 3D overlay. Only `StudioPage` imports
 * it (dynamically), like features/world/viewer-module.ts for the live views.
 */
export { WorldViewer, WorldViewerProvider, blockIconUrl, blockIconAtlas, loadBundle } from '@mineclaw/world-viewer';
export { StudioWorld } from './studioWorld';
export { Overlay3D } from './overlay3d';
export { Raycaster, Vector2, Vector3 } from 'three';
