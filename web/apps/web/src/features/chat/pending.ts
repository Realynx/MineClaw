/**
 * Optimistic chat: what the composer shows between "send" and the bot's reply.
 *
 * 1. `sending`: the POST is in flight. An optimistic bubble shows the text.
 * 2. `awaiting`: the POST answered `{accepted, id}`. `id` (or `feedSeq`, when present) is the seq of our `viewer` feed line;
 *    a non-numeric id is a correlation id and we match the viewer line by `requestId` instead. Once the viewer line is in the
 *    feed, the real line replaces the optimistic bubble (`echoed`), and a "replying…" hint stays until…
 * 3. resolved: a `said`/`whisper` line from the target bot appears AFTER our viewer line's seq (or one that names it in
 *    `replyTo`). The bot may merge several quick lines into one turn, so one reply resolves every pending message before it.
 * Failures: `failed` (not accepted / error) and `rate_limited` (429 with Retry-After) keep the text for a retry.
 */
import type { ChatAccepted, FeedLine } from '@mineclaw/contracts';

export type PendingState = 'sending' | 'awaiting' | 'failed' | 'rate_limited';

export interface PendingMessage {
  localId: string;
  requestId: string;
  bot: string;
  text: string;
  createdAt: number;
  state: PendingState;
  /** seq of our viewer line in the feed, once known. */
  viewerSeq: number | null;
  /** The viewer line is in the feed (the optimistic bubble hides). */
  echoed: boolean;
  error?: string;
  retryAt?: number;
}

/** Reads the viewer-line seq from a chat answer: `feedSeq` if present, else a numeric `id`. */
export function viewerSeqFrom(res: ChatAccepted & { feedSeq?: unknown }, requestId: string): number | null {
  if (typeof res.feedSeq === 'number' && Number.isInteger(res.feedSeq)) return res.feedSeq;
  if (typeof res.feedSeq === 'string' && /^\d+$/.test(res.feedSeq)) return Number(res.feedSeq);
  if (typeof res.id === 'number') return res.id;
  if (typeof res.id === 'string' && res.id !== requestId && /^\d+$/.test(res.id)) return Number(res.id);
  return null;
}

export function createPending(bot: string, text: string, requestId: string, now = Date.now()): PendingMessage {
  return { localId: requestId, requestId, bot, text, createdAt: now, state: 'sending', viewerSeq: null, echoed: false };
}

export function onAccepted(p: PendingMessage, res: ChatAccepted & { feedSeq?: unknown }): PendingMessage {
  if (!res.accepted) return { ...p, state: 'failed', error: res.note ?? 'The bot did not accept the message.' };
  return { ...p, state: 'awaiting', viewerSeq: viewerSeqFrom(res, p.requestId) ?? p.viewerSeq };
}

export function onFailed(p: PendingMessage, error: string, retryAfterMs?: number, now = Date.now()): PendingMessage {
  if (retryAfterMs !== undefined) return { ...p, state: 'rate_limited', error, retryAt: now + retryAfterMs };
  return { ...p, state: 'failed', error };
}

function sameBot(line: FeedLine, bot: string, aliases: readonly string[]): boolean {
  return line.bot === bot || aliases.includes(line.bot);
}

/**
 * A line the bot itself spoke. `said`: `bot` is the speaker. `whisper`: `bot` is the hearer for incoming whispers, so an
 * outgoing whisper must also come `from` the bot.
 */
export function isBotReply(l: FeedLine, bot: string, aliases: readonly string[] = []): boolean {
  if (!sameBot(l, bot, aliases)) return false;
  if (l.kind === 'said') return true;
  if (l.kind === 'whisper') return !!l.from && (l.from === bot || l.from === l.bot || aliases.includes(l.from));
  return false;
}

/**
 * Matches pending messages against the feed. Returns the messages still pending (with echo/seq updates) and the ids that the
 * bot has answered. Pure; call it whenever the feed or the pending list changes.
 */
export function reconcile(
  pending: readonly PendingMessage[],
  lines: readonly FeedLine[],
  aliases: readonly string[] = [],
): { pending: PendingMessage[]; resolved: string[] } {
  const resolved: string[] = [];
  const out: PendingMessage[] = [];
  for (const p0 of pending) {
    let p = p0;
    if (p.state === 'sending' || p.state === 'awaiting') {
      const echo = lines.find(
        (l) =>
          l.kind === 'viewer' &&
          sameBot(l, p.bot, aliases) &&
          ((p.viewerSeq !== null && l.seq === p.viewerSeq) || (!!l.requestId && l.requestId === p.requestId)),
      );
      if (echo) p = { ...p, viewerSeq: echo.seq, echoed: true };
    }
    if (p.state === 'awaiting' && p.viewerSeq !== null) {
      const seq = p.viewerSeq;
      const reply = lines.find((l) => isBotReply(l, p.bot, aliases) && (l.seq > seq || l.replyTo === seq));
      if (reply) {
        resolved.push(p.localId);
        continue;
      }
    }
    out.push(p);
  }
  return { pending: out, resolved };
}

/** Bubbles to draw for pending messages: only the ones whose real viewer line is not in the feed yet. */
export function optimisticBubbles(pending: readonly PendingMessage[]): PendingMessage[] {
  return pending.filter((p) => !p.echoed || p.state === 'failed' || p.state === 'rate_limited');
}

/** Whether to show "replying…" under the thread. */
export function awaitingReply(pending: readonly PendingMessage[]): boolean {
  return pending.some((p) => p.state === 'awaiting' || p.state === 'sending');
}

/** A message with no reply after this long gets a quiet "no reply yet" note (bots can be busy or asleep). */
export const REPLY_PATIENCE_MS = 90_000;
