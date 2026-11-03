'use client';

import { ArrowLeft, Box, Brain, Bug, ChevronDown, Eye, History, LayoutPanelLeft, MessageSquareText, Package, Wrench } from 'lucide-react';
import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useState } from 'react';
import { Badge, Button, Card, CardHeader, EmptyState, ErrorState, Skeleton, SkeletonLines } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { ChatPane } from '@/features/chat/ChatPane';
import { InFlight, KIND_LABEL, StatusDot, useHiveIndex, Vitals } from '@/features/live/common';
import { DebuggerDrawer } from '@/features/live/DebuggerDrawer';
import { HistoryDrawer } from '@/features/live/HistoryDrawer';
import { ScriptCard } from '@/features/live/ScriptCard';
import { VisionDrawer } from '@/features/live/VisionDrawer';
import { WorldHost } from '@/features/world/integration';
import { WorldPane } from '@/features/world/WorldPane';
import { useNow, useStatus } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { ago, cn, dimName } from '@/lib/utils';
import { parsePos } from '@mineclaw/contracts';

type Pane = 'now' | 'world';

export default function BotPage() {
  const { bot: raw } = useParams<{ bot: string }>();
  const bot = decodeURIComponent(raw);
  const { devDetails } = usePrefs();
  const status = useStatus();
  const now = useNow(bot);
  const hive = useHiveIndex();
  const node = hive.get(bot);
  const view = status.data?.bots.find((b) => b.name === bot);
  const [pane, setPane] = useState<Pane>('now');
  const [history, setHistory] = useState<false | 'turns' | 'context'>(false);
  const [vision, setVision] = useState(false);
  const [debug, setDebug] = useState(false);
  const [showThinking, setShowThinking] = useState(false);
  const n = now.data;
  const brain = node?.brain ?? view?.brain ?? null;
  const pos = parsePos(n?.body?.pos ?? view?.pos);
  const aliases = node ? [node.id] : view?.short ? [view.short] : [];
  const kind = view?.kind ?? now.data?.kind ?? node?.kind;
  const owner = view?.owner ?? now.data?.owner ?? node?.owner;

  if (now.error?.status === 404) {
    return (
      <div className="p-6">
        <Card>
          <EmptyState title={`No bot called ${bot}`} hint="It may have gone offline." action={<Link href="/live" className="text-sm text-accent">Back to the hive</Link>} />
        </Card>
      </div>
    );
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      {/* header */}
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-border bg-surface px-4 py-3 sm:px-6">
        <Link href="/live" aria-label="Back to the hive" className="grid size-8 place-items-center rounded-lg text-muted hover:bg-surface-2 hover:text-fg">
          <ArrowLeft className="size-4" />
        </Link>
        <StatusDot status={view?.status} />
        <h1 className="text-lg font-semibold tracking-tight text-fg">{bot}</h1>
        {kind ? <Badge>{KIND_LABEL[kind]}</Badge> : null}
        {owner && kind !== 'main' ? <Link href={`/live/${encodeURIComponent(owner)}`} className="text-xs text-muted hover:text-fg">of {owner}</Link> : null}
        {n?.brainState ? <span className="text-sm text-muted">{n.brainState}</span> : null}
        <div className="ml-auto flex flex-wrap items-center gap-1.5">
          <Segmented<Pane>
            label="Main pane"
            size="sm"
            value={pane}
            onChange={setPane}
            options={[
              { value: 'now', label: <><LayoutPanelLeft className="size-3.5" /> Now</> },
              { value: 'world', label: <><Box className="size-3.5" /> World</> },
            ]}
          />
          <span className="mx-1 h-5 w-px bg-border" aria-hidden />
          <Button size="sm" variant="ghost" onClick={() => setHistory('turns')} disabled={!brain}>
            <History className="size-3.5" /> History
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setHistory('context')} disabled={!brain}>
            <MessageSquareText className="size-3.5" /> Context
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setVision(true)}>
            <Eye className="size-3.5" /> Vision
          </Button>
          {n?.script ? (
            <Button size="sm" variant="ghost" onClick={() => setDebug(true)}>
              <Bug className="size-3.5" /> Debugger
            </Button>
          ) : null}
        </div>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_24rem] xl:grid-cols-[minmax(0,1fr)_28rem]">
        <div className="min-h-0 overflow-auto">
          {pane === 'world' ? (
            <div className="flex h-full min-h-[28rem] flex-col">
              <div className="min-h-0 flex-1">
                <WorldHost>
                  <WorldPane follow={bot} />
                </WorldHost>
              </div>
              <div className="flex flex-wrap items-center gap-4 border-t border-border bg-surface px-5 py-3 text-sm">
                {n?.body ? <div className="w-64"><Vitals hp={n.body.hp} food={n.body.food} compact /></div> : null}
                <p className="min-w-0 flex-1 truncate text-muted">{n?.thought?.text}</p>
              </div>
            </div>
          ) : (
            <div className="mx-auto max-w-4xl space-y-4 p-4 sm:p-6">
              {/* current thought */}
              <Card>
                <div className="p-5">
                  <div className="mb-3 flex flex-wrap items-center gap-x-3 gap-y-1.5">
                    <span className="inline-flex items-center gap-1.5 text-xs font-medium tracking-wide text-subtle uppercase">
                      <Brain className="size-3.5" /> Thinking about
                    </span>
                    {n?.inFlight ? <InFlight inFlight={n.inFlight} devDetails={devDetails} /> : null}
                    {n?.thought?.at ? <span className="ml-auto text-xs text-subtle">{ago(n.thought.at)}</span> : null}
                  </div>
                  {now.isPending ? (
                    <SkeletonLines lines={2} />
                  ) : now.error ? (
                    <ErrorState error={now.error} onRetry={() => void now.refetch()} />
                  ) : n?.thought?.text ? (
                    <p className="text-[17px] leading-relaxed text-pretty text-fg">{n.thought.text}</p>
                  ) : (
                    <p className="text-muted">{kind === 'script' ? 'This body runs a script only; it has no brain of its own.' : 'No thought yet.'}</p>
                  )}
                  {n?.thought?.thinking ? (
                    <div className="mt-3">
                      <button
                        onClick={() => setShowThinking((s) => !s)}
                        aria-expanded={showThinking}
                        className="inline-flex items-center gap-1 text-xs font-medium text-muted hover:text-fg"
                      >
                        <ChevronDown className={cn('size-3.5 transition-transform', showThinking && 'rotate-180')} />
                        {showThinking ? 'Hide thinking' : 'Show thinking'}
                      </button>
                      {showThinking ? <p className="mt-2 border-l-2 border-border pl-3 text-sm leading-relaxed text-muted italic">{n.thought.thinking}</p> : null}
                    </div>
                  ) : null}
                </div>
                <div className="flex flex-wrap items-center gap-x-5 gap-y-2 border-t border-border px-5 py-3 text-[13px]">
                  <span className="min-w-0 text-muted">
                    <span className="text-subtle">Task</span> <span className="font-medium text-fg">{n?.task ?? view?.task ?? 'Idle'}</span>
                  </span>
                  {n?.tool ? (
                    <span className="inline-flex min-w-0 items-center gap-1.5 text-muted">
                      <Wrench className="size-3.5 text-subtle" />
                      <span className="font-medium text-fg">{n.tool.name}</span>
                      {n.tool.argsShort ? <code className="truncate font-mono text-xs text-subtle">{n.tool.argsShort}</code> : null}
                    </span>
                  ) : null}
                </div>
              </Card>

              {n?.script ? (
                <ScriptCard script={n.script} onDebug={() => setDebug(true)} />
              ) : now.isPending ? (
                <Skeleton className="h-60 rounded-card" />
              ) : null}

              <Card>
                <CardHeader title="Body" icon={<Package className="size-4" />} subtitle={n?.body?.held ? `Holding ${n.body.held}` : undefined} />
                <div className="space-y-3 p-5">
                  {n?.body || view ? <Vitals hp={n?.body?.hp ?? view?.hp ?? 0} food={n?.body?.food ?? view?.food ?? 0} /> : <SkeletonLines lines={1} />}
                  <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted">
                    <span>{dimName(n?.body?.dim ?? view?.dim)}</span>
                    {devDetails && pos ? <span className="tabular">{pos.map((v) => Math.floor(v)).join(', ')}</span> : null}
                    {kind === 'subhive' && owner ? <span>Driven by {owner}’s brain</span> : null}
                  </div>
                </div>
              </Card>
            </div>
          )}
        </div>
        <ChatPane bot={bot} aliases={aliases} className="h-[32rem] border-t border-border bg-surface lg:h-auto lg:border-t-0 lg:border-l" />
      </div>

      <HistoryDrawer
        key={history || 'closed'}
        open={!!history}
        onOpenChange={(o) => !o && setHistory(false)}
        brain={brain}
        body={bot}
        subhive={kind === 'subhive'}
        initialTab={history || 'turns'}
      />
      <VisionDrawer open={vision} onOpenChange={setVision} body={bot} />
      <DebuggerDrawer open={debug} onOpenChange={setDebug} body={bot} />
    </div>
  );
}
