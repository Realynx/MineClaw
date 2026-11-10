/**
 * The plot the engine works in: one state per cell (`id << 4 | meta`) in a typed array, for a box around the design.
 * The box is the design's own box plus the room a piston can carry a block out of it, plus a border the engine reads
 * but never writes, so the neighbour of a neighbour of any part is always a cell of the array.
 *
 * A cell's index grows with x, then z, then y, so sorting indexes sorts cells by y, z, x: the order of every list the
 * engine returns.
 */
import { GROUND_ID, type Cell, type CircuitInput, type Pos } from './api.js';
import { DOWN, EAST, NORTH, SOUTH, UP, WEST } from './orientation.js';

/** The state of undisturbed ground: a solid, immovable conductor with no block id of its own. */
export const GROUND = GROUND_ID << 4;

/**
 * Set on the state of a part that is left out of the simulation (nothing holds it up, or the version does not have
 * it). The low twelve bits keep the state as it was placed, which is what the cell still shows; with the bit set the
 * state matches no block id, so every rule sees an empty, non-solid cell.
 */
export const INERT = 1 << 12;

/** The state a cell shows: an inert part shows as it was placed. */
export const shownState = (state: number): number => (state >= INERT ? state & (INERT - 1) : state);

/** A piston moves at most 12 blocks and its head: one push carries a block no further out than this. */
const REACH = 13;
/** Cells past that which are only ever read. */
const BORDER = 2;
/** The longest side of a design the engine takes: twice the studio's plot. */
const MAX_SIDE = 128;

export class Grid {
  readonly sizeX: number;
  readonly sizeY: number;
  readonly sizeZ: number;
  readonly size: number;
  /** The state of every cell now. */
  readonly cells: Int16Array;
  /** The design as it was given, with the ground filled in: what `displayCells` compares with. */
  readonly design: Int16Array;
  /** The index step to the neighbour on each side, by direction. */
  readonly step: Int32Array;

  private readonly x0: number;
  private readonly y0: number;
  private readonly z0: number;
  /** The top layer of the ground; null without ground. */
  private readonly groundTop: number | null;

  constructor(input: CircuitInput) {
    const min = [0, 0, 0];
    const max = [0, 0, 0];
    input.cells.forEach((c, n) => {
      const [x, y, z, id] = c;
      if (!Number.isInteger(x) || !Number.isInteger(y) || !Number.isInteger(z))
        throw new RangeError(`@mineclaw/redstone: a cell needs whole coordinates, got ${x}, ${y}, ${z}`);
      if (!Number.isInteger(id) || id < GROUND_ID || id > 255)
        throw new RangeError(`@mineclaw/redstone: no block has id ${id}`);
      for (let a = 0; a < 3; a++) {
        min[a] = n === 0 ? c[a]! : Math.min(min[a]!, c[a]!);
        max[a] = n === 0 ? c[a]! : Math.max(max[a]!, c[a]!);
      }
    });
    for (let a = 0; a < 3; a++)
      if (max[a]! - min[a]! >= MAX_SIDE)
        throw new RangeError(`@mineclaw/redstone: a design may be at most ${MAX_SIDE} cells a side`);

    const pad = REACH + BORDER;
    this.x0 = min[0]! - pad;
    this.y0 = min[1]! - pad;
    this.z0 = min[2]! - pad;
    this.sizeX = max[0]! - min[0]! + 1 + 2 * pad;
    this.sizeY = max[1]! - min[1]! + 1 + 2 * pad;
    this.sizeZ = max[2]! - min[2]! + 1 + 2 * pad;
    this.size = this.sizeX * this.sizeY * this.sizeZ;
    this.cells = new Int16Array(this.size);
    this.design = new Int16Array(this.size);

    const layer = this.sizeX * this.sizeZ;
    this.step = new Int32Array(6);
    this.step[DOWN] = -layer;
    this.step[UP] = layer;
    this.step[NORTH] = -this.sizeX;
    this.step[SOUTH] = this.sizeX;
    this.step[WEST] = -1;
    this.step[EAST] = 1;

    const ground = input.ground;
    this.groundTop = ground && Number.isFinite(ground.topY) ? Math.floor(ground.topY) : null;
    this.fill(input);
  }

  /** The cell's index, or -1 when it lies outside the box. */
  index(x: number, y: number, z: number): number {
    const dx = x - this.x0;
    const dy = y - this.y0;
    const dz = z - this.z0;
    if (dx < 0 || dy < 0 || dz < 0 || dx >= this.sizeX || dy >= this.sizeY || dz >= this.sizeZ) return -1;
    return (dy * this.sizeZ + dz) * this.sizeX + dx;
  }

  /** The index of a position; -1 outside the box and for a position that is not whole numbers. */
  indexOf(at: Pos | Cell): number {
    const [x, y, z] = at;
    return Number.isInteger(x) && Number.isInteger(y) && Number.isInteger(z) ? this.index(x, y, z) : -1;
  }

  pos(index: number): Pos {
    const x = index % this.sizeX;
    const rest = (index - x) / this.sizeX;
    const z = rest % this.sizeZ;
    return [x + this.x0, (rest - z) / this.sizeZ + this.y0, z + this.z0];
  }

  /** Whether a block may be put in the cell: the border is read only. */
  writable(index: number): boolean {
    if (index < 0 || index >= this.size) return false;
    const x = index % this.sizeX;
    const rest = (index - x) / this.sizeX;
    const z = rest % this.sizeZ;
    const y = (rest - z) / this.sizeZ;
    return (
      x >= BORDER &&
      y >= BORDER &&
      z >= BORDER &&
      x < this.sizeX - BORDER &&
      y < this.sizeY - BORDER &&
      z < this.sizeZ - BORDER
    );
  }

  /** What a cell outside the box holds: ground up to the ground's top, air above it. Nothing there ever changes. */
  outside(at: Pos): number {
    return this.groundTop !== null && at[1] <= this.groundTop ? GROUND : 0;
  }

  private fill(input: CircuitInput): void {
    const { design } = this;
    if (this.groundTop !== null) {
      const layers = Math.min(this.sizeY, Math.max(0, this.groundTop - this.y0 + 1));
      design.fill(GROUND, 0, layers * this.sizeX * this.sizeZ);
      for (const p of input.ground?.dug ?? []) {
        const i = this.indexOf(p);
        if (i >= 0 && p[1] <= this.groundTop) design[i] = 0;
      }
    }
    for (const c of input.cells)
      design[this.index(c[0], c[1], c[2])] = c[3] === GROUND_ID ? GROUND : (c[3] << 4) | (c[4] & 15);
    this.cells.set(design);
  }
}
