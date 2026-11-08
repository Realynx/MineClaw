'use client';

/**
 * The studio's conversation: the model picker (the bot's configured models), the transcript with each reply's ops and cost,
 * and the composer. Its input follows the world chat bar's rules: T / Enter in the world opens it (the mouse is freed),
 * Enter sends, Enter on an empty line or Esc closes it and hands the keys back to the world.
 */
import type { DesignerMessage, DesignerModel } from '@mineclaw/contracts';
import { Bot, ChevronLeft, ChevronRight, Send, Sparkles, User } from 'lucide-react';
import { forwardRef, useEffect, useId, useImperativeHandle, useRef, useState, type KeyboardEvent } from 'react';
import { Button, Select, Spinner } from '@/components/ui/primitives';
import { cn, usd } from '@/lib/utils';
import { chatBarKey } from '@/features/world/directives';

export interface ChatPanelHandle {
  focus(): void;
}

export const ChatPanel = forwardRef<
  ChatPanelHandle,
  {
    messages: DesignerMessage[];
    kind?: 'build' | 'mine';
    models: DesignerModel[];
    model: string;
    onModel: (id: string) => void;
    costUsd: number;
    busy: boolean;
    canWrite: boolean;
    hasSelection: boolean;
    withSelection: boolean;
    onWithSelection: (on: boolean) => void;
    onSend: (text: string) => void;
    /** Leaves the composer (Enter on an empty line, Esc): back to the world. */
    onClose: () => void;
    error: string | null;
  }
>(function ChatPanel(p, ref) {
  const [text, setText] = useState('');
  const [collapsed, setCollapsed] = useState(p.kind === 'mine');
  const panelId = useId();
  const focusWhenOpen = useRef(false);
  const input = useRef<HTMLTextAreaElement>(null);
  const list = useRef<HTMLOListElement>(null);
  useImperativeHandle(ref, () => ({ focus: () => {
    if (collapsed) {
      focusWhenOpen.current = true;
      setCollapsed(false);
    } else input.current?.focus();
  } }), [collapsed]);
  useEffect(() => {
    if (!collapsed && focusWhenOpen.current) {
      focusWhenOpen.current = false;
      input.current?.focus();
    }
  }, [collapsed]);
  useEffect(() => {
    if (!collapsed) list.current?.scrollTo({ top: list.current.scrollHeight });
  }, [p.messages.length, p.busy, collapsed]);
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    e.stopPropagation(); // the world's shortcuts never see what is typed here
    if (e.nativeEvent.isComposing || (e.key === 'Enter' && e.shiftKey)) return;
    const action = chatBarKey(e.key, text, p.canWrite && !p.busy);
    if (!action) return;
    e.preventDefault();
    if (action === 'send') {
      p.onSend(text.trim());
      setText('');
      return; // keep typing to the model
    }
    input.current?.blur();
    p.onClose();
  };
  return (
    <aside
      className={cn('flex min-h-0 w-full shrink-0 flex-col border-l border-border bg-surface', collapsed ? 'lg:w-11' : 'lg:w-[360px]')}
      aria-label="Design chat"
    >
      <button type="button" aria-label={collapsed ? 'Expand designer chat' : 'Collapse designer chat'}
        aria-expanded={!collapsed} aria-controls={panelId} onClick={() => setCollapsed((v) => !v)}
        className={cn('flex shrink-0 items-center gap-2 border-b border-border p-3 text-xs text-muted hover:bg-surface-2 hover:text-fg', collapsed && 'lg:flex-col lg:px-2')}>
        <Sparkles className="size-4 shrink-0 text-accent" />
        <span className={cn(collapsed && 'lg:[writing-mode:vertical-rl]')}>{p.kind === 'mine' ? 'Mine assistant' : 'Design assistant'}</span>
        {p.busy ? <Spinner className="size-3.5" /> : collapsed ? <ChevronLeft className="size-3.5" /> : <ChevronRight className="ml-auto size-3.5" />}
      </button>
      <div id={panelId} hidden={collapsed} className={cn('min-h-0 flex-1 flex-col', collapsed ? 'hidden' : 'flex')}>
      <div className="space-y-2 border-b border-border p-3">
        <div className="flex items-center gap-2">
          <Sparkles className="size-4 text-accent" />
          <h2 className="text-sm font-semibold text-fg">Design with</h2>
          <span
            className="ml-auto text-xs text-muted"
            data-testid="session-cost"
            title="What this session cost (counted against the bot's budget)"
          >
            {usd(p.costUsd)}
          </span>
        </div>
        <Select
          aria-label="Model"
          value={p.model}
          onChange={(e) => p.onModel(e.target.value)}
          className="h-8 w-full"
          disabled={p.models.length === 0}
        >
          {p.models.map((m) => (
            <option key={m.id} value={m.id}>
              {m.label}
              {m.costHint ? ` · ${m.costHint}` : ''}
            </option>
          ))}
        </Select>
      </div>
      <ol
        ref={list}
        className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3"
        aria-label="Transcript"
        aria-live="polite"
      >
        {p.messages.length === 0 ? (
          <li className="rounded-lg border border-dashed border-border p-3 text-sm text-muted">
            {p.kind === 'mine' ? 'Ask the AI to plan a mine, extend a tunnel, or connect your selected sections.' : 'Tell the AI what to build (“a small cobblestone watchtower with a ladder”), or build by hand and ask it to finish.'} Your edits and the selection reach it with your next message.
          </li>
        ) : null}
        {p.messages.map((m) => (
          <MessageItem key={`${m.seq}:${m.at}`} m={m} />
        ))}
        {p.busy ? (
          <li className="flex items-center gap-2 text-sm text-muted" data-testid="designing">
            <Spinner className="size-4" /> Designing…
          </li>
        ) : null}
      </ol>
      <div className="space-y-2 border-t border-border p-3">
        {p.error ? (
          <p className="text-xs text-danger" role="alert">
            {p.error}
          </p>
        ) : null}
        <label className={cn('flex items-center gap-2 text-xs', p.hasSelection ? 'text-muted' : 'text-subtle')}>
          <input
            type="checkbox"
            checked={p.withSelection && p.hasSelection}
            disabled={!p.hasSelection}
            onChange={(e) => p.onWithSelection(e.target.checked)}
          />
          Send the selection with the message (“the selection”)
        </label>
        <div className="flex items-end gap-2">
          <textarea
            ref={input}
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={onKey}
            rows={2}
            maxLength={2000}
            disabled={!p.canWrite}
            aria-label="Message the designer"
            placeholder={
              p.canWrite
                ? 'Ask for a build or a change (Enter sends, Enter on an empty line returns to the world)'
                : 'Your account needs the design role'
            }
            className="min-h-[2.5rem] flex-1 resize-none rounded-lg border border-border bg-canvas px-2.5 py-1.5 text-sm text-fg outline-none focus:border-accent"
          />
          <Button
            size="sm"
            onClick={() => {
              if (!text.trim()) return;
              p.onSend(text.trim());
              setText('');
            }}
            disabled={!p.canWrite || p.busy || !text.trim()}
            aria-label="Send"
          >
            <Send className="size-3.5" />
          </Button>
        </div>
      </div>
      </div>
    </aside>
  );
});

type Message = DesignerMessage;

/** One chat message: who said it, the text, the ops it made, and (the AI's) model and cost. */
function MessageItem({ m }: { m: Message }) {
  const user = m.role === 'user';
  return (
    <li className={cn('flex gap-2 text-sm', user ? 'flex-row-reverse' : '')} data-role={m.role}>
      <span
        className={cn(
          'mt-0.5 grid size-6 shrink-0 place-items-center rounded-full',
          user ? 'bg-accent-soft text-accent' : 'bg-surface-2 text-muted',
        )}
      >
        {user ? <User className="size-3.5" /> : <Bot className="size-3.5" />}
      </span>
      <div className={cn('max-w-[85%] rounded-lg px-3 py-2', user ? 'bg-accent-soft text-fg' : 'bg-surface-2 text-fg')}>
        <p className="whitespace-pre-wrap">{m.text}</p>
        {m.ops?.length ? (
          <ul className="mt-1.5 flex flex-wrap gap-1" aria-label="What it changed">
            {m.ops.map((o, i) => (
              <li key={i} className="rounded bg-ok-soft px-1.5 py-0.5 text-[11px] text-ok" title={`${o.cells} cells`}>
                {o.label}
              </li>
            ))}
          </ul>
        ) : null}
        <MessageCost m={m} />
      </div>
    </li>
  );
}

const hasCost = (m: Message) => m.costUsd !== null && m.costUsd !== undefined;

/** The AI's model and what the message cost (when known). */
function MessageCost({ m }: { m: Message }) {
  if (m.role !== 'assistant' || !(m.model || hasCost(m))) return null;
  return (
    <p className="mt-1 text-[11px] text-subtle" data-testid="message-cost">
      {m.model ?? ''}
      {hasCost(m) ? ` · ${usd(m.costUsd)}` : ''}
    </p>
  );
}
