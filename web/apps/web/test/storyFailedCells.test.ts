/**
 * Which build cells count as failed (the map's dashed ring and tooltip, the Retry tool, "Retry failed cells (N)") and which are
 * drawn as holograms. Two kinds of cell carry a reason without having failed: one waiting for the block in it to be dug out
 * (`clear`), and one a newer build took over (superseded). Neither is a failure and a retry changes neither, so neither is
 * counted; a superseded cell is not drawn at all.
 */
import { describe, expect, it } from 'vitest';
import { ghostSuperseded, hologramCells } from '@/features/story/bodyLook';
import { cellTitle, chestRuleCell, clearCellsByColumn, failedByBuild, failedCells } from '@/features/story/tools';

type G = [number, number, number, number, number, string, string, ('dig' | 'place' | 'pour')?, (string | null)?];

const SUPERSEDED = 'superseded by build b7: newer design owns this cell';

/** One row of cells along x, each a case (x names it below). */
const ghosts: G[] = [
  [0, 64, 0, 5, 0, 'b1', 'blocked', 'place', 'out of reach: no path to block'],
  [1, 64, 0, 5, 0, 'b1', 'blocked', 'place'], // blocked by an older bot, which kept no reason
  [2, 64, 0, 5, 0, 'b1', 'todo', 'place', 'server undid my placement at 2,64,0 (protected area?)'], // resting, or being tried again
  [3, 64, 0, 5, 0, 'b1', 'todo', 'place'],
  [4, 64, 0, 5, 0, 'b1', 'clear', 'place', 'holds stone: dug out first'],
  [5, 64, 0, 5, 0, 'b1', 'blocked', 'place', SUPERSEDED],
  [6, 64, 0, 1, 0, 'b2', 'blocked', 'dig', SUPERSEDED], // a placed design's empty space the owner later built in
  [7, 64, 0, 53, 2, 'b2', 'misrotated', 'place', 'placed rising east, wants rising south'],
  [8, 64, 0, 5, 0, 'b2', 'blocked', 'place', 'conflicts with quarry z3: the quarry digs this cell'],
];

describe('failed build cells', () => {
  it('keeps blocked cells and cells with a failure reason; a cell to clear, a superseded or a quarry-held one is not failed', () => {
    const failed = failedCells(ghosts);
    expect(failed.map((f) => f.cell[0])).toEqual([0, 1, 2, 7]);
    expect(failed[0]).toEqual({
      build: 'b1',
      cell: [0, 64, 0],
      reason: 'out of reach: no path to block',
      state: 'blocked',
    });
    expect(failed[1]!.reason).toBe('blocked after repeated failures');
    expect(failed[2]!.reason).toContain('server undid my placement');
  });

  it('a Retry drag over only such cells sends nothing, and a build is not asked to retry them', () => {
    expect(failedCells(ghosts, [3, 0, 6, 0])).toEqual([]);
    const by = failedByBuild(failedCells(ghosts));
    expect(by.get('b1')!.cells.map((c) => c[0])).toEqual([0, 1, 2]);
    expect(by.get('b2')!.cells.map((c) => c[0])).toEqual([7]);
  });

  it('a cell a quarry digs is drawn but not counted: the server leaves it out of Retry (it did not fail)', () => {
    expect(failedCells(ghosts, [8, 0, 8, 0])).toEqual([]);
    expect(failedCells(ghosts, null, true).map((f) => f.cell[0])).toEqual([0, 1, 2, 7, 8]);
  });

  it('reads superseded from the reason the director gives, whatever build it names', () => {
    expect(ghostSuperseded(SUPERSEDED)).toBe(true);
    expect(ghostSuperseded('superseded by build b12: newer design owns this cell')).toBe(true);
    expect(ghostSuperseded('out of reach: no path to block')).toBe(false);
    expect(ghostSuperseded(null)).toBe(false);
    expect(ghostSuperseded(undefined)).toBe(false);
  });
});

/** The map's tooltip over a cell of {@link ghosts}, by its x (as StoryMap hovers it: a ring the map draws, else the cell to clear). */
function titleAt(x: number): string {
  const f = failedCells(ghosts, null, true).find((c) => c.cell[0] === x);
  return cellTitle(f, f ? undefined : clearCellsByColumn(ghosts).get(`${x},0`));
}

describe('the map tooltip over a build cell', () => {
  it('names the cell state instead of always saying failed', () => {
    expect(titleAt(0)).toBe('Build b1: 0,64,0 blocked: out of reach: no path to block (Retry tool: O)');
    expect(titleAt(1)).toBe('Build b1: 1,64,0 blocked after repeated failures (Retry tool: O)');
    expect(titleAt(2)).toBe(
      'Build b1: 2,64,0 resting after: server undid my placement at 2,64,0 (protected area?) (Retry tool: O)',
    );
    expect(titleAt(7)).toBe('Build b2: 7,64,0 wrong rotation: placed rising east, wants rising south (Retry tool: O)');
    expect(titleAt(8)).toBe('Build b2: 8,64,0 blocked: conflicts with quarry z3: the quarry digs this cell');
  });

  it('a cell to clear says so, without the Retry tool (failedCells leaves it out, so it has its own lookup)', () => {
    expect(titleAt(4)).toBe('Build b1: 4,64,0 to clear: holds stone: dug out first');
    expect(clearCellsByColumn(ghosts)).toBe(clearCellsByColumn(ghosts)); // built once per ghost list
  });

  it('nothing to say over an open cell, a superseded one or empty ground', () => {
    expect(titleAt(3)).toBe('');
    expect(titleAt(5)).toBe('');
    expect(titleAt(6)).toBe('');
    expect(titleAt(42)).toBe('');
  });

  it('a column with two cells to clear names the lowest', () => {
    const two: G[] = [
      [0, 65, 0, 5, 0, 'b1', 'clear', 'place', 'holds dirt: dug out first'],
      [0, 64, 0, 5, 0, 'b1', 'clear', 'place', 'holds stone: dug out first'],
    ];
    expect(cellTitle(undefined, clearCellsByColumn(two).get('0,0'))).toBe(
      'Build b1: 0,64,0 to clear: holds stone: dug out first',
    );
  });
});

describe("cells the game's chest rule blocks", () => {
  const RULE = "vanilla can't place a chest here: it would touch the double chest at 1,64,0 (leave a gap)";
  const chests: G[] = [
    [2, 64, 0, 54, 0, 'b3', 'blocked', 'place', RULE],
    [3, 64, 0, 54, 0, 'b3', 'blocked', 'place', 'out of reach: no path to block'],
  ];

  it('are drawn as blocked but left out of Retry: its count, its drag and the build row', () => {
    expect(failedCells(chests).map((f) => f.cell[0])).toEqual([3]);
    expect(failedCells(chests, [2, 0, 2, 0])).toEqual([]);
    expect(failedByBuild(failedCells(chests)).get('b3')!.cells).toEqual([[3, 64, 0]]);
    const marked = failedCells(chests, null, true);
    expect(marked.map((f) => f.cell[0])).toEqual([2, 3]);
    expect(marked.map(chestRuleCell)).toEqual([true, false]);
  });

  it('the tooltip names the rule without the Retry tool (a retry changes nothing there)', () => {
    const [rule, reach] = failedCells(chests, null, true);
    expect(cellTitle(rule)).toBe(`Build b3: 2,64,0 blocked: ${RULE}`);
    expect(cellTitle(reach)).toBe('Build b3: 3,64,0 blocked: out of reach: no path to block (Retry tool: O)');
  });
});

describe('holograms of superseded cells', () => {
  const state = (blocks: G[]) =>
    ({
      builds: [
        { id: 'b1', kind: 'block', label: 'planks', status: 'active', blocksTotal: 6, blocksDone: 0 },
        { id: 'b2', kind: 'remove', label: 'remove', status: 'active', blocksTotal: 3, blocksDone: 0 },
      ],
      ghosts: { rev: 9, blocks },
    }) as unknown as Parameters<typeof hologramCells>[0];

  it('leaves them out (the newer build draws that cell); a cell to clear is still drawn over the block in the way', () => {
    const { cells } = hologramCells(state(ghosts));
    expect(cells.map((c) => c.x)).toEqual([0, 1, 2, 3, 4, 7, 8]);
    expect(cells.find((c) => c.x === 4)!.state).toBe('blocked');
    expect(cells.find((c) => c.x === 3)!.state).toBe('todo');
  });

  it('a cell that becomes superseded changes the revision, so the viewer drops its hologram', () => {
    const blockedAt3 = (reason: string): G[] =>
      ghosts.map((g) => (g[0] === 3 ? [3, 64, 0, 5, 0, 'b1', 'blocked', 'place', reason] : g));
    const before = blockedAt3('out of reach: no path to block');
    const after = blockedAt3(SUPERSEDED);
    expect(hologramCells(state(after)).rev).not.toBe(hologramCells(state(before)).rev);
    expect(hologramCells(state(after)).cells.map((c) => c.x)).not.toContain(3);
  });
});
