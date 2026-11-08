'use client';

import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type DragEvent, type RefObject } from 'react';
import { inside, keyOf, maskSelection, plotMode, type Selection, type V3 } from './canvas';
import type { Overlay3D } from './overlay3d';
import type { Studio } from './studio';
import type * as ViewerMod from './studio-viewer';
import { MineModuleController } from './mine/MineModuleController';
import { decodeModuleDrag, MINE_MODULE_MIME, moduleCells, moduleName, snapModuleAt, type MineModuleInstance, type MineModuleSpec } from './mine/modules';
import { mineRayBox } from './mine/placementRay';
import { minePlacementPoint, modulePlacementHeight, surfaceModuleAt, type PlacementHeight, type PlacementHeightMode } from './mine/placementHeight';
import { modulePlacementPreview } from './mine/placementPreview';
import { pictureDistance } from './PictureControls';
import { mineCameraGesture } from './studioPointer';
import { presetModules, type MinePreset } from './mine/presets';

const NONE: readonly MineModuleInstance[] = [];
const noSubscription = () => () => undefined;
type ComponentClip = { spec: MineModuleSpec; level: number };

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
  notice: (text: string | null) => void;
}) {
  const { studio, mod, viewer, overlay, ready } = p;
  const controller = useMemo(() => studio.mine ? new MineModuleController(studio) : null, [studio]);
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
  const [hover, setHover] = useState<V3 | null>(null);
  const ownedSelection = useRef<Selection | null>(null);
  const selectedShape = useRef<{ id: string; spec: MineModuleSpec; at: V3 } | null>(null);
  const lastPreview = useRef('');
  const nativeDrag = useRef(false);
  const selected = modules.find((m) => m.id === selectedId) ?? null;
  const live = useRef({ ...p, modules, placing, heightMode, fixedY, placementHeight, connection, clipboard, selectedId });
  useEffect(() => { live.current = { ...p, modules, placing, heightMode, fixedY, placementHeight, connection, clipboard, selectedId }; });
  useEffect(() => () => controller?.dispose(), [controller]);
  useEffect(() => {
    if (controller?.recoveredLocal) live.current.notice('Recovered saved components from this browser. Save the design to keep them with its revision on the server.');
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
    setPlacing(null); setPlacementHeight(null); setConnection(null); setHover(null);
    lastPreview.current = '';
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
    lastPreview.current = ''; setHover(null); overlay.setMinePlacement(null);
  }, [overlay]);
  const setPlacementY = useCallback((y: number) => {
    if (!Number.isFinite(y)) return;
    const level = Math.max(plotMode().minY + 1, Math.min(plotMode().maxY - 2, Math.round(y)));
    setFixedY(level); setHeightMode('fixed');
    if (live.current.placing) setPlacementHeight({ mode: 'fixed', surface: false, level });
    clearPreview();
  }, [clearPreview]);

  const copy = useCallback((id = live.current.selectedId) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    if (!item) return null;
    const clip = { spec: structuredClone(item.spec), level: item.at[1] };
    setClipboard(clip);
    live.current.notice(`Copied ${item.spec.label || moduleName(item.spec.kind)} with its dimensions, rotation and entrance Y${clip.level}. Paste to place another.`);
    return clip;
  }, [controller]);
  const pasteClip = useCallback((clip: ComponentClip | null) => {
    if (!clip || !live.current.canEdit) return;
    live.current.activateSelection?.();
    if (document.pointerLockElement) document.exitPointerLock();
    setPlacing(structuredClone(clip.spec)); setPlacementHeight({ mode: 'fixed', surface: false, level: clip.level }); setConnection(null);
    nativeDrag.current = false;
    clearPreview();
    live.current.notice(`Place a copy at Y${clip.level}. Its settings are preserved; click to place or Escape to cancel.`);
  }, [clearPreview]);
  const paste = useCallback(() => { pasteClip(live.current.clipboard); }, [pasteClip]);
  const duplicate = useCallback((id = live.current.selectedId) => {
    if (live.current.canEdit) pasteClip(copy(id));
  }, [copy, pasteClip]);
  const combine = useCallback((ids: string[]) => {
    if (!controller || !live.current.canEdit) return;
    const item = controller.combine(ids);
    if (!item) { live.current.notice(controller.lastError ?? 'Choose at least two components to combine.'); return; }
    live.current.activateSelection?.();
    cancel(); select(item.id);
    live.current.notice(`Combined ${ids.length} pieces into one component. Move, rotate, copy or rename it in the inspector.`);
  }, [cancel, controller, select]);
  const setPlacementMode = useCallback((mode: PlacementHeightMode) => {
    setHeightMode(mode);
    if (live.current.placing) setPlacementHeight(modulePlacementHeight(live.current.placing, mode, live.current.fixedY, live.current.connection));
    clearPreview();
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
    const ray = rayAt(x, y);
    const height = spec && (live.current.placementHeight ?? modulePlacementHeight(spec, live.current.heightMode, live.current.fixedY, live.current.modules.find((m) => m.id === live.current.selectedId)));
    const at = ray && spec && height && studio.terrain && minePlacementPoint(ray.ray, studio.terrain, spec, height);
    if (!at || !spec) { overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); return null; }
    let position = snapModuleAt(spec, at, live.current.modules);
    // Surface snapping must not lift an entrance off its terrain or bury it in a neighbouring hill.
    if (height?.surface && studio.terrain && surfaceModuleAt(spec, position, studio.terrain)[1] !== position[1]) position = at;
    const prepared = modulePlacementPreview(spec);
    const min = prepared.bounds?.min.map((v, i) => v + position[i]!) as V3 | undefined;
    const max = prepared.bounds?.max.map((v, i) => v + position[i]!) as V3 | undefined;
    if (!min || !max || min[1] <= plotMode().minY || !inside(...min) || !inside(...max)) {
      overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); return null;
    }
    setHover((old) => old?.every((v, i) => v === position[i]) ? old : position);
    const previewKey = JSON.stringify([spec, position]);
    if (lastPreview.current !== previewKey) {
      if (!lastPreview.current && prepared.simplified) live.current.notice('Large component: showing its bounds while positioning. The full excavation is kept when placed.');
      lastPreview.current = previewKey;
      overlay.setMinePlacementGeometry(prepared.positions, position);
    }
    return position;
  }, [overlay, rayAt, studio]);

  const commit = useCallback((x: number, y: number, spec = live.current.placing) => {
    if (!controller || !spec || !live.current.canEdit) return;
    const at = preview(x, y, spec);
    if (!at) { live.current.notice('This piece does not fit here. Point inside the plot at its placement height.'); return; }
    const item = controller.add(spec, at);
    if (!item) { live.current.notice(controller.lastError ?? 'The piece could not be placed here.'); return; }
    cancel(); select(item.id);
    viewer.current?.engine()?.surface.focus();
    live.current.notice(`${item.spec.label || moduleName(item.spec.kind)} placed at ${item.at.join(', ')}. Drag an axis to move it.`);
  }, [cancel, controller, preview, select, viewer]);

  useEffect(() => {
    if (!controller || !ready) return;
    const surface = viewer.current?.engine()?.surface;
    if (!surface) return;
    const down = (event: PointerEvent) => {
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
      if (nearest) { event.preventDefault(); event.stopImmediatePropagation(); select(nearest.id); surface.focus(); }
    };
    const move = (event: PointerEvent) => { if (live.current.placing && !event.altKey && !(event.buttons & 4)) preview(event.clientX, event.clientY); };
    const leave = () => { overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); };
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && live.current.placing) {
        event.preventDefault(); event.stopImmediatePropagation(); cancel(); return;
      }
      if (document.activeElement !== surface) return;
      if ((event.ctrlKey || event.metaKey) && !event.altKey) {
        const name = event.key.toLowerCase();
        if ((name === 'v' && live.current.clipboard) || ((name === 'c' || name === 'd') && live.current.selectedId)) {
          event.preventDefault(); event.stopImmediatePropagation();
          if (name === 'c') copy(); else if (name === 'v') paste(); else duplicate();
          return;
        }
      }
      if (!live.current.selectedId) return;
      if (!event.ctrlKey && !event.metaKey && (event.code === 'KeyF' || event.code === 'NumpadDecimal')) {
        event.preventDefault(); event.stopImmediatePropagation(); frame(); return;
      }
      if (!live.current.canEdit) return;
      const item = controller.snapshot().find((m) => m.id === live.current.selectedId);
      if (!item) return;
      if (event.key === 'Delete' || event.key === 'Backspace') {
        event.preventDefault(); event.stopImmediatePropagation(); controller.remove(item.id); select(null);
      } else if (event.key === '[' || event.key === ']') {
        event.preventDefault(); event.stopImmediatePropagation();
        if (controller.update(item.id, { ...item.spec, rotation: (item.spec.rotation + (event.key === ']' ? 1 : 3)) % 4 })) select(item.id);
        else live.current.notice(controller.lastError);
      }
    };
    const dragEnd = () => { if (nativeDrag.current) cancel(); };
    surface.addEventListener('pointerdown', down, true);
    surface.addEventListener('pointermove', move);
    surface.addEventListener('pointerleave', leave);
    window.addEventListener('keydown', key, true);
    window.addEventListener('dragend', dragEnd);
    return () => {
      surface.removeEventListener('pointerdown', down, true);
      surface.removeEventListener('pointermove', move);
      surface.removeEventListener('pointerleave', leave);
      window.removeEventListener('keydown', key, true);
      window.removeEventListener('dragend', dragEnd);
    };
  }, [cancel, commit, controller, copy, duplicate, frame, overlay, paste, preview, rayAt, ready, select, viewer]);

  const moveSelected = useCallback((delta: V3, group?: string) => {
    const item = controller?.snapshot().find((m) => m.id === selectedId);
    if (!controller || !item) return false;
    if (!live.current.canEdit) return true;
    const at: V3 = [item.at[0] + delta[0], item.at[1] + delta[1], item.at[2] + delta[2]];
    if (controller.move(item.id, at, group)) select(item.id);
    else live.current.notice(controller.lastError);
    return true;
  }, [controller, select, selectedId]);

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
    clipboard, copy, paste, duplicate, combine,
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
    remove: (id: string) => { if (live.current.canEdit && controller?.remove(id)) { cancel(); select(null); } },
  };
}
