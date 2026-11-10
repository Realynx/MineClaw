'use client';

import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type DragEvent, type RefObject } from 'react';
import { isTyping } from '@/features/world/directives';
import { keyOf, maskSelection, plotMode, type Selection, type V3 } from './canvas';
import type { Overlay3D } from './overlay3d';
import type { Studio } from './studio';
import type * as ViewerMod from './studio-viewer';
import type { NoticeOptions } from './studioSave';
import { MineModuleController } from './mine/MineModuleController';
import { decodeModuleDrag, joinedPortCells, MINE_MODULE_MIME, moduleCells, moduleName, openPortCells, snapModuleAt, type MineModuleInstance, type MineModuleSpec } from './mine/modules';
import { mineRayBox } from './mine/placementRay';
import { minePlacementPoint, modulePlacementHeight, surfaceModuleAt, type PlacementHeight, type PlacementHeightMode } from './mine/placementHeight';
import { modulePlacementPreview } from './mine/placementPreview';
import { pictureDistance } from './PictureControls';
import { keyMoves } from './studioInput';
import { mineCameraGesture } from './studioPointer';
import { presetModules, type MinePreset } from './mine/presets';

const NONE: readonly MineModuleInstance[] = [];
const noSubscription = () => () => undefined;
type ComponentClip = { spec: MineModuleSpec; level: number };
const plus = (a: V3, b: V3): V3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

/** Pointer placement and selection; the controller owns cells, overlapping modules and undo history. */
export function useMinePlacement(p: {
  studio: Studio;
  mod: typeof ViewerMod;
  viewer: RefObject<WorldViewerHandle | null>;
  overlay: Overlay3D;
  ready: boolean;
  canEdit: boolean;
  selecting: boolean;
  activateSelection?: () => void;
  notice: (text: string | null, options?: NoticeOptions) => void;
  /** The working level was set (feet Y): the page cuts the view away above it. */
  onWorkLevel?: (y: number) => void;
}) {
  const { studio, mod, viewer, overlay, ready } = p;
  // Built without following the canvas: a development build runs this factory twice and keeps one result. The effect below
  // attaches the kept one, so the other never writes its layout over it.
  const controller = useMemo(() => studio.mine ? new MineModuleController(studio, undefined, { attach: false }) : null, [studio]);
  const subscribe = useCallback((fn: () => void) => controller?.subscribe(fn) ?? noSubscription(), [controller]);
  const getSnapshot = useCallback(() => controller?.snapshot() ?? NONE, [controller]);
  const modules = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [placing, setPlacing] = useState<MineModuleSpec | null>(null);
  const [heightMode, setHeightMode] = useState<PlacementHeightMode>('auto');
  const [fixedY, setFixedY] = useState(11);
  const [placementHeight, setPlacementHeight] = useState<PlacementHeight | null>(null);
  const [connection, setConnection] = useState<MineModuleInstance | null>(null);
  const [clipboard, setClipboard] = useState<ComponentClip | null>(null);
  /** The pieces ticked in the outliner's Combine checklist (null: the checklist is closed). Ctrl+G combines them. */
  const [checked, setChecked] = useState<string[] | null>(null);
  const [hover, setHover] = useState<V3 | null>(null);
  /** Why the outline shown (a piece in hand, a component being dragged) cannot stand where it is, or null. */
  const [refused, setRefused] = useState<string | null>(null);
  /** Where the pointer last previewed a piece in hand: a rotation key redraws it there. */
  const pointerAt = useRef<[number, number] | null>(null);
  /** The surface the key and pointer listeners are bound to (null while unbound). */
  const bound = useRef<HTMLElement | null>(null);
  const ownedSelection = useRef<Selection | null>(null);
  const selectedShape = useRef<{ id: string; spec: MineModuleSpec; at: V3 } | null>(null);
  const lastPreview = useRef('');
  const nativeDrag = useRef(false);
  /** The component a gizmo drag previews: where it and its selection box stood when the drag began. */
  const dragging = useRef<{ id: string; spec: MineModuleSpec; at: V3; min: V3; max: V3 } | null>(null);
  /**
   * A press on a component's body: past a few pixels it drags the piece across its own level, snapped to a neighbour's
   * port, through the same preview and single commit as a handle drag. `grab` is where the press met the piece's level.
   */
  const bodyPress = useRef<{ pointer: number; x: number; y: number; id: string; spec: MineModuleSpec; at: V3; grab: V3; started: boolean; delta: V3 } | null>(null);
  const cancelBody = useRef<() => void>(() => undefined);
  const selected = modules.find((m) => m.id === selectedId) ?? null;
  const live = useRef({ ...p, modules, placing, heightMode, fixedY, placementHeight, connection, clipboard, selectedId, checked });
  useEffect(() => { live.current = { ...p, modules, placing, heightMode, fixedY, placementHeight, connection, clipboard, selectedId, checked }; });
  useEffect(() => controller?.attach(), [controller]);
  useEffect(() => () => controller?.dispose(), [controller]);
  useEffect(() => {
    // It stays until dismissed: recovered work is lost again unless saved, and a dropped component is easy to miss.
    if (controller?.restoreNote) live.current.notice(controller.restoreNote, { tone: 'warn', sticky: true });
  }, [controller]);

  const select = useCallback((id: string | null) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    setSelectedId(item?.id ?? null);
    const previous = selectedShape.current;
    if (item && previous?.id === item.id && previous.spec === item.spec && previous.at === item.at && studio.selection === ownedSelection.current) return;
    selectedShape.current = item ?? null;
    ownedSelection.current = item ? maskSelection(new Set(moduleCells(item.spec, item.at).cells.map((c) => keyOf(c[0], c[1], c[2])))) : null;
    studio.setSelection(ownedSelection.current);
  }, [controller, studio]);

  useEffect(() => studio.subscribe(() => {
    if (studio.selection !== ownedSelection.current) setSelectedId(null);
  }), [studio]);
  useEffect(() => {
    if (!selectedId || studio.selection !== ownedSelection.current) return;
    select(selectedId);
  }, [modules, selectedId, select, studio]);

  const cancel = useCallback(() => {
    setPlacing(null); setPlacementHeight(null); setConnection(null); setHover(null); setRefused(null);
    lastPreview.current = '';
    pointerAt.current = null;
    nativeDrag.current = false;
    overlay.setMinePlacement(null);
  }, [overlay]);

  const frame = useCallback((id = live.current.selectedId) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    const engine = viewer.current?.engine();
    if (!item || !engine) return;
    cancel(); select(item.id);
    const bounds = studio.selection!;
    const center = bounds.min.map((value, axis) => (value + bounds.max[axis]!) / 2) as V3;
    engine.focus(...center, { distance: pictureDistance(bounds, engine.camera.aspect, engine.camera.fov) });
    engine.surface.focus();
  }, [cancel, controller, select, studio, viewer]);

  const position = useCallback((id: string, at: V3) => {
    if (!controller || !live.current.canEdit) return;
    if (controller.move(id, at)) select(id);
    else live.current.notice(controller.lastError);
  }, [controller, select]);

  const move = useCallback((id: string) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    if (!item || !live.current.canEdit) return;
    live.current.activateSelection?.();
    cancel(); select(id);
    viewer.current?.engine()?.surface.focus();
    live.current.notice(`Moving ${item.spec.label || moduleName(item.spec.kind)}: drag an axis, use arrow keys / PgUp / PgDn, or edit Position X, Y, Z.`);
  }, [cancel, controller, select, viewer]);

  const arm = useCallback((spec: MineModuleSpec, id: string | null = null, options?: { drag?: boolean }) => {
    if (!live.current.canEdit) return;
    // Existing pieces always use translation controls; they never enter the creation/placement path.
    if (id) { move(id); return; }
    if (document.pointerLockElement) document.exitPointerLock();
    const selected = controller?.snapshot().find((m) => m.id === live.current.selectedId) ?? null;
    const height = modulePlacementHeight(spec, live.current.heightMode, live.current.fixedY, selected);
    setPlacing(spec); setPlacementHeight(height); setConnection(selected); setHover(null);
    nativeDrag.current = options?.drag ?? false;
    lastPreview.current = '';
    overlay.setMinePlacement(null);
    live.current.notice(height.surface ? 'Point at the terrain surface to start this access route. Click to place; Escape cancels.' : `Point at the mine to preview at Y${height.level}. Click to place; Escape cancels.`);
  }, [controller, move, overlay]);

  const clearPreview = useCallback(() => {
    lastPreview.current = ''; setHover(null); setRefused(null); overlay.setMinePlacement(null);
  }, [overlay]);
  /** Sets the working level: new pieces and pastes go to this feet Y, and the page cuts the view away above it. */
  const setPlacementY = useCallback((y: number) => {
    if (!Number.isFinite(y)) return;
    const level = Math.max(plotMode().minY + 1, Math.min(plotMode().maxY - 2, Math.round(y)));
    setFixedY(level); setHeightMode('fixed');
    live.current.fixedY = level; live.current.heightMode = 'fixed';
    if (live.current.placing) setPlacementHeight(live.current.placementHeight = { mode: 'fixed', surface: false, level });
    clearPreview();
    live.current.onWorkLevel?.(level);
  }, [clearPreview]);

  const copy = useCallback((id = live.current.selectedId) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    if (!item) return null;
    const clip = { spec: structuredClone(item.spec), level: item.at[1] };
    setClipboard(clip);
    live.current.notice(`Copied ${item.spec.label || moduleName(item.spec.kind)} with its dimensions, rotation and entrance Y${clip.level}. Paste to place another.`);
    return clip;
  }, [controller]);
  /** A paste goes to the working level when one is set; a duplicate (`keepLevel`) stays at its original's. */
  const pasteClip = useCallback((clip: ComponentClip | null, keepLevel = false) => {
    if (!clip || !live.current.canEdit) return;
    live.current.activateSelection?.();
    if (document.pointerLockElement) document.exitPointerLock();
    const level = !keepLevel && live.current.heightMode === 'fixed' ? live.current.fixedY : clip.level;
    setPlacing(structuredClone(clip.spec)); setPlacementHeight({ mode: 'fixed', surface: false, level }); setConnection(null);
    nativeDrag.current = false;
    clearPreview();
    live.current.notice(`Place a copy at Y${level}. Its settings are preserved; click to place or Escape to cancel.`);
  }, [clearPreview]);
  const paste = useCallback(() => { pasteClip(live.current.clipboard); }, [pasteClip]);
  const duplicate = useCallback((id = live.current.selectedId) => {
    if (live.current.canEdit) pasteClip(copy(id), true);
  }, [copy, pasteClip]);
  const combine = useCallback((ids: string[]) => {
    if (!controller || !live.current.canEdit) return;
    const item = controller.combine(ids);
    if (!item) { live.current.notice(controller.lastError ?? 'Choose at least two components to combine.'); return; }
    setChecked(null);
    live.current.activateSelection?.();
    cancel(); select(item.id);
    live.current.notice(`Combined ${ids.length} pieces into one component. Move, rotate, copy or rename it in the inspector.`);
  }, [cancel, controller, select]);
  /** Ctrl+G: combines the ticked pieces, or opens the checklist (the selected piece ticked) and says what to do. */
  const combineChecked = useCallback(() => {
    const ids = (live.current.checked ?? []).filter((id) => live.current.modules.some((m) => m.id === id));
    if (ids.length >= 2) { combine(ids); return; }
    setChecked(live.current.selectedId ? [live.current.selectedId] : []);
    live.current.notice('Tick at least two components under Combine, then press Ctrl+G again.');
  }, [combine]);
  const ungroup = useCallback((id = live.current.selectedId) => {
    if (!controller || !id || !live.current.canEdit) return;
    const parts = controller.ungroup(id);
    if (!parts) { live.current.notice(controller.lastError); return; }
    cancel(); select(null);
    live.current.notice(`Ungrouped into ${parts.length} components. Select one to edit it, or undo to group them again.`);
  }, [cancel, controller, select]);
  const setPlacementMode = useCallback((mode: PlacementHeightMode) => {
    setHeightMode(mode);
    live.current.heightMode = mode;
    if (live.current.placing) setPlacementHeight(modulePlacementHeight(live.current.placing, mode, live.current.fixedY, live.current.connection));
    clearPreview();
    if (mode === 'fixed') live.current.onWorkLevel?.(live.current.fixedY);
  }, [clearPreview]);

  const rayAt = useCallback((x: number, y: number) => {
    const engine = viewer.current?.engine();
    if (!engine) return null;
    const rect = engine.surface.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    const ray = new mod.Raycaster();
    ray.setFromCamera(new mod.Vector2((x - rect.left) / rect.width * 2 - 1, 1 - (y - rect.top) / rect.height * 2), engine.camera);
    return ray;
  }, [mod, viewer]);

  const preview = useCallback((x: number, y: number, spec = live.current.placing) => {
    pointerAt.current = [x, y];
    const ray = rayAt(x, y);
    const height = spec && (live.current.placementHeight ?? modulePlacementHeight(spec, live.current.heightMode, live.current.fixedY, live.current.modules.find((m) => m.id === live.current.selectedId)));
    const at = ray && spec && height && studio.terrain && minePlacementPoint(ray.ray, studio.terrain, spec, height);
    const prepared = spec ? modulePlacementPreview(spec) : null;
    if (!at || !spec || !prepared?.bounds) { overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); setRefused(null); return null; }
    let position = snapModuleAt(spec, at, live.current.modules);
    // Surface snapping must not lift an entrance off its terrain or bury it in a neighbouring hill.
    if (height?.surface && studio.terrain && surfaceModuleAt(spec, position, studio.terrain)[1] !== position[1]) position = at;
    setHover((old) => old?.every((v, i) => v === position[i]) ? old : position);
    const previewKey = JSON.stringify([spec, position]);
    if (lastPreview.current !== previewKey) {
      if (!lastPreview.current && prepared.simplified) live.current.notice('Large component: showing its bounds while positioning. The full excavation is kept when placed.');
      lastPreview.current = previewKey;
      // A place it cannot go keeps its outline, red, with the controller's reason (the click then places nothing and says it).
      const reason = controller?.check(spec, position) ?? null;
      setRefused(reason);
      overlay.setMinePlacementGeometry(prepared.positions, position, reason !== null);
      overlay.setMinePorts(openPortCells(live.current.modules), joinedPortCells(spec, position, live.current.modules));
    }
    return position;
  }, [controller, overlay, rayAt, studio]);

  /** A quarter turn of the piece in hand (`by` 1 clockwise, 3 counter-clockwise), redrawn where the pointer is. */
  const turnPlacing = useCallback((by: 1 | 3) => {
    const spec = live.current.placing;
    if (!spec || !live.current.canEdit) return;
    const next = { ...spec, rotation: (spec.rotation + by) % 4 };
    live.current.placing = next;
    setPlacing(next);
    lastPreview.current = '';
    const p = pointerAt.current;
    if (p) preview(p[0], p[1], next);
  }, [preview]);

  /** PgUp / PgDn with nothing selected: the working level up or down by `by`, a piece in hand redrawn where the pointer is. */
  const stepLevel = useCallback((by: number) => {
    const from = live.current.heightMode === 'fixed' ? live.current.fixedY : live.current.placementHeight?.level ?? live.current.fixedY;
    setPlacementY(from + by);
    const p = pointerAt.current;
    if (p && live.current.placing) preview(p[0], p[1]);
    return true;
  }, [preview, setPlacementY]);

  /** Looks straight down on the design (the plot's middle when it is empty), at the working level when one is set. */
  const topView = useCallback(() => {
    const engine = viewer.current?.engine();
    if (!engine) return;
    const b = studio.canvas.bounds(), mid = Math.floor((plotMode().size - 1) / 2);
    const x = b ? Math.floor((b.min[0] + b.max[0]) / 2) : mid, z = b ? Math.floor((b.min[2] + b.max[2]) / 2) : mid;
    const y = live.current.heightMode === 'fixed' ? live.current.fixedY : b ? b.max[1] : (studio.terrain?.surface(x, z) ?? 63) + 1;
    const distance = b ? pictureDistance({ min: [b.min[0], y, b.min[2]], max: [b.max[0], y, b.max[2]] }, engine.camera.aspect, engine.camera.fov) : 64;
    engine.focus(x, y, z, { distance, pitch: 89.9 });
    engine.surface.focus();
  }, [studio, viewer]);
  /** Frames everything the design digs and places, from the current direction. */
  const frameAll = useCallback(() => {
    const engine = viewer.current?.engine(), b = studio.canvas.bounds();
    if (!engine) return;
    if (!b) { live.current.notice('Nothing to frame yet: place a layout or a preset first.'); return; }
    const center = b.min.map((v, axis) => (v + b.max[axis]!) / 2) as V3;
    engine.focus(...center, { distance: pictureDistance(b, engine.camera.aspect, engine.camera.fov) });
    engine.surface.focus();
  }, [studio, viewer]);

  const commit = useCallback((x: number, y: number, spec = live.current.placing) => {
    if (!controller || !spec || !live.current.canEdit) return;
    const at = preview(x, y, spec);
    if (!at) { live.current.notice('This piece does not fit here. Point inside the plot at its placement height.'); return; }
    const item = controller.add(spec, at);
    if (!item) { live.current.notice(controller.lastError ?? 'The piece could not be placed here.'); return; }
    cancel(); select(item.id);
    viewer.current?.engine()?.surface.focus();
    live.current.notice(`${item.spec.label || moduleName(item.spec.kind)} placed at ${item.at.join(', ')}. Drag it or an axis to move it.`);
  }, [cancel, controller, preview, select, viewer]);

  /**
   * What a key does here, or null when it is not this hook's. A piece in hand takes the keys first: [ ] and R turn it, Escape
   * drops it, and the arrows and Delete do nothing (they would act on the component selected before). Otherwise, with the
   * canvas focused, the copy keys and the selected component's frame, Delete and turn.
   */
  const keyAction = useCallback((event: KeyboardEvent): (() => void) | null => {
    const surface = bound.current;
    if (!controller || !surface) return null;
    // Escape during a body drag puts the piece back (before the page's Escape clears the selection).
    if (event.key === 'Escape' && bodyPress.current?.started) return () => cancelBody.current();
    if (live.current.placing) {
      if (event.key === 'Escape') return cancel;
      const plain = !event.ctrlKey && !event.metaKey && !event.altKey;
      if (plain && !isTyping(event.target)) {
        if (event.key === '[' || event.key === ']' || event.code === 'KeyR') {
          const back = event.key === '[' || (event.code === 'KeyR' && event.shiftKey);
          return () => turnPlacing(back ? 3 : 1);
        }
        // PgUp / PgDn step the working level the piece goes in at, even while something is still selected.
        if (event.key === 'PageUp' || event.key === 'PageDown')
          return () => void stepLevel((event.key === 'PageUp' ? 1 : -1) * (event.shiftKey ? 5 : 1));
        const moves = studio.selection && event.key.startsWith('Arrow');
        if (moves || event.key === 'Delete' || event.key === 'Backspace') return () => undefined;
      }
    }
    // Ctrl+G and Ctrl+Shift+G also work from the outliner and its checklist, where the pieces are ticked; never while typing.
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'g' && live.current.canEdit
      && !(isTyping(event.target) && !(event.target instanceof HTMLInputElement && event.target.type === 'checkbox'))) {
      if (!event.shiftKey) return live.current.modules.length >= 2 ? combineChecked : null;
      const group = live.current.modules.find((m) => m.id === live.current.selectedId && m.spec.kind === 'custom');
      return group ? () => ungroup(group.id) : null;
    }
    if (document.activeElement !== surface) return null;
    if ((event.ctrlKey || event.metaKey) && !event.altKey) {
      const name = event.key.toLowerCase();
      if (name === 'v' && live.current.clipboard) return paste;
      if ((name === 'c' || name === 'd') && live.current.selectedId) return name === 'c' ? () => void copy() : () => duplicate();
    }
    if (!live.current.selectedId) return null;
    if (!event.ctrlKey && !event.metaKey && (event.code === 'KeyF' || event.code === 'NumpadDecimal')) return () => frame();
    if (!live.current.canEdit) return null;
    const item = controller.snapshot().find((m) => m.id === live.current.selectedId);
    if (!item) return null;
    if (event.key === 'Delete' || event.key === 'Backspace') return () => { controller.remove(item.id); select(null); };
    if (event.key !== '[' && event.key !== ']') return null;
    return () => {
      if (controller.update(item.id, { ...item.spec, rotation: (item.spec.rotation + (event.key === ']' ? 1 : 3)) % 4 })) select(item.id);
      else live.current.notice(controller.lastError);
    };
  }, [cancel, combineChecked, controller, copy, duplicate, frame, paste, select, stepLevel, studio, turnPlacing, ungroup]);
  /** True when this hook's key listener takes the key: the page's own key handling then leaves it alone. */
  const handlesKey = useCallback((event: KeyboardEvent) => keyAction(event) !== null, [keyAction]);

  useEffect(() => {
    if (!controller || !ready) return;
    const surface = viewer.current?.engine()?.surface;
    if (!surface) return;
    bound.current = surface;
    const down = (event: PointerEvent) => {
      // A right click puts the piece in hand down; it never reaches the canvas tools.
      if (event.button === 2 && live.current.placing && !document.pointerLockElement) {
        event.preventDefault(); event.stopImmediatePropagation(); cancel(); return;
      }
      if (event.button !== 0 || mineCameraGesture(event) || document.pointerLockElement) return;
      if (live.current.placing) {
        event.preventDefault(); event.stopImmediatePropagation(); commit(event.clientX, event.clientY); return;
      }
      if (!live.current.selecting) return;
      const ray = rayAt(event.clientX, event.clientY);
      if (!ray || overlay.pickHandle(ray)) return;
      let nearest: { id: string; distance: number } | null = null;
      for (const item of controller.snapshot()) {
        if (item.visible === false) continue;
        for (const cell of moduleCells(item.spec, item.at).cells) {
          const at: V3 = [cell[0], cell[1], cell[2]];
          const distance = mineRayBox(ray.ray, at, at);
          if (distance !== null && (!nearest || distance < nearest.distance)) nearest = { id: item.id, distance };
        }
      }
      if (!nearest) return;
      event.preventDefault(); event.stopImmediatePropagation(); select(nearest.id); surface.focus();
      const item = controller.snapshot().find((m) => m.id === nearest.id);
      const terrain = live.current.studio.terrain;
      const grab = item && terrain && minePlacementPoint(ray.ray, terrain, item.spec, { mode: 'fixed', surface: false, level: item.at[1] });
      bodyPress.current = item && grab && live.current.canEdit
        ? { pointer: event.pointerId, x: event.clientX, y: event.clientY, id: item.id, spec: item.spec, at: item.at, grab, started: false, delta: [0, 0, 0] }
        : null;
    };
    const move = (event: PointerEvent) => { if (live.current.placing && !event.altKey && !(event.buttons & 4)) preview(event.clientX, event.clientY); };
    const leave = () => { overlay.setMinePlacement(null); lastPreview.current = ''; pointerAt.current = null; setHover(null); setRefused(null); };
    const key = (event: KeyboardEvent) => {
      const act = keyAction(event);
      if (!act) return;
      event.preventDefault(); event.stopImmediatePropagation(); act();
    };
    const dragEnd = () => { if (nativeDrag.current) cancel(); };
    surface.addEventListener('pointerdown', down, true);
    surface.addEventListener('pointermove', move);
    surface.addEventListener('pointerleave', leave);
    window.addEventListener('keydown', key, true);
    window.addEventListener('dragend', dragEnd);
    return () => {
      if (bound.current === surface) bound.current = null;
      surface.removeEventListener('pointerdown', down, true);
      surface.removeEventListener('pointermove', move);
      surface.removeEventListener('pointerleave', leave);
      window.removeEventListener('keydown', key, true);
      window.removeEventListener('dragend', dragEnd);
    };
  }, [cancel, commit, controller, keyAction, overlay, preview, rayAt, ready, select, viewer]);

  const moveSelected = useCallback((delta: V3, group?: string) => {
    const item = controller?.snapshot().find((m) => m.id === selectedId);
    if (!controller || !item) return false;
    if (!live.current.canEdit) return true;
    const at: V3 = [item.at[0] + delta[0], item.at[1] + delta[1], item.at[2] + delta[2]];
    if (controller.move(item.id, at, group)) select(item.id);
    else live.current.notice(controller.lastError);
    return true;
  }, [controller, select, selectedId]);

  // A gizmo drag moves only the outline and the handles; the layout, its cells and the readouts change once, on release.
  const endDrag = useCallback(() => {
    dragging.current = null;
    setRefused(null);
    overlay.setMinePlacement(null);
    overlay.setSelection(studio.selection);
  }, [overlay, studio]);
  const dragStart = useCallback(() => {
    const item = controller?.snapshot().find((m) => m.id === live.current.selectedId);
    const box = studio.selection;
    if (!item || !box || box !== ownedSelection.current || !live.current.canEdit || live.current.placing) return false;
    dragging.current = { id: item.id, spec: item.spec, at: item.at, min: box.min, max: box.max };
    return true;
  }, [controller, studio]);
  const dragPreview = useCallback((delta: V3) => {
    const d = dragging.current;
    const prepared = d ? modulePlacementPreview(d.spec) : null;
    if (!d || !prepared?.bounds || !controller) return false;
    const at = plus(d.at, delta);
    // Shown where it would go, red with the controller's reason when it cannot stand there: the release then moves nothing.
    const reason = controller.check(d.spec, at);
    setRefused(reason);
    overlay.setMinePlacementGeometry(prepared.positions, at, reason !== null);
    overlay.setMinePorts(openPortCells(live.current.modules, d.id), joinedPortCells(d.spec, at, live.current.modules, d.id));
    overlay.setSelection({ min: plus(d.min, delta), max: plus(d.max, delta) });
    return true;
  }, [controller, overlay]);
  const dragCommit = useCallback((delta: V3) => {
    const d = dragging.current;
    endDrag();
    if (!d || !controller || !live.current.canEdit || delta.every((v) => v === 0)) return;
    if (controller.move(d.id, plus(d.at, delta))) select(d.id);
    else if (controller.lastError) live.current.notice(controller.lastError);
  }, [controller, endDrag, select]);
  const dragCancel = useCallback(() => { if (dragging.current) endDrag(); }, [endDrag]);

  // A body drag (see bodyPress): window listeners, so the pointer may leave the view while it is held.
  useEffect(() => {
    const BODY_DRAG_PX = 5;
    const stop = () => {
      const b = bodyPress.current;
      bodyPress.current = null;
      if (b?.started) dragCancel();
    };
    cancelBody.current = stop;
    const move = (event: PointerEvent) => {
      const b = bodyPress.current;
      if (!b || event.pointerId !== b.pointer || !studio.terrain) return;
      if (!b.started) {
        if (Math.hypot(event.clientX - b.x, event.clientY - b.y) < BODY_DRAG_PX) return;
        if (!dragStart() || dragging.current?.id !== b.id) { dragCancel(); bodyPress.current = null; return; }
        b.started = true;
      }
      const ray = rayAt(event.clientX, event.clientY);
      const point = ray && minePlacementPoint(ray.ray, studio.terrain, b.spec, { mode: 'fixed', surface: false, level: b.at[1] });
      if (!point) return;
      // Across its own level only (the handles move it up and down), snapped to the nearest open port of another piece.
      const at: V3 = [b.at[0] + point[0] - b.grab[0], b.at[1], b.at[2] + point[2] - b.grab[2]];
      const snapped = snapModuleAt(b.spec, at, live.current.modules, b.id);
      const delta: V3 = [snapped[0] - b.at[0], 0, snapped[2] - b.at[2]];
      if (delta[0] === b.delta[0] && delta[2] === b.delta[2]) return;
      b.delta = delta;
      dragPreview(delta);
    };
    const up = (event: PointerEvent) => {
      const b = bodyPress.current;
      if (!b || event.pointerId !== b.pointer) return;
      bodyPress.current = null;
      if (b.started) dragCommit(b.delta);
    };
    const cancelled = (event: PointerEvent) => { if (event.pointerId === bodyPress.current?.pointer) stop(); };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, true);
    window.addEventListener('pointercancel', cancelled, true);
    window.addEventListener('blur', stop);
    return () => {
      stop();
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up, true);
      window.removeEventListener('pointercancel', cancelled, true);
      window.removeEventListener('blur', stop);
    };
  }, [dragCancel, dragCommit, dragPreview, dragStart, rayAt, studio]);

  // Arrow keys: the presses of one frame are one move, and a held key is one undo step (see keyMoves).
  const nudgeMove = useRef<(delta: V3, group: string) => void>(() => undefined);
  useEffect(() => {
    nudgeMove.current = (delta, group) => {
      const item = controller?.snapshot().find((m) => m.id === live.current.selectedId);
      if (!controller || !item || !live.current.canEdit) return;
      if (controller.move(item.id, plus(item.at, delta), group)) select(item.id);
      else live.current.notice(controller.lastError);
    };
  });
  const nudger = useRef<((delta: V3) => void) | null>(null);
  /** True when a component is selected (a read-only page takes the key and moves nothing). */
  const nudgeSelected = useCallback((delta: V3) => {
    if (!controller?.snapshot().some((m) => m.id === live.current.selectedId)) return false;
    if (live.current.canEdit) (nudger.current ??= keyMoves((d, group) => nudgeMove.current(d, group)))(delta);
    return true;
  }, [controller]);

  /** True, and says why, when a raw move, turn or mirror of `s` would shift cells a component owns from under it. */
  const refuseRaw = useCallback((s: Selection) => {
    if (!controller?.ownsAny(s)) return false;
    live.current.notice('Select the component to move it.');
    return true;
  }, [controller]);

  const dragOver = (event: DragEvent) => {
    if (!controller || !live.current.canEdit || !event.dataTransfer.types.includes(MINE_MODULE_MIME)) return;
    event.preventDefault(); event.dataTransfer.dropEffect = 'copy';
    if (live.current.placing) preview(event.clientX, event.clientY);
  };
  const drop = (event: DragEvent) => {
    if (!controller || !live.current.canEdit) return;
    const spec = decodeModuleDrag(event.dataTransfer.getData(MINE_MODULE_MIME));
    if (!spec) return;
    event.preventDefault(); event.stopPropagation(); commit(event.clientX, event.clientY, spec);
  };

  return {
    modules, selectedId: selected?.id ?? null, placing, placementY: placementHeight?.level ?? fixedY,
    placementMode: placementHeight?.mode ?? heightMode, placementSurface: placementHeight?.surface ?? heightMode === 'surface', setPlacementMode,
    hover, setPlacementY, select, arm, cancel, moveSelected, frame, position,
    dragStart, dragPreview, dragCommit, dragCancel, nudgeSelected,
    /** Why the outline in view cannot be placed or moved there, or null. */
    refused, handlesKey, refuseRaw,
    clipboard, copy, paste, duplicate, combine, ungroup, checked, setChecked,
    /** The working level (feet Y) new pieces and pastes go to, or null while placement height is automatic or on the surface. */
    workLevel: heightMode === 'fixed' ? fixedY : null,
    stepLevel, topView, frameAll,
    /** How the layout opened, when the reader should know (unsaved work recovered, components left out); else null. */
    restoreNote: controller?.restoreNote ?? null,
    updatePlacing: (spec: MineModuleSpec) => { if (live.current.canEdit) { setPlacing(spec); lastPreview.current = ''; } },
    rememberDesign: (id: number, revision: number) => controller?.rememberDesign(id, revision),
    setVisible: (id: string, visible: boolean) => controller?.setVisible(id, visible),
    insertPreset: (preset: MinePreset) => {
      if (!controller || !live.current.canEdit) return false;
      const items = controller.addMany(presetModules(preset, studio.terrain), `Add ${preset.name}`);
      if (!items?.length) { live.current.notice(controller.lastError ?? 'Could not insert this preset.'); return false; }
      cancel(); select(items[0]!.id);
      void studio.setAnchor(moduleCells(items[0]!.spec, items[0]!.at).entry).catch(() => undefined);
      live.current.notice(`${preset.name}: ${items.length} editable components. Select one to move, resize or remove it.`);
      return true;
    },
    dragOver, drop,
    move,
    update: (id: string, spec: MineModuleSpec) => {
      if (!live.current.canEdit) return;
      if (controller?.update(id, spec)) select(id); else live.current.notice(controller?.lastError ?? 'Cannot update this piece.');
    },
    remove: (id: string) => {
      // An outliner row can remove a piece that is not the selected one: the selection then stays.
      if (live.current.canEdit && controller?.remove(id)) { cancel(); if (id === live.current.selectedId) select(null); }
    },
  };
}
