'use client';

/**
 * A container's slots laid out like the game's screen for that block (rows of nine for chests and shulker boxes, 3×3 for a
 * dispenser or dropper, a row of five for a hopper, the furnace's input → output with its fuel below, the brewing stand's
 * bottles under the ingredient), with the inventory panel's own slot cells: icons from the version's atlas, counts, durability
 * bars, the glint and the tooltip. Pure DOM and CSS.
 */
import type { ContainerView } from '@mineclaw/contracts';
import { ArrowRight, Flame } from 'lucide-react';
import { useMemo } from 'react';
import { containerLayout, type ContainerLayout } from './containerLayout';
import type { GridCell } from './grid';
import { Slot, Tip, useItemIcons, useSlotTip } from './InventoryPanel';

export function ContainerGrid({ view }: { view: Pick<ContainerView, 'kind' | 'size' | 'slots' | 'version'> }) {
  const layout: ContainerLayout = useMemo(() => containerLayout(view), [view]);
  const icons = useItemIcons(view.version ?? 'b1.8.1');
  const { box, tip, onTip } = useSlotTip();
  const cells: GridCell[] =
    layout.shape === 'rows'
      ? layout.rows.flat()
      : layout.shape === 'furnace'
        ? [layout.input, layout.fuel, layout.output]
        : [...layout.bottles, layout.ingredient, layout.fuel];
  const tipItem = tip ? cells.find((c) => c.index === tip.index)?.item : null;
  const one = (cell: GridCell, label: string) => (
    <Slot
      key={cell.index}
      cell={cell}
      version={view.version ?? 'b1.8.1'}
      icons={icons}
      pulse={null}
      label={label}
      onTip={onTip}
      testid="ctr-slot"
    />
  );
  return (
    <div
      ref={box}
      className="relative inline-flex flex-col gap-0.5"
      data-testid="container-grid"
      data-kind={view.kind}
      data-size={view.size}
    >
      {layout.shape === 'rows' ? (
        layout.rows.map((row, r) => (
          <div key={r} className="flex gap-0.5" role="group" aria-label={`Row ${r + 1}`}>
            {row.map((c) => one(c, `Slot ${c.index + 1}`))}
          </div>
        ))
      ) : layout.shape === 'furnace' ? (
        <div className="flex items-center gap-3 py-1" role="group" aria-label="Furnace">
          <div className="flex flex-col items-center gap-1">
            {one(layout.input, 'Input')}
            <Flame className="size-3.5 text-warn" aria-hidden />
            {one(layout.fuel, 'Fuel')}
          </div>
          <ArrowRight className="size-5 text-subtle" aria-hidden />
          {one(layout.output, 'Output')}
        </div>
      ) : (
        <div className="flex flex-col items-center gap-2 py-1" role="group" aria-label="Brewing stand">
          <div className="flex gap-3">
            {one(layout.fuel, 'Fuel')}
            {one(layout.ingredient, 'Ingredient')}
          </div>
          <div className="flex gap-2">{layout.bottles.map((c, i) => one(c, `Bottle ${i + 1}`))}</div>
        </div>
      )}
      {tip && tipItem ? <Tip item={tipItem} at={tip.at} /> : null}
    </div>
  );
}
