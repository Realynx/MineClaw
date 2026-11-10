/**
 * Where this browser last had the Controller's map: the colony it showed, the point it looked at and the zoom. The next
 * visit opens that colony there, while its state is still on its way, so the map never starts anywhere else. One view
 * per account in localStorage; a copy in the page's memory carries it over a re-entry when storage is blocked. The
 * working level is not remembered.
 */
import { ZOOM_MAX, ZOOM_MIN } from './math';

export interface StoryView {
  colony: string;
  /** The point the map looks at (world x and z). */
  x: number;
  z: number;
  /** The world height the picture shows (blocks). */
  zoom: number;
}

const keyOf = (user: string) => `mcx-story-view:${user}`;

/** What this page saved last, per account. */
const held = new Map<string, StoryView>();

/** A stored value as a view (the zoom within what the map can show); null when it is not one. */
function parseView(raw: unknown): StoryView | null {
  if (!raw || typeof raw !== 'object') return null;
  const { colony, x, z, zoom } = raw as Record<string, unknown>;
  if (typeof colony !== 'string' || !colony) return null;
  if (typeof x !== 'number' || typeof z !== 'number' || typeof zoom !== 'number') return null;
  if (!Number.isFinite(x) || !Number.isFinite(z) || !Number.isFinite(zoom)) return null;
  return { colony, x, z, zoom: Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, zoom)) };
}

/** The remembered view: what this page saved last, else what the browser stored (null when neither holds a view). */
export function loadView(user: string): StoryView | null {
  const own = held.get(user);
  if (own) return own;
  try {
    const raw = globalThis.localStorage?.getItem(keyOf(user));
    return raw ? parseView(JSON.parse(raw)) : null;
  } catch {
    return null;
  }
}

export function saveView(user: string, view: StoryView): void {
  const v = parseView(view);
  if (!v) return;
  held.set(user, v);
  try {
    globalThis.localStorage?.setItem(keyOf(user), JSON.stringify(v));
  } catch {
    // storage unavailable: the view holds for this page session
  }
}

/** Forgets the view (its colony is gone). */
export function clearView(user: string): void {
  held.delete(user);
  try {
    globalThis.localStorage?.removeItem(keyOf(user));
  } catch {
    // storage unavailable: nothing was stored
  }
}

/** The view when it is of `colony` (a view of another colony says nothing about this one). */
export function viewFor(view: StoryView | null | undefined, colony: string | null | undefined): StoryView | null {
  return view && view.colony === colony ? view : null;
}
