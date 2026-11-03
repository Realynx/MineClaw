import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { FeedLine, FeedResponse, Me } from '@mineclaw/contracts';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TooltipProvider } from '@/components/ui/overlays';
import { ChatPane } from '@/features/chat/ChatPane';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { StaticAuthProvider } from '@/lib/auth';
import { liveKeys } from '@/lib/live/dispatch';

const at = '2026-11-02T10:00:00+00:00';
const urlOf = (i: RequestInfo | URL): string => (typeof i === 'string' ? i : i instanceof URL ? i.href : i.url);
const me = (roles: Me['roles'], player: string | null = 'poofimafox'): Me => ({
  username: 'fox',
  player,
  roles,
  totpEnrolled: false,
  sessionCreatedAt: at,
  idleExpiresAt: at,
  absoluteExpiresAt: at,
});
const line = (seq: number, kind: FeedLine['kind'], text: string, extra: Partial<FeedLine> = {}): FeedLine => ({
  seq,
  at,
  bot: 'maid_chan',
  kind,
  from: kind === 'said' || kind === 'internal' ? 'maid_chan' : 'kiwi',
  text,
  origin: 'game',
  ...extra,
});

function setup(user: Me, lines: FeedLine[]) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData<FeedResponse>(liveKeys.feed, { lines, last: lines.at(-1)?.seq ?? 0 });
  render(
    <QueryClientProvider client={qc}>
      <TooltipProvider>
        <StaticAuthProvider me={user}>
          <div style={{ height: 600 }}>
            <ChatPane bot="maid_chan" />
          </div>
        </StaticAuthProvider>
      </TooltipProvider>
    </QueryClientProvider>,
  );
  return qc;
}

describe('ChatPane', () => {
  let posts: Array<{ url: string; body: unknown }>;
  let answer: () => Response;
  beforeEach(() => {
    posts = [];
    setCsrfToken('tok-test-0000000000000');
    answer = () => new Response(JSON.stringify({ accepted: true, id: 'x', feedSeq: 11 }), { status: 202, headers: { 'Content-Type': 'application/json' } });
    setFetch((input, init) => {
      posts.push({ url: urlOf(input), body: JSON.parse(typeof init?.body === 'string' ? init.body : 'null') as unknown });
      return Promise.resolve(answer());
    });
    localStorage.clear();
  });
  afterEach(() => setCsrfToken(null));

  it('threads the conversation and hides private speech behind the toggle', async () => {
    setup(me(['viewer', 'chat']), [line(1, 'chat', 'hello maid'), line(2, 'internal', 'secret plan'), line(3, 'said', 'hi Robin!')]);
    expect(screen.getByText('hello maid')).toBeInTheDocument();
    expect(screen.getByText('hi Robin!')).toBeInTheDocument();
    expect(screen.queryByText('secret plan')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /private/i }));
    expect(screen.getByText('secret plan')).toBeInTheDocument();
  });

  it('sends as the linked player, shows a pending bubble, and resolves when the bot replies after our line', async () => {
    const qc = setup(me(['viewer', 'chat']), [line(10, 'said', 'earlier')]);
    const box = screen.getByLabelText(/message maid_chan as poofimafox/i);
    await userEvent.type(box, 'claude, set it to day{Enter}');

    expect(posts[0]?.url).toBe('/api/bots/maid_chan/chat');
    expect(posts[0]?.body).toMatchObject({ text: 'claude, set it to day' });
    expect((posts[0]?.body as { requestId: string }).requestId).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
    expect(screen.getByText('claude, set it to day')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/maid_chan is replying/i)).toBeInTheDocument());

    // our viewer line lands in the feed (seq 11 = feedSeq), then the reply
    act(() => {
      qc.setQueryData<FeedResponse>(liveKeys.feed, {
        lines: [line(10, 'said', 'earlier'), line(11, 'viewer', 'claude, set it to day', { from: 'poofimafox', origin: 'viewer' })],
        last: 11,
      });
    });
    expect(screen.getAllByText('claude, set it to day')).toHaveLength(1); // the optimistic bubble gave way to the real line
    expect(screen.getByText(/maid_chan is replying/i)).toBeInTheDocument();

    act(() => {
      qc.setQueryData<FeedResponse>(liveKeys.feed, (prev) => ({ lines: [...prev!.lines, line(12, 'said', 'Setting the time to day!')], last: 12 }));
    });
    await waitFor(() => expect(screen.queryByText(/is replying/i)).not.toBeInTheDocument());
    expect(screen.getByText('Setting the time to day!')).toBeInTheDocument();
  });

  it('shows the rate limit with a retry countdown', async () => {
    answer = () => new Response(JSON.stringify({ error: 'Slow down', code: 'rate_limited' }), { status: 429, headers: { 'Content-Type': 'application/json', 'Retry-After': '5' } });
    setup(me(['viewer', 'chat']), []);
    await userEvent.type(screen.getByLabelText(/message maid_chan/i), 'hi{Enter}');
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/retry in \d+s/i));
    expect(screen.getByLabelText(/message maid_chan/i)).toBeDisabled();
  });

  it('hides the composer without the chat role or a linked player', () => {
    setup(me(['viewer']), [line(1, 'said', 'hi')]);
    expect(screen.queryByLabelText(/message maid_chan/i)).not.toBeInTheDocument();
  });
});
