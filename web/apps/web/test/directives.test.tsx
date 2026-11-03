import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { FeedResponse } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ChatOverlay } from '@/features/world/InWorld';
import { directiveText, isTyping, nextSelection, selectByNumber, sendDirective } from '@/features/world/directives';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { liveKeys } from '@/lib/live/dispatch';

const urlOf = (i: RequestInfo | URL): string => (typeof i === 'string' ? i : i instanceof URL ? i.href : i.url);

describe('directives', () => {
  let posts: Array<{ url: string; body: { text: string; requestId: string } }>;
  beforeEach(() => {
    posts = [];
    setCsrfToken('tok-test-0000000000000');
    setFetch((input, init) => {
      posts.push({ url: urlOf(input), body: JSON.parse(String(init?.body)) as { text: string; requestId: string } });
      return Promise.resolve(new Response(JSON.stringify({ accepted: true, id: 'x', feedSeq: 3 }), { status: 202, headers: { 'Content-Type': 'application/json' } }));
    });
  });
  afterEach(() => setCsrfToken(null));

  it('sends one chat line to every selected bot through the chat route, each with its own request id', async () => {
    const res = await sendDirective(['maid_chan_102', 'maid_chan_114', 'maid_chan_102'], '  come here (12, 64, -3) ');
    expect(res).toEqual([
      { bot: 'maid_chan_102', ok: true },
      { bot: 'maid_chan_114', ok: true },
    ]);
    expect(posts.map((p) => new URL(p.url, 'http://x').pathname).sort()).toEqual(['/api/bots/maid_chan_102/chat', '/api/bots/maid_chan_114/chat']);
    expect(posts.every((p) => p.body.text === 'come here (12, 64, -3)')).toBe(true);
    expect(new Set(posts.map((p) => p.body.requestId)).size).toBe(2);
    expect(Object.keys(posts[0]!.body).sort()).toEqual(['requestId', 'text']); // never a player or a command grant
  });

  it('reports a refused send per bot and sends nothing without targets or text', async () => {
    setFetch(() => Promise.resolve(new Response(JSON.stringify({ error: 'forbidden', code: 'forbidden' }), { status: 403, headers: { 'Content-Type': 'application/json' } })));
    const res = await sendDirective(['maid_chan'], 'hi');
    expect(res[0]).toMatchObject({ bot: 'maid_chan', ok: false });
    expect(await sendDirective([], 'hi')).toEqual([]);
    expect(await sendDirective(['maid_chan'], '   ')).toEqual([]);
  });

  it('phrases directives naturally with the coordinates', () => {
    expect(directiveText('come', [12, 64, -3])).toBe('come here (12, 64, -3)');
    expect(directiveText('stay', [0, 70, 5])).toBe('stay here (0, 70, 5)');
  });

  it('selection: click replaces, Shift toggles, Ctrl clears; number keys pick the nth bot in view', () => {
    expect(nextSelection(['a'], 'b', { shift: false, ctrl: false })).toEqual(['b']);
    expect(nextSelection(['a'], 'b', { shift: true, ctrl: false })).toEqual(['a', 'b']);
    expect(nextSelection(['a', 'b'], 'a', { shift: true, ctrl: false })).toEqual(['b']);
    expect(nextSelection(['a', 'b'], 'c', { shift: false, ctrl: true })).toEqual([]);
    expect(selectByNumber([], ['near', 'far'], 2, false)).toEqual(['far']);
    expect(selectByNumber(['near'], ['near', 'far'], 2, true)).toEqual(['near', 'far']);
    expect(selectByNumber(['near'], ['near'], 5, false)).toEqual(['near']);
  });

  it('knows when a key belongs to a text field', () => {
    const input = document.createElement('input');
    const div = document.createElement('div');
    const editable = document.createElement('div');
    editable.contentEditable = 'true';
    document.body.append(input, div, editable);
    expect(isTyping(input)).toBe(true);
    expect(isTyping(div)).toBe(false);
    expect(isTyping(editable)).toBe(true);
    expect(isTyping(null)).toBe(false);
    input.remove();
    div.remove();
    editable.remove();
  });
});

describe('ChatOverlay', () => {
  afterEach(cleanup);
  function setup(props: Partial<Parameters<typeof ChatOverlay>[0]> = {}) {
    const qc = new QueryClient();
    qc.setQueryData<FeedResponse>(liveKeys.feed, { lines: [], last: 0 });
    const onSend = vi.fn();
    const onClose = vi.fn();
    render(
      <QueryClientProvider client={qc}>
        <ChatOverlay open targets={['maid_chan_102']} marker={[12, 64, -3]} canChat onSend={onSend} onClose={onClose} results={null} {...props} />
      </QueryClientProvider>,
    );
    return { onSend, onClose };
  }

  it('keys typed in the chat bar never reach the world shortcuts (T, digits, F, Tab)', async () => {
    const world = vi.fn();
    window.addEventListener('keydown', world);
    try {
      const { onSend } = setup();
      const box = screen.getByRole('textbox', { name: /chat to the selected bots/i });
      await userEvent.type(box, 'T1F go{Enter}');
      expect(world).not.toHaveBeenCalled();
      expect(onSend).toHaveBeenCalledWith('T1F go');
    } finally {
      window.removeEventListener('keydown', world);
    }
  });

  it('a directive chip fills the bar with the target block; Esc closes without sending', async () => {
    const { onSend, onClose } = setup();
    await userEvent.click(screen.getByRole('button', { name: 'Mine here' }));
    expect(screen.getByRole('textbox')).toHaveValue('mine here (12, 64, -3)');
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
    expect(onSend).not.toHaveBeenCalled();
  });

  it('without the chat role the bar cannot send', () => {
    setup({ canChat: false });
    expect(screen.getByRole('textbox')).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Come here' })).toBeNull();
  });
});
