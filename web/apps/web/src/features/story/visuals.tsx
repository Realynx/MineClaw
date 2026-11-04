'use client';

/**
 * Story Mode's own look: pawn avatars (a blocky face drawn from the pawn's name, no game skins), tool icons (lucide-based and
 * our own SVGs), the map's marker glyphs (Path2D paths in a 16-unit box), item icons cut from the committed b1.8.1 atlases,
 * and the palette the map overlay uses in both themes.
 */
import { storyItemLabel } from '@mineclaw/contracts';
import {
  AppWindow,
  Archive,
  Axe,
  Ban,
  BedDouble,
  BrickWall,
  Check,
  Crosshair,
  DoorOpen,
  Eraser,
  Fence,
  Flame,
  HandGrab,
  Hammer,
  House,
  Lamp,
  Pickaxe,
  Scissors,
  Square,
  SquareDashed,
  Wheat,
  X,
  type LucideIcon,
} from 'lucide-react';
import { memo, useEffect, useState, type SVGProps } from 'react';
import { cn } from '@/lib/utils';

export function hashOf(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}

/** Pawn colours (map token rings and portrait backs). */
export const PAWN_COLORS = [
  '#5ec8e5',
  '#f28c6b',
  '#9bd36a',
  '#c49bf2',
  '#f2d16b',
  '#58d6ae',
  '#f26bab',
  '#8aa9ff',
  '#f2a65e',
  '#7de0e0',
  '#c9e26b',
  '#ff9fd0',
];
export const pawnColor = (name: string) => PAWN_COLORS[hashOf(name) % PAWN_COLORS.length]!;

export const COLORS = {
  select: '#f5c542',
  ai: '#a78bfa',
  ghost: { todo: '#7fd3ff', leased: '#ffd166', done: '#8ee59b', blocked: '#ff6b6b' } as Record<string, string>,
  designation: {
    chop: '#e8a85a',
    cut: '#9ad16b',
    harvest: '#f2d16b',
    mine: '#c3ccd6',
    haul: '#6bc5f2',
    hunt: '#f2836b',
    deconstruct: '#f29e6b',
    forbid: '#ff5f6d',
    allow: '#6ee7a8',
    build: '#7fd3ff',
  } as Record<string, string>,
  danger: '#ff5f6d',
  order: '#f5c542',
  chest: '#d9a25f',
  bench: '#9ec7ff',
};

const SKIN = ['#f2d2b6', '#e3b48f', '#c98e66', '#9c6a48', '#6e4a33'];
const HAIR = ['#2b1f17', '#5a3a22', '#a2652f', '#d9b25f', '#e8e2d0', '#7d2d24', '#3b4a6b'];

/** A blocky face drawn from the name: skin, hair colour and cut, eyes; the back is the pawn's colour. */
/** A pawn's pixel portrait (pure by name: memoised, the colonist bar re-renders on every state poll). */
export const Avatar = memo(function Avatar({
  name,
  size = 40,
  ai = false,
  className,
}: {
  name: string;
  size?: number;
  ai?: boolean;
  className?: string;
}) {
  const h = hashOf(name);
  const skin = SKIN[h % SKIN.length]!;
  const hair = HAIR[(h >> 4) % HAIR.length]!;
  const cut = (h >> 8) % 4;
  const eye = (h >> 12) % 3 === 0 ? '#3d6fb5' : (h >> 12) % 3 === 1 ? '#4a7d3a' : '#4a3424';
  const back = ai ? '#2a2445' : pawnColor(name);
  return (
    <svg
      viewBox="0 0 16 16"
      width={size}
      height={size}
      className={cn('block', className)}
      aria-hidden
      shapeRendering="crispEdges"
    >
      <rect width="16" height="16" fill={back} opacity={ai ? 1 : 0.85} />
      <rect x="4" y="3" width="8" height="9" fill={skin} />
      {/* hair */}
      <rect x="4" y="3" width="8" height={cut === 2 ? 1 : 2} fill={hair} />
      {cut === 0 ? <rect x="4" y="5" width="1" height="3" fill={hair} /> : null}
      {cut === 0 ? <rect x="11" y="5" width="1" height="3" fill={hair} /> : null}
      {cut === 1 ? <rect x="3" y="4" width="10" height="2" fill={hair} /> : null}
      {cut === 3 ? <rect x="4" y="5" width="2" height="1" fill={hair} /> : null}
      {/* eyes and mouth */}
      <rect x="5" y="7" width="2" height="1" fill="#fff" />
      <rect x="9" y="7" width="2" height="1" fill="#fff" />
      <rect x="6" y="7" width="1" height="1" fill={eye} />
      <rect x="9" y="7" width="1" height="1" fill={eye} />
      <rect x="7" y="10" width="2" height="1" fill="#00000055" />
      {/* shoulders */}
      <rect x="2" y="13" width="12" height="3" fill={ai ? '#4b3f7a' : '#00000040'} />
      {ai ? <path d="M11.5 1.5h3v3h-3z" fill="#a78bfa" /> : null}
    </svg>
  );
});

/** A small brain mark for AI bodies (our own glyph). */
export function AiMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 16 16" className={className} aria-hidden>
      <path
        d="M6 2.5a2.5 2.5 0 0 0-2.4 3.1A2.5 2.5 0 0 0 3 10.4 2.5 2.5 0 0 0 6.5 13.5H8V2.5H6Zm4 0a2.5 2.5 0 0 1 2.4 3.1 2.5 2.5 0 0 1 .6 4.8 2.5 2.5 0 0 1-3.5 3.1H8V2.5h2Z"
        fill="currentColor"
      />
      <path d="M8 4v9M5.5 7h1.5M9 9h1.5" stroke="#00000066" strokeWidth="0.8" fill="none" />
    </svg>
  );
}

/** Our own stairs icon (lucide has none). */
export function StairsIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      {...props}
    >
      <path d="M3 20h5v-5h5v-5h5V5h3" />
      <path d="M3 20h18" opacity=".5" />
    </svg>
  );
}

export function BlockIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" {...props}>
      <path d="M12 3 4 7.5v9L12 21l8-4.5v-9L12 3Z" />
      <path d="M4 7.5 12 12l8-4.5M12 12v9" />
    </svg>
  );
}

export function BenchIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" {...props}>
      <rect x="3" y="6" width="18" height="5" rx="1" />
      <path d="M5 11v8M19 11v8M8 6V4h8v2" />
    </svg>
  );
}

export function RoofIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      {...props}
    >
      <path d="M2 13 12 5l10 8" />
      <path d="M5 11v8h14v-8" opacity=".45" />
    </svg>
  );
}

type IconC = LucideIcon | ((p: SVGProps<SVGSVGElement>) => React.JSX.Element);

export const TOOL_ICONS: Record<string, IconC> = {
  chop: Axe,
  cut: Scissors,
  harvest: Wheat,
  mine: Pickaxe,
  haul: HandGrab,
  hunt: Crosshair,
  deconstruct: Hammer,
  cancel: X,
  forbid: Ban,
  allow: Check,
  wall: BrickWall,
  floor: Square,
  roof: RoofIcon,
  door: DoorOpen,
  window: AppWindow,
  stairs: StairsIcon,
  fence: Fence,
  block: BlockIcon,
  remove: Eraser,
  crafting_table: BenchIcon,
  furnace: Flame,
  chest: Archive,
  bed: BedDouble,
  torch: Lamp,
  storage: Archive,
  zone: SquareDashed,
  home: House,
};

export function ToolIcon({ id, className }: { id: string; className?: string }) {
  const I = TOOL_ICONS[id] ?? SquareDashed;
  return <I className={cn('size-4', className)} aria-hidden />;
}

/** Map marker glyphs in a 16-unit box (our own drawings), filled. */
export const GLYPHS: Record<string, string> = {
  chop: 'M4 13 10 7l1.5 1.5L5.5 14.5ZM9.5 3.5c2-1 4 .5 4.5 2.5L11 9 7.5 5.5Z',
  cut: 'M4 4a2 2 0 1 1 0 .1ZM4 12a2 2 0 1 1 0 .1ZM5.5 5 13 11l-1 1-7-5.5ZM5.5 11 13 5l-1-1-7 5.5Z',
  harvest:
    'M8 2c1.5 1.5 1.5 3 0 4.5C6.5 5 6.5 3.5 8 2Zm-3 4c2 0 3 1.5 3 3-2 0-3-1.5-3-3Zm6 0c0 1.5-1 3-3 3 0-1.5 1-3 3-3ZM7.3 8h1.4v6H7.3Z',
  mine: 'M2.5 6C5 3 11 3 13.5 6l-1 .8C10.6 5 5.4 5 3.5 6.8ZM7.3 5h1.4v9H7.3Z',
  haul: 'M3 8.5 5 6.5l2 2V3h2v5.5l2-2 2 2-5 5Z',
  hunt: 'M8 2v3M8 11v3M2 8h3M11 8h3M8 5.5a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5Z',
  deconstruct: 'M3 5l4-2 2 2-1 1 5 5-2 2-5-5-1 1-2-2Z',
  forbid: 'M8 2.5a5.5 5.5 0 1 1 0 11 5.5 5.5 0 0 1 0-11Zm-3.2 2.9 6.3 6.3a4 4 0 0 0-6.3-6.3Z',
  allow: 'M3 8.5 4.5 7l2.5 2.5L11.5 5 13 6.5l-6 6Z',
  build: 'M3 13V6l5-3 5 3v7H9.5V9h-3v4Z',
  chest: 'M2.5 5h11v8h-11Zm0 0 1.5-2h8l1.5 2M7 7.5h2v2H7Z',
  bench: 'M2 5h12v3H2Zm1 3h2v5H3Zm8 0h2v5h-2Z',
  drafted: 'M8 1.5 13 3.5v4c0 3-2.2 5.5-5 7-2.8-1.5-5-4-5-7v-4Z',
  home: 'M8 1.8 14.5 7.5h-2V14H9.6v-3.8H6.4V14H3.5V7.5h-2Z',
  bed: 'M1.5 5h2v3.5h11V13h-2v-2h-9v2h-2Zm3.5.5h3.5v2.5H5Zm4.5.5H13a1 1 0 0 1 1 1v1H10Z',
};

const pathCache = new Map<string, Path2D>();
export function glyph(id: string): Path2D | null {
  const d = GLYPHS[id];
  if (!d || typeof Path2D === 'undefined') return null;
  let p = pathCache.get(id);
  if (!p) pathCache.set(id, (p = new Path2D(d)));
  return p;
}

// ------------------------------------------------------------------ item icons

/** b1.8.1 gui/items.png cells of items 256+ and terrain.png cells of blocks: set by the viewer module once it loads. */
let iconSource: ((id: number, meta: number) => { url: string; cell: number } | null) | null = null;
const iconListeners = new Set<() => void>();

export function setItemIconSource(f: typeof iconSource): void {
  iconSource = f;
  for (const l of iconListeners) l();
}

function useIconSource() {
  const [, bump] = useState(0);
  useEffect(() => {
    const l = () => bump((n) => n + 1);
    iconListeners.add(l);
    return () => void iconListeners.delete(l);
  }, []);
  return iconSource;
}

/** An item or block icon from the committed textures (a 16×16 cell of a 256×256 sheet), or a lettered tile until they load. */
export function ItemIcon({
  id,
  meta = 0,
  size = 20,
  className,
}: {
  id: number;
  meta?: number;
  size?: number;
  className?: string;
}) {
  const src = useIconSource();
  const hit = src?.(id, Math.max(0, meta)) ?? null;
  const label = storyItemLabel(id, Math.max(0, meta));
  if (!hit) {
    return (
      <span
        className={cn(
          'inline-grid shrink-0 place-items-center rounded-sm bg-surface-3 text-[9px] font-semibold text-muted',
          className,
        )}
        style={{ width: size, height: size }}
        title={label}
        aria-label={label}
      >
        {label.slice(0, 2)}
      </span>
    );
  }
  const x = hit.cell % 16,
    y = Math.floor(hit.cell / 16);
  return (
    <span
      className={cn('inline-block shrink-0', className)}
      title={label}
      aria-label={label}
      role="img"
      style={{
        width: size,
        height: size,
        backgroundImage: `url(${hit.url})`,
        backgroundSize: `${size * 16}px ${size * 16}px`,
        backgroundPosition: `-${x * size}px -${y * size}px`,
        imageRendering: 'pixelated',
      }}
    />
  );
}
