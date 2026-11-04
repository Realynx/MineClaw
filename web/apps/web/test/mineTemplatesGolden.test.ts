import { describe, expect, it } from 'vitest';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { cellsHash, edgeOf, fromStep, ringIndex, spiral, strip, type MineCells } from '@/features/studio/mine/mineTemplates';

/** A template's result in one line: cell count and hash, entry, path length and hash, label (or the error it throws). */
function summary(run: () => MineCells): string {
  try {
    const m = run();
    const pathHash = cellsHash(m.path.map(([x, y, z]) => [x, y, z, 0, 0]));
    return `${m.cells.length}:${cellsHash(m.cells)} entry ${m.entry.join(',')} path ${m.path.length}:${pathHash} | ${m.label}`;
  } catch (e) {
    return `throws ${(e as Error).message}`;
  }
}

const STEPS: Array<Record<string, unknown>> = [
  {},
  { template: 'spiral' },
  { template: 'SPIRAL_SHAFT', x: 4, z: '7', size: 5, core: 1, top: 40, bottom: 20, dir: 'ccw', torchEvery: 3, landings: [30, 25, 30, 'x', 99] },
  { template: 'shaft', size: '6', core: 3, top: '70', bottom: -4, dir: 'Counterclockwise', torch_every: 2 },
  { template: 'shaft', size: 2, core: 9, dir: 'cw', x: 1.5, z: 'a' },
  { template: 'spiral', top: 'surface', size: 3 },
  { template: 'strip' },
  { template: 'strip_mine', x: 3, y: '20', z: -2, axis: ' Z ', length: 12, width: 2, height: 3, spacing: 2, branchLength: 4, sides: 'left', torchEvery: 4 },
  { template: 'strip', axis: 'zeta', length: 99, width: 0, height: 9, spacing: 0, branch_length: 70, sides: 'right', torch_every: 1 },
  { template: 'strip', sides: 'none', length: 5, spacing: 1, torchEvery: 2 },
  { template: 'strip', sides: 'BOTH', length: 20, torchEvery: 3, axis: 'x' },
  { template: 'room' },
  { template: 7 },
];

describe('mine templates (golden, pinned before the step reader and templates were split)', () => {
  it('reads every kind of step the same, with and without a terrain', () => {
    const terrain = new MineTerrain('12345', 'b1.8.1');
    const out = STEPS.map((s) => [summary(() => fromStep(s, null)), summary(() => fromStep(s, terrain))]);
    expect(out).toMatchSnapshot();
  });

  it('spirals and strips over their parameter ranges', () => {
    const out: string[] = [];
    for (const size of [3, 4, 5, 6])
      for (const core of [1, 2, 4])
        for (const cw of [true, false])
          out.push(summary(() => spiral({ x: 2, z: 3, top: 30, bottom: 12, size, core, cw, torchEvery: 2, landings: [20, 14] })));
    for (const axis of ['x', 'z'] as const)
      for (const sides of ['both', 'left', 'right', 'none'] as const)
        for (const torchEvery of [0, 1, 3, 4])
          out.push(summary(() => strip({ x: 1, y: 11, z: 2, axis, sides, torchEvery, length: 17, width: 2, spacing: 3, branchLength: 5 })));
    expect(out).toMatchSnapshot();
  });

  it('edges and ring places of every walkway cell', () => {
    const out: string[] = [];
    for (let n = 3; n <= 6; n++)
      for (let lz = 0; lz < n; lz++) {
        let row = '';
        for (let lx = 0; lx < n; lx++) row += `${edgeOf(lx, lz, n)}/${ringIndex(lx, lz, n)} `;
        out.push(`${n}: ${row}`);
      }
    expect(out).toMatchSnapshot();
  });
});
