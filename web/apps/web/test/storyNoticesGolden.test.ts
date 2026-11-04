/**
 * Goldens for the notification rules (notices.ts): every condition text and its pawns/tab over the scripted colony's states and
 * hand-made ones that reach each branch, and a long run of messages through `addMessage` / `ingestAlerts` (levels, positions,
 * pawns, merging in the stack and the log, the log cap, unread). They pin the texts the page shows.
 */
import type { StoryColonyState, StoryPawn } from '@mineclaw/contracts';
import { beforeAll, describe, expect, it } from 'vitest';
import {
  addMessage,
  conditions,
  emptyNotices,
  ingestAlerts,
  LOG_CAP,
  type NoticeState,
} from '@/features/story/notices';
import { storyUiStates, type StoryUiStates } from './storyUiFixture';

let S: StoryUiStates;
beforeAll(() => {
  S = storyUiStates();
});

const plain = (s: NoticeState) => ({ ...s, seen: [...s.seen] });

describe('conditions', () => {
  it('every condition', () => {
    const w = S.worked;
    const p = (i: number, extra: Partial<StoryPawn> = {}): StoryPawn => ({ ...w.pawns[i % w.pawns.length]!, ...extra });
    const store = (contents: Array<[number, number, number]>, knownAt: number | null = 1) =>
      ({ ...(w.storages?.[0] ?? {}), pos: [0, 64, 0], knownAt, contents }) as never;
    const farm = w.zones.filter((z) => z.kind === 'farm');
    const blocked = (list: Array<[string, number]>) =>
      list.map(([reason, count], i) => ({ reason, count, pawns: [`p${i}`, `q${i}`] }));
    const cases: Record<string, StoryColonyState | undefined> = {
      undefined: undefined,
      founded: S.founded,
      ordered: S.ordered,
      worked: w,
      oneOffline: { ...w, pawns: [p(0, { online: false }), p(1)] },
      manyOffline: { ...w, pawns: [p(0, { online: false }), p(1, { online: false }), p(2)] },
      noBeds: { ...w, beds: undefined },
      oneHomeless: { ...w, pawns: [p(0), p(1)], beds: [{ pos: [0, 0, 0], pawn: p(0).name }] as never },
      manyHomeless: { ...w, pawns: [p(0), p(1), p(2)], beds: [{ pos: [0, 0, 0], pawn: null }] as never },
      unknownStores: { ...w, storages: [store([], null)] },
      noFood: { ...w, zones: [], storages: [store([[4, 0, 10]])], pawns: [p(0, { inventory: [] })] },
      foodInStore: { ...w, zones: [], storages: [store([[297, 0, 2]])] },
      foodEmptyStack: { ...w, zones: [], storages: [store([[297, 0, 0]])], pawns: [p(0, { inventory: [] })] },
      foodOnPawn: { ...w, zones: [], storages: [store([])], pawns: [p(0, { inventory: [[260, 0, 1]] as never })] },
      farmNoSeeds: { ...w, zones: farm, storages: [store([[297, 0, 2]])], pawns: [p(0, { inventory: [] })] },
      farmSeeds: {
        ...w,
        zones: farm,
        storages: [
          store([
            [297, 0, 2],
            [295, 0, 3],
          ]),
        ],
      },
      unstored: { ...w, unstoredKinds: ['logs', 'cobblestone'] },
      oneStuck: { ...w, pawns: [p(0, { stuck: true } as never), p(1, { stuck: true, online: false } as never)] },
      manyStuck: { ...w, pawns: [p(0, { stuck: true } as never), p(1, { stuck: true, online: true } as never)] },
      blockedOne: { ...w, blockedWork: blocked([['waiting: no bench', 1]]) },
      blockedTwo: {
        ...w,
        blockedWork: blocked([
          ['no tools', 2],
          ['stuck', 3],
          ['far away', 1],
        ]),
      },
      blockedThree: {
        ...w,
        blockedWork: blocked([
          ['no tools', 2],
          ['work not taken yet', 3],
          ['far away', 1],
          ['dark', 4],
          ['x', 0],
        ]),
      },
      blockedNone: {
        ...w,
        blockedWork: blocked([
          ['stuck', 2],
          ['work not taken yet', 1],
          ['no tools', 0],
        ]),
      },
      timeOff: { ...w, timeControl: { ...w.timeControl!, available: false } },
      timeOn: { ...w, timeControl: { ...w.timeControl!, available: true } },
      noTime: { ...w, timeControl: undefined },
    };
    const out: Record<string, unknown> = {};
    for (const [name, st] of Object.entries(cases)) {
      out[name] = conditions(st);
      out[`${name} op`] = conditions(st, { mainOp: true });
      out[`${name} not op`] = conditions(st, { mainOp: false });
      out[`${name} op null`] = conditions(st, { mainOp: null });
    }
    expect(out).toMatchSnapshot();
  });
});

describe('messages', () => {
  it('addMessage over a long run', () => {
    let s = emptyNotices();
    const trace: unknown[] = [];
    const step = (label: string, f: () => NoticeState) => {
      s = f();
      trace.push([label, plain(s)]);
    };
    const t0 = 1_762_000_000_000;
    step('info', () => addMessage(s, { level: 'info', text: 'Dara chopped a tree at 5, 64, 2' }, t0));
    step('same kind', () =>
      addMessage(s, { level: 'info', text: 'Dara chopped a tree at 6,64,2', pawn: 'dara' }, t0 + 10),
    );
    step('warn pos', () =>
      addMessage(s, { level: 'warn', text: 'cut failed 3 times', pos: [1, 2, 3], pawn: null }, t0 + 20, 2),
    );
    step('danger', () => addMessage(s, { level: 'danger', text: 'Gwen is starving' }, t0 + 30));
    step('ok scaled', () => addMessage(s, { level: 'ok', text: 'Built the hut' }, t0 + 40, 0.5));
    step('unknown level', () => addMessage(s, { level: 'shout', text: 'Something' }, t0 + 50));
    step('null level', () => addMessage(s, { level: null as never, text: 'Other' }, t0 + 60));
    step('alert fix', () =>
      addMessage(
        s,
        {
          level: 'warn',
          text: 'No fuel at the furnace',
          alert: {
            id: 'a1',
            level: 'warn',
            text: 'No fuel',
            at: t0,
            pawn: 'fenn',
            fix: { workbench: [4, 64, 4] },
          } as never,
        },
        t0 + 70,
      ),
    );
    step('alert merge keeps', () => addMessage(s, { level: 'warn', text: 'No fuel at the furnace' }, t0 + 80));
    step('page toast', () => addMessage(s, { level: 'info', text: 'Saved', stack: false, source: 'page' }, t0 + 90));
    step('log open', () =>
      addMessage(s, { level: 'info', text: 'Saved', stack: false, source: 'page' }, t0 + 95, 1, true),
    );
    step('log merge stale', () =>
      addMessage(s, { level: 'info', text: 'Dara chopped a tree at 7,64,2' }, t0 + 200_000),
    );
    step('pawn from alert', () =>
      addMessage(
        s,
        { level: 'info', text: 'hello', alert: { level: 'info', text: 'hello', pawn: 'ivo' } as never },
        t0 + 210_000,
      ),
    );
    // The log cap: many distinct kinds.
    let big = s;
    for (let i = 0; i < LOG_CAP + 5; i++)
      big = addMessage(
        big,
        { level: 'info', text: `kind ${'x'.repeat(i % 7)} ${String.fromCharCode(97 + (i % 26))}${Math.floor(i / 26)}` },
        t0 + 300_000 + i,
      );
    trace.push(['cap', big.log.length, big.log[0], big.log.at(-1), big.unread, big.nextId, big.stack.length]);
    expect(trace).toMatchSnapshot();
  });

  it('ingestAlerts', () => {
    const now = 1_762_000_000_000;
    const alerts = [
      { id: 1, level: 'warn', text: 'Old one', at: (now - 120_000) / 1000 },
      { id: 2, level: 'danger', text: 'Fresh one', at: now - 1000 },
      { level: 'info', text: 'No id', at: new Date(now - 2000).toISOString() },
      { level: 'info', text: 'Bad date', at: 'nonsense' },
      { level: 'info', text: 'No date' },
    ] as never[];
    const a = ingestAlerts(emptyNotices(), alerts, now);
    const b = ingestAlerts(a, alerts, now + 5);
    expect({
      a: plain(a),
      same: a === b,
      c: plain(ingestAlerts(a, alerts.slice(0, 1), now, 2, true)),
    }).toMatchSnapshot();
  });
});
