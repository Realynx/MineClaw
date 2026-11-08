import { afterEach, expect, it, vi } from 'vitest';
import { DEFAULT_PREVIEW_FPS, getPreviewFps, setPreviewFps, thumbnailFrameRate } from '../src/render/previewRate';
import { getViewerQuality } from '../src/render/viewerQuality';
import { ThumbnailView } from '../src/render/thumbnails';
import type { WorldRuntime } from '../src/render/runtime';

afterEach(() => { setPreviewFps(DEFAULT_PREVIEW_FPS); vi.unstubAllGlobals(); });

it('changes every mounted preview schedule while preserving full-view quality and network interest', () => {
  vi.stubGlobal('document', { visibilityState: 'visible' });
  const rt = {
    store: { bot: () => ({ name: 'pawn', dim: 0, x: 0, y: 64, z: 0, yaw: 0 }) },
    quality: { state: { profile: { thumbFpsScale: 1 } } },
    takeSlot: vi.fn(() => true), thumbSize: (w: number, h: number) => [w, h],
  } as unknown as WorldRuntime;
  const canvas = () => ({ getContext: () => ({}), clientWidth: 200, clientHeight: 100, width: 200, height: 100 }) as unknown as HTMLCanvasElement;
  const a = new ThumbnailView(rt, canvas(), 'pawn', { xray: false });
  const b = new ThumbnailView(rt, canvas(), 'pawn', { fps: 60, xray: false });
  const full = getViewerQuality();
  const interest = a.interest();
  setPreviewFps(2);
  for (const view of [a, b]) {
    expect(view.frame(rt, 0)).not.toBeNull();
    expect(view.frame(rt, 250)).toBeNull();
    expect(view.frame(rt, 500)).not.toBeNull();
  }
  setPreviewFps(20);
  for (const view of [a, b]) {
    expect(view.frame(rt, 540)).toBeNull();
    expect(view.frame(rt, 550)).not.toBeNull();
  }
  expect(getViewerQuality()).toBe(full);
  expect(a.interest()).toEqual(interest);
  expect(thumbnailFrameRate(undefined, 1)).toBe(20);
  expect(thumbnailFrameRate(3, 1)).toBe(3); // explicit slower preview remains allowed
});

it('clamps bad preview limits and retains the renderer profile’s ability to lower the rate', () => {
  setPreviewFps(999);
  expect(getPreviewFps()).toBe(60);
  setPreviewFps(-1);
  expect(getPreviewFps()).toBe(1);
  setPreviewFps(Number.NaN);
  expect(getPreviewFps()).toBe(DEFAULT_PREVIEW_FPS);
  expect(thumbnailFrameRate(undefined, 0.5)).toBe(1.5);
});
