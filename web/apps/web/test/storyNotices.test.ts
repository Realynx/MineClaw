import type { StoryAlert, StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import {
  addMessage,
  conditions,
  dismiss,
  dismissAll,
  emptyNotices,
  expire,
  extend,
  FRESH_MS,
  ingestAlerts,
  LOG_CAP,
  normalize,
  noticeKey,
  posIn,
  STACK_CAP,
  TTL,
  visible,
} from '@/features/story/notices';

const T0 = 1_800_000_000_000;
const alert = (i: number, text: string, level = 'warn', at = T0): StoryAlert => ({ id: `a${i}`, at: new Date(at).toISOString(), level, text });

describe('normalising and coalescing', () => {
  it('takes positions and numbers out of a message kind', () => {
    expect(normalize('Cut at 5,64,2 failed 3 times: double_plant at 5, 64, -2 is busy')).toBe('cut at # failed # times: double_plant at # is busy');
    expect(noticeKey('warn', 'Cut at 1,2,3 failed 1 times')).toBe(noticeKey('warn', 'Cut at -40,70,9 failed 12 times'));
    expect(noticeKey('warn', 'x')).not.toBe(noticeKey('info', 'x'));
    expect(posIn('dig at -4, 63, 12 failed')).toEqual([-4, 63, 12]);
    expect(posIn('no place here')).toBeNull();
  });

  it('20 failures of the same kind are one message with a count, its timer refreshed', () => {
    let s = emptyNotices();
    const alerts = Array.from({ length: 20 }, (_, i) => alert(i, `Cut at ${i},64,${-i} failed ${i + 1} times: busy: claimed by maid_chan_67`));
    for (let i = 0; i < 20; i++) s = ingestAlerts(s, [alerts[i]!], T0 + i * 1000);
    expect(s.stack).toHaveLength(1);
    expect(s.stack[0]).toMatchObject({ count: 20, text: alerts[19]!.text, pos: [19, 64, -19] });
    expect(s.stack[0]!.expires).toBe(T0 + 19_000 + TTL.warn);
    // the log keeps one entry of the kind too, with the count
    expect(s.log).toHaveLength(1);
    expect(s.log[0]!.count).toBe(20);
    expect(s.unread).toBe(1);
  });

  it('never shows an alert twice after a poll (dedupe by id), and keeps old ones out of the stack', () => {
    let s = emptyNotices();
    const a = [alert(1, 'Ann is idle'), alert(2, 'Bob is idle', 'warn', T0 - FRESH_MS - 1000)];
    s = ingestAlerts(s, a, T0);
    expect(s.stack.map((n) => n.text)).toEqual(['Ann is idle']);
    expect(s.log).toHaveLength(2);
    const again = ingestAlerts(s, a, T0 + 500);
    expect(again).toBe(s); // nothing new: the same state
  });

  it("the page's own toasts are logged, not stacked", () => {
    const s = addMessage(emptyNotices(), { level: 'ok', text: 'Blueprint cancelled', stack: false, source: 'page' }, T0);
    expect(s.stack).toHaveLength(0);
    expect(s.log[0]).toMatchObject({ text: 'Blueprint cancelled', source: 'page' });
  });

  it('keeps the log to its cap', () => {
    let s = emptyNotices();
    for (let i = 0; i < LOG_CAP + 30; i++) s = addMessage(s, { level: 'info', text: `event ${String.fromCharCode(65 + (i % 26))}${'x'.repeat(i)}` }, T0 + i);
    expect(s.log).toHaveLength(LOG_CAP);
  });
});

describe('expiry, the cap, dismissing', () => {
  it('info expires after 6 s, warn after 12 s, danger stays; not while held', () => {
    let s = emptyNotices();
    s = addMessage(s, { level: 'info', text: 'a pawn levelled up' }, T0);
    s = addMessage(s, { level: 'warn', text: 'Ann is idle' }, T0);
    s = addMessage(s, { level: 'danger', text: 'Ann is starving' }, T0);
    expect(expire(s, T0 + 5000).stack).toHaveLength(3);
    expect(expire(s, T0 + 6000).stack.map((n) => n.level)).toEqual(['warn', 'danger']);
    expect(expire(s, T0 + 6000, true).stack).toHaveLength(3); // the pointer is over the stack
    expect(expire(s, T0 + 12_000).stack.map((n) => n.level)).toEqual(['danger']);
    expect(expire(s, T0 + 3_600_000).stack.map((n) => n.level)).toEqual(['danger']);
    // after a hover, at least a few seconds more
    const later = extend(s, T0 + 5900);
    expect(expire(later, T0 + 6100).stack).toHaveLength(3);
    // a shorter test lifetime
    const fast = addMessage(emptyNotices(), { level: 'warn', text: 'x' }, T0, 0.01);
    expect(expire(fast, T0 + 200).stack).toHaveLength(0);
  });

  it('shows at most four, newest first, and counts the rest', () => {
    let s = emptyNotices();
    for (let i = 0; i < 7; i++) s = addMessage(s, { level: 'warn', text: `kind ${'abcdefg'[i]}` }, T0 + i);
    const v = visible(s);
    expect(v.shown).toHaveLength(STACK_CAP);
    expect(v.shown[0]!.text).toBe('kind g');
    expect(v.more).toBe(3);
    expect(dismiss(s, v.shown[0]!.key).stack).toHaveLength(6);
    expect(dismissAll(s).stack).toHaveLength(0);
    expect(dismissAll(s).log).toHaveLength(7); // the log keeps them
  });
});

describe('conditions from the colony state', () => {
  const pawn = (name: string, extra: object = {}) => ({ name, display: name.toUpperCase(), online: true, inventory: [], ...extra });
  const st = (o: object) => ({ pawns: [], zones: [], alerts: [], ...o }) as unknown as StoryColonyState;

  it('offline pawns, pawns without a bed, main not op', () => {
    const c = conditions(
      st({
        pawns: [pawn('ann'), pawn('bob'), pawn('cid', { online: false })],
        beds: [{ pos: [0, 64, 0], pawn: 'ann' }],
        timeControl: { available: false },
      }),
    );
    expect(c.map((x) => [x.key, x.text])).toEqual([
      ['offline', 'CID is offline'],
      ['bed', 'BOB has no bed'],
      ['op', "Main isn't op: time of day and commands are off"],
    ]);
    expect(c[1]!.pawns).toEqual(['bob']);
  });

  it('no food and no seeds, once storage contents are known; they clear when stocked', () => {
    const chest = (contents: number[][]) => ({ chest: [0, 64, 0], priority: 'normal', filter: { allow: [], deny: [] }, contents, knownAt: T0 });
    const zones = [{ id: 1, kind: 'farm', area: [0, 64, 0, 4, 64, 4] }];
    expect(conditions(st({ storages: [chest([[4, 0, 64]])], zones })).map((x) => x.key)).toEqual(['food', 'seeds']);
    expect(conditions(st({ storages: [chest([[297, 0, 3], [295, 0, 10]])], zones }))).toEqual([]);
    // contents never seen: nothing said
    expect(conditions(st({ storages: [{ ...chest([]), knownAt: null }], zones }))).toEqual([]);
    // food a pawn carries counts
    expect(conditions(st({ storages: [chest([])], pawns: [pawn('ann', { inventory: [[297, 0, 1]] })] })).map((x) => x.key)).toEqual([]);
    expect(conditions(undefined)).toEqual([]);
  });

  it('kinds with no storage and stuck pawns are conditions while they last', () => {
    const c = conditions(st({ unstoredKinds: ['Oak Wood', 'Dirt'], pawns: [pawn('ann', { stuck: 'stuck in a pit at 1,62,1' }), pawn('bob')] }));
    expect(c.map((x) => [x.key, x.text])).toEqual([
      ['unstored', 'No storage for: Oak Wood, Dirt'],
      ['stuck', 'ANN is stuck'],
    ]);
    expect(c[1]!.pawns).toEqual(['ann']);
    expect(conditions(st({ unstoredKinds: [], pawns: [pawn('ann')] }))).toEqual([]);
  });
});
