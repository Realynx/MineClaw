import type { FeedLine } from '@mineclaw/contracts';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it } from 'vitest';
import { bubbleOf, publishFeed, resetChat, subscribeChat, type ChatSay } from '@/features/world/chatBubbles';
import { createWorldSource } from '@/features/world/WorldSource';
import { applyLiveEvent, liveKeys } from '@/lib/live/dispatch';

let seq = 0;
const line = (over: Partial<FeedLine>): FeedLine => ({
  seq: ++seq,
  at: new Date().toISOString(),
  bot: 'maid_chan',
  kind: 'chat',
  from: 'poofimafox',
  text: 'hello',
  origin: 'game',
  ...over,
});

afterEach(() => resetChat());

describe('chat bubbles from the feed', () => {
  it('shows what players and bots say, and viewer lines', () => {
    expect(bubbleOf(line({ kind: 'chat' }), null)).toEqual({ from: 'poofimafox', text: 'hello', kind: 'chat' });
    expect(bubbleOf(line({ kind: 'said', from: 'maid_chan', text: 'Hi!' }), null)).toEqual({
      from: 'maid_chan',
      text: 'Hi!',
      kind: 'said',
    });
    expect(bubbleOf(line({ kind: 'viewer', origin: 'viewer' }), 'poofimafox')).toMatchObject({
      from: 'poofimafox',
      kind: 'viewer',
    });
  });

  it('never shows private speech, system lines, commands or lines without a speaker', () => {
    expect(bubbleOf(line({ kind: 'internal', from: 'maid_chan' }), 'poofimafox')).toBeNull();
    expect(bubbleOf(line({ kind: 'muted', from: 'maid_chan' }), 'poofimafox')).toBeNull();
    expect(bubbleOf(line({ kind: 'system', from: null, text: 'kiwi joined the game' }), null)).toBeNull();
    expect(bubbleOf(line({ kind: 'viewer', text: '/time set day' }), 'poofimafox')).toBeNull();
    expect(bubbleOf(line({ kind: 'chat', from: '' }), null)).toBeNull();
    expect(bubbleOf(line({ kind: 'chat', text: '   ' }), null)).toBeNull();
  });

  it('shows whispers only when the signed-in player is a party', () => {
    const w = line({ kind: 'whisper', from: 'PoofImaFox', text: 'psst' });
    expect(bubbleOf(w, 'poofimafox')).toEqual({ from: 'PoofImaFox', text: 'psst', kind: 'whisper' });
    expect(bubbleOf(w, 'kiwi')).toBeNull();
    expect(bubbleOf(w, null)).toBeNull();
    expect(bubbleOf(line({ kind: 'whisper', from: 'kiwi' }), 'poofimafox')).toBeNull();
  });

  it('publishes each stream line once to every subscriber, with its own player', () => {
    const fox: ChatSay[] = [];
    const guest: ChatSay[] = [];
    const offFox = subscribeChat(
      (s) => fox.push(s),
      () => 'poofimafox',
    );
    subscribeChat((s) => guest.push(s));
    const a = line({ kind: 'said', from: 'maid_chan', text: 'one' });
    const b = line({ kind: 'whisper', from: 'poofimafox', text: 'two' });
    const c = line({ kind: 'internal', from: 'maid_chan', text: 'thinking' });
    publishFeed({ lines: [a, b, c], last: c.seq });
    publishFeed({ lines: [a, b], last: b.seq }); // a Last-Event-ID replay
    expect(fox.map((s) => s.text)).toEqual(['one', 'two']);
    expect(guest.map((s) => s.text)).toEqual(['one']);
    offFox();
    publishFeed({ lines: [line({ text: 'three' })], last: seq });
    expect(fox).toHaveLength(2);
    expect(guest.map((s) => s.text)).toEqual(['one', 'three']);
  });

  it('the shared stream feeds it (one subscription per world runtime through WorldSource.chat)', () => {
    const qc = new QueryClient();
    const got: ChatSay[] = [];
    const off = createWorldSource(qc, { player: () => 'poofimafox' }).chat!((s) => got.push(s));
    const l = line({ kind: 'said', from: 'maid_chan_109', text: 'Chopping birch.' });
    applyLiveEvent(qc, 'feed', { lines: [l], last: l.seq });
    expect(got).toEqual([{ from: 'maid_chan_109', text: 'Chopping birch.', kind: 'said' }]);
    // the text chat's query cache still gets the line
    expect(qc.getQueryData<{ lines: FeedLine[] }>(liveKeys.feed)?.lines.map((x) => x.seq)).toContain(l.seq);
    off();
  });
});
