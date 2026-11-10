import type { PerspectiveCamera } from 'three';
import { plotMode, type V3 } from './canvas';
import type { Overlay3D } from './overlay3d';
import type { Studio } from './studio';
import type * as ViewerMod from './studio-viewer';
import type { Drag, InputState } from './studioInput';

export type PointerPosition = { x: number; y: number };
type Modifiers = { shift: boolean; ctrl: boolean };

/** The camera owns these gestures before any authoring tool gets a chance to act. */
export const mineCameraGesture = (event: Pick<PointerEvent, 'button' | 'altKey'>) =>
  event.button === 1 || (event.button === 0 && event.altKey);

export interface StudioPointerActions {
  canEdit: boolean;
  down: (button: number, mods: Modifiers, at?: PointerPosition, signal?: AbortSignal) => Promise<void>;
  up: (button: number, mods: Modifiers, at?: PointerPosition, signal?: AbortSignal) => Promise<void>;
  cancel: () => void;
  move: (delta: V3, group: string) => void;
  setDrag: (update: (drag: Drag | null) => Drag | null) => void;
  error: (error: unknown) => void;
  /** A gizmo press: true when the selection only previews the move and commits it once on release (a mine component). */
  dragStart?: () => boolean;
  /** Shows the selection moved by `delta` from where the drag started; false when it cannot stand there. */
  dragPreview?: (delta: V3) => boolean;
  /** The release: one move by the last delta the preview accepted. */
  dragCommit?: (delta: V3) => void;
  /** Escape, a lost pointer or a lost window: the selection goes back to where the drag started. */
  dragCancel?: () => void;
  /** A pointer gesture on the canvas starts (true) or ends (false); heavy readouts wait for its end. */
  gesture?: (held: boolean) => void;
}

const along = (axis: 'x' | 'y' | 'z', n: number): V3 => [axis === 'x' ? n : 0, axis === 'y' ? n : 0, axis === 'z' ? n : 0];

/** One binding survives selection changes, so a drag keeps its origin and undo group across React renders. */
export function bindStudioPointer(p: {
  surface: HTMLCanvasElement;
  camera: PerspectiveCamera;
  studio: Studio;
  overlay: Overlay3D;
  mod: Pick<typeof ViewerMod, 'Vector2' | 'Vector3' | 'Raycaster'>;
  mine: boolean;
  state: () => InputState;
  actions: () => StudioPointerActions;
}) {
  const { surface, camera, studio, overlay, mod, mine } = p;
  let gizmo: { axis: 'x' | 'y' | 'z'; x: number; y: number; dx: number; dy: number; pixels: number; applied: number; group: string; preview: boolean } | null = null;
  let pressed: { id: number; button: number } | null = null;
  let queue = Promise.resolve();
  let generation = 0;
  let abort = new AbortController();
  const enqueue = (action: (signal: AbortSignal) => Promise<void>) => {
    const current = generation;
    const signal = abort.signal;
    queue = queue.then(() => current === generation ? action(signal) : undefined).catch((error: unknown) => p.actions().error(error));
  };
  const capture = (event: PointerEvent) => {
    pressed = { id: event.pointerId, button: event.button };
    p.actions().gesture?.(true);
    if (document.pointerLockElement === surface) return;
    try { surface.setPointerCapture?.(event.pointerId); } catch { /* The pointer may already have ended. */ }
  };
  const release = () => {
    const id = pressed?.id;
    pressed = null;
    if (id === undefined) return;
    p.actions().gesture?.(false);
    if (surface.hasPointerCapture?.(id)) surface.releasePointerCapture(id);
  };
  const consume = (event: Event) => { event.preventDefault(); event.stopImmediatePropagation(); };
  const onDown = (event: PointerEvent) => {
    if (mine && mineCameraGesture(event)) return;
    if (pressed) return;
    const locked = document.pointerLockElement === surface;
    const canEdit = p.actions().canEdit;
    if (!locked && event.button === 0 && studio.selection && canEdit) {
      const rect = surface.getBoundingClientRect();
      const ray = new mod.Raycaster();
      ray.setFromCamera(new mod.Vector2((event.clientX - rect.left) / rect.width * 2 - 1, 1 - (event.clientY - rect.top) / rect.height * 2), camera);
      const axis = overlay.pickHandle(ray);
      if (axis) {
        consume(event); surface.focus(); capture(event);
        const origin = overlay.gizmo.position.clone();
        const unit = new mod.Vector3(axis === 'x' ? 1 : 0, axis === 'y' ? 1 : 0, axis === 'z' ? 1 : 0);
        const a = origin.clone().project(camera), b = origin.add(unit).project(camera);
        const dx = (b.x - a.x) * rect.width / 2, dy = -(b.y - a.y) * rect.height / 2;
        const pixels = Math.max(1, Math.hypot(dx, dy));
        const preview = p.actions().dragStart?.() ?? false;
        gizmo = { axis, x: event.clientX, y: event.clientY, dx: dx / pixels, dy: dy / pixels, pixels, applied: 0, group: `gizmo-${performance.now()}`, preview };
        return;
      }
    }
    if (!locked && !mine) return; // The builder retains its immersive click-to-lock flow.
    if (event.button !== 0 && event.button !== 2 && !locked) return;
    // A view-only user can still select a region; every mutation needs editing permission.
    if (!canEdit && (p.state().tool !== 'select' || p.state().pasting)) return;
    consume(event); surface.focus(); capture(event);
    const mods = { shift: event.shiftKey, ctrl: event.ctrlKey || event.metaKey };
    const at = mine ? { x: event.clientX, y: event.clientY } : undefined;
    enqueue((signal) => p.actions().down(event.button, mods, at, signal));
  };
  const onMove = (event: PointerEvent) => {
    if (!gizmo || event.pointerId !== pressed?.id || !p.actions().canEdit) return;
    const steps = Math.round(((event.clientX - gizmo.x) * gizmo.dx + (event.clientY - gizmo.y) * gizmo.dy) / gizmo.pixels);
    if (gizmo.preview) {
      // Only the outline follows the pointer; a position it refuses keeps the last one shown.
      if (steps !== gizmo.applied && p.actions().dragPreview?.(along(gizmo.axis, steps))) gizmo.applied = steps;
      return;
    }
    const delta = steps - gizmo.applied;
    if (!delta) return;
    const axis = gizmo.axis === 'x' ? 0 : gizmo.axis === 'y' ? 1 : 2;
    const before = studio.selection?.min[axis];
    p.actions().move([gizmo.axis === 'x' ? delta : 0, gizmo.axis === 'y' ? delta : 0, gizmo.axis === 'z' ? delta : 0], gizmo.group);
    const after = studio.selection?.min[axis];
    // Refused/clipped moves must not accumulate an invisible offset from the pointer.
    if (before !== undefined && after !== undefined) gizmo.applied += after - before;
  };
  const onUp = (event: PointerEvent) => {
    if (event.pointerId !== pressed?.id || event.button !== pressed.button) return;
    consume(event); release();
    if (gizmo) {
      const done = gizmo;
      gizmo = null;
      if (done.preview) p.actions().dragCommit?.(along(done.axis, done.applied));
      return;
    }
    const mods = { shift: event.shiftKey, ctrl: event.ctrlKey || event.metaKey };
    const at = mine ? { x: event.clientX, y: event.clientY } : undefined;
    enqueue((signal) => p.actions().up(event.button, mods, at, signal));
  };
  const cancel = () => {
    generation++; abort.abort(); abort = new AbortController();
    if (gizmo?.preview) p.actions().dragCancel?.();
    gizmo = null; release(); p.actions().cancel();
  };
  const onKey = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || !gizmo?.preview) return;
    consume(event);
    gizmo = null; release();
    p.actions().dragCancel?.();
  };
  const onCancel = (event: PointerEvent) => { if (event.pointerId === pressed?.id) cancel(); };
  const onAux = (event: MouseEvent) => { if (event.button === 1) event.preventDefault(); };
  const onWheel = (event: WheelEvent) => {
    if (mine) {
      // The drag projection is fixed at press time; don't change its camera until the gesture ends.
      if (pressed) consume(event);
      return; // An idle selection never steals the wheel from zoom.
    }
    if (p.state().tool !== 'select') return;
    if (p.state().drag) {
      consume(event);
      p.actions().setDrag((drag) => drag ? { ...drag, dy: Math.max(-plotMode().maxY, Math.min(plotMode().maxY, drag.dy + (event.deltaY < 0 ? 1 : -1))) } : drag);
    } else if (studio.selection) {
      consume(event);
      const current = studio.selection;
      studio.setSelection({ ...current, mask: null, max: [current.max[0], Math.max(current.min[1], Math.min(plotMode().maxY, current.max[1] + (event.deltaY < 0 ? 1 : -1))), current.max[2]] });
    }
  };
  surface.addEventListener('pointerdown', onDown, true);
  window.addEventListener('pointermove', onMove);
  window.addEventListener('pointerup', onUp, true);
  window.addEventListener('pointercancel', onCancel, true);
  surface.addEventListener('lostpointercapture', onCancel);
  window.addEventListener('blur', cancel);
  window.addEventListener('keydown', onKey, true);
  surface.addEventListener('mousedown', onAux);
  surface.addEventListener('wheel', onWheel, { capture: true, passive: false });
  return () => {
    cancel();
    surface.removeEventListener('pointerdown', onDown, true);
    window.removeEventListener('pointermove', onMove);
    window.removeEventListener('pointerup', onUp, true);
    window.removeEventListener('pointercancel', onCancel, true);
    surface.removeEventListener('lostpointercapture', onCancel);
    window.removeEventListener('blur', cancel);
    window.removeEventListener('keydown', onKey, true);
    surface.removeEventListener('mousedown', onAux);
    surface.removeEventListener('wheel', onWheel, true);
  };
}
