'use client';

/**
 * The game-feel layer over the world pane: the vanilla crosshair (free cam, mouse locked), what it points at, the selected
 * bots, a Minecraft-style chat bar (T / Enter) with directive chips for a target block, and the bots' replies fading in
 * above it. Everything here sits inside the pane's fullscreen element, so it stays visible in fullscreen.
 */
import type { AimTarget } from '@mineclaw/world-viewer';
import type { FeedLine, WorldBot } from '@mineclaw/contracts';
import { Crosshair as CrosshairIcon, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useFeed } from '@/lib/live/hooks';
import { useTicker } from '@/lib/live/ticker';
import { cn } from '@/lib/utils';
import { DIRECTIVES, directiveText, type SendResult } from './directives';

/** Vanilla's 9×9 "+" drawn in inverted colour (white blended by difference, like the game's GL_ONE_MINUS_DST_COLOR). */
export function Crosshair() {
  return (
    <div
      className="pointer-events-none absolute top-1/2 left-1/2 size-[18px] -translate-x-1/2 -translate-y-1/2 mix-blend-difference"
      aria-hidden
      data-testid="crosshair"
    >
      <div className="absolute top-0 left-[8px] h-[18px] w-[2px] bg-white" />
      <div className="absolute top-[8px] left-0 h-[2px] w-[18px] bg-white" />
    </div>
  );
}

/** "maid_chan_102 · 18 ♥ 15 food" or "oak planks · 12, 64, -3", just under the crosshair. */
export function TargetReadout({ target, bot }: { target: AimTarget | null; bot: WorldBot | undefined }) {
  if (!target) return null;
  return (
    <div
      className="pointer-events-none absolute top-1/2 left-1/2 mt-5 -translate-x-1/2 rounded bg-black/55 px-2 py-0.5 font-mono text-[11px] whitespace-nowrap text-white"
      role="status"
      data-testid="aim-readout"
    >
      {target.kind === 'bot' ? (
        <>
          <span className="font-semibold text-[#ffff55]">{target.name}</span>
          {bot ? (
            <span className="text-white/85">
              {' '}
              · ♥ {bot.hp}/20 · food {bot.food}/20
            </span>
          ) : null}
        </>
      ) : (
        <>
          {target.name.replace(/_/g, ' ')} · {target.x}, {target.y}, {target.z}
        </>
      )}
    </div>
  );
}

/** The selected bots (click with the crosshair; Shift adds, Ctrl clears; 1–9 picks the nth nearest). */
export function SelectionBar({
  selected,
  marker,
  onClear,
  onClearMarker,
}: {
  selected: string[];
  marker: [number, number, number] | null;
  onClear: () => void;
  onClearMarker: () => void;
}) {
  if (!selected.length && !marker) return null;
  return (
    <div
      className="absolute top-3 right-3 flex max-w-[60%] flex-wrap items-center justify-end gap-1.5 font-mono text-[11px] text-white"
      data-testid="selection"
    >
      {selected.length ? (
        <span className="inline-flex items-center gap-1 rounded bg-black/60 px-2 py-1">
          <span className="text-[#55ff55]">Selected</span> {selected.join(', ')}
          <button onClick={onClear} aria-label="Clear the selection" className="ml-1 text-white/70 hover:text-white">
            <X className="size-3" />
          </button>
        </span>
      ) : null}
      {marker ? (
        <span className="inline-flex items-center gap-1 rounded bg-black/60 px-2 py-1">
          <CrosshairIcon className="size-3 text-[#38bdf8]" /> {marker.join(', ')}
          <button
            onClick={onClearMarker}
            aria-label="Clear the target block"
            className="ml-1 text-white/70 hover:text-white"
          >
            <X className="size-3" />
          </button>
        </span>
      ) : null}
    </div>
  );
}

const SPOKEN = new Set<FeedLine['kind']>(['said', 'whisper', 'chat', 'viewer']);

/** Chat lines of the selected bots, newest last; while the bar is closed only the last few seconds' lines (they fade). */
function useChatLines(bots: string[], open: boolean): FeedLine[] {
  const feed = useFeed();
  const now = useTicker(1000);
  return useMemo(() => {
    const set = new Set(bots);
    const lines = (feed.data?.lines ?? []).filter((l) => set.has(l.bot) && SPOKEN.has(l.kind));
    const recent = open ? lines : lines.filter((l) => now - Date.parse(l.at) < 10_000);
    return recent.slice(-8);
  }, [feed.data, bots, open, now]);
}

/** The Minecraft-style chat: replies above, the input bar at the bottom while open. */
export function ChatOverlay({
  open,
  targets,
  marker,
  canChat,
  onSend,
  onClose,
  results,
}: {
  open: boolean;
  /** Bots a line goes to (the selection, else the followed bot). */
  targets: string[];
  marker: [number, number, number] | null;
  canChat: boolean;
  onSend: (text: string) => void;
  onClose: () => void;
  results: SendResult[] | null;
}) {
  const [text, setText] = useState('');
  const input = useRef<HTMLInputElement>(null);
  const lines = useChatLines(targets, open);
  useEffect(() => {
    if (open) {
      setText('');
      requestAnimationFrame(() => input.current?.focus());
    }
  }, [open]);
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    e.stopPropagation(); // the world's shortcuts never see what is typed here
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    } else if (e.key === 'Enter' && !e.nativeEvent.isComposing) {
      e.preventDefault();
      if (text.trim() && targets.length && canChat) {
        onSend(text.trim());
        setText('');
        onClose();
      }
    }
  };
  return (
    <div className="absolute inset-x-3 bottom-9 flex flex-col items-start gap-1.5" data-testid="chat-overlay">
      {lines.length ? (
        <ul
          className="max-w-[min(42rem,90%)] space-y-0.5 font-mono text-[12px] leading-snug text-white"
          aria-label="Chat"
          aria-live="polite"
        >
          {lines.map((l) => (
            <li key={l.seq} className={cn('rounded-sm bg-black/50 px-1.5 py-0.5', !open && 'animate-chat-fade')}>
              <span className={l.kind === 'said' || l.kind === 'whisper' ? 'text-[#ffff55]' : 'text-white/80'}>
                &lt;{l.from ?? l.bot}&gt;
              </span>{' '}
              {l.text}
            </li>
          ))}
        </ul>
      ) : null}
      {open ? (
        <div className="w-full space-y-1.5">
          {marker && canChat ? (
            <div className="flex flex-wrap gap-1.5" aria-label="Directives">
              {DIRECTIVES.map((d) => (
                <button
                  key={d.kind}
                  type="button"
                  onMouseDown={(e) => e.preventDefault()} // keep the input focused
                  onClick={() => {
                    setText(directiveText(d.kind, marker));
                    input.current?.focus();
                  }}
                  className="rounded-sm bg-black/60 px-2 py-0.5 font-mono text-[11px] text-white hover:bg-black/80"
                >
                  {d.label}
                </button>
              ))}
            </div>
          ) : null}
          <div className="flex items-center gap-2 bg-black/60 px-2 py-1.5 font-mono text-[13px] text-white">
            <span className="shrink-0 text-white/60">
              {targets.length ? `to ${targets.join(', ')}` : 'select a bot'}
            </span>
            <input
              ref={input}
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={onKey}
              disabled={!canChat || targets.length === 0}
              maxLength={256}
              aria-label="Chat to the selected bots"
              placeholder={
                !canChat
                  ? 'Your account cannot chat with the bots'
                  : targets.length
                    ? 'Say something (Enter sends, Esc closes)'
                    : 'Click a bot with the crosshair first'
              }
              className="min-w-0 flex-1 bg-transparent text-white outline-none placeholder:text-white/40"
            />
          </div>
        </div>
      ) : results && results.some((r) => !r.ok) ? (
        <div className="rounded-sm bg-black/60 px-2 py-0.5 font-mono text-[11px] text-[#ff5555]" role="alert">
          Not sent to{' '}
          {results
            .filter((r) => !r.ok)
            .map((r) => `${r.bot} (${r.error ?? 'error'})`)
            .join(', ')}
        </div>
      ) : null}
    </div>
  );
}
