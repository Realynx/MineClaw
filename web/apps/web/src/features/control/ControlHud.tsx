'use client';

import { useEffect, useState, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** How long the held item's name stays after the hand changes, and the red flash after a hit, in ms. */
export const HELD_NAME_MS = 2000;
export const HURT_MS = 500;
/** A full air supply, in ticks: the air row shows only below it (under water). */
const FULL_AIR = 300;

type Fill = 'full' | 'half' | 'empty';

/** Ten icons for a value out of 20, two points each, as the game's status bar draws them. */
function fills(points: number): Fill[] {
  const v = Math.max(0, Math.min(20, Math.ceil(points)));
  return Array.from({ length: 10 }, (_, i) => (v >= 2 * i + 2 ? 'full' : v === 2 * i + 1 ? 'half' : 'empty'));
}

/** Bubbles left for an air supply in ticks: the last one goes as the supply nears 0. */
export function bubbles(air: number): number {
  return Math.max(0, Math.min(10, Math.ceil(((air - 2) * 10) / FULL_AIR)));
}

function Row({
  label,
  glyph,
  colour,
  icons,
  align,
}: {
  label: string;
  glyph: string;
  colour: string;
  icons: Fill[];
  align: 'left' | 'right';
}) {
  return (
    <div
      role="img"
      aria-label={label}
      className={cn('flex gap-px text-sm leading-none', align === 'right' && 'flex-row-reverse')}
    >
      {icons.map((fill, i) => (
        <span
          key={i}
          data-fill={fill}
          className={cn(fill === 'empty' ? 'text-white/25' : colour, fill === 'half' && 'opacity-60')}
        >
          {glyph}
        </span>
      ))}
    </div>
  );
}

export interface ControlHudProps {
  hp: number;
  food: number;
  /** Armour points (0..20); absent from an older server. Hidden while nothing is worn, as in the game. */
  armor?: number | undefined;
  /** Air supply in ticks (300 = full); absent from an older server. Shown only while it runs down. */
  air?: number | undefined;
  /** The selected hotbar slot and the name of what it holds (null = an empty hand). */
  slot: number;
  held: string | null;
  /** The hotbar, drawn under the status rows. */
  children?: ReactNode;
}

/** The pawn's status bar: hearts and armour on the left, hunger and air on the right, the held item's name for a moment
 * after the hand changes, and a red edge on the screen when the pawn is hurt. */
export function ControlHud({ hp, food, armor, air, slot, held, children }: ControlHudProps) {
  const [lastHp, setLastHp] = useState(hp);
  const [hurt, setHurt] = useState(0);
  if (hp !== lastHp) {
    setLastHp(hp);
    if (hp < lastHp) setHurt((n) => n + 1);
  }
  useEffect(() => {
    if (!hurt) return;
    const timer = setTimeout(() => setHurt(0), HURT_MS);
    return () => clearTimeout(timer);
  }, [hurt]);

  const hand = `${slot}:${held ?? ''}`;
  const [lastHand, setLastHand] = useState(hand);
  const [shownName, setShownName] = useState<{ name: string; n: number } | null>(null);
  if (hand !== lastHand) {
    setLastHand(hand);
    setShownName(held ? { name: held, n: (shownName?.n ?? 0) + 1 } : null);
  }
  useEffect(() => {
    if (!shownName) return;
    const timer = setTimeout(() => setShownName(null), HELD_NAME_MS);
    return () => clearTimeout(timer);
  }, [shownName]);

  const points = (v: number) => Math.max(0, Math.min(20, Math.ceil(v)));
  return (
    <>
      {hurt > 0 && (
        <div
          key={hurt}
          data-testid="damage-vignette"
          className="pointer-events-none absolute inset-0 z-10 shadow-[inset_0_0_160px_40px_rgba(220,38,38,0.55)]"
        />
      )}
      <div className="absolute bottom-4 left-1/2 z-10 -translate-x-1/2 text-white">
        {shownName && (
          <p data-testid="held-name" className="mb-1 text-center text-sm [text-shadow:1px_1px_2px_black]">
            {shownName.name}
          </p>
        )}
        <div className="mb-1.5 flex items-end justify-between gap-6 [text-shadow:1px_1px_2px_black]">
          <div className="grid gap-0.5">
            {armor !== undefined && armor > 0 && (
              <Row
                label={`Armour ${points(armor)} / 20`}
                glyph="■"
                colour="text-slate-200"
                icons={fills(armor)}
                align="left"
              />
            )}
            <Row label={`Health ${points(hp)} / 20`} glyph="♥" colour="text-red-500" icons={fills(hp)} align="left" />
          </div>
          <div className="grid justify-items-end gap-0.5">
            {air !== undefined && air < FULL_AIR && (
              <Row
                label={`Air ${bubbles(air)} / 10`}
                glyph="○"
                colour="text-sky-300"
                icons={Array.from({ length: 10 }, (_, i) => (i < bubbles(air) ? 'full' : 'empty'))}
                align="right"
              />
            )}
            <Row
              label={`Food ${points(food)} / 20`}
              glyph="◆"
              colour="text-amber-500"
              icons={fills(food)}
              align="right"
            />
          </div>
        </div>
        {children}
      </div>
    </>
  );
}
