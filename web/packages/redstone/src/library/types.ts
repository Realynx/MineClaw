/**
 * The gate library's data. A component is a layout per game version plus the spec that its tests prove against the engine;
 * the studio places a layout as cells and keeps its ports for wiring.
 */
import type { Cell, Pos, RedstoneVersion, Side } from '../api.js';

/**
 * Where a signal enters or leaves a component. `at` is the cell just outside the layout's box where the user's dust goes
 * (a west port has x = -1), at a y inside the box; `side` is the face of the box it sits on.
 */
export interface Port {
  name: string;
  kind: 'in' | 'out';
  at: Pos;
  side: Side;
}

/** Cells are relative to the box's min corner and lie inside `size`; y 0 stands on the ground. */
export interface ComponentLayout {
  size: readonly [x: number, y: number, z: number];
  cells: readonly Cell[];
  ports: readonly Port[];
}

/**
 * What a component must do, in game ticks and port names.
 * - `truth`: for every row, set the inputs (in the order of `inputs`; 0 is off, 1 is on), wait `settleTicks`, then compare
 *   the outputs (in the order of `outputs`).
 * - `sequence`: ordered steps. A step first applies `set` (input levels that stay), then pulses `pulse.port` (on for
 *   `ticks`, then off again), then waits `wait` and compares the outputs in `expect`. `minPulseTicks` is the shortest
 *   input pulse the component is specified for. A memory cell powers up in either state, so a sequence starts by
 *   resetting it.
 * - `clock`: `output` repeats every `periodTicks` and is on for `highTicks` of them.
 *
 * `analog` (1.12.2 only) names the ports that carry a signal strength 0..15 instead of on and off: their values in rows,
 * `set` and `expect` are levels. The harness drives an analog input with a daylight sensor held at the level and reads an
 * analog output as the level of the dust on its port cell. A port that is not listed is on (1) or off (0).
 */
export type ComponentSpec =
  | {
      kind: 'truth';
      inputs: readonly string[];
      outputs: readonly string[];
      rows: ReadonlyArray<readonly [inputs: readonly number[], outputs: readonly number[]]>;
      settleTicks: number;
      analog?: readonly string[];
    }
  | {
      kind: 'sequence';
      minPulseTicks: number;
      steps: ReadonlyArray<{
        set?: Readonly<Record<string, number>>;
        pulse?: { port: string; ticks: number };
        wait: number;
        expect?: Readonly<Record<string, number>>;
      }>;
      analog?: readonly string[];
    }
  | { kind: 'clock'; output: string; periodTicks: number; highTicks: number };

export type ComponentCategory = 'logic' | 'memory' | 'timing' | 'arithmetic' | 'routing';

/** `id` is stable (`gate.xor`): saved designs refer to it. A version without a layout does not have the component. */
export interface ComponentDef {
  id: string;
  name: string;
  category: ComponentCategory;
  description: string;
  layouts: Partial<Record<RedstoneVersion, ComponentLayout>>;
  spec: ComponentSpec;
}

/**
 * A layout mirrored first, in its own frame (`'x'` swaps east and west, `'z'` north and south), and then turned
 * `quarterTurns` clockwise seen from above. The result is a layout again: cells and ports relative to the new box's min
 * corner, `size` with x and z swapped after an odd turn, every meta mirrored and turned with its block.
 */
export type TransformLayout = (
  l: ComponentLayout,
  quarterTurns: number,
  mirror: 'x' | 'z' | null,
  v: RedstoneVersion,
) => ComponentLayout;

/**
 * How the harness wires an input port: dust on the port cell, a solid block one cell further out and a lever on top of
 * that block. `spurLeft` and `spurRight` add one more dust cell beside the port cell (left or right when facing the
 * box), which bends the feeder: a component has to work with all three.
 */
export type Feeder = 'straight' | 'spurLeft' | 'spurRight';

export interface VerifyResult {
  ok: boolean;
  failures: string[];
}

/**
 * Runs a component's spec against the engine in one version, on ground at y -1. An output port is on when the dust on its
 * port cell has power above 0 (an analog one reads that power). A component without a layout for that version fails, and
 * so does one with analog ports in b1.8.1, which has no level source to drive them.
 */
export type VerifyComponent = (def: ComponentDef, v: RedstoneVersion, feeder?: Feeder) => VerifyResult;
