/**
 * The gizmo's preview drag over the kinds of component a studio page has (mine modules, redstone components): the first
 * kind whose `dragStart` takes the press owns that drag until it is committed or cancelled, so the other kinds never see
 * its steps. The owner is kept in a holder the page keeps across renders, since the page rebuilds its actions on each.
 */
import type { V3 } from '../canvas';

export interface GizmoDrag {
  /** A gizmo press: true when this kind holds the selection and previews the move. */
  dragStart: () => boolean;
  /** Shows the held selection moved by `delta`; false when it cannot be shown. */
  dragPreview: (delta: V3) => boolean;
  /** The release: one move by `delta`. */
  dragCommit: (delta: V3) => void;
  /** The selection goes back. */
  dragCancel: () => void;
}

/** One gizmo drag over `kinds`, in order; `owner` holds the index of the kind that took the press. */
export function gizmoDrags(kinds: readonly GizmoDrag[], owner: { current: number | null }): GizmoDrag {
  const take = () => {
    const k = owner.current === null ? undefined : kinds[owner.current];
    owner.current = null;
    return k;
  };
  return {
    dragStart: () => {
      const i = kinds.findIndex((k) => k.dragStart());
      owner.current = i < 0 ? null : i;
      return i >= 0;
    },
    dragPreview: (delta) => (owner.current === null ? false : (kinds[owner.current]?.dragPreview(delta) ?? false)),
    dragCommit: (delta) => take()?.dragCommit(delta),
    dragCancel: () => take()?.dragCancel(),
  };
}
