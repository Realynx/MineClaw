import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { CameraRig, lookDir } from '../src/render/cameraRig';

/** A camera posed by the rig in fly mode, the way hand control of a pawn writes yaw and pitch into it every frame. */
function flyCamera(yaw: number, pitch: number): { rig: CameraRig; cam: THREE.PerspectiveCamera } {
  const rig = new CameraRig();
  const cam = new THREE.PerspectiveCamera();
  rig.mode = 'fly';
  rig.flyPos.set(100, 70, 100);
  rig.flyYaw = yaw;
  rig.flyPitch = pitch;
  rig.apply(cam, 0, { keys: new Set() }, 0);
  return { rig, cam };
}

describe('fly camera looking straight up or down', () => {
  const cases = [90, -90].flatMap((pitch) => [0, 90, 180, 270].map((yaw) => [pitch, yaw] as const));

  it.each(cases)('pitch %i at yaw %i keeps the heading of the yaw', (pitch, yaw) => {
    const { rig, cam } = flyCamera(yaw, pitch);
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(cam.quaternion);
    const ahead = lookDir(yaw, 0);
    // The screen's right is the heading turned a quarter turn, whatever the pitch: never a fixed world axis.
    expect(right.x).toBeCloseTo(-ahead.z, 6);
    expect(right.y).toBeCloseTo(0, 6);
    expect(right.z).toBeCloseTo(ahead.x, 6);
    // Still straight down (pitch 90) or up to the eye, and the pitch the app wrote is left as it was.
    expect(cam.getWorldDirection(new THREE.Vector3()).y * Math.sign(pitch)).toBeLessThan(-0.9999);
    expect(rig.flyPitch).toBe(pitch);
  });

  it('leaves every other pitch exactly as it is', () => {
    for (const [yaw, pitch] of [
      [45, 30],
      [200, -60],
      [315, 89.9],
      [10, -89.9],
    ] as const) {
      const forward = flyCamera(yaw, pitch).cam.getWorldDirection(new THREE.Vector3());
      const want = lookDir(yaw, pitch);
      expect(forward.x).toBeCloseTo(want.x, 9);
      expect(forward.y).toBeCloseTo(want.y, 9);
      expect(forward.z).toBeCloseTo(want.z, 9);
    }
  });
});
