/**
 * How the Story map shows a body and the queue in the world (pure rules, tested on their own):
 * - A pawn or AI body the world viewer draws as its full player model gets only overlay marks on top (selection brackets, a
 *   colour ring at its feet, name, drafted and job marks); zoomed out until a body is a few pixels, or when the viewer has no
 *   model for it (not loaded yet, beyond the entity distance, drawn as a box), it is the flat 2D token as before.
 * - A body on a storey above the level cut is x-rayed like the blocks there: no model, a faint token.
 * - Queued builds become hologram cells (the viewer's `setHolograms`), one revision per change of the ghosts or the builds.
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { holoState, type HoloCell } from '@mineclaw/world-viewer/settings';

/** Below this many screen pixels per block a body (1.8 blocks tall) is too small to read: the 2D token takes over. */
export const TOKEN_PX = 9;

export type BodyLook = 'model' | 'token' | 'cut';

/**
 * `detail`: what the viewer drew for the body last frame (true = the posed model, false = a far box, null = nothing: not in
 * the world store, beyond the entity distance, off screen). `pxPerBlock`: the map's zoom. `feetY` against `cutY`: the storey.
 */
export function bodyLook(o: { detail: boolean | null; pxPerBlock: number; feetY: number; cutY: number | null }): BodyLook {
  if (o.cutY !== null && Math.floor(o.feetY + 0.01) >= o.cutY) return 'cut';
  return o.detail === true && o.pxPerBlock >= TOKEN_PX ? 'model' : 'token';
}

/** Where the overlay anchors a body (its hit point and marks): the middle of the model, or the feet of a flat token. */
export function anchorLift(look: BodyLook): number {
  return look === 'model' ? 0.9 : 0.2;
}

/** Whether a ghost is a cell to dig: its 8th element says so (the bot since the mine designer: a dig ghost carries the block
 * expected there), else id 0 (an older 7-element ghost). */
export function ghostDigs(g: NonNullable<StoryColonyState['ghosts']>['blocks'][number]): boolean {
  return g[7] === 'dig' || (g[7] === undefined && g[3] === 0);
}

/** The queued cells to draw as holograms (queue order: the ghosts' own order) and the revision that gates rebuilding. A
 * `place` ghost with id 0 (air) has nothing to draw and is left out. */
export function hologramCells(st: Pick<StoryColonyState, 'ghosts' | 'builds'> | undefined): { cells: HoloCell[]; rev: string } {
  const builds = new Map((st?.builds ?? []).map((b) => [String(b.id), b.status] as const));
  const ghosts = st?.ghosts;
  const cells: HoloCell[] = [];
  for (const g of ghosts?.blocks ?? []) {
    const cell = holoCell(g, builds);
    if (cell) cells.push(cell);
  }
  const sig = [...builds].map(([id, s]) => `${id}:${s}`).join(',');
  return { cells, rev: `${ghosts?.rev ?? 0}|${cells.length}|${sig}` };
}

/** A ghost's hologram cell (null: nothing to draw). */
function holoCell(
  g: NonNullable<StoryColonyState['ghosts']>['blocks'][number],
  builds: ReadonlyMap<string, string>,
): HoloCell | null {
  const state = holoState(g[6], builds.get(String(g[5])));
  if (!state) return null;
  // The 8th element (the bot since the mine designer): `dig` cells carry the block expected there (the world's when queued),
  // so they are told apart by kind, not by id 0; they draw as red outlines until the cell is air. Older 7-element ghosts:
  // id 0 = dig.
  const dig = ghostDigs(g);
  if (!dig && g[3] === 0) return null;
  return { x: g[0], y: g[1], z: g[2], id: dig ? 0 : g[3], meta: dig ? 0 : g[4], build: g[5], state };
}
