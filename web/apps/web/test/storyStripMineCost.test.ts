/**
 * What the Controller does with a colony whose builds hold a 47k-cell strip mine (live 11-10): every `story` delta (2 Hz) is merged
 * into the state, and a delta that touches a ghost gives the state new ghosts, so the map recomputes its hologram cells and the
 * viewer plans its holograms again. Each step timed on a synthetic colony of that shape, against the 25 ms a frame has at 40 fps
 * (Fox's software-rendered WebGL). Numbers are printed for the lane's diagnosis; the assertions only pin what each step returns.
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { planHolograms } from '@mineclaw/world-viewer/settings';
import { describe, expect, it } from 'vitest';
import { hologramCells } from '@/features/story/bodyLook';
import { editHolograms } from '@/features/story/flyEdit';
import { ghostsLowFirst } from '@/features/story/mapDraw';
import { mergeColony } from '@/features/story/model';
import { clearCellsByColumn, failedCells } from '@/features/story/tools';
import { plainMergeGhosts } from './ghostMergeReference';

type Ghost = NonNullable<StoryColonyState['ghosts']>['blocks'][number];

/** Twelve mine builds of ~3,900 dig cells (a corridor and twenty 193-long branches each), a torch build, a blocked leaves build. */
function ghostRows(): Ghost[] {
  const rows: Ghost[] = [];
  const levels = [17, 16, 12, 11];
  for (let i = 0; i < 12; i++) {
    const x0 = [1680, 1760, 1840][i % 3]! + (i % 6 >= 3 ? 2 : 0),
      y = levels[Math.floor(i / 3)]!,
      id = `b${300000 + i}`;
    const add = (x: number, z: number) =>
      rows.push([x, y, z, 1, 0, id, i === 0 && z % 97 === 0 ? 'leased' : 'todo', 'dig']);
    for (let x = x0; x < x0 + 80; x++) add(x, 1122);
    for (let k = 0; k < 20; k++) for (let z = 1026; z <= 1218; z++) if (z !== 1122) add(x0 + 4 * k, z);
  }
  for (let k = 0; k < 54; k++)
    rows.push([1682 + 4 * (k % 27), k < 27 ? 18 : 13, 1130 + (k % 5), 50, 4, 'b300012', 'todo', 'place']);
  for (let k = 0; k < 142; k++)
    rows.push([1820 + (k % 21), 64, 1176 + Math.floor(k / 21), 18, 4, 'b200002', 'todo', 'place']);
  rows.push([1838, 83, 1194, 98, 0, 'b200001', 'blocked', 'place', 'no path to block']);
  return rows;
}

function colony(): StoryColonyState {
  const builds = [
    ...Array.from({ length: 13 }, (_, i) => ({
      id: `b${300000 + i}`,
      kind: 'mine',
      label: 'mine (3920 blocks)',
      status: i < 3 ? 'active' : 'queued',
    })),
    { id: 'b200001', kind: 'block', label: 'block (1367 blocks, stonebrick)', status: 'active' },
    { id: 'b200002', kind: 'block', label: 'block (142 blocks, leaves)', status: 'blocked' },
  ];
  return {
    id: 'story-1',
    rev: 1000,
    full: true,
    at: 0,
    builds,
    ghosts: { rev: 1000, blocks: ghostRows() },
  } as unknown as StoryColonyState;
}

/** A delta after one dig: the dug cell done, the next one leased. */
function delta(rev: number, i: number): StoryColonyState {
  return {
    id: 'story-1',
    rev,
    since: rev - 1,
    full: false,
    at: rev,
    builds: [{ id: 'b300000', kind: 'mine', label: 'mine (3920 blocks)', status: 'active' }],
    ghosts: {
      rev,
      blocks: [
        [1680 + i, 17, 1122, 1, 0, 'b300000', 'done', 'dig'],
        [1681 + i, 17, 1122, 1, 0, 'b300000', 'leased', 'dig'],
      ],
    },
    removed: {},
  } as unknown as StoryColonyState;
}

/** The mean time of `run` over `n` calls (after one untimed call), in ms. */
function time<T>(n: number, run: (i: number) => T): { ms: number; out: T } {
  let out = run(0);
  const t0 = performance.now();
  for (let i = 1; i <= n; i++) out = run(i);
  return { ms: (performance.now() - t0) / n, out };
}

describe('the Controller with a 47k-cell strip mine queued', () => {
  it('what a delta costs: merging, hologram cells, the viewer plan', () => {
    const base = colony();
    const n = 10;
    // Before the lag lane's S5: every delta's ghosts merged through a map of every held ghost (the reference merge).
    let plain = base.ghosts!;
    const before = time(n, (i) => (plain = plainMergeGhosts(plain, delta(1000 + i + 1, i).ghosts)!));
    let state = base;
    const merge = time(n, (i) => (state = mergeColony(state, delta(1000 + i + 1, i))));
    const merged = state.ghosts!;
    // A ghost delta, as the page sees it: new ghosts, so the map's revision and the fly editor's overlay ask for the
    // hologram cells (computed once, the second asker gets the same answer), then the viewer plans.
    let k = n + 1;
    const fresh = () => (state = mergeColony(state, delta(1000 + ++k, k % 70)));
    const cells = time(n, () => (fresh(), hologramCells(state)));
    const cellsOnly = cells.ms - time(n, fresh).ms;
    const again = time(n, () => hologramCells(state));
    const fly = time(n, () => editHolograms(hologramCells(state).cells, []));
    const plan = time(n, () => planHolograms(cells.out.cells));
    const failed = time(n, () => failedCells(state.ghosts!.blocks));
    const clear = time(n, () => clearCellsByColumn([...state.ghosts!.blocks]));
    // Holograms off, angled view: the map draws the ghosts lowest first on every frame.
    const sortEveryFrame = time(n, () => [...state.ghosts!.blocks].sort((a, b) => a[1] - b[1]));
    const sortOnce = time(n, () => ghostsLowFirst(state.ghosts!.blocks));
    const perDelta = merge.ms + cellsOnly + again.ms + fly.ms + plan.ms;
    // eslint-disable-next-line no-console -- the measured costs, for the lane's diagnosis
    console.log(
      [
        `${state.ghosts!.blocks.length} ghosts held; per delta that touches a ghost (2 Hz), on the main thread:`,
        `  before: mergeGhosts with a map of every ghost     ${before.ms.toFixed(2)} ms`,
        `  mergeColony (indexed mergeGhosts)                ${merge.ms.toFixed(2)} ms`,
        `  hologramCells, new ghosts (StoryMap's rev)       ${cellsOnly.toFixed(2)} ms  -> ${cells.out.cells.length} cells`,
        `  hologramCells, the second asker (same answer)    ${again.ms.toFixed(3)} ms`,
        `  StoryFlyEditor overlay (hologramCells + edits)   ${fly.ms.toFixed(2)} ms`,
        `  planHolograms (the viewer, when its rev moves)  ${plan.ms.toFixed(2)} ms  -> ${plan.out.cells} drawn, ${plan.out.capped.size} builds capped`,
        `  failedCells (map ring, Retry count)             ${failed.ms.toFixed(2)} ms`,
        `  clearCellsByColumn (new ghost list)              ${clear.ms.toFixed(2)} ms`,
        `  per frame, holograms off, angled: ghosts sorted  ${sortEveryFrame.ms.toFixed(2)} ms before, ${sortOnce.ms.toFixed(3)} ms now (once per list)`,
        `  together ${perDelta.toFixed(1)} ms a delta (a frame at 40 fps has 25 ms), before the viewer's own rebuild`,
      ].join('\n'),
    );
    // Both merges keep every ghost (the dug cell's state replaced in place) and agree; the plan stops at its cap.
    expect(merged.blocks).toEqual(plain.blocks);
    expect(state.ghosts!.blocks.length).toBe(base.ghosts!.blocks.length);
    expect(cells.out.cells.length).toBeGreaterThan(40_000);
    expect(plan.out.cells).toBeLessThanOrEqual(20_000);
  }, 120_000);
});
