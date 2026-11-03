'use client';

import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { MapPin } from 'lucide-react';
import Link from 'next/link';
import { memo } from 'react';
import { useNow } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { cn, dimName } from '@/lib/utils';
import { Badge, Skeleton } from '@/components/ui/primitives';
import { BotThumbnail } from '@/features/world/integration';
import { InFlight, KIND_LABEL, ScriptBadge, StatusDot, Vitals } from './common';

/** Labels over the 3D thumbnail: solid so they read on any scene. */
const chip = 'inline-flex h-5 items-center rounded-md bg-surface px-1.5 text-[11px] font-medium shadow-card';

/** One bot in the hive grid: thumbnail, vitals, task, one-line thought, script and brain state. Calm by design. */
function BotCardInner({ bot, node, compact }: { bot: BotStatusView; node: HiveTreeNode | undefined; compact?: boolean }) {
  const { devDetails } = usePrefs();
  const now = useNow(bot.name);
  const n = now.data;
  const kind = bot.kind ?? n?.kind ?? node?.kind;
  const thought = n?.thought?.text;
  const hp = n?.body?.hp ?? bot.hp;
  const food = n?.body?.food ?? bot.food;
  const offline = bot.status !== 'online';
  const script = n?.script ?? bot.script ?? null;
  const brainless = kind === 'subhive' || kind === 'script';
  const brainState = n?.brainState ?? bot.brain;
  return (
    <Link
      href={`/live/${encodeURIComponent(bot.name)}`}
      className={cn(
        'group flex flex-col overflow-hidden rounded-card border border-border bg-surface shadow-card transition-[border-color,box-shadow]',
        'hover:border-border-strong hover:shadow-pop focus-visible:outline-2',
        offline && 'opacity-70',
      )}
      aria-label={`${bot.name}: ${n?.task ?? bot.task ?? 'idle'}`}
      data-body={bot.name}
    >
      <div className={cn('relative w-full border-b border-border', compact ? 'aspect-[2/1]' : 'aspect-[16/9]')}>
        {/* Subhive bodies redraw less often: twenty of them share one software renderer with the rest of the page. */}
        <BotThumbnail bot={bot.name} fps={compact ? 2 : 3} className="absolute inset-0" />
        <div className="absolute top-2 left-2 flex gap-1">
          {kind ? <span className={cn(chip, 'text-fg')}>{KIND_LABEL[kind]}</span> : null}
          {bot.dim !== 0 ? <span className={cn(chip, 'text-warn')}>{dimName(bot.dim)}</span> : null}
          {bot.status === 'connecting' ? <span className={cn(chip, 'text-info')}>joining</span> : null}
        </div>
        {n?.inFlight ? (
          <div className="absolute right-2 bottom-2 rounded-md bg-surface px-2 py-1 shadow-card">
            <InFlight inFlight={n.inFlight} />
          </div>
        ) : null}
      </div>
      <div className={cn('flex flex-1 flex-col', compact ? 'gap-2 p-3' : 'gap-3 p-4')}>
        <div className="flex items-center gap-2">
          <StatusDot status={bot.status} />
          <h3 className={cn('min-w-0 flex-1 truncate font-semibold text-fg', compact ? 'text-[14px]' : 'text-[15px]')}>{bot.name}</h3>
          {brainState ? <span className="max-w-[45%] truncate text-xs text-subtle">{brainState}</span> : null}
        </div>
        <Vitals hp={hp} food={food} compact />
        <div className="min-h-0 space-y-1.5">
          <p className="truncate text-[13px] font-medium text-fg">{n?.task ?? bot.task ?? <span className="text-subtle">Idle</span>}</p>
          {brainless ? null : now.isPending ? (
            <Skeleton className="h-4 w-4/5" />
          ) : thought ? (
            <p className={cn('text-[13px] leading-snug text-muted', compact ? 'line-clamp-1' : 'line-clamp-2')}>“{thought}”</p>
          ) : (
            <p className="text-[13px] text-subtle">No thought yet.</p>
          )}
        </div>
        <div className="mt-auto flex flex-wrap items-center gap-1.5">
          {script ? <ScriptBadge script={script} /> : null}
          {n?.body?.held ? <Badge>{n.body.held}</Badge> : null}
          {devDetails && bot.pos ? (
            <Badge>
              <MapPin className="size-3" /> {bot.pos}
            </Badge>
          ) : null}
        </div>
      </div>
    </Link>
  );
}

/** The fields a card shows: the status row changes every second (`at`), the card only when these do. */
function sameCard(a: { bot: BotStatusView; node: HiveTreeNode | undefined; compact?: boolean }, b: typeof a): boolean {
  const x = a.bot, y = b.bot;
  return (
    a.node === b.node &&
    a.compact === b.compact &&
    x.name === y.name &&
    x.status === y.status &&
    x.hp === y.hp &&
    x.food === y.food &&
    x.task === y.task &&
    x.brain === y.brain &&
    x.dim === y.dim &&
    x.pos === y.pos &&
    x.kind === y.kind &&
    x.script?.name === y.script?.name &&
    x.script?.line === y.script?.line &&
    x.script?.status === y.script?.status
  );
}

export const BotCard = memo(BotCardInner, sameCard);
