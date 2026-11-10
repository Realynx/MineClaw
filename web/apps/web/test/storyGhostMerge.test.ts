/**
 * The Controller merges every `story` delta's ghosts into what it holds. With a strip mine queued that is ~47k ghosts, so the
 * merge keeps an index with the ghosts it made and touches only the delta's cells. These tests hold it to the plain merge it
 * replaced (a map of every ghost by "<build>|x,y,z") over seeded deltas, and check that a delta no longer reads every held ghost.
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { hologramCells } from '@/features/story/bodyLook';
import { ghostsLowFirst } from '@/features/story/mapDraw';
import { mergeColony, mergeGhosts } from '@/features/story/model';
import { plainMergeGhosts as plainMerge, type Ghost, type Ghosts } from './ghostMergeReference';

/** A small deterministic generator (mulberry32). */
function rng(seed: number): () => number {
  let a = seed;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const STATES = ['todo', 'leased', 'done', 'blocked'];

describe('mergeGhosts with its index', () => {
  it('gives what the plain merge gives, over seeded deltas, removals and duplicates', () => {
    const r = rng(7);
    const pick = <T>(xs: readonly T[]) => xs[Math.floor(r() * xs.length)]!;
    const builds: Array<string | number> = ['b1', 'b2', 3, 'b4', '5'];
    const ghost = (): Ghost =>
      [
        Math.floor(r() * 6) - (r() < 0.1 ? 3_000_000 : 0), // now and then beyond the packed range
        60 + Math.floor(r() * 3),
        Math.floor(r() * 6),
        r() < 0.5 ? 0 : 4,
        0,
        pick(builds),
        pick(STATES),
        r() < 0.5 ? 'dig' : 'place',
      ] as Ghost;
    for (let run = 0; run < 40; run++) {
      // A whole state may hold the same cell twice; the merge keeps one.
      let held: Ghosts = { rev: 1, blocks: Array.from({ length: Math.floor(r() * 40) }, ghost) };
      let plain: Ghosts = held;
      for (let step = 0; step < 30; step++) {
        const next =
          r() < 0.2 ? undefined : { rev: step + 2, blocks: Array.from({ length: Math.floor(r() * 5) }, ghost) };
        const gone = r() < 0.15 ? [pick(builds), 'zz'] : null;
        const removed =
          r() < 0.3
            ? [
                ...plain.blocks.slice(0, 2).map((g) => `${g[5]}|${g[0]},${g[1]},${g[2]}`),
                'b1|9,9,9',
                'nonsense',
                'b1|1.0,60,0',
              ]
            : null;
        const a = mergeGhosts(held, next, gone, removed)!;
        const b = plainMerge(plain, next, gone, removed)!;
        expect(a.blocks).toEqual(b.blocks);
        expect(a.rev).toBe(b.rev);
        expect(a === held).toBe(b === plain);
        held = a;
        plain = b;
      }
    }
  });

  it('a delta reads only its own ghosts, not every one held', () => {
    let reads = 0;
    const counted = (g: Ghost): Ghost =>
      new Proxy(g, {
        get(t, k, rcv) {
          if (typeof k === 'string' && /^\d+$/.test(k)) reads++;
          return Reflect.get(t, k, rcv) as unknown;
        },
      });
    const blocks: Ghost[] = [];
    for (let x = 0; x < 100; x++)
      for (let z = 0; z < 100; z++) blocks.push(counted([x, 17, z, 1, 0, 'b1', 'todo', 'dig'] as Ghost));
    const delta = (rev: number, x: number): StoryColonyState =>
      ({
        id: 's',
        rev,
        since: rev - 1,
        full: false,
        at: rev,
        ghosts: { rev, blocks: [[x, 17, 0, 1, 0, 'b1', 'done', 'dig']] },
      }) as unknown as StoryColonyState;
    let st = { id: 's', rev: 1, full: true, at: 1, ghosts: { rev: 1, blocks } } as unknown as StoryColonyState;
    st = mergeColony(st, delta(2, 0)); // the first delta indexes what the whole state brought
    reads = 0;
    for (let i = 1; i <= 10; i++) st = mergeColony(st, delta(2 + i, i));
    expect(reads).toBe(0);
    expect(st.ghosts!.blocks).toHaveLength(10_000);
    expect(st.ghosts!.blocks[100 * 5]![6]).toBe('done'); // (5, 17, 0) in its place
    // A removed ghost is found through the index too.
    st = mergeColony(st, { ...delta(20, 0), ghosts: undefined, removed: { ghosts: ['b1|99,17,99'] } });
    expect(st.ghosts!.blocks).toHaveLength(9_999);
  });

  it('the index stays with the ghosts it was made for: merging an older state again is still right', () => {
    const g = (x: number, s: string): Ghost => [x, 64, 0, 4, 0, 'b', s, 'place'] as Ghost;
    const base: Ghosts = { rev: 1, blocks: [g(0, 'todo'), g(1, 'todo')] };
    const a = mergeGhosts(base, { rev: 2, blocks: [g(0, 'leased')] })!;
    const b = mergeGhosts(base, { rev: 2, blocks: [g(2, 'todo')] })!;
    const c = mergeGhosts(a, { rev: 3, blocks: [g(1, 'done'), g(3, 'todo'), g(4, 'todo')] })!;
    expect(a.blocks.map((x) => x[6])).toEqual(['leased', 'todo']);
    expect(b.blocks.map((x) => [x[0], x[6]])).toEqual([
      [0, 'todo'],
      [1, 'todo'],
      [2, 'todo'],
    ]);
    expect(c.blocks.map((x) => x[6])).toEqual(['leased', 'done', 'todo', 'todo']);
    // `a` handed its index on to `c` (where cell 4 sits at 3): `a` is indexed afresh, cell 4 is new to it.
    expect(mergeGhosts(a, { rev: 4, blocks: [g(4, 'leased')] })!.blocks.map((x) => [x[0], x[6]])).toEqual([
      [0, 'leased'],
      [1, 'todo'],
      [4, 'leased'],
    ]);
  });
});

describe('hologramCells', () => {
  it('answers again only when the ghosts or a build status changed', () => {
    const ghosts: Ghosts = { rev: 1, blocks: [[0, 64, 0, 4, 0, 'b1', 'todo', 'place'] as Ghost] };
    const builds = [{ id: 'b1', status: 'active' }] as unknown as StoryColonyState['builds'];
    const first = hologramCells({ ghosts, builds });
    // A new builds list (every delta brings one) with the same statuses: the same answer.
    expect(hologramCells({ ghosts, builds: [...builds!] })).toBe(first);
    const paused = hologramCells({ ghosts, builds: [{ id: 'b1', status: 'paused' }] as never });
    expect(paused).not.toBe(first);
    expect(paused.cells[0]!.state).toBe('waiting');
    expect(hologramCells({ ghosts: { ...ghosts }, builds }).cells).toEqual(first.cells);
  });
});

describe('the angled map without holograms', () => {
  it('sorts the ghosts lowest first once per list, not every frame', () => {
    const blocks = [
      [0, 17, 0],
      [1, 11, 0],
      [2, 16, 0],
      [3, 11, 1],
    ] as unknown as Ghosts['blocks'];
    const sorted = ghostsLowFirst(blocks);
    expect(sorted.map((g) => g[0])).toEqual([1, 3, 2, 0]);
    expect(ghostsLowFirst(blocks)).toBe(sorted);
    expect(blocks.map((g) => g[0])).toEqual([0, 1, 2, 3]); // the held list is left as it is
  });
});
