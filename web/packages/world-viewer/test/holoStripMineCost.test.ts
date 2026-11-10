/**
 * What the build holograms cost with a 47k-cell strip mine queued (the Story colony of 11-10): the plan, the worker's meshing,
 * the main thread's share of a rebuild (the dig outlines' geometry), and a pawn's dig landing on a queued cell. Synthetic cells
 * of the live queue's shape. Numbers are printed for the lag lane's diagnosis; the assertions pin what each step returns.
 */
import type * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { HoloRequest, HoloResult } from '../src/mesh/holo';
import { InlineMeshHost, type MeshHost } from '../src/mesh/host';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import { HologramLayer } from '../src/render/holograms';
import { HOLO_CAP, planHolograms, type HoloCell } from '../src/render/holoPlan';

/** Twelve mine builds of ~3,900 dig cells (a corridor and twenty 193-long branches each), torches, a stone brick tower. */
function stripMineCells(): HoloCell[] {
  const cells: HoloCell[] = [];
  const levels = [17, 16, 12, 11];
  for (let i = 0; i < 12; i++) {
    const x0 = [1680, 1760, 1840][i % 3]! + (i % 6 >= 3 ? 2 : 0),
      y = levels[Math.floor(i / 3)]!,
      build = `b${300000 + i}`;
    const add = (x: number, z: number) =>
      cells.push({ x, y, z, id: 0, meta: 0, build, state: i === 0 && z % 97 === 0 ? 'active' : 'todo' });
    for (let x = x0; x < x0 + 80; x++) add(x, 1122);
    for (let k = 0; k < 20; k++) for (let z = 1026; z <= 1218; z++) if (z !== 1122) add(x0 + 4 * k, z);
  }
  for (let k = 0; k < 54; k++)
    cells.push({
      x: 1682 + 4 * (k % 27),
      y: k < 27 ? 18 : 13,
      z: 1130 + (k % 5),
      id: 50,
      meta: 4,
      build: 'b300012',
      state: 'todo',
    });
  for (let k = 0; k < 142; k++)
    cells.push({
      x: 1820 + (k % 21),
      y: 64,
      z: 1176 + Math.floor(k / 21),
      id: 98,
      meta: 0,
      build: 'b200001',
      state: 'todo',
    });
  return cells;
}

/** The mean time of `run` over `n` calls (after one untimed call), in ms. */
async function time<T>(n: number, run: (i: number) => T | Promise<T>): Promise<{ ms: number; out: T }> {
  let out = await run(0);
  const t0 = performance.now();
  for (let i = 1; i <= n; i++) out = await run(i);
  return { ms: (performance.now() - t0) / n, out };
}

/** The inline (same-thread) host, with the time spent inside it kept apart: in the browser that part runs in the mesh worker. */
function timedHost(inner: MeshHost): {
  host: MeshHost;
  requests: HoloRequest[][];
  cells: () => number;
  workerMs: () => number;
} {
  const requests: HoloRequest[][] = [];
  let ms = 0;
  const host = {
    holo: async (dim: number, reqs: HoloRequest[]): Promise<HoloResult[]> => {
      requests.push(reqs);
      const t0 = performance.now();
      const rs = await inner.holo(dim, reqs);
      ms += performance.now() - t0;
      return rs;
    },
  } as unknown as MeshHost;
  const cells = () => requests.at(-1)!.reduce((n, r) => n + r.blocks.length / 5, 0);
  const workerMs = () => {
    const out = ms;
    ms = 0;
    return out;
  };
  return { host, requests, cells, workerMs };
}

function vertices(layer: HologramLayer): number {
  let n = 0;
  layer.group.traverse((o) => {
    const g = (o as THREE.Mesh).geometry;
    if (g && o.visible) n += g.getAttribute('position').count;
  });
  return n;
}

describe('build holograms with a 47k-cell strip mine queued', () => {
  it('what a ghost delta and a dig cost the viewer', async () => {
    const cells = stripMineCells();
    const inline = new InlineMeshHost();
    await inline.configure('b1.8.1', null, false);
    const n = 5;
    const plan = await time(n, () => planHolograms(cells));
    const reqs: HoloRequest[] = [
      { key: 'todo', blocks: plan.out.groups.todo, mode: 'build' },
      { key: 'dig', blocks: plan.out.dig, mode: 'dig' },
    ];
    const worker = await time(n, () => inline.holo(0, reqs));

    const { host, requests, cells: asked, workerMs } = timedHost(inline);
    const layer = new HologramLayer(host, createChunkUniforms());
    layer.set(cells, 'r0');
    await layer.idle;
    const firstWorker = workerMs();
    // A ghost delta: one cell dug (gone from the list), the next leased. The revision moves.
    const rebuild = await time(n, async (i) => {
      layer.set(cells.slice(i + 1), `r${i + 1}`);
      await layer.idle;
    });
    const deltaMain = rebuild.ms - workerMs() / (n + 1);
    const askedOnDelta = asked();
    // A pawn's dig lands on a queued cell (the world's block change): the cells around it go again.
    const at = cells[200]!;
    const dig = await time(n, async () => {
      layer.blocksChanged(0, [[at.x, at.y, at.z]]);
      await layer.idle;
    });
    const digMain = dig.ms - workerMs() / (n + 1);
    const askedOnDig = asked();
    const st = layer.stats();
    // eslint-disable-next-line no-console -- the measured costs, for the lane's diagnosis
    console.log(
      [
        `${cells.length} queued cells; ${st.cells} drawn as blocks, ${st.digs} dig outlines (cap ${HOLO_CAP}):`,
        `  planHolograms                                    ${plan.ms.toFixed(2)} ms`,
        `  worker meshing (all groups at once)              ${worker.ms.toFixed(2)} ms (first build in the layer: ${firstWorker.toFixed(2)} ms)`,
        `  a ghost delta: set + rebuild on the main thread  ${deltaMain.toFixed(2)} ms  (${askedOnDelta} cells sent to the worker)`,
        `  a dig on a queued cell: rebuild, main thread     ${digMain.toFixed(2)} ms  (${askedOnDig} cells sent to the worker)`,
        `  vertices drawn every frame                       ${vertices(layer)}`,
        `  worker round trips                               ${requests.length}`,
      ].join('\n'),
    );
    expect(plan.out.cells).toBe(HOLO_CAP);
    expect(st.digs + st.cells).toBe(HOLO_CAP);
    expect(layer.has(cells[300]!.x, cells[300]!.y, cells[300]!.z, 'dig')).toBe(true);
    layer.dispose();
  }, 120_000);
});
