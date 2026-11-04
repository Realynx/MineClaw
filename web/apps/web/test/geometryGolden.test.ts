import type { GraphNode } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { contains, indexGraph, walk } from '@/features/graph/geometry';

const node = (o: Partial<GraphNode>): GraphNode => ({ id: 1, x: 0.5, y: 0.5, ...o }) as GraphNode;

const NODES: GraphNode[] = [
  node({ id: 1, x: 0.4, y: 0.6, w: 0.2, h: 0.1, r0: 0.1, r1: 0.3, a0: 0.5, a1: 2.5, r: 0.05 }),
  node({ id: 2, x: 0.7, y: 0.2 }),
  node({ id: 3, x: 0.5, y: 0.5, r0: 0, r1: 0.5, a0: 5, a1: 6.3 }),
];

describe('graph geometry (golden, pinned before contains became per-layout)', () => {
  it('hit-tests every layout over a grid', () => {
    const out: string[] = [];
    for (const layout of ['icicle', 'radial', 'nodelink'] as const)
      for (const n of NODES)
        for (const unit of [0, 0.02]) {
          let row = '';
          for (let y = 0; y <= 20; y++) for (let x = 0; x <= 20; x++) row += contains(n, layout, x / 20, y / 20, unit) ? '#' : '.';
          out.push(`${layout} ${n.id} ${unit} ${row}`);
        }
    expect(out).toMatchSnapshot();
  });

  it('walks every node in every direction', () => {
    const nodes = [
      node({ id: 1, parent: null, x: 0.5 }),
      node({ id: 2, parent: 1, x: 0.2 }),
      node({ id: 3, parent: 1, x: 0.8 }),
      node({ id: 4, parent: 2, x: 0.1 }),
      node({ id: 5, parent: null, x: 0.9 }),
    ];
    const idx = indexGraph({ nodes, edges: [] });
    const out: string[] = [];
    for (const from of [null, 1, 2, 3, 4, 5, 99])
      for (const dir of ['up', 'down', 'left', 'right'] as const) out.push(`${from} ${dir} → ${walk(idx, from, dir)}`);
    expect(out).toMatchSnapshot();
  });
});
