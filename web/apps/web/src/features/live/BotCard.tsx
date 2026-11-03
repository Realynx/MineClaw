'use client';

import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { MapPin } from 'lucide-react';
import Link from 'next/link';
import { useNow } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { cn, dimName } from '@/lib/utils';
import { Badge, Skeleton } from '@/components/ui/primitives';
import { BotThumbnail } from '@/features/world/integration';
import { InFlight, KIND_LABEL, ScriptBadge, StatusDot, Vitals } from './common';

/** One bot in the hive grid: thumbnail, vitals, task, one-line thought, script and brain state. Calm by design. */
export function BotCard({ bot, node }: { bot: BotStatusView; node: HiveTreeNode | undefined }) {
  const { devDetails } = usePrefs();
  const now = useNow(bot.name);
  const n = now.data;
  const thought = n?.thought?.text;
  const hp = n?.body?.hp ?? bot.hp;
  const food = n?.body?.food ?? bot.food;
  const offline = bot.status !== 'online';
  return (
    <Link
      href={`/live/${encodeURIComponent(bot.name)}`}
      className={cn(
        'group flex flex-col overflow-hidden rounded-card border border-border bg-surface shadow-card transition-[border-color,box-shadow]',
        'hover:border-border-strong hover:shadow-pop focus-visible:outline-2',
        offline && 'opacity-70',
      )}
      aria-label={`${bot.name}: ${bot.task ?? 'idle'}`}
    >
      <div className="relative aspect-[16/9] w-full border-b border-border">
        <BotThumbnail bot={bot.name} className="absolute inset-0" />
        <div className="absolute top-2 left-2 flex gap-1">
          {node ? <Badge className="bg-surface">{KIND_LABEL[node.kind]}</Badge> : null}
          {bot.dim !== 0 ? <Badge tone="warn" className="backdrop-blur">{dimName(bot.dim)}</Badge> : null}
        </div>
        {n?.inFlight ? (
          <div className="absolute right-2 bottom-2 rounded-md bg-surface px-2 py-1 shadow-card">
            <InFlight inFlight={n.inFlight} />
          </div>
        ) : null}
      </div>
      <div className="flex flex-1 flex-col gap-3 p-4">
        <div className="flex items-center gap-2">
          <StatusDot status={bot.status} />
          <h3 className="min-w-0 flex-1 truncate text-[15px] font-semibold text-fg">{bot.name}</h3>
          {n?.brainState ? <span className="truncate text-xs text-subtle">{n.brainState}</span> : null}
        </div>
        <Vitals hp={hp} food={food} compact />
        <div className="min-h-0 space-y-1.5">
          <p className="truncate text-[13px] font-medium text-fg">{n?.task ?? bot.task ?? <span className="text-subtle">Idle</span>}</p>
          {now.isPending ? (
            <Skeleton className="h-4 w-4/5" />
          ) : thought ? (
            <p className="line-clamp-2 text-[13px] leading-snug text-muted">“{thought}”</p>
          ) : (
            <p className="text-[13px] text-subtle">{node?.kind === 'script' ? 'Script-only body — no brain of its own.' : 'No thought yet.'}</p>
          )}
        </div>
        <div className="mt-auto flex flex-wrap items-center gap-1.5">
          {n?.script ? <ScriptBadge script={n.script} /> : null}
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
