// @mineclaw/world-viewer: the browser voxel viewer for MineClaw hives.

export { WorldViewer, type WorldViewerProps, type WorldViewerHandle } from './react/WorldViewer';
export { WorldViewerProvider, WorldThumbnail, useWorldRuntime, disposeWorldRuntimes, type WorldViewerProviderProps, type WorldThumbnailProps } from './react/WorldThumbnail';
export { WorldViewerEngine, type EngineOptions } from './render/engine';
export { WorldRuntime, type RuntimeOptions, type RuntimeMemory, type RuntimeView, type ViewSetup } from './render/runtime';
export { registerThumbnail, ThumbnailView, type ThumbnailViewOptions, type ThumbnailHandle } from './render/thumbnails';
export * from './types';
export type { AtlasMap, AtlasStateEntry, AtlasFace, AtlasQuad, AtlasShape, AtlasLayer, BiomeTintKind } from './atlas/types';
export { ATLAS_FLAG_SPAN, ATLAS_FLAG_WRAP } from './atlas/types';
export { normalizeIndex, normalizeEntities, normalizeOverlays, camelize } from './wire/normalize';
export { decodeMcwc, McwcError, type McwcEntry, type McwcPayload } from './wire/mcwc';
export { cellIndex, unpackSnapshot, parseLight, lightPacked } from './wire/chunk';
export { hashKey } from './wire/hash';
export { blockName, type GameVersion } from './world/blocks';
export type { BotView, EntityView } from './world/entities';
