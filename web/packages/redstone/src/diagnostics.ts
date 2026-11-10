/**
 * What the engine tells the user it could not simulate faithfully, in plain words. The checks themselves are where the
 * facts are: loading a design (engine.ts) and the tick loop.
 */
import type { Diagnostic, DiagnosticCode, Pos, RedstoneVersion } from './api.js';

const GAME: Readonly<Record<RedstoneVersion, string>> = { 'b1.8.1': 'Beta 1.8.1', '1.12.2': 'Minecraft 1.12.2' };

/** A part with nothing to stand or hang on. In the game it would drop off, so it takes no part here. */
export const floatingText = (part: string): string =>
  `${part} has nothing to hold it here. It would drop off in the game, so it is left out of the simulation.`;

/** A block with redstone behaviour the engine has no model for. */
export const unsupportedText = (): string =>
  'This block is not simulated. It stays as placed: it gives no power and does not react to any.';

/** A state no version of the game has (a torch that neither stands nor hangs). */
export const unknownStateText = (part: string): string =>
  `${part} is in a state the game does not have, so it is left out of the simulation.`;

export const notInVersionText = (v: RedstoneVersion, part: string | null): string =>
  part === null
    ? `${GAME[v]} does not have this block.`
    : `${GAME[v]} does not have ${part.toLowerCase()} placed this way, so it is left out of the simulation.`;

export const unstableText = (limit: number): string =>
  `More than ${limit} updates in one game tick. The tick was cut short here: the circuit does not settle within a tick.`;

export const slimeText = (): string =>
  "A slime block is in a piston's path. It moves here as a plain block: the blocks stuck to it in the game stay put.";

export const outOfPlotText = (): string =>
  'A piston would push a block out of the plot here, so it does not move. In the game it might.';

/** The update-order patterns the engine can see, said once per cell. */
export const UPDATE_ORDER_TEXT = {
  guard:
    'In Beta 1.8.1 a piston ignores updates while another piston of its kind is moving, and this one missed one. ' +
    'In the game which pistons miss an update depends on the order of updates.',
  dust:
    'This piston moves while dust is telling its neighbours of a change. In Beta 1.8.1 the game can move it while the ' +
    'dust is only half updated, so the result can differ there.',
  zeroTick:
    'This piston was powered and unpowered again within one game tick, so it did not move. Pulses that short come ' +
    'from the order of updates and can come out differently in the game.',
} as const;

/** The diagnostics of one simulation: the ones found when the design was loaded and the ones raised while running. */
export class DiagnosticList {
  private readonly found: Diagnostic[] = [];
  private raised: Diagnostic[] = [];

  /** A diagnostic from loading the design: it stays for the life of the simulation. */
  find(code: DiagnosticCode, at: Pos, text: string): void {
    this.found.push({ at, code, text });
  }

  /** A diagnostic from running; the same code at the same cell is kept once. */
  raise(code: DiagnosticCode, at: Pos, text: string): void {
    const same = (d: Diagnostic): boolean =>
      d.code === code && d.at[0] === at[0] && d.at[1] === at[1] && d.at[2] === at[2];
    if (!this.raised.some(same)) this.raised.push({ at, code, text });
  }

  list(): Diagnostic[] {
    return [...this.found, ...this.raised];
  }

  save(): Diagnostic[] {
    return this.raised.slice();
  }

  load(saved: unknown): void {
    this.raised = (saved as Diagnostic[]).slice();
  }
}
