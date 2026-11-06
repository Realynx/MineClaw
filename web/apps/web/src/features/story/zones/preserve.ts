/**
 * Preserve zones (docs/web/story-mode.md "Preserve zones"): the range a newly painted Preserve zone keeps (the Zones tool's depth
 * and height, kept while the page is open), the zones route's `preserve` bodies, the line a zone's settings say about it, and the
 * repair builds (kind `preserve`: one per preserved zone, never cancelled by hand).
 */
import {
  STORY_PRESERVE_MAX_RANGE,
  type StoryBuild,
  type StoryZone,
  type StoryZonePreserve,
  type StoryZoneRequest,
} from '@mineclaw/contracts';
import { useSyncExternalStore } from 'react';

/** A Preserve change from a zone's settings: switch it, set its range, take a new snapshot. */
export type PreserveChange = { on?: boolean; below?: number; above?: number; retake?: boolean };

/** The range a new Preserve zone keeps: layers under and over its painted level (the bot's defaults: 1 and 3). */
export type PreserveRange = { below: number; above: number };

let range: PreserveRange = { below: 1, above: 3 };
const listeners = new Set<() => void>();

/** The range a newly painted Preserve zone gets (kept while the page is open). */
export function preservePaintRange(): PreserveRange {
  return range;
}

export function usePreservePaintRange(): [PreserveRange, (r: Partial<PreserveRange>) => void] {
  const v = useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => void listeners.delete(l);
    },
    () => range,
    () => range,
  );
  return [
    v,
    (r) => {
      range = { ...range, ...r };
      for (const l of listeners) l();
    },
  ];
}

/** A whole number 0..32 from an input's text; null when it is not one. */
export function parseRange(text: string): number | null {
  const t = text.trim();
  if (!/^\d+$/.test(t)) return null;
  const n = Number(t);
  return n <= STORY_PRESERVE_MAX_RANGE ? n : null;
}

/** The zones route's body for a change. */
export function preserveBody(z: Pick<StoryZone, 'id'>, change: PreserveChange): StoryZoneRequest {
  return { op: 'preserve', id: z.id, ...change };
}

/** Why a zone can't preserve its blocks (null: it can): a mine zone digs them. */
export function preserveRefused(z: Pick<StoryZone, 'kind'>): string | null {
  return z.kind === 'mine' ? 'A quarry digs its blocks: paint a Preserve zone beside it instead' : null;
}

/** "y 60 → 67". */
export const preserveRangeText = (p: Pick<StoryZonePreserve, 'y1' | 'y2'>): string => `y ${p.y1} → ${p.y2}`;

/** The counts under the status: blocks kept, not seen yet, held by the owner's work. */
export function preserveCounts(p: StoryZonePreserve): string {
  const out = [`${p.cells} block${p.cells === 1 ? '' : 's'} kept`];
  if (p.unknown) out.push(`${p.unknown} not seen yet`);
  if (p.held) out.push(`${p.held} held by your builds or designations`);
  return out.join(' · ');
}

/** The toast after a Preserve change was answered. */
export function preserveSaid(change: PreserveChange, p: StoryZonePreserve | null | undefined): string {
  if (change.on === false) return 'Preserve off: the blocks are no longer kept';
  if (change.retake) return 'New snapshot: the blocks are kept as they are now';
  return p ? `Preserving ${preserveRangeText(p)}` : 'Preserve on';
}

// ------------------------------------------------------------------ repair builds

/** A preserved zone's repair build (kind `preserve`): first in the queue, removed only by switching Preserve off. */
export const isRepairBuild = (b: Pick<StoryBuild, 'kind'> | null | undefined): boolean => b?.kind === 'preserve';

/** A build's title: a repair build says "Repairs: <zone>" (the bot's label is "repairs: <zone>"); others their label. */
export function buildTitle(b: Pick<StoryBuild, 'kind' | 'label'>): string {
  if (!isRepairBuild(b)) return b.label;
  return `Repairs: ${b.label.replace(/^repairs:\s*/i, '')}`;
}
