'use client';

/**
 * A body's inventory, laid out like the game's inventory screen: the armour column (head..feet) with the off hand (1.12.2),
 * the 3×9 main inventory and the 1×9 hotbar with the held slot highlighted. Icons come from the viewer's bundled atlases for
 * the body's own game version; counts, durability bars, an enchantment glint and a tooltip (name, id:meta, durability).
 *
 * Pure DOM and CSS: no WebGL context, nothing per frame. It polls `GET /api/bots/:bot/inventory` every 2 s only while it is
 * mounted (an open panel) and the tab is visible, keeps the last snapshot on screen while refetching, and marks the slots
 * that changed with one soft pulse.
 */
import type { BodyInventory, InventorySlot } from '@mineclaw/contracts';
import { Backpack, Shield } from 'lucide-react';
import { memo, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { ApiError } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { ago, cn } from '@/lib/utils';
import { Skeleton } from '@/components/ui/primitives';
import {
  ARMOR_LABELS,
  changedSlots,
  durabilityColour,
  durabilityFraction,
  glints,
  inventoryGrid,
  tooltipLines,
  type GridCell,
} from './grid';
import { iconsFor, type GameVersion, type IconOf } from './itemIcons';
import s from './inventory.module.css';

export const INVENTORY_POLL_MS = 2000;

/** The inventory of one body, polled while `enabled` (the panel is open); paused while the tab is hidden. */
export function useBodyInventory(bot: string | null | undefined, enabled = true) {
  return useApi(
    'bots.inventory',
    { params: { bot: bot ?? '' } },
    {
      enabled: !!bot && enabled,
      refetchInterval: (q) =>
        q.state.error instanceof ApiError && q.state.error.status === 404 ? INVENTORY_POLL_MS * 5 : INVENTORY_POLL_MS,
      refetchIntervalInBackground: false,
      staleTime: 0,
    },
  );
}

/** The item icon lookup for a version (null until its atlases load, or when the viewer can't load). */
function useItemIcons(version: GameVersion | null | undefined): IconOf | null {
  const [icons, setIcons] = useState<{ v: GameVersion; of: IconOf } | null>(null);
  useEffect(() => {
    if (!version) return;
    let live = true;
    void iconsFor(version).then((of) => {
      if (live && of) setIcons({ v: version, of });
    });
    return () => {
      live = false;
    };
  }, [version]);
  return icons && icons.v === version ? icons.of : null;
}

function abbrev(name: string): string {
  const parts = name.split('_').filter(Boolean);
  return (parts.length > 1 ? parts.map((p) => p[0]).join('') : name.slice(0, 2)).slice(0, 3).toUpperCase();
}

interface SlotProps {
  cell: GridCell;
  version: BodyInventory['version'];
  icons: IconOf | null;
  held?: boolean;
  /** Changed in the last snapshot: the pulse remounts per snapshot (its key). */
  pulse: number | null;
  ghost?: ReactNode;
  label: string;
  onTip: (cell: GridCell | null, el: HTMLElement | null) => void;
}

const Slot = memo(function Slot({ cell, version, icons, held, pulse, ghost, label, onTip }: SlotProps) {
  const it = cell.item;
  // A damaged tool's meta is its damage, not a variant: its icon is the undamaged one.
  const url = it && icons ? icons(it.id, it.durability ? 0 : it.meta) : null;
  const frac = it ? durabilityFraction(it) : null;
  const glint = it ? glints(version, it) : false;
  const mask: CSSProperties | undefined = url
    ? { WebkitMaskImage: `url(${url})`, maskImage: `url(${url})` }
    : undefined;
  return (
    <div
      className={cn(s.slot, held && s.held)}
      data-testid={`inv-slot-${cell.index}`}
      data-slot={cell.index}
      data-item={it ? it.name : undefined}
      data-count={it ? it.count : undefined}
      data-held={held ? 'true' : undefined}
      data-glint={glint ? 'true' : undefined}
      tabIndex={it ? 0 : -1}
      aria-label={
        it
          ? `${label}: ${it.display}${it.count > 1 ? ` ×${it.count}` : ''}${held ? ' (held)' : ''}`
          : `${label}: empty${held ? ' (held)' : ''}`
      }
      onMouseEnter={(e) => it && onTip(cell, e.currentTarget)}
      onMouseLeave={() => onTip(null, null)}
      onFocus={(e) => it && onTip(cell, e.currentTarget)}
      onBlur={() => onTip(null, null)}
    >
      {it ? (
        <>
          {url ? (
            // eslint-disable-next-line @next/next/no-img-element -- a data URL drawn here from the bundled atlas
            <img src={url} alt="" className={s.icon} draggable={false} />
          ) : (
            <span className={s.fallback}>{abbrev(it.name)}</span>
          )}
          {glint ? <span className={s.glint} style={mask} aria-hidden /> : null}
          {frac !== null && frac < 1 ? (
            <span className={s.bar} aria-hidden>
              <span style={{ width: `${Math.max(1, Math.round(frac * 100))}%`, background: durabilityColour(frac) }} />
            </span>
          ) : null}
          {it.count > 1 ? <span className={s.count}>{it.count}</span> : null}
        </>
      ) : ghost ? (
        <span className={s.ghost} aria-hidden>
          {ghost}
        </span>
      ) : null}
      {pulse !== null ? <span key={pulse} className={s.pulse} aria-hidden /> : null}
    </div>
  );
}, sameSlot);

/** A poll rebuilds every cell object; a slot re-renders only when what it shows changed. */
function sameSlot(a: SlotProps, b: SlotProps): boolean {
  const x = a.cell.item,
    y = b.cell.item;
  const same =
    x === y ||
    (!!x &&
      !!y &&
      x.id === y.id &&
      x.meta === y.meta &&
      x.count === y.count &&
      x.enchanted === y.enchanted &&
      x.display === y.display &&
      x.durability?.left === y.durability?.left &&
      x.durability?.max === y.durability?.max);
  return (
    same &&
    a.cell.index === b.cell.index &&
    a.version === b.version &&
    a.icons === b.icons &&
    a.held === b.held &&
    a.pulse === b.pulse &&
    a.label === b.label &&
    a.onTip === b.onTip &&
    !!a.ghost === !!b.ghost
  );
}

function Tip({ item, at }: { item: InventorySlot; at: CSSProperties }) {
  const [first, ...rest] = tooltipLines(item);
  return (
    <div className={s.tip} style={at} role="tooltip" data-testid="inv-tooltip">
      <div className={item.enchanted ? s.ench : undefined}>{first}</div>
      {rest.map((l) => (
        <div key={l} className={s.sub}>
          {l}
        </div>
      ))}
    </div>
  );
}

/** The grid itself (exported for tests and other panels). */
export function InventoryGrid({
  inv,
  changed,
  stamp,
}: {
  inv: BodyInventory;
  changed?: ReadonlySet<number>;
  stamp?: number;
}) {
  const grid = useMemo(() => inventoryGrid(inv), [inv]);
  const icons = useItemIcons(inv.version ?? 'b1.8.1');
  const box = useRef<HTMLDivElement>(null);
  const [tip, setTip] = useState<{ index: number; at: CSSProperties } | null>(null);
  const onTip = useCallback((cell: GridCell | null, el: HTMLElement | null) => {
    const root = box.current;
    if (!cell || !el || !root) return setTip(null);
    // Anchored to the slot's outer edge (so it never runs off the panel) and above it, or below for the top rows.
    const a = el.getBoundingClientRect(),
      b = root.getBoundingClientRect();
    const right = a.left - b.left > b.width / 2,
      below = a.top - b.top < 64;
    setTip({
      index: cell.index,
      at: {
        ...(right ? { right: b.right - a.right } : { left: a.left - b.left }),
        top: below ? a.bottom - b.top + 6 : a.top - b.top - 6,
        transform: below ? undefined : 'translateY(-100%)',
      },
    });
  }, []);
  const cells = [...grid.armor, ...(grid.offhand ? [grid.offhand] : []), ...grid.main.flat(), ...grid.hotbar];
  const tipItem = tip ? cells.find((c) => c.index === tip.index)?.item : null;
  const pulse = (i: number) => (changed?.has(i) ? (stamp ?? 0) : null);
  const one = (cell: GridCell, label: string, extra: Partial<SlotProps> = {}) => (
    <Slot
      key={cell.index}
      cell={cell}
      version={inv.version}
      icons={icons}
      pulse={pulse(cell.index)}
      label={label}
      onTip={onTip}
      {...extra}
    />
  );
  const used = cells.filter((c) => c.item).length;
  return (
    <div
      ref={box}
      className="relative inline-flex flex-col gap-2.5"
      data-testid="inventory-grid"
      data-version={inv.version ?? ''}
    >
      <div className="flex items-end gap-3">
        <div className="flex flex-col gap-0.5" role="group" aria-label="Armour">
          {grid.armor.map((c, k) => one(c, ARMOR_LABELS[k] ?? 'Armour', { ghost: <Shield className="size-full" /> }))}
        </div>
        <div className="flex min-w-0 flex-1 flex-col justify-end gap-1.5 pb-0.5 text-xs text-muted">
          {grid.offhand ? (
            <div className="flex items-center gap-2">
              {one(grid.offhand, 'Off hand')}
              <span>Off hand</span>
            </div>
          ) : null}
          <span className="tabular text-subtle">
            {used} of {cells.length} slots used
          </span>
        </div>
      </div>
      <div className="flex flex-col gap-0.5" role="group" aria-label="Main inventory">
        {grid.main.map((row, r) => (
          <div key={r} className="flex gap-0.5">
            {row.map((c) => one(c, `Slot ${c.index}`))}
          </div>
        ))}
      </div>
      <div className="flex gap-0.5" role="group" aria-label="Hotbar">
        {grid.hotbar.map((c) => one(c, `Hotbar ${c.index + 1}`, { held: c.index === grid.held }))}
      </div>
      {tip && tipItem ? <Tip item={tipItem} at={tip.at} /> : null}
    </div>
  );
}

/**
 * The Inventory section of a body's panel: polls while mounted, keeps the previous snapshot during refetches, pulses changed
 * slots, and says when the body is offline (its last known inventory) or unknown.
 */
export function InventorySection({ bot, className, compact }: { bot: string; className?: string; compact?: boolean }) {
  const q = useBodyInventory(bot);
  const inv = q.data && q.data.name ? q.data : null;
  const prev = useRef<BodyInventory | null>(null);
  const [delta, setDelta] = useState<{ changed: Set<number>; stamp: number }>({ changed: new Set(), stamp: 0 });
  useEffect(() => {
    if (!inv) return;
    const p = prev.current;
    prev.current = inv;
    if (p && p.at === inv.at) return;
    const changed = changedSlots(p, inv);
    if (changed.size) setDelta((d) => ({ changed, stamp: d.stamp + 1 }));
  }, [inv]);
  const notFound = q.error instanceof ApiError && q.error.status === 404;
  return (
    <section className={cn('space-y-2', className)} aria-label={`${bot}'s inventory`} data-testid="inventory-section">
      <div className="flex items-center gap-2 text-xs font-medium tracking-wide text-subtle uppercase">
        <Backpack className="size-3.5" /> Inventory
        {inv && !inv.online ? (
          <span className="ml-auto text-[11px] font-normal tracking-normal normal-case text-warn">
            Offline · last known {ago(inv.at)}
          </span>
        ) : inv?.version && !compact ? (
          <span className="ml-auto text-[11px] font-normal tracking-normal normal-case">{inv.version}</span>
        ) : null}
      </div>
      {inv ? (
        <div className={cn(!inv.online && 'opacity-80')}>
          <InventoryGrid inv={inv} changed={delta.changed} stamp={delta.stamp} />
        </div>
      ) : notFound ? (
        <p className="py-3 text-sm text-muted" data-testid="inventory-unknown">
          No inventory known for {bot} yet. It shows once the body is in game here.
        </p>
      ) : q.error ? (
        <p className="py-3 text-sm text-danger">Could not load the inventory.</p>
      ) : (
        <Skeleton className="h-[16.5rem] w-[21.5rem] max-w-full rounded-lg" />
      )}
    </section>
  );
}
