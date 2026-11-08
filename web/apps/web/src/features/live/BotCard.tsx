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

type Now = ReturnType<typeof useNow>;

/** What a card shows of a bot: the live `now` view where it has it, else the status row. */
function cardFacts(bot: BotStatusView, n: Now['data'], node: HiveTreeNode | undefined) {
  const kind = kindOf(bot, n, node);
  return {
    kind,
    task: n?.task ?? bot.task,
    ...vitalsOf(bot, n),
    script: n?.script ?? bot.script ?? null,
    brainless: kind === 'subhive' || kind === 'script',
    brainState: n?.brainState ?? bot.brain,
  };
}

const kindOf = (bot: BotStatusView, n: Now['data'], node: HiveTreeNode | undefined) =>
  bot.kind ?? n?.kind ?? node?.kind;

function vitalsOf(bot: BotStatusView, n: Now['data']) {
  const body = n?.body;
  return { hp: body?.hp ?? bot.hp, food: body?.food ?? bot.food };
}

/** The 3D thumbnail with its chips and the in-flight call. */
function CardThumb({
  bot,
  n,
  kind,
  compact,
}: {
  bot: BotStatusView;
  n: Now['data'];
  kind: ReturnType<typeof cardFacts>['kind'];
  compact?: boolean;
}) {
  return (
    <div className={cn('relative w-full border-b border-border', compact ? 'aspect-[2/1]' : 'aspect-[16/9]')}>
      {/* Every preview obeys the shared FPS slider and the renderer's fair scheduling budget. */}
      <BotThumbnail bot={bot.name} className="absolute inset-0" />
      <CardChips bot={bot} kind={kind} />
      {n?.inFlight ? (
        <div className="absolute right-2 bottom-2 rounded-md bg-surface px-2 py-1 shadow-card">
          <InFlight inFlight={n.inFlight} />
        </div>
      ) : null}
    </div>
  );
}

/** The chips over the thumbnail: kind, dimension (when not the overworld), joining. */
function CardChips({ bot, kind }: { bot: BotStatusView; kind: ReturnType<typeof cardFacts>['kind'] }) {
  return (
    <div className="absolute top-2 left-2 flex gap-1">
      {kind ? <span className={cn(chip, 'text-fg')}>{KIND_LABEL[kind]}</span> : null}
      {bot.dim !== 0 ? <span className={cn(chip, 'text-warn')}>{dimName(bot.dim)}</span> : null}
      {bot.status === 'connecting' ? <span className={cn(chip, 'text-info')}>joining</span> : null}
    </div>
  );
}

/** The one-line thought (none for a brainless body). */
function CardThought({ now, compact }: { now: Now; compact?: boolean }) {
  const thought = now.data?.thought?.text;
  if (now.isPending) return <Skeleton className="h-4 w-4/5" />;
  if (thought)
    return (
      <p className={cn('text-[13px] leading-snug text-muted', compact ? 'line-clamp-1' : 'line-clamp-2')}>
        “{thought}”
      </p>
    );
  return <p className="text-[13px] text-subtle">No thought yet.</p>;
}

/** The footer badges: script, held item, and (dev details) the position. */
function CardBadges({
  bot,
  n,
  script,
}: {
  bot: BotStatusView;
  n: Now['data'];
  script: ReturnType<typeof cardFacts>['script'];
}) {
  const { devDetails } = usePrefs();
  return (
    <div className="mt-auto flex flex-wrap items-center gap-1.5">
      {script ? <ScriptBadge script={script} /> : null}
      {n?.body?.held ? <Badge>{n.body.held}</Badge> : null}
      {devDetails && bot.pos ? (
        <Badge>
          <MapPin className="size-3" /> {bot.pos}
        </Badge>
      ) : null}
    </div>
  );
}

/** One bot in the hive grid: thumbnail, vitals, task, one-line thought, script and brain state. Calm by design. */
function BotCardInner({
  bot,
  node,
  compact,
}: {
  bot: BotStatusView;
  node: HiveTreeNode | undefined;
  compact?: boolean;
}) {
  const now = useNow(bot.name);
  const n = now.data;
  const f = cardFacts(bot, n, node);
  return (
    <article
      className={cn(
        'group flex flex-col overflow-hidden rounded-card border border-border bg-surface shadow-card transition-[border-color,box-shadow]',
        'hover:border-border-strong hover:shadow-pop focus-visible:outline-2',
        bot.status !== 'online' && 'opacity-70',
      )}
      aria-label={`${bot.name}: ${f.task ?? 'idle'}`}
      data-body={bot.name}
    >
      <Link
        href={`/live/${encodeURIComponent(bot.name)}?pane=world`}
        aria-label={`View ${bot.name} in the world`}
        className="block focus-visible:outline-2"
      >
        <CardThumb bot={bot} n={n} kind={f.kind} compact={compact} />
      </Link>
      <Link
        href={`/live/${encodeURIComponent(bot.name)}`}
        aria-label={`${bot.name}: ${f.task ?? 'idle'}`}
        className={cn('flex flex-1 flex-col focus-visible:outline-2', compact ? 'gap-2 p-3' : 'gap-3 p-4')}
      >
        <div className="flex items-center gap-2">
          <StatusDot status={bot.status} />
          <h3 className={cn('min-w-0 flex-1 truncate font-semibold text-fg', compact ? 'text-[14px]' : 'text-[15px]')}>
            {bot.name}
          </h3>
          {f.brainState ? <span className="max-w-[45%] truncate text-xs text-subtle">{f.brainState}</span> : null}
        </div>
        <Vitals hp={f.hp} food={f.food} compact />
        <div className="min-h-0 space-y-1.5">
          <p className="truncate text-[13px] font-medium text-fg">
            {f.task ?? <span className="text-subtle">Idle</span>}
          </p>
          {f.brainless ? null : <CardThought now={now} compact={compact} />}
        </div>
        <CardBadges bot={bot} n={n} script={f.script} />
      </Link>
    </article>
  );
}

/** The status-row fields a card shows (its script compared by name, line and status). */
const CARD_FIELDS = [
  'name',
  'status',
  'hp',
  'food',
  'task',
  'brain',
  'dim',
  'pos',
  'kind',
] as const satisfies ReadonlyArray<keyof BotStatusView>;

const sameScript = (x: BotStatusView['script'], y: BotStatusView['script']): boolean =>
  x?.name === y?.name && x?.line === y?.line && x?.status === y?.status;

/** The fields a card shows: the status row changes every second (`at`), the card only when these do. */
function sameCard(a: { bot: BotStatusView; node: HiveTreeNode | undefined; compact?: boolean }, b: typeof a): boolean {
  return (
    a.node === b.node &&
    a.compact === b.compact &&
    CARD_FIELDS.every((k) => a.bot[k] === b.bot[k]) &&
    sameScript(a.bot.script, b.bot.script)
  );
}

export const BotCard = memo(BotCardInner, sameCard);
