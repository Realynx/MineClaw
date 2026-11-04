'use client';

/**
 * The Architect's Zones tools (docs/web/story-mode.md "Zones"): work zones (Growing zone with its plant, Woodlot, Quarry) that
 * replace each other where painted, the overlay layers (Home area, Keep out) that never do, and Erase with the kind it erases
 * (Any by default). Zone tools stay in hand until Esc or a right-click, like RimWorld's; Shift-drag from inside a zone extends it.
 */
import { isOverlayZone, type StoryPlant, type StoryZone } from '@mineclaw/contracts';
import type { ReactNode } from 'react';
import { useSyncExternalStore } from 'react';
import { cn } from '@/lib/utils';
import { GrowPlantPicker } from '../GrowingZones';
import { ZONE_TOOLS } from '../tools';
import { QuarryModePicker } from './quarry';
import { ERASE_ANY, type ZoneTool } from './zonePaint';

let eraseKind = ERASE_ANY;
const listeners = new Set<() => void>();
const useEraseKind = () =>
  useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => void listeners.delete(l);
    },
    () => eraseKind,
    () => eraseKind,
  );

type Btn = (t: ZoneTool, icon: string, label: string, hint: string) => ReactNode;

export function ZoneToolbar({
  tool,
  onTool,
  plants,
  zones,
  btn,
}: {
  tool: { kind: string };
  onTool: (t: ZoneTool) => void;
  plants?: readonly StoryPlant[] | null;
  zones: readonly StoryZone[];
  btn: Btn;
}) {
  const erase = useEraseKind();
  const work = ZONE_TOOLS.filter((z) => !isOverlayZone(z.id));
  const overlays = ZONE_TOOLS.filter((z) => isOverlayZone(z.id));
  const zt = tool.kind === 'zone' ? (tool as unknown as ZoneTool) : null;
  const target = zt?.into !== undefined ? zones.find((z) => String(z.id) === String(zt.into)) : undefined;
  const kinds: Array<[string, string]> = [
    [ERASE_ANY, 'Any'],
    ...ZONE_TOOLS.map((z) => [z.id, z.label] as [string, string]),
  ];
  return (
    <>
      {work.map((z) =>
        btn({ kind: 'zone', id: z.id, op: 'add' }, 'zone', z.label, `${z.hint} (replaces other work zones)`),
      )}
      {overlays.map((z) => btn({ kind: 'zone', id: z.id, op: 'add' }, 'zone', z.label, `${z.hint} (an overlay layer)`))}
      {btn(
        { kind: 'zone', id: erase, op: 'remove' },
        'cancel',
        'Erase zone',
        'Drag over zones to erase those cells (empty zones go, a zone cut in two becomes two)',
      )}
      {zt?.op === 'remove' ? (
        <div className="w-full" data-testid="zone-erase-kind">
          <div className="mb-1 text-[11px] font-semibold text-muted">Erase</div>
          <div className="flex flex-wrap gap-1" role="radiogroup" aria-label="Erase which zones">
            {kinds.map(([id, label]) => (
              <button
                key={id}
                type="button"
                role="radio"
                aria-checked={erase === id}
                onClick={() => {
                  eraseKind = id;
                  for (const l of listeners) l();
                  onTool({ kind: 'zone', id, op: 'remove' });
                }}
                className={cn(
                  'rounded border px-1.5 py-0.5 text-[11px]',
                  erase === id ? 'border-accent bg-accent-soft text-fg' : 'border-border text-muted hover:text-fg',
                )}
              >
                {label}
              </button>
            ))}
          </div>
        </div>
      ) : null}
      {zt?.op === 'add' && zt.id === 'farm' && zt.into === undefined ? <GrowPlantPicker plants={plants} /> : null}
      {zt?.op === 'add' && zt.id === 'mine' && zt.into === undefined ? <QuarryModePicker /> : null}
      {zt?.op === 'add' && zt.into !== undefined ? (
        <p className="w-full text-[11px] text-accent" data-testid="zone-extending">
          Extending {target?.name || target?.label || `zone ${zt.into}`}: painted cells join it. Esc to stop.
        </p>
      ) : null}
      <p className="w-full text-[11px] leading-snug text-muted">
        Drag to paint. Cells touching a zone of the same type join it; work zones replace each other; Home area and Keep
        out are overlay layers. Shift-drag from inside a zone extends that zone.
      </p>
    </>
  );
}
