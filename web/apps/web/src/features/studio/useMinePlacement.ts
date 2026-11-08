'use client';

import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type DragEvent, type RefObject } from 'react';
import { keyOf, maskSelection, type Selection, type V3 } from './canvas';
import type { Overlay3D } from './overlay3d';
import type { Studio } from './studio';
import type * as ViewerMod from './studio-viewer';
import { MineModuleController } from './mine/MineModuleController';
import { decodeModuleDrag, MINE_MODULE_MIME, moduleCells, snapModuleAt, type MineModuleInstance, type MineModuleSpec } from './mine/modules';
import { minePlanePoint, mineRayBox } from './mine/placementRay';

const NONE: readonly MineModuleInstance[] = [];
const noSubscription = () => () => undefined;

/** Pointer placement and selection; the controller owns cells, overlapping modules and undo history. */
export function useMinePlacement(p: {
  studio: Studio;
  mod: typeof ViewerMod;
  viewer: RefObject<WorldViewerHandle | null>;
  overlay: Overlay3D;
  ready: boolean;
  canEdit: boolean;
  selecting: boolean;
  notice: (text: string | null) => void;
}) {
  const { studio, mod, viewer, overlay, ready } = p;
  const controller = useMemo(() => studio.mine ? new MineModuleController(studio) : null, [studio]);
  const subscribe = useCallback((fn: () => void) => controller?.subscribe(fn) ?? noSubscription(), [controller]);
  const getSnapshot = useCallback(() => controller?.snapshot() ?? NONE, [controller]);
  const modules = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [placing, setPlacing] = useState<MineModuleSpec | null>(null);
  const [movingId, setMovingId] = useState<string | null>(null);
  const [placementY, setPlacementY] = useState(11);
  const [hover, setHover] = useState<V3 | null>(null);
  const ownedSelection = useRef<Selection | null>(null);
  const lastPreview = useRef('');
  const nativeDrag = useRef(false);
  const selected = modules.find((m) => m.id === selectedId) ?? null;
  const live = useRef({ ...p, modules, placing, movingId, placementY, selectedId });
  useEffect(() => { live.current = { ...p, modules, placing, movingId, placementY, selectedId }; });
  useEffect(() => () => controller?.dispose(), [controller]);

  const select = useCallback((id: string | null) => {
    const item = controller?.snapshot().find((m) => m.id === id);
    setSelectedId(item?.id ?? null);
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
    setPlacing(null); setMovingId(null); setHover(null);
    lastPreview.current = '';
    nativeDrag.current = false;
    overlay.setMinePlacement(null);
  }, [overlay]);

  const arm = useCallback((spec: MineModuleSpec, id: string | null = null, options?: { drag?: boolean }) => {
    if (!live.current.canEdit) return;
    if (document.pointerLockElement) document.exitPointerLock();
    const existing = controller?.snapshot().find((m) => m.id === id);
    const level = existing?.at[1] ?? live.current.placementY;
    if (existing) setPlacementY(level);
    setPlacing(spec); setMovingId(id); setHover(null);
    nativeDrag.current = options?.drag ?? false;
    lastPreview.current = '';
    if (!options?.drag) viewer.current?.engine()?.focus(existing?.at[0] ?? 31, level, existing?.at[2] ?? 31, { distance: 40, pitch: 55 });
    live.current.notice('Point at the mine to preview. Click to place; Escape cancels.');
  }, [controller, viewer]);

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
    const at = ray && minePlanePoint(ray.ray, live.current.placementY);
    if (!at || !spec) { overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); return null; }
    const position = snapModuleAt(spec, at, live.current.modules, live.current.movingId ?? undefined);
    const cells = moduleCells(spec, position).cells;
    if (!cells.length || cells.some((c) => c[0] < 0 || c[0] > 63 || c[2] < 0 || c[2] > 63 || c[1] < 1 || c[1] > 127)) {
      overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); return null;
    }
    setHover((old) => old?.every((v, i) => v === position[i]) ? old : position);
    const previewKey = JSON.stringify([spec, position]);
    if (lastPreview.current !== previewKey) {
      lastPreview.current = previewKey;
      overlay.setMinePlacement(cells.map((c) => [c[0], c[1], c[2]] as V3));
    }
    return position;
  }, [overlay, rayAt]);

  const commit = useCallback((x: number, y: number, spec = live.current.placing) => {
    if (!controller || !spec || !live.current.canEdit) return;
    const at = preview(x, y, spec);
    if (!at) { live.current.notice('This piece does not fit here. Point inside the plot at the chosen Y level.'); return; }
    const id = live.current.movingId;
    // Moving with edited dimensions should be one undoable replacement, handled by the controller.
    const item = id ? (controller.replace(id, spec, at) ? controller.snapshot().find((m) => m.id === id) : null) : controller.add(spec, at);
    if (!item) { live.current.notice(controller.lastError ?? 'The piece could not be placed here.'); return; }
    cancel(); select(item.id);
    viewer.current?.engine()?.surface.focus();
    live.current.notice(`${moduleCells(item.spec, item.at).label} placed at ${item.at.join(', ')}. Drag an axis to move it.`);
  }, [cancel, controller, preview, select, viewer]);

  useEffect(() => {
    if (!controller || !ready) return;
    const surface = viewer.current?.engine()?.surface;
    if (!surface) return;
    const down = (event: PointerEvent) => {
      if (event.button !== 0 || document.pointerLockElement) return;
      if (live.current.placing) {
        event.preventDefault(); event.stopImmediatePropagation(); commit(event.clientX, event.clientY); return;
      }
      if (!live.current.selecting) return;
      const ray = rayAt(event.clientX, event.clientY);
      if (!ray || overlay.pickHandle(ray)) return;
      let nearest: { id: string; distance: number } | null = null;
      for (const item of controller.snapshot()) {
        for (const cell of moduleCells(item.spec, item.at).cells) {
          const at: V3 = [cell[0], cell[1], cell[2]];
          const distance = mineRayBox(ray.ray, at, at);
          if (distance !== null && (!nearest || distance < nearest.distance)) nearest = { id: item.id, distance };
        }
      }
      if (nearest) { event.preventDefault(); event.stopImmediatePropagation(); select(nearest.id); surface.focus(); }
    };
    const move = (event: PointerEvent) => { if (live.current.placing) preview(event.clientX, event.clientY); };
    const leave = () => { overlay.setMinePlacement(null); lastPreview.current = ''; setHover(null); };
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && live.current.placing) {
        event.preventDefault(); event.stopImmediatePropagation(); cancel(); return;
      }
      if (document.activeElement !== surface || !live.current.selectedId || !live.current.canEdit) return;
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
  }, [cancel, commit, controller, overlay, preview, rayAt, ready, select, viewer]);

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
    modules, selectedId: selected?.id ?? null, placing, placementY, hover, setPlacementY, select, arm, cancel, moveSelected,
    updatePlacing: (spec: MineModuleSpec) => { if (live.current.canEdit) { setPlacing(spec); lastPreview.current = ''; } },
    rememberDesign: (id: number, revision: number) => controller?.rememberDesign(id, revision),
    dragOver, drop,
    move: (id: string) => { const item = controller?.snapshot().find((m) => m.id === id); if (item) arm(item.spec, id); },
    update: (id: string, spec: MineModuleSpec) => {
      if (!live.current.canEdit) return;
      if (controller?.update(id, spec)) select(id); else live.current.notice(controller?.lastError ?? 'Cannot update this piece.');
    },
    remove: (id: string) => { if (live.current.canEdit && controller?.remove(id)) { cancel(); select(null); } },
  };
}
