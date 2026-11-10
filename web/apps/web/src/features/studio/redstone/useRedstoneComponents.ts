'use client';

/**
 * A redstone design's components in the studio page: the controller (./RedstoneComponentController) on the page's studio,
 * and the two things a click in the world can mean while the Library tab asks for it: where a component goes, or which
 * cells around a selection are a new component's ports. Any other kind of design gets an inert result.
 */
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget, WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useState, useSyncExternalStore, type RefObject } from 'react';
import { inside, plotMode } from '../canvas';
import { FACE_NORMAL } from '../facing';
import type { Studio } from '../studio';
import { portSideOf } from './ports';
import { RedstoneComponentController, type ComponentSnapshot } from './RedstoneComponentController';

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
}): RedstoneComponents {
  const { enabled, studio, viewer, stage, live } = p;
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

  return useMemo(
    () => ({ controller, snap, pick, setPick, picked, clearPicked }),
    [controller, snap, pick, picked, clearPicked],
  );
}
