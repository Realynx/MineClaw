'use client';

import type { FeedLine } from '@mineclaw/contracts';
import { ArrowDown, EyeOff, Eye, Gamepad2, Globe, Lock, RotateCcw, Send } from 'lucide-react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { api, ApiError, errorText } from '@/lib/api/client';
import { useAuth } from '@/lib/auth';
import { useFeed } from '@/lib/live/hooks';
import { useTicker } from '@/lib/live/ticker';
import { setPrefs, usePrefs } from '@/lib/prefs';
import { clock, cn, newRequestId } from '@/lib/utils';
import { Button, ErrorState, SkeletonLines } from '@/components/ui/primitives';
import { Tooltip } from '@/components/ui/overlays';
import {
  awaitingReply,
  createPending,
  onAccepted,
  onFailed,
  optimisticBubbles,
  reconcile,
  REPLY_PATIENCE_MS,
  type PendingMessage,
} from './pending';

const PRIVATE = new Set(['internal', 'muted']);
const MAX = 256;

function Line({ line, mine, bot }: { line: FeedLine; mine: boolean; bot: string }) {
  if (line.kind === 'system') {
    return (
      <div className="my-2 text-center text-[11px] text-subtle" title={clock(line.at)}>
        {line.text}
      </div>
    );
  }
  if (PRIVATE.has(line.kind)) {
    return (
      <div className="my-1.5 flex items-start gap-2 pr-8 pl-9 text-xs text-subtle" title={`${line.kind} · ${clock(line.at)}`}>
        <Lock className="mt-0.5 size-3 shrink-0" aria-label="private speech" />
        <p className="italic">{line.text}</p>
      </div>
    );
  }
  const fromBot = line.kind === 'said' || (line.kind === 'whisper' && (line.from === bot || line.from === line.bot));
  const viaWeb = line.kind === 'viewer' || line.origin === 'viewer';
  return (
    <div className={cn('my-1 flex gap-2', mine ? 'justify-end pl-10' : 'pr-10')}>
      {!mine ? (
        <span
          aria-hidden
          className={cn(
            'mt-5 grid size-7 shrink-0 place-items-center rounded-full text-[11px] font-semibold uppercase',
            fromBot ? 'bg-accent-soft text-accent' : 'bg-surface-3 text-muted',
          )}
        >
          {(line.from ?? '?').slice(0, 1)}
        </span>
      ) : null}
      <div className={cn('min-w-0', mine && 'flex flex-col items-end')}>
        <div className={cn('mb-0.5 flex items-center gap-1.5 text-[11px] text-subtle', mine && 'flex-row-reverse')}>
          <span className="font-medium text-muted">{mine ? 'You' : (line.from ?? 'unknown')}</span>
          <span>{clock(line.at)}</span>
          {line.kind === 'whisper' ? <span className="rounded bg-surface-2 px-1">whisper</span> : null}
          {viaWeb ? (
            <Globe className="size-3" aria-label="sent from the web" />
          ) : line.kind === 'chat' ? (
            <Gamepad2 className="size-3" aria-label="in game" />
          ) : null}
        </div>
        <p
          className={cn(
            'rounded-2xl px-3 py-1.5 text-[13.5px] leading-relaxed break-words whitespace-pre-wrap',
            mine ? 'rounded-tr-md bg-accent text-accent-fg' : fromBot ? 'rounded-tl-md bg-surface-2 text-fg' : 'rounded-tl-md border border-border bg-surface text-fg',
          )}
        >
          {line.text}
        </p>
      </div>
    </div>
  );
}

function PendingBubble({ p, onRetry, now }: { p: PendingMessage; onRetry: () => void; now: number }) {
  const failed = p.state === 'failed' || p.state === 'rate_limited';
  const wait = p.retryAt ? Math.max(0, Math.ceil((p.retryAt - now) / 1000)) : 0;
  return (
    <div className="my-1 flex justify-end pl-10">
      <div className="flex flex-col items-end">
        <p
          className={cn(
            'rounded-2xl rounded-tr-md px-3 py-1.5 text-[13.5px] leading-relaxed break-words whitespace-pre-wrap',
            failed ? 'border border-danger/40 bg-danger-soft text-fg' : 'bg-accent/70 text-accent-fg',
          )}
        >
          {p.text}
        </p>
        <div className="mt-0.5 flex items-center gap-1.5 text-[11px]" role={failed ? 'alert' : 'status'}>
          {p.state === 'sending' ? <span className="text-subtle">Sending…</span> : null}
          {p.state === 'awaiting' ? <span className="text-subtle">Sent</span> : null}
          {failed ? (
            <>
              <span className="text-danger">{p.state === 'rate_limited' && wait > 0 ? `Slow down — retry in ${wait}s` : (p.error ?? 'Not sent')}</span>
              <button onClick={onRetry} disabled={wait > 0} className="inline-flex items-center gap-0.5 font-medium text-accent disabled:opacity-50">
                <RotateCcw className="size-3" /> Retry
              </button>
            </>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function TypingHint({ bot, slow }: { bot: string; slow: boolean }) {
  return (
    <div className="my-2 flex items-center gap-2 pl-9 text-xs text-subtle" role="status" aria-live="polite">
      {slow ? (
        <span>No reply yet — {bot} may be busy. It will show up here when it comes.</span>
      ) : (
        <>
          <span className="flex gap-0.5" aria-hidden>
            {[0, 1, 2].map((i) => (
              <span key={i} className="size-1.5 animate-pulse-dot rounded-full bg-subtle" style={{ animationDelay: `${i * 0.2}s` }} />
            ))}
          </span>
          <span>{bot} is replying…</span>
        </>
      )}
    </div>
  );
}

/** The conversation with one bot: viewer, in-game and bot lines threaded together; private speech behind a toggle. */
export function ChatPane({ bot, aliases: aliasList, className }: { bot: string; aliases?: string[]; className?: string }) {
  // Callers pass a fresh array each render; key the memo on its content so the effects below don't re-run every render.
  const aliasKey = (aliasList ?? []).join('\n');
  const aliases = useMemo(() => (aliasKey ? aliasKey.split('\n') : []), [aliasKey]);
  const { me, canChat } = useAuth();
  const { showPrivateSpeech } = usePrefs();
  const feed = useFeed();
  const [pending, setPending] = useState<PendingMessage[]>([]);
  const [text, setText] = useState('');
  const scroller = useRef<HTMLDivElement>(null);
  const [atBottom, setAtBottom] = useState(true);
  const [unseen, setUnseen] = useState(0);
  const now = useTicker(1000, pending.length > 0);

  const lines = useMemo(() => {
    const all = (feed.data?.lines ?? []).filter((l) => l.bot === bot || aliases.includes(l.bot));
    return showPrivateSpeech ? all : all.filter((l) => !PRIVATE.has(l.kind));
  }, [feed.data, bot, aliases, showPrivateSpeech]);
  const privateCount = useMemo(
    () => (feed.data?.lines ?? []).filter((l) => (l.bot === bot || aliases.includes(l.bot)) && PRIVATE.has(l.kind)).length,
    [feed.data, bot, aliases],
  );

  // Resolve pending messages against the feed.
  useEffect(() => {
    if (!feed.data || pending.length === 0) return;
    const r = reconcile(pending, feed.data.lines, aliases);
    const changed = r.resolved.length > 0 || r.pending.some((p, i) => p !== pending[i]);
    if (changed) setPending(r.pending);
  }, [feed.data, pending, aliases]);

  // Stick to the bottom when the reader is there; otherwise count what arrived.
  const count = lines.length + pending.length;
  const prevCount = useRef(count);
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (atBottom) el.scrollTop = el.scrollHeight;
    else if (count > prevCount.current) setUnseen((u) => u + (count - prevCount.current));
    prevCount.current = count;
  }, [count, atBottom]);

  const onScroll = () => {
    const el = scroller.current;
    if (!el) return;
    const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
    setAtBottom(bottom);
    if (bottom) setUnseen(0);
  };

  const send = async (p: PendingMessage) => {
    setPending((list) => [...list.filter((x) => x.localId !== p.localId), p]);
    try {
      const res = await api('bots.chat', { params: { bot }, body: { text: p.text, requestId: p.requestId } });
      setPending((list) => list.map((x) => (x.localId === p.localId ? onAccepted(x, res as typeof res & { feedSeq?: unknown }) : x)));
    } catch (e) {
      const rate = e instanceof ApiError && e.status === 429 ? (e.retryAfterMs ?? 5000) : undefined;
      setPending((list) => list.map((x) => (x.localId === p.localId ? onFailed(x, errorText(e), rate) : x)));
    }
  };

  const submit = () => {
    const t = text.trim();
    if (!t || t.length > MAX) return;
    setText('');
    setAtBottom(true);
    void send(createPending(bot, t, newRequestId()));
  };

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    }
  };

  const rateLimited = pending.find((p) => p.state === 'rate_limited' && (p.retryAt ?? 0) > now);
  const waiting = awaitingReply(pending);
  const oldestWaiting = pending.filter((p) => p.state === 'awaiting').reduce((m, p) => Math.min(m, p.createdAt), Infinity);
  const bubbles = optimisticBubbles(pending);

  return (
    <section className={cn('flex min-h-0 flex-col', className)} aria-label={`Chat with ${bot}`}>
      <div className="flex items-center gap-2 border-b border-border px-4 py-2.5">
        <h2 className="flex-1 text-[13px] font-semibold text-fg">Chat</h2>
        <Tooltip content={showPrivateSpeech ? 'Hide private speech (internal thoughts, muted lines)' : 'Show private speech (internal thoughts, muted lines)'}>
          <Button
            size="sm"
            variant="ghost"
            aria-pressed={showPrivateSpeech}
            onClick={() => setPrefs({ showPrivateSpeech: !showPrivateSpeech })}
          >
            {showPrivateSpeech ? <Eye className="size-3.5" /> : <EyeOff className="size-3.5" />}
            Private{privateCount ? <span className="tabular text-subtle">{privateCount}</span> : null}
          </Button>
        </Tooltip>
      </div>
      <div className="relative min-h-0 flex-1">
        <div ref={scroller} onScroll={onScroll} className="h-full overflow-y-auto px-3 py-3" aria-live="polite" aria-relevant="additions">
          {feed.isPending ? (
            <SkeletonLines lines={6} className="p-2" />
          ) : feed.error ? (
            <ErrorState error={feed.error} onRetry={() => void feed.refetch()} title="Chat feed unavailable" />
          ) : lines.length === 0 && bubbles.length === 0 ? (
            <p className="px-4 py-10 text-center text-sm text-muted">No messages with {bot} yet.</p>
          ) : (
            lines.map((l) => <Line key={l.seq} line={l} bot={bot} mine={!!me.player && l.from === me.player && !PRIVATE.has(l.kind)} />)
          )}
          {bubbles.map((p) => (
            <PendingBubble key={p.localId} p={p} now={now} onRetry={() => void send({ ...p, state: 'sending', error: undefined, retryAt: undefined })} />
          ))}
          {waiting ? <TypingHint bot={bot} slow={now - oldestWaiting > REPLY_PATIENCE_MS} /> : null}
        </div>
        {!atBottom && unseen > 0 ? (
          <button
            onClick={() => {
              setAtBottom(true);
              setUnseen(0);
            }}
            className="absolute bottom-3 left-1/2 inline-flex -translate-x-1/2 items-center gap-1 rounded-full border border-border bg-surface px-3 py-1 text-xs font-medium text-fg shadow-pop"
          >
            <ArrowDown className="size-3" /> {unseen} new
          </button>
        ) : null}
      </div>
      {canChat ? (
        <form
          className="border-t border-border p-3"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <div className="flex items-end gap-2 rounded-xl border border-border bg-surface-2 p-1.5 focus-within:border-accent focus-within:ring-2 focus-within:ring-[var(--ring)]">
            <label htmlFor={`chat-${bot}`} className="sr-only">
              Message {bot} as {me.player}
            </label>
            <textarea
              id={`chat-${bot}`}
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={onKey}
              rows={1}
              maxLength={MAX + 40}
              placeholder={`Message ${bot} as ${me.player ?? ''}…`}
              className="max-h-32 min-h-8 flex-1 resize-none bg-transparent px-2 py-1.5 text-[13.5px] text-fg outline-none placeholder:text-subtle"
              style={{ fieldSizing: 'content' }}
              disabled={!!rateLimited}
            />
            <Button type="submit" variant="primary" size="icon-sm" aria-label="Send" disabled={!text.trim() || text.trim().length > MAX || !!rateLimited}>
              <Send className="size-3.5" />
            </Button>
          </div>
          <div className="mt-1 flex justify-between px-1 text-[11px] text-subtle">
            <span>
              {rateLimited ? `Rate limited — wait ${Math.ceil(((rateLimited.retryAt ?? now) - now) / 1000)}s` : 'Enter to send · Shift+Enter for a new line'}
            </span>
            <span className={cn('tabular', text.trim().length > MAX && 'text-danger')}>
              {text.length > MAX - 40 ? `${text.trim().length}/${MAX}` : ''}
            </span>
          </div>
        </form>
      ) : null}
    </section>
  );
}
