/**
 * Layouts are drawn as text so a reader can check them against the game by eye: one string per row, west to east, rows
 * from north to south, one block of rows per layer from the ground up. A character stands for one block and meta.
 */
import type { Cell, Pos, Side } from '../api.js';
import type { ComponentDef, ComponentLayout, Port } from './types.js';

export type Legend = Readonly<Record<string, readonly [id: number, meta: number]>>;

const STONE = 1;
const DUST = 55;
const TORCH = 76;
const REPEATER = 93;

/**
 * The characters every layout shares.
 * - `.` air, `#` stone, `+` dust.
 * - Torches point away from the block they hang on: `}` hangs on the block to its west, `{` east, `v` north, `^` south;
 *   `i` stands on the block below.
 * - Repeaters by the side they put out on, at 2 game ticks: `N`, `E`, `S`, `W`; lower case at 4 game ticks.
 */
export const LEGEND: Legend = {
  '#': [STONE, 0],
  '+': [DUST, 0],
  '}': [TORCH, 1],
  '{': [TORCH, 2],
  v: [TORCH, 3],
  '^': [TORCH, 4],
  i: [TORCH, 5],
  N: [REPEATER, 0],
  E: [REPEATER, 1],
  S: [REPEATER, 2],
  W: [REPEATER, 3],
  n: [REPEATER, 4],
  e: [REPEATER, 5],
  s: [REPEATER, 6],
  w: [REPEATER, 7],
};

/** A repeater putting out on `side` with a delay of `gameTicks` (2, 4, 6 or 8). */
export function repeater(side: Side, gameTicks: 2 | 4 | 6 | 8): readonly [number, number] {
  const facing = { north: 0, east: 1, south: 2, west: 3 }[side];
  return [REPEATER, facing | ((gameTicks / 2 - 1) << 2)];
}

/** One layout for both versions: it uses only parts and rules the two share, and the tests prove it in each. */
export const both = (l: ComponentLayout): ComponentDef['layouts'] => ({ 'b1.8.1': l, '1.12.2': l });

/** An input port: the user's dust goes on `at`, just outside the box on `side`. */
export const input = (name: string, side: Side, at: Pos): Port => ({ name, kind: 'in', at, side });
export const output = (name: string, side: Side, at: Pos): Port => ({ name, kind: 'out', at, side });

/**
 * A layout built from smaller ones: each part's cells moved by its offset, plus loose cells (the wires between parts).
 * Two parts may not claim one cell.
 */
export function compose(
  size: readonly [number, number, number],
  parts: ReadonlyArray<{ layout: ComponentLayout; at: Pos }>,
  loose: ReadonlyArray<readonly [x: number, y: number, z: number, block: string]>,
  ports: readonly Port[],
): ComponentLayout {
  const cells = new Map<string, Cell>();
  const put = (c: Cell): void => {
    const key = `${c[0]},${c[1]},${c[2]}`;
    if (cells.has(key)) throw new Error(`@mineclaw/redstone: two parts claim (${key})`);
    cells.set(key, c);
  };
  for (const { layout, at } of parts)
    for (const [x, y, z, id, meta] of layout.cells) put([x + at[0], y + at[1], z + at[2], id, meta]);
  for (const [x, y, z, ch] of loose) {
    const block = LEGEND[ch];
    if (!block) throw new Error(`@mineclaw/redstone: no block is drawn as '${ch}'`);
    put([x, y, z, block[0], block[1]]);
  }
  const sorted = [...cells.values()].sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);
  return { size, cells: sorted, ports };
}

/**
 * A layout from its drawing. `layers[y]` is the layer at height y, as rows from north to south; every row has the same
 * length and every layer the same rows. `extra` adds characters for this layout only.
 */
export function draw(
  layers: readonly (readonly string[])[],
  ports: readonly Port[],
  extra: Legend = {},
): ComponentLayout {
  const legend: Legend = { ...LEGEND, ...extra };
  const sy = layers.length;
  const sz = layers[0]?.length ?? 0;
  const sx = layers[0]?.[0]?.length ?? 0;
  const cells: Cell[] = [];
  layers.forEach((rows, y) => {
    if (rows.length !== sz) throw new Error(`@mineclaw/redstone: layer ${y} has ${rows.length} rows, not ${sz}`);
    rows.forEach((row, z) => {
      if (row.length !== sx) throw new Error(`@mineclaw/redstone: row ${z} of layer ${y} is not ${sx} long`);
      [...row].forEach((ch, x) => {
        if (ch === '.') return;
        const block = legend[ch];
        if (!block) throw new Error(`@mineclaw/redstone: no block is drawn as '${ch}'`);
        cells.push([x, y, z, block[0], block[1]]);
      });
    });
  });
  return { size: [sx, sy, sz], cells, ports };
}
