/**
 * Chat bubbles in the 3D world: the shared SSE client publishes every `feed` frame here (lib/live/dispatch.ts), and the world
 * viewer subscribes once per runtime through `WorldSource.chat`, so every view (the world pane, fullscreen free cam, the
 * follow/iso/top cameras, the hive-card thumbnails, any page that shows the world) gets the bubbles from one subscription.
 * Only stream frames count: a REST re-fetch or a resync never replays old lines as fresh bubbles, and a seq is shown once.
 *
 * What becomes a bubble (`bubbleOf`): what a player says in game (`chat`), what a bot says (`said`), what the signed-in
 * user sent from the web as their player (`viewer`), and whispers only when the signed-in user's player sent them. Private
 * speech (`internal`, `muted`), `system` lines and commands (`/…`) never do. The text chat UI is unchanged.
 */
import type { FeedLine, FeedResponse } from '@mineclaw/contracts';
import type { ChatSay } from '@mineclaw/world-viewer';

export type { ChatSay };

/** The bubble for a feed line, or null when the line is not shown over a head. `player` = the signed-in user's player. */
export function bubbleOf(line: FeedLine, player: string | null | undefined): ChatSay | null {
  const from = line.from?.trim();
  const text = line.text?.trim();
  if (!from || !text || text.startsWith('/')) return null;
  if (SPOKEN.has(line.kind)) return { from, text, kind: line.kind as ChatSay['kind'] };
  // a whisper only when it is the user's own; internal, muted and system lines never
  return line.kind === 'whisper' && sameName(from, player) ? { from, text, kind: 'whisper' } : null;
}

/** The kinds said out loud (in game, by the bot, from the web). */
const SPOKEN = new Set<FeedLine['kind']>(['chat', 'said', 'viewer']);

const sameName = (a: string, b: string | null | undefined): boolean => !!b && a.toLowerCase() === b.toLowerCase();

type Listener = (s: ChatSay) => void;
interface Sub {
  fn: Listener;
  player: () => string | null | undefined;
}
const subs = new Set<Sub>();
const seen = new Set<number>();
const SEEN_CAP = 2000;

/** The shared SSE client calls this for every `feed` frame. Each seq is published once (Last-Event-ID replays are dropped). */
export function publishFeed(d: FeedResponse): void {
  for (const line of d.lines) {
    if (seen.has(line.seq)) continue;
    seen.add(line.seq);
    if (seen.size > SEEN_CAP) seen.delete(seen.values().next().value as number);
    for (const s of subs) {
      const b = bubbleOf(line, s.player());
      if (b) s.fn(b);
    }
  }
}

/** Subscribes to bubble-worthy lines (the `WorldSource.chat` hook). `player` reads the signed-in user's player name. */
export function subscribeChat(fn: Listener, player: () => string | null | undefined = () => null): () => void {
  const s: Sub = { fn, player };
  subs.add(s);
  return () => {
    subs.delete(s);
  };
}

/** For tests. */
export function resetChat(): void {
  subs.clear();
  seen.clear();
}
