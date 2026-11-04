'use client';

/** The work roles' icons: a role's `icon` key → a lucide glyph; an unknown key (or none) draws the name's initial. */
import {
  Axe,
  BrickWall,
  CookingPot,
  Fish,
  Hammer,
  Package,
  Pickaxe,
  Shapes,
  Shield,
  Sprout,
  Star,
  Swords,
  Wheat,
  type LucideIcon,
} from 'lucide-react';
import { cn } from '@/lib/utils';

export const ROLE_ICONS: Readonly<Record<string, LucideIcon>> = {
  wheat: Wheat,
  sprout: Sprout,
  pickaxe: Pickaxe,
  axe: Axe,
  brick: BrickWall,
  package: Package,
  hammer: Hammer,
  pot: CookingPot,
  fish: Fish,
  shield: Shield,
  swords: Swords,
  shapes: Shapes,
  star: Star,
};

export const ROLE_ICON_KEYS = Object.keys(ROLE_ICONS);

export function RoleIcon({ icon, name, className }: { icon?: string | null; name: string; className?: string }) {
  const I = icon ? ROLE_ICONS[icon] : undefined;
  if (I) return <I className={cn('size-3.5 shrink-0', className)} aria-hidden />;
  return (
    <span className={cn('inline-grid size-3.5 shrink-0 place-items-center text-[9px] font-bold', className)} aria-hidden>
      {name.slice(0, 1).toUpperCase()}
    </span>
  );
}
