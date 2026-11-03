// @mineclaw/world-viewer: the browser voxel viewer for MineClaw hives.

export { WorldViewer, type WorldViewerProps, type WorldViewerHandle } from './react/WorldViewer';
export { WorldViewerProvider, WorldThumbnail, useWorldRuntime, disposeWorldRuntimes, type WorldViewerProviderProps, type WorldThumbnailProps } from './react/WorldThumbnail';
export { WorldViewerEngine, type EngineOptions } from './render/engine';
export { aimAt, botAlong, rayBox, outlineOf, type AimTarget, type AimBot } from './render/aim';
export { WorldRuntime, type RuntimeOptions, type RuntimeMemory, type RuntimeView, type ViewSetup } from './render/runtime';
export { registerThumbnail, ThumbnailView, type ThumbnailViewOptions, type ThumbnailHandle } from './render/thumbnails';
export { BubbleLayer, ChatBubbles, BUBBLE, wrapText, cleanText, holdMs, bubbleAlpha, findSpeaker, linePx, stackOffsets, type BubbleKind, type BubbleMode, type BubbleMessage, type ShownBubble, type Speaker } from './render/bubbles';
export * from './types';
export { loadBundle, bundleUrls, type AssetBundle, type Pack, type Pack1122, type PackBeta, type PackAnim, type PackBiome } from './assets/bundle';
export { normalizeIndex, normalizeEntities, normalizeOverlays, camelize } from './wire/normalize';
export { decodeMcwc, McwcError, type McwcEntry, type McwcPayload } from './wire/mcwc';
export { cellIndex, unpackSnapshot, parseLight, lightPacked } from './wire/chunk';
export { hashKey } from './wire/hash';
export { blockName, type GameVersion } from './world/blocks';
export { itemLookById, itemName, type ItemLook } from './render/entities/items';
export { blockIconUrl, blockIconAtlas, drawBlockIcon, type IconAtlas } from './render/icons';
export type { BotView, EntityView } from './world/entities';
