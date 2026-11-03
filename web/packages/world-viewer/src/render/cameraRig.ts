// Camera modes: follow (third-person orbit around the selected bot), fly (WASD/QE + mouse, pointer lock) and iso (a narrow-FOV
// perspective from far away: reads as isometric, and blends smoothly with the other modes). Switching modes or focusing
// glides from the current pose to the new one. Yaw/pitch use Minecraft's convention (yaw 0 = south, 90 = west; pitch + down).

import * as THREE from 'three';
import type { CameraMode } from '../types';

export interface Pose {
  pos: THREE.Vector3;
  quat: THREE.Quaternion;
  fov: number;
}

/** Minecraft look direction for (yaw, pitch) in degrees. */
export function lookDir(yaw: number, pitch: number, out = new THREE.Vector3()): THREE.Vector3 {
  const y = (yaw * Math.PI) / 180, p = (pitch * Math.PI) / 180;
  return out.set(-Math.sin(y) * Math.cos(p), -Math.sin(p), Math.cos(y) * Math.cos(p));
}

const ease = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2);

export interface RigInput {
  keys: Set<string>;
}

export class CameraRig {
  mode: CameraMode = 'follow';
  /** What follow / iso look at (the bot's eyes, or a focused point). */
  readonly target = new THREE.Vector3(0, 64, 0);
  orbitYaw = 315;
  orbitPitch = 30;
  orbitDist = 9;
  /** Follow camera pulled in so it stays out of solid blocks (vanilla third person); Infinity = free. */
  collideDist = Infinity;
  readonly flyPos = new THREE.Vector3(0, 80, 0);
  flyYaw = 315;
  flyPitch = 30;
  speed = 12;
  isoYaw = 315;
  isoPitch = 35.264;
  /** World units visible vertically in iso. */
  isoZoom = 48;
  isoFov = 12;
  fov = 70;
  private from: Pose | null = null;
  private t0 = 0;
  private dur = 600;
  private isoYawTarget = 315;

  /** Starts a glide from the camera's current pose (call before changing mode/target). */
  beginTransition(cam: THREE.PerspectiveCamera, now: number, ms = 600): void {
    this.from = { pos: cam.position.clone(), quat: cam.quaternion.clone(), fov: cam.fov };
    this.t0 = now;
    this.dur = ms;
  }

  get transitioning(): boolean {
    return this.from !== null;
  }

  setMode(mode: CameraMode, cam: THREE.PerspectiveCamera, now: number): void {
    if (mode === this.mode) return;
    if (mode === 'fly') {
      // Start flying from where the camera is, looking the same way.
      this.flyPos.copy(cam.position);
      const d = cam.getWorldDirection(new THREE.Vector3());
      this.flyPitch = (-Math.asin(Math.max(-1, Math.min(1, d.y))) * 180) / Math.PI;
      this.flyYaw = ((Math.atan2(-d.x, d.z) * 180) / Math.PI + 360) % 360;
    }
    if (mode === 'follow') {
      const d = cam.getWorldDirection(new THREE.Vector3());
      this.orbitYaw = ((Math.atan2(-d.x, d.z) * 180) / Math.PI + 360) % 360;
      this.orbitPitch = Math.max(5, Math.min(85, (-Math.asin(d.y) * 180) / Math.PI));
    }
    this.beginTransition(cam, now);
    this.mode = mode;
  }

  rotateIso(steps: number): void {
    this.isoYawTarget += 90 * steps;
  }

  /** The pose the current mode wants this frame. */
  desired(dt: number, input: RigInput): Pose {
    const quat = new THREE.Quaternion();
    const m = new THREE.Matrix4();
    const up = new THREE.Vector3(0, 1, 0);
    if (this.mode === 'fly') {
      const k = input.keys;
      const fast = k.has('ShiftLeft') || k.has('ShiftRight') ? 4 : 1;
      const v = this.speed * fast * dt;
      const fwd = lookDir(this.flyYaw, 0);
      const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
      const mv = new THREE.Vector3();
      if (k.has('KeyW') || k.has('ArrowUp')) mv.add(fwd);
      if (k.has('KeyS') || k.has('ArrowDown')) mv.sub(fwd);
      if (k.has('KeyD') || k.has('ArrowRight')) mv.add(right);
      if (k.has('KeyA') || k.has('ArrowLeft')) mv.sub(right);
      if (k.has('KeyE') || k.has('Space') || k.has('PageUp')) mv.y += 1;
      if (k.has('KeyQ') || k.has('PageDown')) mv.y -= 1;
      if (mv.lengthSq() > 0) this.flyPos.addScaledVector(mv.normalize(), v);
      this.flyPos.y = Math.max(-64, Math.min(320, this.flyPos.y));
      const dir = lookDir(this.flyYaw, this.flyPitch);
      m.lookAt(this.flyPos, this.flyPos.clone().add(dir), up);
      quat.setFromRotationMatrix(m);
      return { pos: this.flyPos.clone(), quat, fov: this.fov };
    }
    if (this.mode === 'iso' || this.mode === 'top') {
      const k = input.keys;
      const pan = (this.isoZoom / 2) * dt;
      const fwd = lookDir(this.mode === 'top' ? 180 : this.isoYaw, 0);
      const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
      if (k.has('KeyW') || k.has('ArrowUp')) this.target.addScaledVector(fwd, pan);
      if (k.has('KeyS') || k.has('ArrowDown')) this.target.addScaledVector(fwd, -pan);
      if (k.has('KeyD') || k.has('ArrowRight')) this.target.addScaledVector(right, pan);
      if (k.has('KeyA') || k.has('ArrowLeft')) this.target.addScaledVector(right, -pan);
      this.isoYaw += (this.isoYawTarget - this.isoYaw) * Math.min(1, dt * 8);
      const dist = this.isoDistance();
      const top = this.mode === 'top';
      const dir = lookDir(top ? 180 : this.isoYaw, top ? 89.9 : this.isoPitch);
      const pos = this.target.clone().addScaledVector(dir, -dist);
      m.lookAt(pos, this.target, top ? new THREE.Vector3(0, 0, -1) : up);
      quat.setFromRotationMatrix(m);
      return { pos, quat, fov: this.isoFov };
    }
    const dir = lookDir(this.orbitYaw, this.orbitPitch);
    const pos = this.target.clone().addScaledVector(dir, -Math.min(this.orbitDist, this.collideDist));
    m.lookAt(pos, this.target, up);
    quat.setFromRotationMatrix(m);
    return { pos, quat, fov: this.fov };
  }

  isoDistance(): number {
    return this.isoZoom / 2 / Math.tan((this.isoFov * Math.PI) / 360);
  }

  /** Moves the camera: the desired pose, blended with the transition start while one runs. */
  apply(cam: THREE.PerspectiveCamera, dt: number, input: RigInput, now: number): void {
    const want = this.desired(dt, input);
    if (this.from) {
      const k = Math.min(1, (now - this.t0) / this.dur);
      const e = ease(k);
      cam.position.lerpVectors(this.from.pos, want.pos, e);
      cam.quaternion.slerpQuaternions(this.from.quat, want.quat, e);
      // Blend the FOV in tan space so the iso dolly reads as a smooth zoom.
      const ta = Math.tan((this.from.fov * Math.PI) / 360), tb = Math.tan((want.fov * Math.PI) / 360);
      cam.fov = (Math.atan(ta + (tb - ta) * e) * 360) / Math.PI;
      if (k >= 1) this.from = null;
    } else {
      cam.position.copy(want.pos);
      cam.quaternion.copy(want.quat);
      cam.fov = want.fov;
    }
    cam.updateProjectionMatrix();
    cam.updateMatrixWorld();
  }

  /** Mouse look / orbit by a pixel delta. */
  look(dx: number, dy: number): void {
    if (this.mode === 'fly') {
      this.flyYaw = (this.flyYaw + dx * 0.15 + 360) % 360;
      this.flyPitch = Math.max(-89.9, Math.min(89.9, this.flyPitch + dy * 0.15));
    } else if (this.mode === 'follow') {
      this.orbitYaw = (this.orbitYaw + dx * 0.3 + 360) % 360;
      this.orbitPitch = Math.max(-20, Math.min(89, this.orbitPitch + dy * 0.3));
    } else {
      this.isoYaw += dx * 0.3;
      this.isoYawTarget = this.isoYaw;
      this.isoPitch = Math.max(10, Math.min(89, this.isoPitch + dy * 0.2));
    }
  }

  /** Iso: drag the target across the ground plane. */
  pan(dx: number, dy: number, viewHeightPx: number): void {
    const k = this.isoZoom / Math.max(1, viewHeightPx);
    const top = this.mode === 'top';
    const fwd = lookDir(top ? 180 : this.isoYaw, 0);
    const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
    this.target.addScaledVector(right, -dx * k).addScaledVector(fwd, (dy * k) / (top ? 1 : Math.max(0.2, Math.sin((this.isoPitch * Math.PI) / 180))));
  }

  wheel(deltaY: number): void {
    const s = Math.sign(deltaY);
    if (this.mode === 'fly') this.speed = Math.max(1, Math.min(200, this.speed * (s > 0 ? 1 / 1.15 : 1.15)));
    else if (this.mode === 'follow') this.orbitDist = Math.max(2, Math.min(96, this.orbitDist * (s > 0 ? 1.12 : 1 / 1.12)));
    else this.isoZoom = Math.max(8, Math.min(320, this.isoZoom * (s > 0 ? 1.12 : 1 / 1.12)));
  }

  /** Fly to look at a point from a short distance (fly mode), or centre on it (follow / iso). */
  focus(x: number, y: number, z: number, cam: THREE.PerspectiveCamera, now: number): void {
    this.beginTransition(cam, now, 800);
    this.target.set(x, y, z);
    if (this.mode === 'fly') {
      const dir = lookDir(this.flyYaw, this.flyPitch);
      this.flyPos.set(x, y, z).addScaledVector(dir, -10);
    }
  }
}
