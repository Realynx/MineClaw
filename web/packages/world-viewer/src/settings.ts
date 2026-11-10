// @mineclaw/world-viewer/settings: the viewer quality store, the renderer probe and the quality profiles, without three.js
// (a settings panel imports this; the renderer reads the same store when it loads).

export {
  DEFAULT_VIEWER_QUALITY,
  QUALITY_LIMITS,
  setViewerQuality,
  resetViewerQuality,
  getViewerQuality,
  viewerQualityRev,
  subscribeViewerQuality,
  effectiveProfile,
  entityReach,
  FrameLimiter,
  viewerStatus,
  type ViewerQuality,
  type EntityDistance,
  type ViewerStatus,
} from './render/viewerQuality';
export { motionLine, type MotionStatus } from './render/viewerQuality';
export { PROFILES, probeRenderer, probeRendererOnce, recommendedPreset, type QualityName, type QualityProfile, type RendererProbe } from './render/quality';
export { DEFAULT_PREVIEW_FPS, PREVIEW_FPS_LIMITS, clampPreviewFps, getPreviewFps, setPreviewFps, subscribePreviewFps } from './render/previewRate';
export { holoState, planHolograms, HOLO_CAP, HOLO_LOD_PX, HOLO_STATES, HOLO_STYLES, type HoloCell, type HoloState, type HoloStyle, type HoloPlan, type HoloBox } from './render/holoPlan';
