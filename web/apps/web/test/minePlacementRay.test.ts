import { PerspectiveCamera, Raycaster, Vector2 } from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { plotMode, setPlotMode, type V3 } from '@/features/studio/canvas';
import { levelPoint, nearestHit, rayBox, rayFromPointer } from '@/features/studio/components/levelRay';
import { prepareOutline } from '@/features/studio/components/outline';
import { volumeOutline } from '@/features/studio/mine/minePreview';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { minePlanePoint, mineRayBox } from '@/features/studio/mine/placementRay';
import { Overlay3D } from '@/features/studio/overlay3d';

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

describe('shared component rays and outlines', () => {
  const down = { origin: { x: 20.5, y: 100, z: 30.5 }, direction: { x: 0, y: -1, z: 0 } };

  it('meets a level in canvas cells on a plot whose ground is not at world 0', () => {
    setPlotMode({ kind: 'build' });
    expect(plotMode().baseY).toBe(64);
    expect(levelPoint(down, 2)).toEqual([20, 2, 30]);
    expect(levelPoint({ ...down, origin: { ...down.origin, y: 60 } }, 2)).toBeNull();
    expect(levelPoint({ ...down, origin: { ...down.origin, x: -3 } }, 2)).toBeNull();
    // A mine's ground is at world 0, so its levels are world heights, as before.
    setPlotMode({ kind: 'mine', terrain: MineTerrain.for('12345', '1.12.2') });
    expect(levelPoint(down, 11)).toEqual(minePlanePoint(down, 11));
    expect(levelPoint(down, 11)).toEqual([20, 11, 30]);
  });

  it('picks the nearest component box along a world ray, the first on a tie, in canvas cells', () => {
    setPlotMode({ kind: 'build' });
    const boxes = (item: { id: string; boxes: [V3, V3][] }) => item.boxes;
    const low = {
      id: 'low',
      boxes: [
        [
          [20, 0, 30],
          [20, 0, 30],
        ],
      ] as [V3, V3][],
    };
    const high = {
      id: 'high',
      boxes: [
        [
          [19, 4, 29],
          [21, 5, 31],
        ],
      ] as [V3, V3][],
    };
    expect(nearestHit(down, [low, high], boxes)).toEqual({ id: 'high', distance: 100 - 64 - 6 });
    expect(nearestHit(down, [low], boxes)).toEqual({ id: 'low', distance: 100 - 64 - 1 });
    expect(nearestHit(down, [{ ...high, id: 'twin' }, high], boxes)!.id).toBe('twin');
    expect(
      nearestHit(
        down,
        [
          {
            id: 'aside',
            boxes: [
              [
                [0, 0, 0],
                [1, 1, 1],
              ],
            ] as [V3, V3][],
          },
        ],
        boxes,
      ),
    ).toBeNull();
    expect(mineRayBox).toBe(rayBox);
  });

  it('casts the camera ray under a client point, and none before the view has a size', () => {
    const surface = document.createElement('canvas');
    const rect = { left: 100, top: 50, width: 800, height: 600 };
    const measure = vi
      .spyOn(surface, 'getBoundingClientRect')
      .mockReturnValue({ ...rect, right: 900, bottom: 650, x: 100, y: 50, toJSON: () => ({}) });
    const camera = new PerspectiveCamera(60, 800 / 600, 0.1, 300);
    camera.position.set(32, 90, 80);
    camera.lookAt(32, 20, 32);
    camera.updateMatrixWorld();
    const mod = { Raycaster, Vector2 };
    const ray = rayFromPointer(mod, { surface, camera }, 500, 350)!;
    const centre = new Raycaster();
    centre.setFromCamera(new Vector2(0, 0), camera);
    expect(ray.ray.origin.distanceTo(centre.ray.origin)).toBeLessThan(1e-9);
    expect(ray.ray.direction.angleTo(centre.ray.direction)).toBeLessThan(1e-9);
    expect(rayFromPointer(mod, null, 500, 350)).toBeNull();
    measure.mockReturnValue({ ...rect, width: 0, right: 100, bottom: 650, x: 100, y: 50, toJSON: () => ({}) });
    expect(rayFromPointer(mod, { surface, camera }, 500, 350)).toBeNull();
  });

  it('prepares an outline from cells relative to the corner', () => {
    expect(prepareOutline([])).toEqual({ bounds: null, positions: new Float32Array(), cells: 0, simplified: false });
    const outline = prepareOutline([
      [0, 0, 0],
      [1, 0, 0],
      [1, 1, 0],
    ]);
    expect(outline.bounds).toEqual({ min: [0, 0, 0], max: [1, 1, 0] });
    expect(outline.cells).toBe(3);
    expect(outline.simplified).toBe(false);
    expect(outline.positions).toEqual(
      volumeOutline([
        [0, 0, 0],
        [1, 0, 0],
        [1, 1, 0],
      ]),
    );
  });

  it('marks component ports through the generic call and the mine alias alike', () => {
    const overlay = new Overlay3D(64);
    overlay.setComponentPorts(
      [
        [1, 0, 1],
        [3, 0, 1],
      ],
      [[3, 0, 1]],
    );
    expect(overlay.portCounts).toEqual([2, 1]);
    overlay.setMinePorts([[1, 0, 1]]);
    expect(overlay.portCounts).toEqual([1, 0]);
    overlay.setComponentPorts(null, [[3, 0, 1]]);
    expect(overlay.portCounts).toEqual([0, 0]);
  });
});
