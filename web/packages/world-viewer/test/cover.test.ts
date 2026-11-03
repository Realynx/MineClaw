import { describe, expect, it } from 'vitest';
import { CameraRig, COVER_MAX } from '../src/render/cameraRig';

// Top-down and iso views draw the whole ground their picture shows (Story Mode zoomed out): the rig says how far that is.
describe('CameraRig.groundCover', () => {
  it('is the footprint half diagonal plus a margin in top-down; nothing in follow / fly', () => {
    const r = new CameraRig();
    r.isoZoom = 100;
    expect(r.groundCover(16 / 9)).toBe(0);
    r.mode = 'top';
    expect(r.groundCover(16 / 9)).toBeCloseTo(Math.hypot(50, (100 * 16) / 9 / 2) + 12, 5);
    // a tall view is never narrower than its height
    expect(r.groundCover(0.5)).toBeCloseTo(Math.hypot(50, 50) + 12, 5);
  });

  it('reaches farther in iso (the ground is foreshortened) and stops at the cap', () => {
    const r = new CameraRig();
    r.isoZoom = 100;
    r.mode = 'top';
    const top = r.groundCover(1.6);
    r.mode = 'iso';
    expect(r.groundCover(1.6)).toBeGreaterThan(top);
    r.isoZoom = 1000;
    expect(r.groundCover(1.6)).toBe(COVER_MAX);
  });

  it("covers Story Mode's farthest zoom (112, with room to spare) in both of its views on a 16:9 map", () => {
    const r = new CameraRig();
    r.isoZoom = 144;
    for (const mode of ['top', 'iso'] as const) {
      r.mode = mode;
      expect(r.groundCover(16 / 9, Infinity)).toBeLessThanOrEqual(COVER_MAX);
    }
  });
});
