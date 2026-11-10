'use client';

/**
 * A redstone design's components in the studio page: the controller (./RedstoneComponentController) on the page's studio,
 * the two things a click in the world can mean while the Library tab asks for it (where a component goes, with a ghost
 * following the mouse when it is not locked, or which cells around a selection are a new component's ports), and the
 * components under the mouse and the keys: a press selects one
 * and drags it without locking the view, and the selected one turns, mirrors, moves, duplicates and goes as a whole. The
 * outliner's actions are here too: frame, combine the ticked ones (Ctrl+G) and ungroup (Ctrl+Shift+G). Any other kind of
 * design gets an inert result.
 */
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget, WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type RefObject } from 'react';
import { isTyping } from '@/features/world/directives';
import { inside, plotMode, type Selection, type V3 } from '../canvas';
import { blockedBefore, levelPoint, nearestHit, rayFromPointer, type PlacementRay } from '../components/levelRay';
import { FACE_NORMAL } from '../facing';
import type { Overlay3D, PortArrow } from '../overlay3d';
import { pictureDistance } from '../PictureControls';
import type { Studio } from '../studio';
import type * as ViewerMod from '../studio-viewer';
import { keyMoves } from '../studioInput';
import type { NoticeOptions } from '../studioSave';
import type { ComponentInstance } from './componentData';
import { portSideOf } from './ports';
import { RedstoneComponentController, type ComponentSnapshot } from './RedstoneComponentController';
import {
  bindComponentPointer,
  bindPlaceGhost,
  componentKeyAction,
  RedstoneComponentDrag,
  type ComponentHit,
} from './redstoneInput';

/** What the next click in the world does. */
export type ComponentPick =
  | { kind: 'place'; def: string; turns: number; mirror: 'x' | 'z' | null; snap: boolean }
  | { kind: 'port'; port: 'in' | 'out' };

export interface RedstoneComponents {
  /** Null when the design is not a redstone one. */
  controller: RedstoneComponentController | null;
  snap: ComponentSnapshot;
  pick: ComponentPick | null;
  setPick: (p: ComponentPick | null) => void;
  /** Cells clicked while picking ports, in order. */
  picked: ReadonlyArray<{ at: Pos; kind: 'in' | 'out' }>;
  clearPicked: () => void;
  /** The selected component (its box is the studio's selection), or null. */
  selectedId: string | null;
  select: (id: string | null) => void;
  /** Why the component being dragged cannot stand where its outline is, or null. */
  refused: string | null;
  /** The components' key listener takes this key: the page's own key rules leave it alone. */
  handlesKey: (e: KeyboardEvent) => boolean;
  /** Arrow keys on the selected component: true when it took the move (a held key is one undo step). */
  nudgeSelected: (delta: V3) => boolean;
  /** A move of the selected component by `delta` (the gizmo): true when it took the move. */
  moveSelected: (delta: V3, group?: string) => boolean;
  /** A gizmo press on the selected component: true when it holds it, and the handle then only previews the move. */
  dragStart: () => boolean;
  /** Shows the held component moved by `delta` (red where it cannot stand); false when none is held. */
  dragPreview: (delta: V3) => boolean;
  /** The gizmo's release: one move by `delta` (one undo step). */
  dragCommit: (delta: V3) => void;
  /** Escape or a lost pointer: the held component goes back. */
  dragCancel: () => void;
  /** The user may change the components now (the page allows edits and no simulation is live). */
  editable: boolean;
  /** Selects a component and centres the view on it. */
  frame: (id: string) => void;
  /** The outliner's Combine checklist: the ticked components, null while it is closed. Ctrl+G combines them. */
  checked: string[] | null;
  setChecked: (ids: string[] | null) => void;
  /** Combines components into one of the design's own, which is then selected. */
  combine: (ids: readonly string[]) => void;
  /** Gives a combined component's parts back (one of the design's own without parts becomes plain blocks). */
  ungroup: (id: string) => void;
  /** A copy beside the component, which is then selected. */
  duplicate: (id: string) => void;
  remove: (id: string) => void;
}

const EMPTY: ComponentSnapshot = { components: [], customDefs: [], error: null, recovered: false };
const noStore = () => () => undefined;
const emptySnap = () => EMPTY;

/** The cell a click aims at (`on`) and the free cell in front of the face it hit (`front`), in canvas coordinates. */
export function aimedCells(t: AimTarget | null): { on: Pos; front: Pos | null } | null {
  if (!t || t.kind !== 'block') return null;
  const y = t.y - plotMode().baseY;
  if (!inside(t.x, y, t.z)) return null;
  const n = FACE_NORMAL[t.face];
  const front: Pos | null = n ? [t.x + n[0], y + n[1], t.z + n[2]] : null;
  return { on: [t.x, y, t.z], front: front && inside(front[0], front[1], front[2]) ? front : null };
}

export function useRedstoneComponents(p: {
  enabled: boolean;
  studio: Studio;
  viewer: RefObject<WorldViewerHandle | null>;
  stage: RefObject<HTMLElement | null>;
  /** A simulation is live: the world's clicks are the circuit's then. */
  live: boolean;
  /** The view's overlay and three's classes: without them the mouse does not select or drag components. */
  overlay?: Overlay3D;
  mod?: Pick<typeof ViewerMod, 'Raycaster' | 'Vector2'>;
  /** The viewer is up. */
  ready?: boolean;
  /** The user may change the design now (not read-only, busy or saving). */
  canEdit?: boolean;
  /** The Select tool is on: a click with the mouse locked selects the component under the crosshair. */
  selecting?: boolean;
  notice?: (text: string | null, o?: NoticeOptions) => void;
}): RedstoneComponents {
  const { enabled, studio, viewer, stage, live, overlay, mod, ready = false } = p;
  // Made in an effect, not while rendering: opening it can write the design's metadata, which the page listens to.
  const [controller, setController] = useState<RedstoneComponentController | null>(null);
  useEffect(() => {
    if (!enabled) return;
    const c = new RedstoneComponentController(studio);
    setController(c);
    return () => {
      c.dispose();
      setController(null);
    };
  }, [enabled, studio]);
  const subscribe = useCallback((fn: () => void) => controller?.subscribe(fn) ?? noStore(), [controller]);
  const read = useCallback(() => controller?.snapshot() ?? EMPTY, [controller]);
  const snap = useSyncExternalStore(subscribe, controller ? read : emptySnap, controller ? read : emptySnap);

  const [pick, setPick] = useState<ComponentPick | null>(null);
  const [picked, setPicked] = useState<ReadonlyArray<{ at: Pos; kind: 'in' | 'out' }>>([]);
  const clearPicked = useCallback(() => setPicked([]), []);

  // While the tab asks for a cell, a click in the world (with the mouse locked to it) answers instead of editing.
  useEffect(() => {
    const el = stage.current;
    if (!controller || !el || !pick || live) return;
    const onDown = (event: PointerEvent) => {
      const lock = document.pointerLockElement;
      if (event.button > 2 || !lock || !el.contains(lock)) return;
      event.preventDefault();
      event.stopPropagation();
      if (event.button === 2) {
        setPick(null);
        return;
      }
      void Promise.resolve(viewer.current?.engine()?.aimNow())
        .then((t) => {
          const cells = aimedCells(t ?? null);
          if (!cells) return;
          if (pick.kind === 'place') {
            const at = cells.front ?? cells.on;
            if (controller.place(pick.def, at, { turns: pick.turns, mirror: pick.mirror, snap: pick.snap }))
              setPick(null);
            return;
          }
          // A port is the cell clicked (the dust there), or the free one in front of the face clicked (beside the box).
          const sel = studio.selection;
          if (!sel) return;
          const box = { min: sel.min, max: sel.max };
          const at = portSideOf(box, cells.on)
            ? cells.on
            : cells.front && portSideOf(box, cells.front)
              ? cells.front
              : null;
          if (!at) return;
          const port = pick.port;
          setPicked((old) => [...old.filter((x) => x.at.join() !== at.join()), { at, kind: port }]);
        })
        .catch(() => undefined);
    };
    el.addEventListener('pointerdown', onDown, true);
    return () => el.removeEventListener('pointerdown', onDown, true);
  }, [controller, studio, stage, viewer, pick, live]);

  // ------------------------------------------------------------------ selection, the mouse and the keys

  const [checked, setChecked] = useState<string[] | null>(null);
  /** What the bound listeners read, current on every render. */
  const flags = useRef({
    live,
    pick,
    canEdit: p.canEdit ?? false,
    selecting: p.selecting ?? false,
    notice: p.notice,
    checked,
  });
  useEffect(() => {
    flags.current = {
      live,
      pick,
      canEdit: p.canEdit ?? false,
      selecting: p.selecting ?? false,
      notice: p.notice,
      checked,
    };
  });
  const editable = useCallback(() => flags.current.canEdit && !flags.current.live, []);
  const say = useCallback((text: string | null) => flags.current.notice?.(text), []);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = useRef<string | null>(null);
  /** The selection this hook made (the selected component's box): another one means nothing is selected here. */
  const owned = useRef<Selection | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const drag = useRef<RedstoneComponentDrag | null>(null);

  const select = useCallback(
    (id: string | null) => {
      const box = id ? controller?.placedOf(id)?.box : null;
      const next = box ? id : null;
      selected.current = next;
      setSelectedId(next);
      const cur = owned.current;
      if (box && cur && studio.selection === cur && same(cur.min, box.min) && same(cur.max, box.max)) return;
      owned.current = box ? { min: v3(box.min), max: v3(box.max) } : null;
      studio.setSelection(owned.current);
    },
    [controller, studio],
  );
  /** The selected component while its box is still the selection. */
  const current = useCallback(() => {
    const id = selected.current;
    if (!id || !controller || studio.selection !== owned.current) return null;
    return controller.snapshot().components.find((c) => c.id === id) ?? null;
  }, [controller, studio]);

  useEffect(
    () =>
      studio.subscribe(() => {
        if (selected.current && studio.selection !== owned.current) {
          selected.current = null;
          setSelectedId(null);
        }
      }),
    [studio],
  );
  // A turn, an undo or a hand edit changes the selected component's box (or takes it away): the selection follows.
  useEffect(() => {
    if (selected.current && studio.selection === owned.current) select(selected.current);
  }, [snap, select, studio]);

  /**
   * The selected component's ports and the ticked ones' as they are: amber where open, green where joined, each with an
   * arrow into the component (an input) or out of it (an output). A drag shows its own while it lasts.
   */
  const showPorts = useCallback(() => {
    if (!overlay || !controller) return;
    const ids = new Set(flags.current.checked ?? []);
    const sel = current()?.id;
    if (sel) ids.add(sel);
    const view = portView(controller, ids);
    overlay.setComponentPorts(view.open, view.joined);
    overlay.setPortArrows(view.arrows);
  }, [overlay, controller, current]);
  useEffect(() => {
    if (!drag.current?.moved) showPorts();
  }, [snap, showPorts, selectedId, checked]);
  useEffect(() => {
    if (!overlay || !controller) return;
    return () => overlay.setComponentPorts(null);
  }, [overlay, controller]);

  useEffect(() => {
    // It stays until dismissed: a component left out is easy to miss.
    if (controller?.restoreNote) flags.current.notice?.(controller.restoreNote, { tone: 'warn', sticky: true });
  }, [controller]);

  /** The overlay shows the selection and the ports as they are (a drag or a placing ghost ended). */
  const rest = useCallback(() => {
    overlay?.setSelection(studio.selection);
    showPorts();
  }, [overlay, studio, showPorts]);

  // A drag in progress ends when a simulation starts: its clicks are the circuit's from then on.
  useEffect(() => {
    if (live) drag.current?.cancel();
  }, [live]);

  useEffect(() => {
    const el = stage.current;
    const engine = ready ? viewer.current?.engine() : null;
    if (!controller || !overlay || !mod || !el || !engine) return;
    const rayAt = (x: number, y: number) => rayFromPointer(mod, viewer.current?.engine(), x, y);
    const d = new RedstoneComponentDrag({
      controller,
      overlay,
      ray: (x, y) => rayAt(x, y)?.ray ?? null,
      canEdit: editable,
      refused: setRefused,
      moved: (id) => select(id),
      rest,
      notice: say,
    });
    drag.current = d;
    d.bind(window);
    const hit = (at: { x: number; y: number } | null): ComponentHit | null => {
      const e = viewer.current?.engine();
      const rect = e?.surface.getBoundingClientRect();
      if (!e || !rect) return null;
      const point = at ?? { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
      const ray = rayAt(point.x, point.y);
      if (!ray) return null;
      // A gizmo handle under the mouse is the gizmo's (the mouse is not locked then).
      if (at && overlay.pickHandle(ray)) return { kind: 'handle' };
      const c = componentUnder(ray.ray, controller, (x, y, z) => studio.canvas.id(x, y, z) !== 0);
      if (!c) return null;
      const origin = v3(c.at);
      return { kind: 'component', id: c.id, origin, grab: levelPoint(ray.ray, origin[1]) };
    };
    const unbind = bindComponentPointer(el, {
      ready: () => !flags.current.live && !flags.current.pick,
      selecting: () => flags.current.selecting,
      hit,
      select,
      press: (press) => {
        if (editable()) d.press(press);
      },
      focus: () => viewer.current?.engine()?.surface.focus(),
      surface: () => viewer.current?.engine()?.surface,
    });
    return () => {
      unbind();
      d.unbind();
      if (drag.current === d) drag.current = null;
    };
  }, [controller, overlay, mod, ready, stage, viewer, select, rest, editable, say, studio]);

  // Placing from the Library with the mouse free: a ghost follows the mouse on the selected component's level (the
  // ground layer, y 0, when none is selected) and a click places it there; the locked click above still works too.
  useEffect(() => {
    const el = stage.current;
    const engine = ready ? viewer.current?.engine() : null;
    if (!controller || !overlay || !mod || !el || !engine || live || pick?.kind !== 'place') return;
    return bindPlaceGhost(el, pick, {
      controller,
      overlay,
      ray: (x, y) => rayFromPointer(mod, viewer.current?.engine(), x, y)?.ray ?? null,
      level: () => current()?.at[1] ?? 0,
      surface: () => viewer.current?.engine()?.surface,
      canEdit: editable,
      refused: setRefused,
      placed: (id) => {
        setPick(null);
        select(id);
      },
      cancel: () => setPick(null),
      rest,
      notice: say,
    });
  }, [controller, overlay, mod, ready, stage, viewer, live, pick, current, editable, select, rest, say]);

  // ------------------------------------------------------------------ the outliner's actions

  const frame = useCallback(
    (id: string) => {
      const box = controller?.placedOf(id)?.box;
      if (!box) return;
      select(id);
      const engine = viewer.current?.engine();
      if (!engine) return;
      const b = { min: v3(box.min), max: v3(box.max) };
      const distance = pictureDistance(b, engine.camera.aspect, engine.camera.fov);
      // The view is in world coordinates: the canvas stands on the plot's base.
      const mid = (i: number) => (b.min[i]! + b.max[i]!) / 2;
      engine.focus(mid(0), mid(1) + plotMode().baseY, mid(2), { distance });
      engine.surface.focus();
    },
    [controller, select, viewer],
  );
  const duplicate = useCallback(
    (id: string) => {
      if (!controller || !editable()) return;
      const copy = controller.duplicate(id);
      if (copy) select(copy.id);
      else say(controller.snapshot().error);
    },
    [controller, editable, say, select],
  );
  const remove = useCallback(
    (id: string) => {
      if (!controller || !editable()) return;
      if (controller.remove(id) && selected.current === id) select(null);
    },
    [controller, editable, select],
  );
  const combine = useCallback(
    (ids: readonly string[]) => {
      if (!controller || !editable()) return;
      const item = controller.combine(ids);
      if (!item) {
        say(controller.snapshot().error);
        return;
      }
      setChecked(null);
      select(item.id);
      say(`Combined ${ids.length} components into one. Move, turn or rename it, or ungroup it (Ctrl+Shift+G).`);
    },
    [controller, editable, say, select],
  );
  /** Ctrl+G: combines the ticked components, or opens the checklist (the selected one ticked) and says what to do. */
  const combineChecked = useCallback(() => {
    const all = controller?.snapshot().components ?? [];
    const ids = (flags.current.checked ?? []).filter((id) => all.some((c) => c.id === id));
    if (ids.length >= 2) return combine(ids);
    setChecked(selected.current ? [selected.current] : []);
    say('Tick at least two components under Combine, then press Ctrl+G again.');
  }, [controller, combine, say]);
  const ungroup = useCallback(
    (id: string) => {
      if (!controller || !editable()) return;
      const parts = controller.ungroup(id);
      if (!parts) {
        say(controller.snapshot().error);
        return;
      }
      if (selected.current === id) select(null);
      say(
        parts.length
          ? `Ungrouped into ${parts.length} components. Undo groups them again.`
          : 'Ungrouped into plain blocks. Undo makes it a component again.',
      );
    },
    [controller, editable, say, select],
  );

  /**
   * Ctrl+G combines (or opens the checklist), Ctrl+Shift+G ungroups the selected component of the design's own. They
   * work from the outliner and its checklist too, but never while typing; undefined when the key is not one of them.
   */
  const groupKeyAction = useCallback(
    (e: KeyboardEvent): (() => void) | null | undefined => {
      if (!controller || !(e.ctrlKey || e.metaKey) || e.altKey || e.key.toLowerCase() !== 'g') return undefined;
      const box = e.target instanceof HTMLInputElement && e.target.type === 'checkbox';
      if (!editable() || drag.current?.moved || (isTyping(e.target) && !box)) return null;
      const s = controller.snapshot();
      if (!e.shiftKey) return s.components.length >= 2 ? combineChecked : null;
      const c = current();
      return c && s.customDefs.some((d) => d.id === c.def) ? () => ungroup(c.id) : null;
    },
    [controller, editable, combineChecked, current, ungroup],
  );

  const keyAction = useCallback(
    (e: KeyboardEvent) => {
      const group = groupKeyAction(e);
      if (group !== undefined) return group;
      const d = drag.current;
      if (!controller || !d) return null;
      return componentKeyAction(e, {
        selected: () => current()?.id ?? null,
        // While a gizmo handle holds the piece its keys do nothing: the release moves the piece as it was picked up.
        canEdit: () => editable() && !d.holdingGizmo,
        inWorld: () => inStage(stage),
        drag: d,
        turn: (id, by) => {
          if (!controller.turn(id, by)) say(controller.snapshot().error);
        },
        mirror: (id, axis) => {
          if (!controller.mirror(id, axis)) say(controller.snapshot().error);
        },
        remove,
        duplicate,
      });
    },
    [controller, current, editable, say, stage, groupKeyAction, remove, duplicate],
  );
  const handlesKey = useCallback((e: KeyboardEvent) => keyAction(e) !== null, [keyAction]);
  useEffect(() => {
    if (!controller) return;
    const onKey = (e: KeyboardEvent) => {
      const act = keyAction(e);
      if (!act) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      act();
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [controller, keyAction]);

  /** One move of the selected component by `delta`, in undo group `group`. */
  const moveBy = useCallback(
    (delta: V3, group?: string) => {
      const c = current();
      if (!controller || !c || !editable()) return;
      if (controller.move(c.id, [c.at[0] + delta[0], c.at[1] + delta[1], c.at[2] + delta[2]], { group })) select(c.id);
      else say(controller.snapshot().error);
    },
    [controller, current, editable, say, select],
  );
  const moveSelected = useCallback(
    (delta: V3, group?: string) => {
      if (!current()) return false;
      moveBy(delta, group);
      return true;
    },
    [current, moveBy],
  );
  // A gizmo handle drag of the selected component: the overlay previews each step, the release is one move.
  const dragStart = useCallback(() => {
    const c = current();
    return !!c && !!drag.current?.gizmoStart(c.id);
  }, [current]);
  const dragPreview = useCallback((delta: V3) => drag.current?.gizmoPreview(delta) ?? false, []);
  const dragCommit = useCallback((delta: V3) => drag.current?.gizmoCommit(delta), []);
  const dragCancel = useCallback(() => drag.current?.gizmoCancel(), []);
  const nudge = useRef(moveBy);
  useEffect(() => {
    nudge.current = moveBy;
  });
  const nudger = useRef<((delta: V3) => void) | null>(null);
  const nudgeSelected = useCallback(
    (delta: V3) => {
      if (!current()) return false;
      if (editable()) (nudger.current ??= keyMoves((d, group) => nudge.current(d, group)))(delta);
      return true;
    },
    [current, editable],
  );

  const editableNow = (p.canEdit ?? false) && !live;
  return useMemo(
    () => ({
      controller,
      snap,
      pick,
      setPick,
      picked,
      clearPicked,
      selectedId,
      select,
      refused,
      handlesKey,
      nudgeSelected,
      moveSelected,
      dragStart,
      dragPreview,
      dragCommit,
      dragCancel,
      editable: editableNow,
      frame,
      checked,
      setChecked,
      combine,
      ungroup,
      duplicate,
      remove,
    }),
    [
      controller,
      snap,
      pick,
      picked,
      clearPicked,
      selectedId,
      select,
      refused,
      handlesKey,
      nudgeSelected,
      moveSelected,
      dragStart,
      dragPreview,
      dragCommit,
      dragCancel,
      editableNow,
      frame,
      checked,
      combine,
      ungroup,
      duplicate,
      remove,
    ],
  );
}

/**
 * The component a world ray picks: the one whose box it meets first, unless a block outside that box (a loose dust, a
 * repeater, the ground) stands in front of it, in which case the press is that block's and no component's.
 */
export function componentUnder(
  ray: PlacementRay,
  controller: Pick<RedstoneComponentController, 'snapshot' | 'placedOf'>,
  solid: (x: number, y: number, z: number) => boolean,
): ComponentInstance | null {
  const all = controller.snapshot().components;
  const nearest = nearestHit(ray, all, (c) => {
    const box = controller.placedOf(c.id)?.box;
    return box ? [[v3(box.min), v3(box.max)] as const] : [];
  });
  if (!nearest || blockedBefore(ray, nearest.distance, solid)) return null;
  return all.find((x) => x.id === nearest.id) ?? null;
}

/** The ports of the given components as the overlay draws them: open and joined cells, and an arrow for each port. */
export function portView(
  controller: Pick<RedstoneComponentController, 'portsOf'>,
  ids: Iterable<string>,
): { open: Pos[]; joined: Pos[]; arrows: PortArrow[] } {
  const view: { open: Pos[]; joined: Pos[]; arrows: PortArrow[] } = { open: [], joined: [], arrows: [] };
  for (const id of ids)
    for (const port of controller.portsOf(id)) {
      (port.link ? view.joined : view.open).push(port.at);
      view.arrows.push({ at: port.at, side: port.side, kind: port.kind, joined: port.link !== null });
    }
  return view;
}

const same = (a: readonly number[], b: readonly number[]) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];
const v3 = (p: Pos): V3 => [p[0], p[1], p[2]];

/** The stage has the keys: the focus is in it or the mouse is locked to it. */
function inStage(stage: RefObject<HTMLElement | null>): boolean {
  const el = stage.current;
  const lock = document.pointerLockElement;
  return !!el && (el.contains(document.activeElement) || (!!lock && el.contains(lock)));
}
