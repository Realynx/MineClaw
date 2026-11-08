import { PerspectiveCamera, Raycaster, Vector2, Vector3, type Mesh } from 'three';
import { afterEach, describe, expect, it } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { Overlay3D } from '@/features/studio/overlay3d';

const overlays: Overlay3D[] = [];
afterEach(() => {
  for (const overlay of overlays.splice(0)) overlay.dispose();
  setPlotMode({ kind: 'build' });
});

function scene(distance = 40) {
  setPlotMode({ kind: 'mine', terrain: { state: () => 16 } });
  const overlay = new Overlay3D(64);
  overlays.push(overlay);
  overlay.setSelection({ min: [30, 20, 30], max: [34, 21, 30] });
  const camera = new PerspectiveCamera(60, 800 / 600, 0.1, 1000);
  const aim = overlay.gizmo.position.clone();
  const position = (away: number) => {
    camera.position.copy(aim).add(new Vector3(0, 0, away));
    camera.lookAt(aim);
    camera.updateMatrixWorld();
  };
  position(distance);
  const pick = (x: number, y: number) => {
    const ray = new Raycaster();
    ray.setFromCamera(new Vector2(x * 2 / 800, y * 2 / 600), camera);
    return overlay.pickHandle(ray);
  };
  return { overlay, camera, position, pick };
}

describe('mine component move handles', () => {
  it('retains its screen size across zoom, viewport changes and camera FOV without rebuilding geometry', () => {
    const { overlay, camera, position } = scene();
    const geometries: unknown[] = [];
    overlay.gizmo.traverse((object) => { if ((object as Mesh).geometry) geometries.push((object as Mesh).geometry); });
    const projectedLength = (height: number) => {
      overlay.updateGizmoScale(camera, height);
      const origin = overlay.gizmo.position.clone().project(camera);
      const end = overlay.gizmo.position.clone().add(new Vector3(2.1 * overlay.gizmo.scale.x, 0, 0)).project(camera);
      return (end.x - origin.x) * height * camera.aspect / 2;
    };
    expect(projectedLength(600)).toBeCloseTo(80);
    const firstScale = overlay.gizmo.scale.x;
    position(80);
    expect(projectedLength(600)).toBeCloseTo(80);
    expect(overlay.gizmo.scale.x).toBeCloseTo(firstScale * 2);
    expect(projectedLength(900)).toBeCloseTo(80);
    camera.fov = 35;
    camera.updateProjectionMatrix();
    expect(projectedLength(900)).toBeCloseTo(80);
    const after: unknown[] = [];
    overlay.gizmo.traverse((object) => { if ((object as Mesh).geometry) after.push((object as Mesh).geometry); });
    expect(after).toEqual(geometries);
  });

  it('accepts a near miss beside the thin shaft but does not claim empty space around the component', () => {
    const { overlay, camera, position, pick } = scene();
    for (const distance of [10, 40, 90]) {
      position(distance);
      overlay.updateGizmoScale(camera, 600);
      expect(pick(35, 0)).toBe('x');
      expect(pick(35, 5)).toBe('x');
      expect(pick(35, 12)).toBeNull();
      expect(pick(110, 0)).toBeNull();
    }
  });

  it('leaves builder handles at their original size and pick width until screen sizing is enabled', () => {
    const { overlay, pick } = scene(10);
    expect(overlay.gizmo.scale.toArray()).toEqual([1, 1, 1]);
    expect(pick(40, 0)).toBe('x');
    expect(pick(40, 5)).toBeNull();
  });

  it('never picks hidden handles or captures them during a design thumbnail', async () => {
    const { overlay, camera, pick } = scene();
    overlay.updateGizmoScale(camera, 600);
    expect(pick(35, 0)).toBe('x');
    await overlay.withoutGuides(() => { expect(pick(35, 0)).toBeNull(); return Promise.resolve(); });
    expect(pick(35, 0)).toBe('x');
    overlay.setSelection(null);
    expect(pick(35, 0)).toBeNull();
  });
});
