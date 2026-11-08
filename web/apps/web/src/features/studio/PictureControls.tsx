'use client';

import type { WorldViewerEngine, WorldViewerHandle } from '@mineclaw/world-viewer';
import { Camera } from 'lucide-react';
import { useState, type RefObject } from 'react';
import { errorText } from '@/lib/api/client';
import type { V3 } from './canvas';

export type PictureBounds = { min: V3; max: V3 };

/** A sphere fit keeps a tall or wide design entirely inside the narrower field of view. */
export function pictureDistance(bounds: PictureBounds, aspect: number, fov = 70): number {
  const radius = Math.hypot(...bounds.max.map((n, i) => n - bounds.min[i]! + 1)) / 2;
  const vertical = (fov * Math.PI) / 360;
  const horizontal = Math.atan(Math.tan(vertical) * Math.max(0.1, aspect));
  return Math.max(5, (radius / Math.sin(Math.min(vertical, horizontal))) * 1.12);
}

export function PictureControls(p: {
  viewer: RefObject<WorldViewerHandle | null>;
  bounds: PictureBounds | null;
  canWrite: boolean;
  busy: boolean;
  designId: number | null;
  dirty: boolean;
  baseY: number;
  defaultOpen?: boolean;
  onCapture: () => Promise<void>;
}) {
  const [taking, setTaking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const look = (yaw: number) => {
    const e = p.viewer.current?.engine();
    if (e && p.bounds) positionPictureCamera(e, p.bounds, p.baseY, yaw);
  };
  return (
    <details
      open={p.defaultOpen || undefined}
      className="border-b border-border bg-surface-1 px-3 py-1 text-xs"
      data-testid="picture-controls"
    >
      <summary className="w-fit cursor-pointer text-muted">Design picture · saved from your camera</summary>
      <div className="mt-1 flex flex-wrap items-center gap-2 pb-1">
        <span className="text-muted">View from</span>
        {(
          [
            ['North', 0],
            ['East', 90],
            ['South', 180],
            ['West', 270],
          ] as const
        ).map(([name, yaw]) => (
          <button
            key={name}
            type="button"
            disabled={!p.bounds || taking}
            onClick={() => look(yaw)}
            className="rounded border border-border px-2 py-1 hover:bg-surface-2"
          >
            {name}
          </button>
        ))}
        <button
          type="button"
          disabled={!p.canWrite || p.busy || taking || !p.designId || p.dirty}
          title={
            p.dirty
              ? 'Save your design changes to update its picture.'
              : 'Update this saved design’s picture without making a revision.'
          }
          className="flex items-center gap-1 rounded bg-accent px-2 py-1 text-accent-fg disabled:opacity-50"
          onClick={() => {
            setTaking(true);
            setError(null);
            void p
              .onCapture()
              .catch((e: unknown) => setError(errorText(e)))
              .finally(() => setTaking(false));
          }}
        >
          <Camera className="size-3" />
          {taking ? 'Taking picture…' : 'Update picture'}
        </button>
        <span className="text-[11px] text-muted">
          Every save captures the current view. Existing designs can update just the picture.
        </span>
        {error ? (
          <span role="alert" className="text-danger">
            {error}
          </span>
        ) : null}
      </div>
    </details>
  );
}

/** Direction shortcuts operate on the imperative viewer from a click, never during React rendering. */
export function positionPictureCamera(e: WorldViewerEngine, bounds: PictureBounds, baseY: number, yaw: number): void {
  e.rig.flyYaw = yaw;
  const center = bounds.min.map((v, i) => (v + bounds.max[i]!) / 2) as V3;
  center[1] += baseY;
  e.focus(...center, { pitch: 25, distance: pictureDistance(bounds, e.camera.aspect, e.camera.fov) });
  // Capture the selected pose even when the user clicks Update picture immediately after the direction.
  e.rig.beginTransition(e.camera, performance.now(), 0);
}
