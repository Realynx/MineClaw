/**
 * The Story map's chest badges (drawn when zoomed in): a storage chest's designated category (its label, else the filter's
 * summary: "Wood", "Food, Tools") and how full it is. The fill comes from the hive's container knowledge
 * (`GET /api/world/containers` over the chests the map knows, polled every 10 s) when that is at least as fresh as the colony's
 * own ledger, else from the ledger (`free_slots`). An unregistered chest a bot has looked into shows its fill alone.
 */
import type { ContainerSummary, StoryCategory, StoryStorage } from '@mineclaw/contracts';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useApi } from '@/lib/api/query';
import { buildTree, filterSummary, type TreeNode } from './storage';

type V3 = [number, number, number];
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

export interface ChestBadge {
  /** The category (null: not storage). */
  text: string | null;
  /** 0..1, null = not known. */
  fill: number | null;
}

const trees = new WeakMap<readonly StoryCategory[], TreeNode[]>();
const DEFAULT_KEY: readonly StoryCategory[] = [];

function treeOf(categories: readonly StoryCategory[] | null | undefined): TreeNode[] {
  const key = categories?.length ? categories : DEFAULT_KEY;
  let t = trees.get(key);
  if (!t) trees.set(key, (t = buildTree(categories)));
  return t;
}

const ms = (t: string | number | null | undefined): number =>
  t === null || t === undefined ? 0 : typeof t === 'number' ? t : Date.parse(t) || 0;

/** The badge of a chest: storage (its category and fill), or a looked-into chest (its fill); null = nothing to show. */
export function chestBadge(
  storage: StoryStorage | undefined,
  summary: ContainerSummary | undefined,
  categories: readonly StoryCategory[] | null | undefined,
): ChestBadge | null {
  const text = storage ? storage.label || shortCategory(filterSummary(treeOf(categories), storage.filter)) : null;
  let fill: number | null = null;
  const ledgerAt = ms(storage?.knownAt);
  if (summary && summary.source !== 'unknown' && summary.size > 0 && (!storage || ms(summary.seenAt) >= ledgerAt))
    fill = summary.used / summary.size;
  else if (storage && storage.freeSlots !== null && storage.freeSlots !== undefined) {
    const slots = storage.doubleWith ? 54 : 27;
    fill = (slots - Math.min(slots, storage.freeSlots)) / slots;
  }
  if (text === null && fill === null) return null;
  return { text, fill: fill === null ? null : Math.max(0, Math.min(1, fill)) };
}

function shortCategory(s: string): string {
  if (s === 'Nothing') return 'Nothing';
  return s.length > 16 ? `${s.slice(0, 15)}…` : s;
}

/** Draws a badge under a chest mark at `at` (canvas px). */
export function drawChestBadge(ctx: CanvasRenderingContext2D, at: { x: number; y: number }, b: ChestBadge): void {
  ctx.save();
  ctx.font = '600 9px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  const text = b.text ?? '';
  const w = Math.max(26, text ? ctx.measureText(text).width + 10 : 26);
  const x = at.x - w / 2,
    y = at.y + 12;
  const h = text ? 15 : 6;
  ctx.fillStyle = 'rgba(12, 18, 32, 0.82)';
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, 3);
  ctx.fill();
  if (text) {
    ctx.fillStyle = '#f3e7c9';
    ctx.fillText(text, at.x, y + 6);
  }
  if (b.fill !== null) {
    const bw = w - 6,
      by = y + h - 4;
    ctx.fillStyle = 'rgba(255, 255, 255, 0.18)';
    ctx.fillRect(x + 3, by, bw, 2);
    ctx.fillStyle = b.fill > 0.9 ? '#ff6b5b' : b.fill > 0.6 ? '#ffd166' : '#7bd88f';
    ctx.fillRect(x + 3, by, Math.max(1, Math.round(bw * b.fill)), 2);
  }
  ctx.restore();
}

/**
 * The hive's container summaries over the chests the map currently knows (`cells()`, read every 5 s), polled every 10 s while
 * `enabled`; the returned ref maps every block of a known container to its summary (for the draw loop).
 */
export function useContainerBadges(cells: () => ReadonlyArray<{ pos: V3 }>, enabled = true) {
  const [box, setBox] = useState<{ x0: number; y0: number; z0: number; x1: number; y1: number; z1: number } | null>(
    null,
  );
  useEffect(() => {
    if (!enabled) return;
    const pick = () => {
      const list = cells();
      if (!list.length) return;
      let x0 = Infinity,
        y0 = Infinity,
        z0 = Infinity,
        x1 = -Infinity,
        y1 = -Infinity,
        z1 = -Infinity;
      for (const { pos } of list) {
        x0 = Math.min(x0, pos[0]);
        y0 = Math.min(y0, pos[1]);
        z0 = Math.min(z0, pos[2]);
        x1 = Math.max(x1, pos[0]);
        y1 = Math.max(y1, pos[1]);
        z1 = Math.max(z1, pos[2]);
      }
      // a little margin, whole 16-block steps (so a small camera move does not re-key the query), at most 512 wide
      const q = (v: number, up: boolean) => (up ? Math.ceil((v + 1) / 16) * 16 : Math.floor((v - 1) / 16) * 16);
      const next = {
        x0: q(x0, false),
        y0: y0 - 2,
        z0: q(z0, false),
        x1: Math.min(q(x1, true), q(x0, false) + 512),
        y1: y1 + 2,
        z1: Math.min(q(z1, true), q(z0, false) + 512),
      };
      setBox((cur) => (cur && JSON.stringify(cur) === JSON.stringify(next) ? cur : next));
    };
    pick();
    const id = setInterval(pick, 5000);
    return () => clearInterval(id);
  }, [cells, enabled]);
  const q = useApi(
    'world.containers',
    { query: box ? { dim: 0, ...box, limit: 300 } : undefined },
    { enabled: enabled && !!box, refetchInterval: 10_000, refetchIntervalInBackground: false, staleTime: 5_000 },
  );
  const byCell = useMemo(() => {
    const m = new Map<string, ContainerSummary>();
    for (const c of q.data?.containers ?? []) for (const cell of c.cells) m.set(k3(cell), c);
    return m;
  }, [q.data]);
  const ref = useRef(byCell);
  useEffect(() => {
    ref.current = byCell;
  }, [byCell]);
  return ref;
}
