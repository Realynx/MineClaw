import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { FeedResponse, Me, StoryColonyState } from '@mineclaw/contracts';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { TooltipProvider } from '@/components/ui/overlays';
import { DirectivesChat } from '@/features/story/DirectivesChat';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { StaticAuthProvider } from '@/lib/auth';
import { liveKeys } from '@/lib/live/dispatch';

const at = '2026-11-08T10:00:00+00:00';
const main = 'main_colony_mind';
const state = { id: 'story-5', name: 'Storehouse', ai: [{ name: 'worker_101', kind: 'subhive' }, { name: main, kind: 'main' }], directives: [] } as unknown as StoryColonyState;
const me: Me = { username: 'fox', player: 'poofimafox', roles: ['viewer', 'story'], totpEnrolled: false, sessionCreatedAt: at, idleExpiresAt: at, absoluteExpiresAt: at };
let posts: Array<{ url: string; body: { directive: { text: string }; requestId: string } }>;
beforeEach(() => {
  posts = [];
  setCsrfToken('tok-test-0000000000000');
  setFetch((input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    posts.push({ url, body: JSON.parse(init?.body as string) as (typeof posts)[number]['body'] });
    return Promise.resolve(new Response(JSON.stringify({ id: 'dir15', delivered: [main] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
  });
  localStorage.clear();
});
afterEach(() => { cleanup(); setCsrfToken(null); });
function setup(canWrite = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData(liveKeys.feed, { lines: [], last: 0 });
  qc.setQueryData(liveKeys.now(main), { bot: main, at, brainState: 'awake' });
  render(<QueryClientProvider client={qc}><TooltipProvider><StaticAuthProvider me={me}><DirectivesChat state={state} canWrite={canWrite} /></StaticAuthProvider></TooltipProvider></QueryClientProvider>);
  return qc;
}
it('sends long colony instructions to the main bot through authenticated story directives and follows its real feed reply', async () => {
  const qc = setup();
  const text = 'Change the mine schedule. '.repeat(20);
  const box = screen.getByRole('textbox');
  fireEvent.change(box, { target: { value: text } });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  await waitFor(() => expect(posts).toHaveLength(1));
  expect(posts[0]?.url).toBe('/api/story/directives');
  expect(posts[0]?.body).toMatchObject({ bots: [main], directive: { kind: 'text', colony: 'story-5', text: text.trim() } });
  expect(posts[0]?.body).not.toHaveProperty('player');
  expect(posts[0]?.body).not.toHaveProperty('allowCommands');
  const requestId = posts[0]!.body.requestId;
  await act(() => qc.setQueryData<FeedResponse>(liveKeys.feed, { last: 2, lines: [
    { seq: 1, at, bot: main, kind: 'viewer', from: 'poofimafox', origin: 'viewer', requestId, text: text.trim() },
    { seq: 2, at, bot: main, kind: 'said', from: main, origin: 'bot', text: 'The requested schedule has been saved.' },
  ] }));
  await waitFor(() => expect(screen.queryByText(/is replying/)).not.toBeInTheDocument());
  expect(screen.getByText('The requested schedule has been saved.')).toBeInTheDocument();
});
it('does not offer a composer without colony write access', () => {
  setup(false);
  expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  expect(screen.getByText(/Colony control permission/)).toBeInTheDocument();
});
