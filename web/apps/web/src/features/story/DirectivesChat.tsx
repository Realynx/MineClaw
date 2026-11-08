'use client';

import type { StoryColonyState, StoryDirectiveResponse } from '@mineclaw/contracts';
import { Bot, MessageSquare } from 'lucide-react';
import { ChatPane } from '@/features/chat/ChatPane';
import { api } from '@/lib/api/client';
import { useAuth } from '@/lib/auth';
import { useNow } from '@/lib/live/hooks';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';

/** The selected main brain, never whichever pawn happened to be selected on the map. */
export function controllerMain(state: StoryColonyState | undefined) {
  return state?.ai?.find((body) => body.kind === 'main') ?? null;
}

/** A directive response acknowledges delivery, not successful execution of the requested edits. */
export function directiveAccepted(result: StoryDirectiveResponse, main: string) {
  return result.delivered === true || (Array.isArray(result.delivered) && result.delivered.some((n) => n.toLowerCase() === main.toLowerCase()));
}

export function DirectivesChat({ state, canWrite, visible = true }: { state: StoryColonyState | undefined; canWrite: boolean; visible?: boolean }) {
  const main = controllerMain(state);
  const colony = state?.id;
  const { me } = useAuth();
  const now = useNow(main?.name);
  const canSend = canWrite && !!me.player && !!colony && !!main;
  return (
    <section className={cn(panel, 'pointer-events-auto flex h-[min(36rem,70vh)] w-[min(35rem,calc(100vw-1rem))] flex-col overflow-hidden')} aria-label="Colony directives">
      <header className="space-y-1 border-b border-border px-3 py-2">
        <div className="flex items-center gap-2 text-sm font-semibold"><Bot className="size-4 text-accent" /> Directives <span className="ml-auto text-xs font-normal text-muted">{state?.name ?? 'Colony'}</span></div>
        <p className="text-xs text-muted">Ask the main bot to change roles, schedules, zones, builds, bills or storage. Its changes appear in the same tabs and map.</p>
        {main ? <p className="truncate text-[11px] text-subtle" role="status">{main.name} · {now.data?.tool?.name === 'colony_control' ? 'Updating colony' : now.data?.inFlight ? 'Thinking…' : now.data?.brainState ?? main.brainState ?? 'Ready'}</p> : null}
      </header>
      {state && main && colony ? (
        <ChatPane
          key={`${state.id}:${main.name}`}
          bot={main.name}
          className="flex-1"
          title="Main bot"
          maxLength={4000}
          visible={visible}
          canSend={canSend}
          placeholder="For example: Make pawns 101–105 miners and give them sleep from midnight to 6."
          sendMessage={async (text, requestId) => {
            const result = await api('story.directives', { body: { bots: [main.name], directive: { kind: 'text', colony, text }, requestId } });
            return { accepted: directiveAccepted(result, main.name), id: String(result.id), feedSeq: result.feedSeq, note: 'The main bot did not accept the request.' };
          }}
        />
      ) : <div className="grid flex-1 place-content-center gap-2 px-6 text-center text-sm text-muted"><MessageSquare className="mx-auto size-6" />{state ? 'Waiting for the main bot to become available.' : 'Select or create a colony first.'}</div>}
      {!canWrite || !me.player ? <p className="border-t border-border px-3 py-2 text-xs text-muted">{!me.player ? 'Link your Minecraft player to send directives.' : 'Colony control permission is required to send directives.'}</p> : null}
      {!!state?.directives?.length && (
        <details className="border-t border-border text-xs">
          <summary className="cursor-pointer px-3 py-2 text-muted">Recent requests ({state.directives.length})</summary>
          <ul className="max-h-32 space-y-2 overflow-y-auto px-3 pb-3">
            {state.directives.slice(-8).reverse().map((d) => <li key={d.id}><span className="mr-2 text-subtle">{d.status}</span>{d.summary}{d.reply ? <p className="mt-0.5 text-muted">{d.reply}</p> : null}</li>)}
          </ul>
        </details>
      )}
    </section>
  );
}
