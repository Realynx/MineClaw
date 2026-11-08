/** A shared ceiling for thumbnail rendering only. Interactive views and world sync never read it. */
export const DEFAULT_PREVIEW_FPS = 3;
export const PREVIEW_FPS_LIMITS = { min: 1, max: 60 } as const;
let cap = DEFAULT_PREVIEW_FPS;
const listeners = new Set<() => void>();

export function clampPreviewFps(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(PREVIEW_FPS_LIMITS.min, Math.min(PREVIEW_FPS_LIMITS.max, Math.round(value)))
    : DEFAULT_PREVIEW_FPS;
}

export const getPreviewFps = () => cap;
export function setPreviewFps(value: number): void {
  const next = clampPreviewFps(value);
  if (next === cap) return;
  cap = next;
  for (const notify of listeners) notify();
}
export function subscribePreviewFps(notify: () => void): () => void {
  listeners.add(notify);
  return () => listeners.delete(notify);
}

export function thumbnailFrameRate(requested: number | undefined, profileScale: number): number {
  const wanted = requested !== undefined && Number.isFinite(requested) ? Math.max(0.5, requested) : cap;
  return Math.min(cap, Math.max(0.5, wanted * profileScale));
}
