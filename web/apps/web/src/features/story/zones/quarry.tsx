'use client';

/**
 * Quarries (docs/web/story-mode.md "Quarries"): the Quarry tool's mode (Quarry: everything to bedrock with a spiral stair, the
 * default; or Ores only), the stair drawn on the map (the drag preview and the quarry zones), and the Quarries window beside the
 * Colony tab (progress, depth, the stair, the mode, what it leaves standing).
 */
import {
  quarryRing,
  quarryStep,
  zoneCellSet,
  type StoryColonyState,
  type StoryQuarry,
  type StoryZone,
} from '@mineclaw/contracts';
import { Pickaxe } from 'lucide-react';
import { useSyncExternalStore } from 'react';
import { cn } from '@/lib/utils';
import { panel } from '../StoryHud';
import { ZonePreserve } from './PreservePanel';

export type QuarryMode = 'quarry' | 'ores';

let mode: QuarryMode = 'quarry';
const listeners = new Set<() => void>();

/** The mode a newly painted mine zone gets (kept while the page is open). */
export function quarryModeNow(): QuarryMode {
  return mode;
}

export function useQuarryMode(): [QuarryMode, (m: QuarryMode) => void] {
  const v = useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => void listeners.delete(l);
    },
    () => mode,
    () => mode,
  );
  return [
    v,
    (m) => {
      mode = m;
      for (const l of listeners) l();
    },
  ];
}

/** The mode picker under the Zones tools while the Quarry tool is in hand. */
export function QuarryModePicker() {
  const [m, setM] = useQuarryMode();
  const opts: Array<[QuarryMode, string, string]> = [
    [
      'quarry',
      'Quarry (everything)',
      'Dig everything down to bedrock, leaving a spiral stair along the edge (2×2 at least)',
    ],
    ['ores', 'Ores only', 'Mine only the ores in the zone (the old mine zone)'],
  ];
  return (
    <div className="w-full" data-testid="quarry-mode">
      <div className="mb-1 text-[11px] font-semibold text-muted">Quarry</div>
      <div className="flex flex-wrap gap-1" role="radiogroup" aria-label="Quarry mode">
        {opts.map(([id, label, hint]) => (
          <button
            key={id}
            type="button"
            role="radio"
            aria-checked={m === id}
            title={hint}
            onClick={() => setM(id)}
            className={cn(
              'rounded border px-1.5 py-0.5 text-[11px]',
              m === id ? 'border-accent bg-accent-soft text-fg' : 'border-border text-muted hover:text-fg',
            )}
          >
            {label}
          </button>
        ))}
      </div>
    </div>
  );
}

type Pt = { x: number; y: number };
type Proj = (x: number, y: number, z: number) => Pt | null;

/** The stair's ring as a path of numbered steps (the first turn of the spiral), drawn at <paramref name="y"/>. */
function drawRing(
  ctx: CanvasRenderingContext2D,
  ring: ReadonlyArray<readonly [number, number]>,
  y: number,
  proj: Proj,
  current = -1,
) {
  ctx.strokeStyle = '#f0a040';
  ctx.lineWidth = 2;
  ctx.beginPath();
  ring.forEach(([x, z], i) => {
    const p = proj(x + 0.5, y, z + 0.5);
    if (!p) return;
    if (i === 0) ctx.moveTo(p.x, p.y);
    else ctx.lineTo(p.x, p.y);
  });
  const first = ring[0] ? proj(ring[0][0] + 0.5, y, ring[0][1] + 0.5) : null;
  if (first) ctx.lineTo(first.x, first.y);
  ctx.stroke();
  ctx.font = '600 10px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ring.forEach(([x, z], i) => {
    const p = proj(x + 0.5, y, z + 0.5);
    if (!p) return;
    ctx.fillStyle = i === current ? '#ffd166' : 'rgba(240,160,64,0.85)';
    ctx.fillRect(p.x - 7, p.y - 7, 14, 14);
    ctx.fillStyle = '#1b1206';
    ctx.fillText(String(i + 1), p.x, p.y + 0.5);
  });
}

/** A Quarry drag's preview: the stair spiral of the zone it makes (step 1 at the top) and how deep it goes. */
export function drawQuarryPreview(
  ctx: CanvasRenderingContext2D,
  zone: StoryZone | undefined,
  level: number,
  proj: Proj,
): string {
  if (!zone) return '';
  const ring = quarryRing(zoneCellSet(zone));
  if (!ring) return ' · too narrow for a quarry (2×2 at least)';
  drawRing(ctx, ring, level + 1.06, proj);
  const depth = Math.max(0, level - 3);
  return ` · stair: ${ring.length} steps a turn, down ~${depth} layers to bedrock`;
}

/** The quarry zones' stairs on the map: the ring with the step being worked highlighted. */
export function drawQuarries(ctx: CanvasRenderingContext2D, zones: readonly StoryZone[], proj: Proj, px: number) {
  if (px < 4) return;
  for (const z of zones) {
    if (z.kind !== 'mine' || z.mode !== 'quarry' || !z.quarry) continue;
    const ring = quarryRing(zoneCellSet(z));
    if (!ring) continue;
    const step = quarryStep(ring, z.quarry.top, z.quarry.layer);
    drawRing(
      ctx,
      ring,
      z.area[1] + 1.04,
      proj,
      ring.findIndex(([x, zz]) => x === step[0] && zz === step[2]),
    );
  }
}

/** Why a cell of the quarry stays, by the bot's kind (`StoryQuarry.left`). */
const LEFT_WORDS: Record<string, string> = {
  lava: 'beside lava',
  water: 'beside water',
  cave: 'walling off a cave',
  sand: 'under sand or gravel',
  pool: 'liquid closed in by rock',
  walled: 'walled in',
  protected: 'protected',
  kept: 'kept (removal cancelled)',
  danger: 'where a pawn met danger',
};

/** What a quarry leaves standing apart from its stair: the count by kind, and the cells (up to 64) when opened. */
function QuarryLeftovers({ q }: { q: StoryQuarry }) {
  const kinds = Object.entries(q.left ?? {})
    .filter(([k, n]) => k !== 'step' && n > 0)
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  if (!kinds.length) return null;
  const total = kinds.reduce((s, [, n]) => s + n, 0);
  return (
    <details className="text-subtle" data-testid="quarry-left">
      <summary className="cursor-pointer">
        {total} cell(s) left standing: {kinds.map(([k, n]) => `${n} ${LEFT_WORDS[k] ?? k}`).join(', ')}
      </summary>
      {q.leftovers?.length ? (
        <ul className="mt-1 max-h-32 space-y-0.5 overflow-y-auto tabular-nums">
          {q.leftovers.map((c) => (
            <li key={c.at.join(',')}>
              {c.at.join(', ')} · {LEFT_WORDS[c.kind] ?? c.kind}
            </li>
          ))}
        </ul>
      ) : null}
    </details>
  );
}

/** The Quarries window beside the Colony tab: each quarry's progress, depth, stair and mode. */
export function QuarryZonesPanel({
  state,
  canWrite,
  onMode,
  onShow,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onMode: (z: StoryZone, mode: QuarryMode) => void;
  onShow: (z: StoryZone) => void;
}) {
  const zones = (state?.zones ?? []).filter((z) => z.kind === 'mine');
  if (!zones.length) return null;
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[40vh] w-[22rem] space-y-2 overflow-y-auto p-3 text-xs')}
      aria-label="Quarries"
      data-testid="quarry-zones"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Pickaxe className="size-3.5" aria-hidden /> Quarries
      </div>
      <ul className="space-y-2">
        {zones.map((z) => {
          const q = z.quarry;
          const pct = q ? Math.round((100 * Math.min(q.layer, q.depth)) / Math.max(1, q.depth)) : 0;
          return (
            <li key={String(z.id)} className="space-y-1 rounded border border-border p-2" data-testid="quarry-zone">
              <div className="flex items-center gap-2">
                <button type="button" className="font-semibold hover:underline" onClick={() => onShow(z)}>
                  {z.name || z.label || `Zone ${z.id}`}
                </button>
                <span className="text-subtle tabular-nums">{z.cells ?? zoneCellSet(z).size} cells</span>
                <select
                  aria-label="Quarry mode"
                  className="ml-auto rounded border border-border bg-surface px-1 py-0.5"
                  value={z.mode ?? 'ores'}
                  disabled={!canWrite}
                  onChange={(e) => onMode(z, e.target.value as QuarryMode)}
                >
                  <option value="quarry">Quarry (everything)</option>
                  <option value="ores">Ores only</option>
                </select>
              </div>
              {q ? (
                <>
                  <div className="h-1.5 overflow-hidden rounded bg-surface-2" aria-label={`${pct}% dug`}>
                    <div className="h-full bg-accent" style={{ width: `${pct}%` }} />
                  </div>
                  <div className="text-muted" data-testid="quarry-status">
                    {q.status ?? `layer ${q.layer + 1} of ${q.depth}`}
                  </div>
                  <div className="text-subtle">
                    From y {q.top} to y {q.bottom} · stair of {q.stair.length} steps
                    {q.stairFixes ? ` · ${q.stairFixes} step(s) need a block` : ''}
                    {!q.left && q.sealed ? ` · ${q.sealed} cell(s) left standing` : ''}
                  </div>
                  <QuarryLeftovers q={q} />
                </>
              ) : (
                <div className="text-muted">Mines the ores in the zone.</div>
              )}
              <ZonePreserve z={z} />
            </li>
          );
        })}
      </ul>
    </section>
  );
}
