/**
 * The studio world's display layer: what a running redstone simulation shows instead of the canvas. It reaches the viewer
 * as block deltas with only the changed cells, survives a column refetch, never changes the canvas, and gives a cell back
 * as soon as the canvas edits it.
 */
import { inflateRawSync } from 'node:zlib';
import type { BlockDeltas } from '@mineclaw/world-viewer';
import { cellIndex, decodeMcwc, lightPacked, parseLight, unpackSnapshot } from '@mineclaw/world-viewer';
import { afterEach, describe, expect, it } from 'vitest';
import { Canvas, setPlotMode, type Cell } from '@/features/studio/canvas';
import type { Version } from '@/features/studio/facing';
import { BASE_Y, StudioWorld } from '@/features/studio/studioWorld';

afterEach(() => setPlotMode({ kind: 'build' }));

const TORCH: Cell = [5, 0, 5, 76, 5];
const DUST: Cell = [6, 0, 5, 55, 0];
/** The torch out, the dust at full power and a piston head where the canvas has air. */
const TORCH_OUT: Cell = [5, 0, 5, 75, 5];
const DUST_15: Cell = [6, 0, 5, 55, 15];
const HEAD: Cell = [7, 0, 5, 34, 3];

function mount(cells: Cell[] = [TORCH, DUST], version: Version = 'b1.8.1') {
  const canvas = new Canvas();
  canvas.apply(cells);
  const world = new StudioWorld(version, canvas, () => 1000);
  const deltas: BlockDeltas[] = [];
  world.source.subscribe!((d) => deltas.push(d));
  /** Every delta pushed since the last call, each as its list of world blocks. */
  const pushed = () => deltas.splice(0).map((d) => (d.blocks as Record<string, number[][]>)['0']);
  return { canvas, world, pushed };
}

/** A column as the viewer would refetch it: the block and the sky light at a canvas cell. */
async function column(world: StudioWorld, cx = 0, cz = 0) {
  const entry = decodeMcwc(await world.source.chunks(0, [[cx, cz]])).entries[0]!;
  const blocks = unpackSnapshot(inflateRawSync(entry.data));
  const light = parseLight(inflateRawSync(entry.light!));
  return {
    at: (x: number, y: number, z: number): [number, number] => {
      const i = cellIndex(x & 15, y + BASE_Y, z & 15);
      return [blocks.ids[i]!, blocks.meta[i]!];
    },
    sky: (x: number, y: number, z: number) => lightPacked(light, x & 15, y + BASE_Y, z & 15) >> 4,
  };
}

describe('the studio world display layer', () => {
  it('draws a shown state instead of the canvas, also in a refetched column, and leaves the canvas alone', async () => {
    const { canvas, world, pushed } = mount();
    world.setDisplay([TORCH_OUT, DUST_15, HEAD]);
    expect(pushed()).toEqual([
      [
        [5, BASE_Y, 5, 75, 5],
        [6, BASE_Y, 5, 55, 15],
        [7, BASE_Y, 5, 34, 3],
      ],
    ]);
    const col = await column(world);
    expect(col.at(5, 0, 5)).toEqual([75, 5]);
    expect(col.at(6, 0, 5)).toEqual([55, 15]);
    expect(col.at(7, 0, 5)).toEqual([34, 3]);
    expect(canvas.get(5, 0, 5)).toEqual([76, 5]);
    expect(canvas.get(6, 0, 5)).toEqual([55, 0]);
    expect(canvas.get(7, 0, 5)).toEqual([0, 0]);
    expect(canvas.size).toBe(2);
  });

  it('puts every cell back to what the canvas holds when the display is taken away', async () => {
    const { canvas, world, pushed } = mount();
    canvas.apply([[9, -1, 9, 0, 0]]); // a dug cell: air in the ground
    // A block shown in the dug cell and one where the grass is: both go back to what is really there.
    world.setDisplay([TORCH_OUT, HEAD, [9, -1, 9, 4, 0], [10, -1, 10, 4, 0]]);
    pushed();
    world.setDisplay(null);
    expect(pushed()).toEqual([
      [
        [5, BASE_Y, 5, 76, 5],
        [7, BASE_Y, 5, 0, 0],
        [9, BASE_Y - 1, 9, 0, 0],
        [10, BASE_Y - 1, 10, 2, 0],
      ],
    ]);
    const col = await column(world);
    expect(col.at(5, 0, 5)).toEqual([76, 5]);
    expect(col.at(7, 0, 5)).toEqual([0, 0]);
    expect(col.at(9, -1, 9)).toEqual([0, 0]);
    expect(col.at(10, -1, 10)).toEqual([2, 0]);
    // Nothing is shown any more, so there is nothing to take away a second time.
    world.setDisplay(null);
    expect(pushed()).toEqual([]);
  });

  it('sends one delta with only the cells whose shown state changed, and none when nothing did', () => {
    const { world, pushed } = mount();
    world.setDisplay([TORCH_OUT, DUST_15]);
    expect(pushed()).toHaveLength(1);
    world.setDisplay([TORCH_OUT, [6, 0, 5, 55, 14]]);
    expect(pushed()).toEqual([[[6, BASE_Y, 5, 55, 14]]]);
    world.setDisplay([TORCH_OUT, [6, 0, 5, 55, 14]]);
    expect(pushed()).toEqual([]);
    // A cell that is no longer listed is one whose shown state changed too: back to the canvas.
    world.setDisplay([TORCH_OUT]);
    expect(pushed()).toEqual([[[6, BASE_Y, 5, 55, 0]]]);
    world.setDisplay([]);
    expect(pushed()).toEqual([[[5, BASE_Y, 5, 76, 5]]]);
  });

  it('lets the canvas win: a cell the canvas changes is drawn as the canvas has it from then on', async () => {
    const { canvas, world, pushed } = mount();
    world.setDisplay([TORCH_OUT, DUST_15]);
    pushed();
    const edit: Cell[] = [[5, 0, 5, 4, 0]];
    canvas.apply(edit);
    world.push(edit);
    expect(pushed()).toEqual([[[5, BASE_Y, 5, 4, 0]]]);
    const col = await column(world);
    expect(col.at(5, 0, 5)).toEqual([4, 0]);
    expect(col.at(6, 0, 5)).toEqual([55, 15]);
    // Only the dust is still shown differently, so only the dust goes back.
    world.setDisplay(null);
    expect(pushed()).toEqual([[[6, BASE_Y, 5, 55, 0]]]);
  });

  it('shows nothing outside the plot and never the ground as a simulated state', async () => {
    const { world, pushed } = mount();
    world.setDisplay([
      [64, 0, 0, 4, 0],
      [5, 48, 5, 4, 0],
      [5, 0, 5, -1, 0],
    ]);
    expect(pushed()).toEqual([]);
    expect((await column(world)).at(5, 0, 5)).toEqual([76, 5]);
  });

  it.each(['b1.8.1', '1.12.2'] as const)(
    'lights the thin redstone parts on their own cell in %s, and a shown block shades like a placed one',
    async (version) => {
      const parts: Cell[] = [
        [1, 0, 1, 93, 0],
        [2, 0, 1, 70, 0],
        [3, 0, 1, 72, 0],
        [4, 0, 1, 149, 0],
        [5, 0, 1, 143, 1],
        [6, 0, 1, 151, 0],
        [7, 0, 1, 154, 0],
        [8, 0, 1, 131, 0],
        [9, 0, 1, 1, 0],
      ];
      const { world } = mount(parts, version);
      let col = await column(world);
      for (const [x] of parts.slice(0, 8)) expect([x, col.sky(x, 0, 1)]).toEqual([x, 15]);
      // The grass under a repeater is still the ground's top; a full block is dark on its own cell and lit above.
      expect(col.sky(1, -1, 1)).toBe(0);
      expect(col.sky(9, 0, 1)).toBe(0);
      expect(col.sky(9, 1, 1)).toBe(15);
      // A block the simulation shows on open grass (one a piston pushed there) takes the sky like a placed block.
      expect(col.sky(12, 0, 1)).toBe(15);
      world.setDisplay([[12, 0, 1, 1, 0]]);
      col = await column(world);
      expect(col.sky(12, 0, 1)).toBe(0);
      expect(col.sky(12, 1, 1)).toBe(15);
    },
  );
});
