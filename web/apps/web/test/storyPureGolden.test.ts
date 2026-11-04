/**
 * Goldens for the Story page's smaller pure rules: the client's delta merge (mergeColony / mergeGhosts over the scripted colony's
 * real deltas and hand-made ones), dispatch, pawn moods, failed-label clusters, chest badges, bench station lines, bill update
 * bodies, holograms, the water placement preview, zone labels and outlines, the zone drawing (a recording canvas) and the
 * material lists. Bodies are pinned as JSON text so key order is pinned too.
 */
import { createHash } from 'node:crypto';
import type { StoryBill, StoryColonyState, StoryZone } from '@mineclaw/contracts';
import { zoneCellSet } from '@mineclaw/contracts';
import { beforeAll, describe, expect, it } from 'vitest';
import { hologramCells } from '@/features/story/bodyLook';
import { chestBadge } from '@/features/story/chestBadges';
import { clusterFailed, type FailedMark } from '@/features/story/failedLabels';
import { materialsFor } from '@/features/story/materials';
import { dispatch, mergeColony, mergeGhosts, pawnMood } from '@/features/story/model';
import { draftOf, stationLines, updateBillBody } from '@/features/story/storage';
import { pendingHolo } from '@/features/story/WaterTools';
import { drawZones, outlineOf, zoneLabel } from '@/features/story/zones/zoneDraw';
import { storyUiStates, type StoryUiStates } from './storyUiFixture';

let S: StoryUiStates;
beforeAll(() => {
  S = storyUiStates();
});

const json = (v: unknown) => JSON.stringify(v);
/** A long JSON text's digest (whole colony states would make the snapshot huge). */
const hash = (s: string) => `${s.length}:${createHash('sha256').update(s).digest('hex').slice(0, 16)}`;

describe('the delta merge', () => {
  it('mergeColony over the colony’s deltas and edge cases', () => {
    let held: StoryColonyState | undefined = undefined;
    const trace: string[] = [];
    for (const d of [S.founded, S.ordered, ...S.deltas]) {
      held = mergeColony(held, d);
      trace.push(hash(json(held)));
    }
    const o = S.ordered;
    const same = (d: Partial<StoryColonyState>) => mergeColony(o, { ...o, full: false, ...d });
    const cases = {
      older: same({ rev: o.rev - 5 }) === o,
      muchOlder: hash(json(same({ rev: o.rev - 2000 }))),
      timeOnly: hash(json(same({ rev: o.rev, timeOfDay: (o.timeOfDay ?? 0) + 100, hour: 9, at: 'later' }))),
      timeSame: same({ rev: o.rev, timeOfDay: o.timeOfDay, timeFrozen: o.timeFrozen }) === o,
      timeNull: same({ rev: o.rev, timeOfDay: null }) === o,
      frozen: hash(
        json(same({ rev: o.rev, timeOfDay: o.timeOfDay, timeFrozen: true, hour: undefined, at: undefined })),
      ),
      removed: hash(
        json(
          mergeColony(o, {
            ...S.deltas[0]!,
            removed: {
              pawns: [o.pawns[0]!.name],
              zones: [o.zones[0]!.id],
              builds: [(o.builds ?? [])[0]?.id ?? 'x'],
              storages: [[0, 64, -2]],
              alerts: ['nope'],
            },
          } as never),
        ),
      ),
      full: mergeColony(o, { ...S.deltas[0]!, full: true }) === S.deltas[0],
      none: mergeColony(undefined, o) === o,
    };
    expect({ trace, cases }).toMatchSnapshot();
  });

  it('mergeGhosts', () => {
    const g = (rev: number, blocks: Array<[number, number, number, number, number, string | number, string]>) =>
      ({ rev, blocks }) as NonNullable<StoryColonyState['ghosts']>;
    const prev = g(3, [
      [0, 64, 0, 1, 0, 'b1', 'queued'],
      [1, 64, 0, 1, 0, 'b1', 'queued'],
      [5, 64, 5, 4, 0, 2, 'queued'],
    ]);
    const next = g(5, [
      [1, 64, 0, 1, 0, 'b1', 'placed'],
      [9, 64, 9, 4, 0, 'b3', 'queued'],
    ]);
    expect({
      noPrev: mergeGhosts(undefined, next) === next,
      untouched: mergeGhosts(prev, g(9, [])) === prev,
      untouchedGone: mergeGhosts(prev, undefined, ['zz']) === prev,
      merged: json(mergeGhosts(prev, next)),
      gone: json(mergeGhosts(prev, undefined, ['b1'])),
      goneNumber: json(mergeGhosts(prev, g(1, []), [2])),
      both: json(mergeGhosts(prev, next, ['b1', 2])),
    }).toMatchSnapshot();
  });
});

describe('dispatch and moods', () => {
  it('dispatch over selections and calls', () => {
    const orders = { route: 'story.orders' as const, body: { order: { kind: 'hold' } } };
    const designations = { route: 'story.designations' as const, body: { op: 'add' } };
    const directive = { kind: 'guard' as const };
    const out: Record<string, string> = {};
    for (const [sn, sel] of Object.entries({
      none: { pawns: [], ai: [] },
      pawn: { pawns: ['a'], ai: [] },
      pawns: { pawns: ['a', 'b'], ai: [] },
      ai: { pawns: [], ai: ['maid_chan'] },
      both: { pawns: ['a', 'b', 'c'], ai: ['maid_chan', 'maid_chan_2'] },
    }))
      for (const [cn, call] of Object.entries({ orders, designations, none: null }))
        for (const [dn, d] of Object.entries({ directive, none: null }))
          out[`${sn} ${cn} ${dn}`] = json(
            dispatch(sel, call, d, 'req-1', cn === 'designations' ? 'designated' : undefined),
          );
    expect(out).toMatchSnapshot();
  });

  it('pawnMood', () => {
    const out: string[] = [];
    for (const online of [true, false, undefined])
      for (const state of ['idle', 'working', 'offline', 'fleeing', 'sleeping', '', undefined])
        for (const [hp, food] of [
          [20, 20],
          [8, 20],
          [9, 4],
          [9, 5],
          [undefined, undefined],
        ])
          for (const drafted of [true, false])
            out.push(`${online} ${state} ${hp} ${food} ${drafted}: ${pawnMood({ online, state, hp, food, drafted })}`);
    expect(out).toMatchSnapshot();
  });
});

describe('labels and badges', () => {
  it('clusterFailed', () => {
    const marks: FailedMark[] = [];
    const reasons = [
      "can't reach dirt at the bottom of the pit",
      'reported done, but the grass is still there',
      "won't dig: someone is standing on it",
      'double_plant at 5,64,2 is busy: another hive bot is working on that part right now',
      '',
    ];
    let id = 0;
    for (let i = 0; i < 40; i++)
      marks.push({
        id: id++,
        kind: ['mine', 'cut', 'chop', 'weird'][i % 4]!,
        reason: reasons[i % 5]!,
        cell: [(i * 7) % 13, 64 + (i % 3) * 2, (i * 5) % 11],
        ...(i % 6 === 0 ? { tone: 'wait' as const } : {}),
      });
    expect({ two: clusterFailed(marks), four: clusterFailed(marks, 4), none: clusterFailed([]) }).toMatchSnapshot();
  });

  it('chestBadge', () => {
    const st = S.worked.storages?.[0];
    const base = {
      ...(st ?? {}),
      chest: [1, 64, -2],
      label: null,
      freeSlots: 10,
      knownAt: 1000,
      doubleWith: null,
    } as never as NonNullable<typeof st>;
    const sum = (extra: object) =>
      ({
        pos: [1, 64, -2],
        dim: 0,
        kind: 'chest',
        size: 27,
        used: 9,
        total: 100,
        top: [],
        seenAt: 2000,
        source: 'seen',
        cells: [],
        ...extra,
      }) as never;
    const out: Record<string, unknown> = {
      nothing: chestBadge(undefined, undefined, S.worked.categories),
      looked: chestBadge(undefined, sum({}), S.worked.categories),
      lookedUnknown: chestBadge(undefined, sum({ source: 'unknown' }), S.worked.categories),
      lookedEmptySize: chestBadge(undefined, sum({ size: 0 }), null),
      ledger: chestBadge(base, undefined, S.worked.categories),
      ledgerDouble: chestBadge(
        { ...base, doubleWith: [2, 64, -2], freeSlots: 60 } as never,
        undefined,
        S.worked.categories,
      ),
      ledgerNone: chestBadge({ ...base, freeSlots: null }, undefined, undefined),
      summaryNewer: chestBadge(base, sum({ seenAt: 5000 }), S.worked.categories),
      summaryOlder: chestBadge(base, sum({ seenAt: 10 }), S.worked.categories),
      summaryOver: chestBadge(base, sum({ used: 40, seenAt: 'x' }), S.worked.categories),
      labelled: chestBadge({ ...base, label: 'Ores and other shiny things' }, undefined, S.worked.categories),
      filtered: chestBadge(
        { ...base, filter: { allow: ['wood', 'food', 'tools', 'stone'], deny: [] } },
        undefined,
        S.worked.categories,
      ),
      isoTimes: chestBadge({ ...base, knownAt: '2026-01-01T00:00:00Z' }, sum({ seenAt: 1 }), S.worked.categories),
    };
    expect(out).toMatchSnapshot();
  });

  it('stationLines', () => {
    const b = (kind: string, station: object | null) => ({ kind, station }) as never;
    expect({
      none: stationLines(b('crafting_table', null)),
      gone: stationLines(b('anvil', { present: false, condition: 'damaged' })),
      shelves: stationLines(b('enchanting_table', { present: true, bookshelves: 7 })),
      offer: stationLines(b('enchanting_table', { bookshelves: 15, maxBookshelves: 15, topOffer: [8, 30] })),
      offerSame: stationLines(b('enchanting_table', { bookshelves: 0, topOffer: [3, 3] })),
      offerHalf: stationLines(b('enchanting_table', { bookshelves: 2, topOffer: [3] })),
      brewDry: stationLines(b('brewing_stand', {})),
      brew: stationLines(b('brewing_stand', { water: ['cauldron at 1,64,2'], fuel: 1 })),
      brewMany: stationLines(b('brewing_stand', { water: ['a', 'b'], fuel: 12 })),
    }).toMatchSnapshot();
  });

  it('updateBillBody', () => {
    const bills = (S.worked.workbenches ?? []).flatMap((w) => w.bills) as StoryBill[];
    const bill: StoryBill = {
      ...bills[0]!,
      minSkill: 3,
      prefer: 'sharpness',
      preferOnly: true,
      fuel: 'coal',
      option: 2,
    };
    const out: Record<string, string> = {};
    for (const [i, b] of [...bills, bill].entries()) {
      const d = draftOf(b);
      out[`${i} same`] = json(updateBillBody(b, d));
      out[`${i} edits`] = json(
        updateBillBody(b, { ...d, target: d.target + 3, mode: 'until', paused: !d.paused, radius: 200, pawn: 'dara' }),
      );
      out[`${i} cleared`] = json(
        updateBillBody(b, { ...d, minSkill: null, prefer: null, preferOnly: false, fuel: null }),
      );
      out[`${i} set`] = json(
        updateBillBody(b, {
          ...d,
          minSkill: 5,
          prefer: 'efficiency',
          preferOnly: true,
          fuel: 'planks',
          option: 9,
          store: { chest: [1, 2, 3] },
        }),
      );
      out[`${i} forever`] = json(updateBillBody(b, { ...d, mode: 'forever' }));
    }
    expect(out).toMatchSnapshot();
  });
});

describe('holograms and zones', () => {
  it('hologramCells', () => {
    const ghosts = {
      rev: 4,
      blocks: [
        [0, 64, 0, 1, 0, 'b1', 'queued'],
        [1, 64, 0, 0, 0, 'b1', 'queued'],
        [2, 64, 0, 4, 0, 'b2', 'placed'],
        [3, 64, 0, 4, 2, 'b2', 'blocked', 'dig'],
        [4, 64, 0, 0, 0, 'b3', 'queued', 'place'],
        [5, 64, 0, 5, 1, 'b4', 'done'],
        [6, 64, 0, 5, 1, 'b9', 'queued'],
      ],
    };
    const builds = [
      { id: 'b1', status: 'active' },
      { id: 'b2', status: 'paused' },
      { id: 'b3', status: 'blocked' },
      { id: 'b4', status: 'done' },
    ];
    expect({
      none: hologramCells(undefined),
      empty: hologramCells({ ghosts: undefined, builds: undefined }),
      sim: hologramCells(S.worked),
      hand: hologramCells({ ghosts, builds } as never),
    }).toMatchSnapshot();
  });

  it('pendingHolo', () => {
    const p = (items: object[], flow: object | null) => ({ label: 'x', items, cells: 1, flow, warnings: [] }) as never;
    expect({
      empty: pendingHolo(p([], null)),
      water: pendingHolo(
        p(
          [
            { kind: 'remove', area: [0, 63, 0, 2, 63, 0], material: 'stone', group: 'g' },
            {
              kind: 'water',
              blocks: [
                [0, 63, 0],
                [1, 63, 0],
              ],
              material: 'water',
              group: 'g',
            },
            { kind: 'water', blocks: [[5, 63, 5]], material: 'lava' },
            { kind: 'water' },
            { kind: 'wall', area: [0, 0, 0, 1, 0, 0], material: 'stone' },
          ],
          {
            cells: [
              [2, 63, 0],
              [3, 63, 0],
            ],
            spread: 2,
          },
        ),
      ),
      capped: pendingHolo(
        p([{ kind: 'remove', area: [0, 0, 0, 70, 0, 70], material: 'stone' }], { cells: [[1, 1, 1]] }),
      ).length,
    }).toMatchSnapshot();
  });

  it('zoneLabel, outlineOf and drawZones', () => {
    const zones = [...S.worked.zones];
    const farm = zones.find((z) => z.kind === 'farm')!;
    const hand: StoryZone[] = [
      ...zones,
      { ...farm, id: 'f2', name: 'Fields', activePlant: 'nope' },
      { ...farm, id: 'f3', name: null, activePlant: null },
      { ...farm, id: 'f4', name: null, plant: null, bills: undefined },
      { ...farm, id: 'h1', kind: 'home', name: null, label: 'Shed' },
      { ...farm, id: 'h2', kind: 'home', name: null, label: null },
      { ...farm, id: 'k1', kind: 'forbidden', name: 'Pit', rows: undefined, area: [-3, 63, -3, -1, 63, -2] },
      { ...farm, id: 'c1', kind: 'chop', name: null },
    ] as StoryZone[];
    const labels = hand.map((z, i) => [zoneLabel(z, i + 1, S.worked.plants), zoneLabel(z, i + 1, null)]);
    const outlines = hand.map((z) => outlineOf(zoneCellSet(z)));
    const log: string[] = [];
    const ctx = new Proxy(
      {},
      {
        get: (_t, k) =>
          k === 'measureText'
            ? (s: string) => ({ width: s.length * 6 })
            : (...a: unknown[]) => {
                log.push(`${String(k)}(${a.map((x) => json(x)).join(',')})`);
              },
        set: (_t, k, v) => {
          log.push(`${String(k)}=${json(v)}`);
          return true;
        },
      },
    ) as unknown as CanvasRenderingContext2D;
    const proj = (x: number, y: number, z: number) => (x < -2 && z < -2 ? null : { x: x * 10 + z * 3, y: z * 10 - y });
    drawZones(ctx, hand, proj, 4, (z, n) => zoneLabel(z, n, S.worked.plants));
    const far = log.length;
    drawZones(ctx, hand, proj, 2, (z, n) => zoneLabel(z, n, S.worked.plants));
    expect({ labels, outlines, log, far }).toMatchSnapshot();
  });

  it('materialsFor', () => {
    const out: Record<string, unknown> = {};
    for (const tool of ['wall', 'floor', 'roof', 'door', 'stairs', 'fence', 'block'] as const) {
      out[`${tool} palette`] = materialsFor(tool, S.architect.entries);
      out[`${tool} none`] = materialsFor(tool, null, new Map([['4:0', 12]]));
      out[`${tool} empty`] = materialsFor(tool, []);
    }
    out.kindsFallback = materialsFor('wall', [{ ...S.architect.entries[0]!, kinds: [], kind: 'wall' }]);
    expect(out).toMatchSnapshot();
  });
});
