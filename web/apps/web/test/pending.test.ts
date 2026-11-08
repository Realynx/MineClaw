import type { FeedLine } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import {
  awaitingReply,
  createPending,
  isBotReply,
  onAccepted,
  onFailed,
  optimisticBubbles,
  reconcile,
  viewerSeqFrom,
} from '@/features/chat/pending';

const at = '2026-11-02T10:00:00+00:00';
const line = (seq: number, kind: FeedLine['kind'], extra: Partial<FeedLine> = {}): FeedLine => ({
  seq,
  at,
  bot: 'maid_chan',
  kind,
  from: kind === 'said' ? 'maid_chan' : 'poofimafox',
  text: `line ${seq}`,
  origin: kind === 'viewer' ? 'viewer' : 'game',
  ...extra,
});

describe('viewerSeqFrom', () => {
  it('prefers feedSeq (the contract: id echoes the request id, feedSeq is the viewer line seq)', () => {
    expect(viewerSeqFrom({ accepted: true, id: 'req00000001', feedSeq: 41 }, 'req00000001')).toBe(41);
  });
  it('tolerates a numeric id as the feed seq when feedSeq is absent', () => {
    expect(viewerSeqFrom({ accepted: true, id: 17 }, 'abcdabcd')).toBe(17);
    expect(viewerSeqFrom({ accepted: true, id: '18' }, 'abcdabcd')).toBe(18);
  });
  it('treats an echoed request id as a correlation id, not a seq', () => {
    expect(viewerSeqFrom({ accepted: true, id: 'abcdabcd' }, 'abcdabcd')).toBeNull();
    expect(viewerSeqFrom({ accepted: true, id: '12345678' }, '12345678')).toBeNull();
  });
});

describe('reconcile', () => {
  it('is a fixed point: an echoed message still waiting for its reply keeps its identity (no setState loop)', () => {
    const p = onAccepted(createPending('maid_chan', 'hi', 'req00000001', 0), { accepted: true, id: 'req00000001', feedSeq: 5 });
    const lines = [line(5, 'viewer', { requestId: 'req00000001' })];
    const once = reconcile([p], lines).pending;
    expect(once[0]).toMatchObject({ echoed: true, viewerSeq: 5 });
    const twice = reconcile(once, lines).pending;
    expect(twice[0]).toBe(once[0]);
  });

  it('hides the optimistic bubble once the viewer line arrives (matched by requestId) and resolves on the reply', () => {
    let p = onAccepted(createPending('maid_chan', 'set it to day', 'reqAAAAAAAA', 0), { accepted: true, id: 'reqAAAAAAAA' });
    expect(p.state).toBe('awaiting');
    expect(p.viewerSeq).toBeNull();

    let r = reconcile([p], [line(10, 'chat'), line(11, 'viewer', { requestId: 'reqAAAAAAAA' })]);
    expect(r.resolved).toEqual([]);
    [p] = r.pending as [typeof p];
    expect(p.echoed).toBe(true);
    expect(p.viewerSeq).toBe(11);
    expect(optimisticBubbles([p])).toEqual([]);
    expect(awaitingReply([p])).toBe(true);

    r = reconcile([p], [line(10, 'chat'), line(11, 'viewer', { requestId: 'reqAAAAAAAA' }), line(12, 'said')]);
    expect(r.resolved).toEqual(['reqAAAAAAAA']);
    expect(r.pending).toEqual([]);
  });

  it('matches the viewer line by feedSeq from the chat answer', () => {
    const p = onAccepted(createPending('maid_chan', 'hi', 'reqBBBBBBBB'), { accepted: true, id: 'reqBBBBBBBB', feedSeq: 20 });
    const r = reconcile([p], [line(20, 'viewer')]);
    expect(r.pending[0]?.echoed).toBe(true);
  });

  it('ignores bot lines from before our viewer line', () => {
    const p = onAccepted(createPending('maid_chan', 'hi', 'reqCCCCCCCC'), { accepted: true, id: 'x', feedSeq: 30 });
    const r = reconcile([p], [line(29, 'said'), line(30, 'viewer')]);
    expect(r.resolved).toEqual([]);
  });

  it('resolves several quick messages with one merged reply (debounced turn)', () => {
    const a = onAccepted(createPending('maid_chan', 'one', 'reqDDDDDDD1'), { accepted: true, id: 'reqDDDDDDD1', feedSeq: 40 });
    const b = onAccepted(createPending('maid_chan', 'two', 'reqDDDDDDD2'), { accepted: true, id: 'reqDDDDDDD2', feedSeq: 41 });
    const r = reconcile([a, b], [line(40, 'viewer'), line(41, 'viewer'), line(42, 'said')]);
    expect(r.resolved.sort()).toEqual(['reqDDDDDDD1', 'reqDDDDDDD2']);
  });

  it('resolves on replyTo even when the reply seq is not after ours', () => {
    const p = onAccepted(createPending('maid_chan', 'q', 'reqEEEEEEEE'), { accepted: true, id: 'reqEEEEEEEE', feedSeq: 50 });
    const r = reconcile([p], [line(50, 'viewer'), line(49, 'said', { replyTo: 50 })]);
    expect(r.resolved).toEqual(['reqEEEEEEEE']);
  });

  it('does not treat a player whispering TO the bot as its reply', () => {
    const incoming = line(61, 'whisper', { from: 'kiwi' });
    const outgoing = line(62, 'whisper', { from: 'maid_chan' });
    expect(isBotReply(incoming, 'maid_chan')).toBe(false);
    expect(isBotReply(outgoing, 'maid_chan')).toBe(true);
    const p = onAccepted(createPending('maid_chan', 'q', 'reqFFFFFFFF'), { accepted: true, id: 'reqFFFFFFFF', feedSeq: 60 });
    expect(reconcile([p], [line(60, 'viewer'), incoming]).resolved).toEqual([]);
    expect(reconcile([p], [line(60, 'viewer'), incoming, outgoing]).resolved).toEqual(['reqFFFFFFFF']);
  });

  it('only resolves on lines of the target bot (or its aliases)', () => {
    const p = onAccepted(createPending('maid_chan_67', 'q', 'reqGGGGGGGG'), { accepted: true, id: 'reqGGGGGGGG', feedSeq: 70 });
    const other = line(71, 'said');
    expect(reconcile([p], [line(70, 'viewer', { bot: 'maid_chan_67' }), other]).resolved).toEqual([]);
    const alias = line(72, 'said', { bot: '67' });
    expect(reconcile([p], [line(70, 'viewer', { bot: 'maid_chan_67' }), alias], ['67']).resolved).toEqual(['reqGGGGGGGG']);
  });

  it('keeps failed and rate-limited messages visible for a retry', () => {
    const f = onFailed(createPending('maid_chan', 'q', 'reqHHHHHHHH'), 'Not sent');
    const rl = onFailed(createPending('maid_chan', 'q2', 'reqIIIIIIII'), 'Slow down', 5000, 1000);
    expect(rl.state).toBe('rate_limited');
    expect(rl.retryAt).toBe(6000);
    expect(optimisticBubbles([f, rl])).toHaveLength(2);
    expect(awaitingReply([f, rl])).toBe(false);
    const r = reconcile([f], [line(80, 'said')]);
    expect(r.pending).toHaveLength(1);
  });

  it('marks a not-accepted answer as failed with the note', () => {
    const p = onAccepted(createPending('maid_chan', 'q', 'reqJJJJJJJJ'), { accepted: false, id: 'reqJJJJJJJJ', note: 'viewer chat disabled' });
    expect(p.state).toBe('failed');
    expect(p.error).toBe('viewer chat disabled');
  });
});


it('does not let an earlier failed delivery echo or intervening reply resolve a retried directive', () => {
  const pending = onAccepted(createPending('maid_chan', 'Set the schedule', 'retry-request-1'), { accepted: true, id: 'dir1', feedSeq: 12 });
  const previous = [line(10, 'viewer', { requestId: 'retry-request-1' }), line(11, 'said')];
  const waiting = reconcile([pending], previous);
  expect(waiting.resolved).toEqual([]);
  expect(waiting.pending[0]?.viewerSeq).toBe(12);
  expect(waiting.pending[0]?.echoed).toBe(false);
  const withEcho = reconcile(waiting.pending, [...previous, line(12, 'viewer', { requestId: 'retry-request-1' })]);
  expect(withEcho.resolved).toEqual([]);
  expect(withEcho.pending[0]?.viewerSeq).toBe(12);
  expect(withEcho.pending[0]?.echoed).toBe(true);
  expect(reconcile(withEcho.pending, [...previous, line(12, 'viewer', { requestId: 'retry-request-1' }), line(13, 'said')]).resolved).toEqual(['retry-request-1']);
});
