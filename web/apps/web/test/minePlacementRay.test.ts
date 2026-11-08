import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { minePlanePoint, mineRayBox } from '@/features/studio/mine/placementRay';

beforeEach(() => setPlotMode({ kind: 'mine', terrain: MineTerrain.for('12345', '1.12.2') }));
afterEach(() => setPlotMode({ kind: 'build' }));

describe('mine placement rays', () => {
  it('projects through the ground to the chosen Y without clamping off-plot drops', () => {
    const ray = { origin: { x: 20.5, y: 80, z: 30.2 }, direction: { x: 0.1, y: -1, z: 0 } };
    expect(minePlanePoint(ray, 11)).toEqual([27, 11, 30]);
    expect(minePlanePoint({ ...ray, direction: { x: 10, y: -1, z: 0 } }, 11)).toBeNull();
    expect(minePlanePoint(ray, 0)).toBeNull();
    expect(minePlanePoint(ray, NaN)).toBeNull();
  });

  it('rejects planes behind or parallel to the camera', () => {
    const origin = { x: 20, y: 80, z: 30 };
    expect(minePlanePoint({ origin, direction: { x: 1, y: 0, z: 0 } }, 11)).toBeNull();
    expect(minePlanePoint({ origin, direction: { x: 0, y: 1, z: 0 } }, 11)).toBeNull();
  });

  it('picks cell volumes in front of the camera, including rays parallel to their sides', () => {
    const ray = { origin: { x: 20.5, y: 80, z: 30.5 }, direction: { x: 0, y: -1, z: 0 } };
    expect(mineRayBox(ray, [20, 11, 30], [20, 12, 30])).toBe(67);
    expect(mineRayBox(ray, [21, 11, 30], [21, 12, 30])).toBeNull();
    expect(mineRayBox(ray, [20, 81, 30], [20, 82, 30])).toBeNull();
  });
});
