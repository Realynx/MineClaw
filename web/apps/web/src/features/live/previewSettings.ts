'use client';

import { useSyncExternalStore } from 'react';
import {
  clampPreviewFps,
  DEFAULT_PREVIEW_FPS,
  getPreviewFps,
  setPreviewFps,
  subscribePreviewFps,
} from '@mineclaw/world-viewer/settings';

export const PREVIEW_FPS_KEY = 'mcx-preview-fps';
let started = false;

/** The shell applies this before any preview mounts; subsequent changes take effect on the next thumbnail frame. */
export function initPreviewSettings(): void {
  if (started || typeof window === 'undefined') return;
  started = true;
  try {
    setPreviewFps(clampPreviewFps(JSON.parse(localStorage.getItem(PREVIEW_FPS_KEY) ?? 'null')));
  } catch {
    setPreviewFps(DEFAULT_PREVIEW_FPS);
  }
  window.addEventListener('storage', (event) => {
    if (event.key !== PREVIEW_FPS_KEY && event.key !== null) return;
    try {
      setPreviewFps(clampPreviewFps(JSON.parse(event.newValue ?? 'null')));
    } catch {
      setPreviewFps(DEFAULT_PREVIEW_FPS);
    }
  });
}

export function savePreviewFps(value: number): void {
  setPreviewFps(value);
  try {
    localStorage.setItem(PREVIEW_FPS_KEY, JSON.stringify(getPreviewFps()));
  } catch {
    /* Private storage still allows the current session's setting. */
  }
}

export function usePreviewFps(): number {
  return useSyncExternalStore(subscribePreviewFps, getPreviewFps, () => DEFAULT_PREVIEW_FPS);
}
