'use client';

import { PREVIEW_FPS_LIMITS } from '@mineclaw/world-viewer/settings';
import { savePreviewFps, usePreviewFps } from './previewSettings';

export function PreviewFpsSlider() {
  const fps = usePreviewFps();
  return (
    <label
      className="flex min-w-44 flex-col gap-1 text-xs text-muted"
      title="Maximum FPS for all preview tiles. Full world views keep their own frame rate."
    >
      <span className="flex items-center justify-between gap-3">
        Preview FPS <span className="font-mono text-fg">{fps}</span>
      </span>
      <input
        aria-label="Maximum preview FPS"
        type="range"
        min={PREVIEW_FPS_LIMITS.min}
        max={PREVIEW_FPS_LIMITS.max}
        step={1}
        value={fps}
        onChange={(event) => savePreviewFps(Number(event.target.value))}
        className="h-1.5 w-full accent-accent"
      />
    </label>
  );
}
