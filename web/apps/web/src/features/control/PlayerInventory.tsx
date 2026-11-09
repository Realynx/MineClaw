'use client';

/* eslint-disable @next/next/no-img-element -- Icons are browser-local data URLs from the user's imported game assets. */

import type { PawnControlInventory } from '@mineclaw/contracts';
import type { IconOf } from '@/features/live/inventory/itemIcons';
import { cn } from '@/lib/utils';

type Item = PawnControlInventory['slots'][number];

export function PlayerSlot({ item, icons, selected, label, onClick, onContextMenu }: {
  item?: Item | null; icons: IconOf | null; selected?: boolean; label: string;
  onClick?: React.MouseEventHandler<HTMLButtonElement>; onContextMenu?: React.MouseEventHandler<HTMLButtonElement>;
}) {
  const image = item ? icons?.(item.id, item.meta) : null;
  return <button type="button" aria-label={label} aria-pressed={selected} title={label}
    onClick={onClick} onContextMenu={onContextMenu}
    className={cn('relative size-11 shrink-0 border-2 bg-black/50 text-white', selected ? 'border-white bg-white/20' : 'border-white/25 hover:border-white/60')}>
    {image ? <img src={image} alt="" draggable={false} className="absolute inset-1 size-8" style={{ imageRendering: 'pixelated' }} />
      : item ? <span className="text-[10px]">{item.id}:{item.meta}</span> : null}
    {item && item.count > 1 && <span className="absolute right-0.5 bottom-0 text-xs font-bold [text-shadow:1px_1px_1px_black]">{item.count}</span>}
  </button>;
}

export function PlayerInventory({ inventory, icons, nameOf, busy, onClick, onClose }: {
  inventory: PawnControlInventory; icons: IconOf | null; nameOf: (id: number, meta: number) => string;
  busy: boolean; onClick: (slot: number, button: 0 | 1, shift: boolean) => void; onClose: () => void;
}) {
  const items = new Map(inventory.slots.map((s) => [s.slot, s]));
  const slot = (i: number) => {
    const item = items.get(i);
    const label = `${item ? `${nameOf(item.id, item.meta)} ×${item.count}` : 'Empty'} · slot ${i}`;
    return <PlayerSlot key={i} icons={icons} item={item} label={label}
      onClick={(e) => { if (!busy) onClick(i, 0, e.shiftKey); }}
      onContextMenu={(e) => { e.preventDefault(); if (!busy) onClick(i, 1, e.shiftKey); }} />;
  };
  const own = inventory.windowId === 0;
  const top = Array.from({ length: inventory.containerSlots }, (_, i) => i);
  return <div className="absolute inset-0 z-30 grid place-items-center bg-black/45 p-4">
    <section role="dialog" aria-modal="true" aria-label={inventory.title || 'Inventory'} className="max-h-full overflow-auto rounded-xl border border-border bg-surface p-4 shadow-xl">
      <div className="mb-3 flex items-center justify-between gap-6"><h2 className="font-semibold">{inventory.title || 'Inventory'}</h2><button onClick={onClose} className="rounded bg-surface-2 px-3 py-1">Close · E</button></div>
      {own ? <div className="flex items-center justify-between gap-4" aria-busy={busy}>
        <div className="grid gap-0.5">{['Head', 'Chest', 'Legs', 'Feet'].map((label, i) => <div key={label} className="flex items-center gap-2 text-xs text-muted">{slot(i + 5)}{label}</div>)}</div>
        <div><p className="mb-1 text-xs text-muted">Crafting</p><div className="flex items-center gap-3"><div className="grid grid-cols-2 gap-0.5">{[1, 2, 3, 4].map(slot)}</div><span aria-hidden>→</span>{slot(0)}</div>
          {inventory.slotCount > 45 && <div className="mt-4 flex items-center gap-2 text-xs text-muted">{slot(45)} Off hand</div>}
        </div>
      </div> : <div className="grid grid-cols-9 gap-0.5" aria-busy={busy}>{top.map(slot)}</div>}
      <p className="mt-3 mb-1 text-xs text-muted">Inventory</p>
      <div className="grid grid-cols-9 gap-0.5">{Array.from({ length: 27 }, (_, i) => inventory.mainStart + i).filter((i) => i < inventory.slotCount).map(slot)}</div>
      <div className="mt-2 grid grid-cols-9 gap-0.5">{Array.from({ length: 9 }, (_, i) => inventory.hotbarStart + i).filter((i) => i < inventory.slotCount).map(slot)}</div>
      <div className="mt-3 flex min-h-11 items-center gap-3 text-xs text-muted">
        {inventory.cursor ? <><span>Carrying</span><PlayerSlot item={{ ...inventory.cursor, slot: -1 }} icons={icons} label={nameOf(inventory.cursor.id, inventory.cursor.meta)} /></> : <span>Left click to move · Right click to split · Shift click to transfer</span>}
      </div>
    </section>
  </div>;
}
