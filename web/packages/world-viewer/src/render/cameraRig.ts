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

/** The most ground a top-down / iso view asks to have drawn (blocks): about 450 chunk columns of sections. */
export const COVER_MAX = 192;

const ease = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2);

export interface RigInput {
  keys: Set<string>;
}

const FORWARD = ['KeyW', 'ArrowUp'], BACK = ['KeyS', 'ArrowDown'], RIGHT = ['KeyD', 'ArrowRight'], LEFT = ['KeyA', 'ArrowLeft'];
const RISE = ['KeyE', 'Space', 'PageUp'], SINK = ['KeyQ', 'PageDown'], FAST = ['ShiftLeft', 'ShiftRight'];

/** Whether any of `codes` is held. */
function held(keys: Set<string>, codes: readonly string[]): boolean {
  for (const c of codes) if (keys.has(c)) return true;
  return false;
}

/** The fly camera's unnormalised move this frame: along its heading `fwd`, sideways and straight up or down. */
function flyMove(fwd: THREE.Vector3, k: Set<string>): THREE.Vector3 {
  const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
  const mv = new THREE.Vector3();
  if (held(k, FORWARD)) mv.add(fwd);
  if (held(k, BACK)) mv.sub(fwd);
  if (held(k, RIGHT)) mv.add(right);
  if (held(k, LEFT)) mv.sub(right);
  if (held(k, RISE)) mv.y += 1;
  if (held(k, SINK)) mv.y -= 1;
  return mv;
}

const UP = new THREE.Vector3(0, 1, 0);
/** Top-down: north is up on screen. */
const NORTH_UP = new THREE.Vector3(0, 0, -1);
const lookM = new THREE.Matrix4();

/** The orientation of a camera at `eye` looking at `at` (a new quaternion: the pose keeps it). */
function lookQuat(eye: THREE.Vector3, at: THREE.Vector3, up: THREE.Vector3): THREE.Quaternion {
  lookM.lookAt(eye, at, up);
  return new THREE.Quaternion().setFromRotationMatrix(lookM);
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
    if (this.mode === 'fly') return this.flyPose(dt, input.keys);
    if (this.mode === 'iso' || this.mode === 'top') return this.isoPose(dt, input.keys);
    return this.followPose();
  }

  /** Fly: WASD / arrows move across, E Space PageUp up, Q PageDown down (Shift 4× faster); the mouse looks. */
  private flyPose(dt: number, k: Set<string>): Pose {
    const fast = held(k, FAST) ? 4 : 1;
    const v = this.speed * fast * dt;
    const mv = flyMove(lookDir(this.flyYaw, 0), k);
    if (mv.lengthSq() > 0) this.flyPos.addScaledVector(mv.normalize(), v);
    this.flyPos.y = Math.max(-64, Math.min(320, this.flyPos.y));
    const dir = lookDir(this.flyYaw, this.flyPitch);
    return { pos: this.flyPos.clone(), quat: lookQuat(this.flyPos, this.flyPos.clone().add(dir), UP), fov: this.fov };
  }

  /** Iso / top-down: WASD / arrows pan the target across the ground; Q/E turns glide; the camera sits far off, narrow FOV. */
  private isoPose(dt: number, k: Set<string>): Pose {
    const top = this.mode === 'top';
    this.panTarget(k, (this.isoZoom / 2) * dt, top);
    this.isoYaw += (this.isoYawTarget - this.isoYaw) * Math.min(1, dt * 8);
    const dist = this.isoDistance();
    const dir = lookDir(top ? 180 : this.isoYaw, top ? 89.9 : this.isoPitch);
    const pos = this.target.clone().addScaledVector(dir, -dist);
    return { pos, quat: lookQuat(pos, this.target, top ? NORTH_UP : UP), fov: this.isoFov };
  }

  private panTarget(k: Set<string>, pan: number, top: boolean): void {
    const fwd = lookDir(top ? 180 : this.isoYaw, 0);
    const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
    if (held(k, FORWARD)) this.target.addScaledVector(fwd, pan);
    if (held(k, BACK)) this.target.addScaledVector(fwd, -pan);
    if (held(k, RIGHT)) this.target.addScaledVector(right, pan);
    if (held(k, LEFT)) this.target.addScaledVector(right, -pan);
  }

  /** Follow: orbit the target, pulled in when a block is in the way. */
  private followPose(): Pose {
    const dir = lookDir(this.orbitYaw, this.orbitPitch);
    const pos = this.target.clone().addScaledVector(dir, -Math.min(this.orbitDist, this.collideDist));
    return { pos, quat: lookQuat(pos, this.target, UP), fov: this.fov };
  }

  /**
   * Top-down and iso: how far from the target the picture reaches on the ground (the footprint's half diagonal plus a
   * margin, for a view `aspect` wide), capped at `max`; 0 in the other modes.
   */
  groundCover(aspect: number, max = COVER_MAX): number {
    if (this.mode !== 'iso' && this.mode !== 'top') return 0;
    const pitch = this.mode === 'top' ? 90 : Math.max(10, this.isoPitch);
    const halfH = this.isoZoom / 2 / Math.sin((pitch * Math.PI) / 180);
    const halfW = (this.isoZoom / 2) * Math.max(1, aspect || 1);
    return Math.min(max, Math.hypot(halfH, halfW) + 12);
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

  /** Editor orbit: turn around the focus point, including views from below, without pointer lock. */
  orbit(dx: number, dy: number): void {
    this.orbitYaw = (this.orbitYaw + dx * 0.3 + 360) % 360;
    this.orbitPitch = Math.max(-89.9, Math.min(89.9, this.orbitPitch + dy * 0.3));
  }

  /** Editor pan: translate the focus point in the camera's screen plane, scaled to the visible distance. */
  panOrbit(dx: number, dy: number, viewHeightPx: number, cam: THREE.PerspectiveCamera): void {
    const distance = Math.max(1, cam.position.distanceTo(this.target));
    const scale = 2 * distance * Math.tan(cam.fov * Math.PI / 360) / Math.max(1, viewHeightPx);
    const right = new THREE.Vector3(1, 0, 0).applyQuaternion(cam.quaternion);
    const up = new THREE.Vector3(0, 1, 0).applyQuaternion(cam.quaternion);
    this.target.addScaledVector(right, -dx * scale).addScaledVector(up, dy * scale);
  }

  /** Editor dolly: the wheel changes the distance to the focus point, never the fly speed. */
  dolly(deltaY: number): void {
    if (!deltaY) return;
    this.orbitDist = Math.max(1, Math.min(256, this.orbitDist * (deltaY > 0 ? 1.12 : 1 / 1.12)));
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
  focus(x: number, y: number, z: number, cam: THREE.PerspectiveCamera, now: number, view?: { distance?: number; pitch?: number }): void {
    this.beginTransition(cam, now, 800);
    this.target.set(x, y, z);
    if (this.mode === 'fly') {
      if (view?.pitch !== undefined) this.flyPitch = view.pitch;
      const dir = lookDir(this.flyYaw, this.flyPitch);
      this.flyPos.set(x, y, z).addScaledVector(dir, -(view?.distance ?? 10));
    }
  }
}
