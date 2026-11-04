'use client';

/**
 * Bulk gear for a selection of pawns (the inspector's gear row, and the right-click menu): Arm, Equip armour, Pick up…, Return
 * gear, Equip tools for their work. Each is ONE gear op for the whole selection; the director spreads the items fairly and
 * the answer's summary is one toast. The Arm button previews who would get what. Also the attack confirm dialog and a pawn's
 * equipment (durability bars, enchantments, its policy).
 */
import type { StoryColonyState, StoryGearItem, StoryGearOp, StoryPawn, StoryPolicy } from '@mineclaw/contracts';
import { Backpack, PackageOpen, Pickaxe, ShieldPlus, Swords } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button, Input, Select } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { durability, enchantLabel, policyOf, pickupChoices, previewArm, storedArmor, type AttackWho } from './gear';

const ICONS: Record<StoryGearOp, typeof Swords> = {
  arm: Swords,
  armor: ShieldPlus,
  pickup: Backpack,
  return: PackageOpen,
  tools: Pickaxe,
};

export function GearActions({
  state,
  pawns,
  canWrite,
  onGear,
}: {
  state: StoryColonyState | undefined;
  pawns: readonly string[];
  canWrite: boolean;
  onGear: (op: StoryGearOp, item?: [number, number], count?: number) => void;
}) {
  const [picking, setPicking] = useState(false);
  const preview = useMemo(() => previewArm(state, pawns), [state, pawns]);
  if (!pawns.length) return null;
  const btn = (op: StoryGearOp, label: string, title: string) => {
    const I = ICONS[op];
    return (
      <Button
        key={op}
        size="sm"
        variant="ghost"
        disabled={!canWrite}
        title={title}
        data-gear={op}
        onClick={() => (op === 'pickup' ? setPicking(true) : onGear(op))}
      >
        <I className="size-3.5" /> {label}
      </Button>
    );
  };
  const armTitle = preview.map((l) => `${l.display}: ${l.text}`).join('\n');
  return (
    <div className="mt-2 border-t border-border pt-1.5" data-testid="gear-actions">
      <div className="mb-0.5 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">
        Gear{pawns.length > 1 ? ` · ${pawns.length} pawns` : ''}
      </div>
      <div className="flex flex-wrap gap-0.5">
        {btn('arm', 'Arm', armTitle || 'Best weapon each')}
        {btn('armor', 'Armour', `The best armour spread over the selection (${storedArmor(state)} pieces stored)`)}
        {btn('pickup', 'Pick up…', 'Fetch an item from storage, the same for each')}
        {btn('tools', 'Tools', 'The tools each pawn’s work and policy need')}
        {btn('return', 'Return', 'Take off armour, store tools and weapons')}
      </div>
      {pawns.length > 1 && preview.length ? (
        <ul className="mt-1 space-y-0.5 text-[10.5px]" data-testid="arm-preview" aria-label="Arm preview">
          {preview.map((l) => (
            <li key={l.pawn} className={cn('truncate', l.missing ? 'text-subtle' : 'text-muted')}>
              <span className="font-semibold text-fg">{l.display}</span> · {l.text}
            </li>
          ))}
        </ul>
      ) : null}
      <PickupDialog
        open={picking}
        state={state}
        n={pawns.length}
        onClose={() => setPicking(false)}
        onPick={(item, count) => {
          setPicking(false);
          onGear('pickup', item, count);
        }}
      />
    </div>
  );
}

export function PickupDialog({
  open,
  state,
  n,
  onClose,
  onPick,
}: {
  open: boolean;
  state: StoryColonyState | undefined;
  n: number;
  onClose: () => void;
  onPick: (item: [number, number], count: number) => void;
}) {
  const choices = useMemo(() => pickupChoices(state), [state]);
  const [sel, setSel] = useState('');
  const [count, setCount] = useState(1);
  const hit = choices.find((c) => c.item.join(':') === sel) ?? choices[0];
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title="Pick up from storage"
      description={`Each of the ${n} selected pawn${n === 1 ? '' : 's'} fetches this many; nobody fetches the same item twice.`}
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={!hit} onClick={() => hit && onPick(hit.item, count)}>
            Pick up
          </Button>
        </>
      }
    >
      {choices.length === 0 ? (
        <p className="text-sm text-muted">The colony&apos;s storage holds nothing it knows of.</p>
      ) : (
        <div className="space-y-2 text-sm">
          <Select
            aria-label="Item to pick up"
            value={hit ? hit.item.join(':') : ''}
            onChange={(e) => setSel(e.target.value)}
          >
            {choices.map((c) => (
              <option key={c.item.join(':')} value={c.item.join(':')}>
                {c.label} ({c.count} stored)
              </option>
            ))}
          </Select>
          <label className="flex items-center gap-2">
            Each
            <Input
              aria-label="How many each"
              type="number"
              min={1}
              max={64}
              className="h-8 w-20"
              value={count}
              onChange={(e) => setCount(Math.max(1, Math.min(64, Math.floor(Number(e.target.value) || 1))))}
            />
          </label>
        </div>
      )}
    </Dialog>
  );
}

/** "Attack PoofimaFox?": a player or another bot is attacked only after this, once per target per session. */
export function AttackConfirm({
  who,
  pawns,
  onConfirm,
  onCancel,
}: {
  who: AttackWho | null;
  pawns: number;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <Dialog
      open={!!who}
      onOpenChange={(o) => !o && onCancel()}
      title={who ? `Attack ${who.name}?` : ''}
      description={
        who
          ? `${who.name} is ${who.kind === 'bot' ? 'a bot' : 'a player'}. ${pawns} drafted pawn${pawns === 1 ? '' : 's'} will fight ${
              who.kind === 'bot' ? 'it' : 'them'
            } until it ends or you undraft. You won't be asked again for ${who.name} this session.`
          : undefined
      }
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="danger" onClick={onConfirm} data-testid="attack-confirm">
            Attack
          </Button>
        </>
      }
    />
  );
}

function GearRow({ it, worn }: { it: StoryGearItem; worn?: boolean }) {
  const d = durability(it);
  const ench = Object.entries(it.ench ?? {});
  return (
    <li className="flex items-center gap-1.5" data-gear-item={it.name ?? it.item.join(':')}>
      <span className={cn('min-w-0 flex-1 truncate', worn && 'font-semibold')}>{it.label ?? it.name ?? it.kind}</span>
      {ench.map(([k, v]) => (
        <span
          key={k}
          className="rounded bg-[#a78bfa]/20 px-1 text-[10px] text-[#7c5cd6] dark:text-[#c4b5fd]"
          data-ench={k}
        >
          {enchantLabel(k, v)}
        </span>
      ))}
      {d.pct !== null ? (
        <span className="flex w-16 items-center gap-1" title={`${d.pct}% durability left`}>
          <span className="block h-1.5 flex-1 overflow-hidden rounded-full bg-surface-3">
            <span
              className={cn(
                'block h-full',
                d.tone === 'worn' ? 'bg-danger' : d.tone === 'low' ? 'bg-[#e2a336]' : 'bg-[#46a758]',
              )}
              style={{ width: `${d.pct}%` }}
            />
          </span>
          <span className="w-7 text-right text-[10px] tabular-nums text-muted">{d.pct}%</span>
        </span>
      ) : null}
    </li>
  );
}

/** A pawn's equipment for its inspector: held, armour, tools, weapons (with durability and enchantments) and its policy. */
export function EquipmentView({ pawn, policies }: { pawn: StoryPawn; policies: readonly StoryPolicy[] }) {
  const e = pawn.equipment;
  const pol = policyOf(pawn, policies);
  return (
    <div className="space-y-1 rounded-md bg-surface-2 px-2 py-1.5" data-testid="pawn-equipment">
      <div className="flex items-center text-[10.5px] font-semibold tracking-wide text-subtle uppercase">
        Equipment
        {pol ? (
          <span className="ml-auto font-normal tracking-normal normal-case" data-testid="pawn-policy">
            Policy: {pol.name}
          </span>
        ) : null}
      </div>
      {!e || (!e.held && !e.armor.length && !e.tools.length && !e.weapons.length) ? (
        <p className="text-muted">Nothing carried</p>
      ) : (
        <ul className="space-y-0.5">
          {e.held ? (
            <li className="text-[10.5px] text-muted">
              Holding <span className="text-fg">{e.held.label ?? e.held.name}</span>
            </li>
          ) : null}
          {[...e.weapons, ...e.tools, ...e.armor].map((it, i) => (
            <GearRow key={i} it={it} worn={e.armor.includes(it)} />
          ))}
        </ul>
      )}
      {pawn.gearTasks?.length ? (
        <div className="text-[10.5px] text-muted">Gear: {pawn.gearTasks.map((t) => t.label ?? t.op).join(', ')}</div>
      ) : null}
    </div>
  );
}
