'use client';

import {
  ArrowLeft,
  Box,
  Brain,
  Bug,
  ChevronDown,
  Eye,
  History,
  LayoutPanelLeft,
  MessageSquareText,
  Package,
  Wrench,
} from 'lucide-react';
import Link from 'next/link';
import { useParams, useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  EmptyState,
  ErrorState,
  Skeleton,
  SkeletonLines,
} from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { ChatPane } from '@/features/chat/ChatPane';
import { InFlight, KIND_LABEL, StatusDot, useHiveIndex, Vitals } from '@/features/live/common';
import { DebuggerDrawer } from '@/features/live/DebuggerDrawer';
import { HistoryDrawer } from '@/features/live/HistoryDrawer';
import { InventorySection } from '@/features/live/inventory/InventoryPanel';
import { ScriptCard } from '@/features/live/ScriptCard';
import { VisionDrawer } from '@/features/live/VisionDrawer';
import { TakeControlButton } from '@/features/control/TakeControlButton';
import { WorldHost } from '@/features/world/integration';
import { WorldPane } from '@/features/world/WorldPane';
import { useNow, useStatus } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { useAuth } from '@/lib/auth';
import { ago, cn, dimName } from '@/lib/utils';
import { parsePos } from '@mineclaw/contracts';

type Pane = 'now' | 'world';
type NowQuery = ReturnType<typeof useNow>;
type NowData = NonNullable<NowQuery['data']>;
type BotView = NonNullable<ReturnType<typeof useStatus>['data']>['bots'][number];
type HiveNode = NonNullable<ReturnType<ReturnType<typeof useHiveIndex>['get']>>;

/** What the page shows about the body, from its `now` snapshot, the status tiles and the hive tree (first that knows). */
function botFacts(n: NowData | undefined, view: BotView | undefined, node: HiveNode | undefined) {
  return {
    brain: node?.brain ?? view?.brain ?? null,
    pos: parsePos(n?.body?.pos ?? view?.pos),
    aliases: aliasesOf(view, node),
    kind: kindOf(n, view, node),
    owner: ownerOf(n, view, node),
  };
}

/** The names chat lines may use for the body: its hive id, else its short name. */
function aliasesOf(view: BotView | undefined, node: HiveNode | undefined): string[] {
  if (node) return [node.id];
  return view?.short ? [view.short] : [];
}

/** main / hive / subhive / script. */
function kindOf(n: NowData | undefined, view: BotView | undefined, node: HiveNode | undefined) {
  return view?.kind ?? n?.kind ?? node?.kind;
}

/** The body whose brain drives this one. */
function ownerOf(n: NowData | undefined, view: BotView | undefined, node: HiveNode | undefined) {
  return view?.owner ?? n?.owner ?? node?.owner;
}
type Kind = ReturnType<typeof botFacts>['kind'];
type Owner = ReturnType<typeof botFacts>['owner'];

function BotPageInner() {
  const { bot: raw } = useParams<{ bot: string }>();
  const bot = decodeURIComponent(raw);
  const { devDetails } = usePrefs();
  const status = useStatus();
  const now = useNow(bot);
  const hive = useHiveIndex();
  const view = status.data?.bots.find((b) => b.name === bot);
  const params = useSearchParams();
  const router = useRouter();
  const pane: Pane = params.get('pane') === 'world' ? 'world' : 'now';
  const setPane = (next: Pane) =>
    router.replace(`/live/${encodeURIComponent(bot)}${next === 'world' ? '?pane=world' : ''}`, { scroll: false });
  const [history, setHistory] = useState<false | 'turns' | 'context'>(false);
  const [vision, setVision] = useState(false);
  const [debug, setDebug] = useState(false);
  const [showThinking, setShowThinking] = useState(false);
  const n = now.data;
  const { brain, pos, aliases, kind, owner } = botFacts(n, view, hive.get(bot));

  if (now.error?.status === 404) return <NoSuchBot bot={bot} />;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <BotHeader
        bot={bot}
        n={n}
        view={view}
        kind={kind}
        owner={owner}
        pane={pane}
        setPane={setPane}
        hasBrain={!!brain}
        openHistory={setHistory}
        openVision={() => setVision(true)}
        openDebugger={() => setDebug(true)}
      />

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_24rem] xl:grid-cols-[minmax(0,1fr)_28rem]">
        <div className="min-h-0 overflow-auto">
          {pane === 'world' ? (
            <WorldView bot={bot} n={n} />
          ) : (
            <NowView
              bot={bot}
              now={now}
              view={view}
              kind={kind}
              owner={owner}
              pos={devDetails ? pos : null}
              devDetails={devDetails}
              thinking={{ shown: showThinking, toggle: () => setShowThinking((s) => !s) }}
              openDebugger={() => setDebug(true)}
            />
          )}
        </div>
        <ChatPane
          bot={bot}
          aliases={aliases}
          className="h-[32rem] border-t border-border bg-surface lg:h-auto lg:border-t-0 lg:border-l"
        />
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

export default function BotPage() {
  return (
    <Suspense fallback={null}>
      <BotPageInner />
    </Suspense>
  );
}

function NoSuchBot({ bot }: { bot: string }) {
  return (
    <div className="p-6">
      <Card>
        <EmptyState
          title={`No bot called ${bot}`}
          hint="It may have gone offline."
          action={
            <Link href="/live" className="text-sm text-accent">
              Back to the hive
            </Link>
          }
        />
      </Card>
    </div>
  );
}

function BotHeader(p: {
  bot: string;
  n: NowData | undefined;
  view: BotView | undefined;
  kind: Kind | undefined;
  owner: Owner;
  pane: Pane;
  setPane: (p: Pane) => void;
  hasBrain: boolean;
  openHistory: (tab: 'turns' | 'context') => void;
  openVision: () => void;
  openDebugger: () => void;
}) {
  const { n, kind, owner } = p;
  const { can } = useAuth();
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-border bg-surface px-4 py-3 sm:px-6">
      <Link
        href="/live"
        aria-label="Back to the hive"
        className="grid size-8 place-items-center rounded-lg text-muted hover:bg-surface-2 hover:text-fg"
      >
        <ArrowLeft className="size-4" />
      </Link>
      <StatusDot status={p.view?.status} />
      <h1 className="text-lg font-semibold tracking-tight text-fg">{p.bot}</h1>
      {kind ? <Badge>{KIND_LABEL[kind]}</Badge> : null}
      {owner && kind !== 'main' ? (
        <Link href={`/live/${encodeURIComponent(owner)}`} className="text-xs text-muted hover:text-fg">
          of {owner}
        </Link>
      ) : null}
      {n?.brainState ? <span className="text-sm text-muted">{n.brainState}</span> : null}
      <div className="ml-auto flex flex-wrap items-center gap-1.5">
        {kind === 'script' && p.pane === 'now' ? (
          <TakeControlButton pawn={p.bot} canControl={can('story')} online={p.view?.status === 'online'}
            returnTo={`/live/${encodeURIComponent(p.bot)}`} />
        ) : null}
        <Segmented<Pane>
          label="Main pane"
          size="sm"
          value={p.pane}
          onChange={p.setPane}
          options={[
            {
              value: 'now',
              label: (
                <>
                  <LayoutPanelLeft className="size-3.5" /> Now
                </>
              ),
            },
            {
              value: 'world',
              label: (
                <>
                  <Box className="size-3.5" /> World
                </>
              ),
            },
          ]}
        />
        <span className="mx-1 h-5 w-px bg-border" aria-hidden />
        <Button size="sm" variant="ghost" onClick={() => p.openHistory('turns')} disabled={!p.hasBrain}>
          <History className="size-3.5" /> History
        </Button>
        <Button size="sm" variant="ghost" onClick={() => p.openHistory('context')} disabled={!p.hasBrain}>
          <MessageSquareText className="size-3.5" /> Context
        </Button>
        <Button size="sm" variant="ghost" onClick={p.openVision}>
          <Eye className="size-3.5" /> Vision
        </Button>
        {n?.script ? (
          <Button size="sm" variant="ghost" onClick={p.openDebugger}>
            <Bug className="size-3.5" /> Debugger
          </Button>
        ) : null}
      </div>
    </div>
  );
}

function WorldView({ bot, n }: { bot: string; n: NowData | undefined }) {
  return (
    <div className="flex h-full min-h-[28rem] flex-col">
      <div className="min-h-0 flex-1">
        <WorldHost>
          <WorldPane follow={bot} returnTo={`/live/${encodeURIComponent(bot)}?pane=world`} />
        </WorldHost>
      </div>
      <div className="flex flex-wrap items-center gap-4 border-t border-border bg-surface px-5 py-3 text-sm">
        {n?.body ? (
          <div className="w-64">
            <Vitals hp={n.body.hp} food={n.body.food} compact />
          </div>
        ) : null}
        <p className="min-w-0 flex-1 truncate text-muted">{n?.thought?.text}</p>
      </div>
    </div>
  );
}

function NowView(p: {
  bot: string;
  now: NowQuery;
  view: BotView | undefined;
  kind: Kind | undefined;
  owner: Owner;
  /** The body's position when the developer details are on. */
  pos: ReturnType<typeof parsePos> | null;
  devDetails: boolean;
  thinking: ThinkingToggle;
  openDebugger: () => void;
}) {
  const n = p.now.data;
  return (
    <div className="mx-auto max-w-4xl space-y-4 p-4 sm:p-6">
      <ThoughtCard now={p.now} view={p.view} kind={p.kind} devDetails={p.devDetails} thinking={p.thinking} />
      {n?.script ? (
        <ScriptCard script={n.script} onDebug={p.openDebugger} />
      ) : p.now.isPending ? (
        <Skeleton className="h-60 rounded-card" />
      ) : null}
      <BodyCard bot={p.bot} n={n} view={p.view} kind={p.kind} owner={p.owner} pos={p.pos} />
    </div>
  );
}

/** The current thought: what the brain is thinking about, its hidden thinking, the task and the tool in hand. */
function ThoughtCard(p: {
  now: NowQuery;
  view: BotView | undefined;
  kind: Kind | undefined;
  devDetails: boolean;
  thinking: ThinkingToggle;
}) {
  const n = p.now.data;
  return (
    <Card>
      <div className="p-5">
        <ThoughtHeader n={n} devDetails={p.devDetails} />
        <ThoughtText now={p.now} kind={p.kind} />
        {n?.thought?.thinking ? <Thinking text={n.thought.thinking} toggle={p.thinking} /> : null}
      </div>
      <div className="flex flex-wrap items-center gap-x-5 gap-y-2 border-t border-border px-5 py-3 text-[13px]">
        <span className="min-w-0 text-muted">
          <span className="text-subtle">Task</span>{' '}
          <span className="font-medium text-fg">{n?.task ?? p.view?.task ?? 'Idle'}</span>
        </span>
        {n?.tool ? <ToolInHand tool={n.tool} /> : null}
      </div>
    </Card>
  );
}

function ThoughtHeader({ n, devDetails }: { n: NowData | undefined; devDetails: boolean }) {
  return (
    <div className="mb-3 flex flex-wrap items-center gap-x-3 gap-y-1.5">
      <span className="inline-flex items-center gap-1.5 text-xs font-medium tracking-wide text-subtle uppercase">
        <Brain className="size-3.5" /> Thinking about
      </span>
      {n?.inFlight ? <InFlight inFlight={n.inFlight} devDetails={devDetails} /> : null}
      {n?.thought?.at ? <span className="ml-auto text-xs text-subtle">{ago(n.thought.at)}</span> : null}
    </div>
  );
}

function ThoughtText({ now, kind }: { now: NowQuery; kind: Kind | undefined }) {
  if (now.isPending) return <SkeletonLines lines={2} />;
  if (now.error) return <ErrorState error={now.error} onRetry={() => void now.refetch()} />;
  const text = now.data?.thought?.text;
  if (text) return <p className="text-[17px] leading-relaxed text-pretty text-fg">{text}</p>;
  return (
    <p className="text-muted">
      {kind === 'script' ? 'This body runs a script only; it has no brain of its own.' : 'No thought yet.'}
    </p>
  );
}

/** The hidden thinking's toggle (kept by the page, so it survives pane switches and new thoughts). */
type ThinkingToggle = { shown: boolean; toggle: () => void };

function Thinking({ text, toggle }: { text: string; toggle: ThinkingToggle }) {
  const showThinking = toggle.shown;
  return (
    <div className="mt-3">
      <button
        onClick={toggle.toggle}
        aria-expanded={showThinking}
        className="inline-flex items-center gap-1 text-xs font-medium text-muted hover:text-fg"
      >
        <ChevronDown className={cn('size-3.5 transition-transform', showThinking && 'rotate-180')} />
        {showThinking ? 'Hide thinking' : 'Show thinking'}
      </button>
      {showThinking ? (
        <p className="mt-2 border-l-2 border-border pl-3 text-sm leading-relaxed text-muted italic">{text}</p>
      ) : null}
    </div>
  );
}

function ToolInHand({ tool }: { tool: NonNullable<NowData['tool']> }) {
  return (
    <span className="inline-flex min-w-0 items-center gap-1.5 text-muted">
      <Wrench className="size-3.5 text-subtle" />
      <span className="font-medium text-fg">{tool.name}</span>
      {tool.argsShort ? <code className="truncate font-mono text-xs text-subtle">{tool.argsShort}</code> : null}
    </span>
  );
}

/** Vitals, dimension (and position with the developer details), who drives it, and the inventory. */
function BodyCard(p: {
  bot: string;
  n: NowData | undefined;
  view: BotView | undefined;
  kind: Kind | undefined;
  owner: Owner;
  pos: ReturnType<typeof parsePos> | null;
}) {
  const body = p.n?.body;
  const { view } = p;
  return (
    <Card>
      <CardHeader
        title="Body"
        icon={<Package className="size-4" />}
        subtitle={body?.held ? `Holding ${body.held}` : undefined}
      />
      <div className="space-y-3 p-5">
        <BodyVitals body={body} view={view} />
        <div className="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted">
          <span>{dimName(body?.dim ?? view?.dim)}</span>
          {p.pos ? <span className="tabular">{p.pos.map((v) => Math.floor(v)).join(', ')}</span> : null}
          {p.kind === 'subhive' && p.owner ? <span>Driven by {p.owner}’s brain</span> : null}
        </div>
      </div>
      <div className="border-t border-border p-5">
        <InventorySection bot={p.bot} />
      </div>
    </Card>
  );
}

const orZero = (a: number | undefined, b: number | undefined) => a ?? b ?? 0;

/** Health and food from the body's snapshot, else the status tile; a placeholder until either is known. */
function BodyVitals({ body, view }: { body: NowData['body'] | undefined; view: BotView | undefined }) {
  if (!body && !view) return <SkeletonLines lines={1} />;
  return <Vitals hp={orZero(body?.hp, view?.hp)} food={orZero(body?.food, view?.food)} />;
}
