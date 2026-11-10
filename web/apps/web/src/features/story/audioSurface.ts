/**
 * The visible surface under the Controller's map centre, which the top view's hearing floor hangs on (StoryAudio). The
 * blocks live in the viewer's mesh worker, so a reading is one small asynchronous query, kept here between listener ticks.
 */

/** How deep under the visible surface at the map centre the top view still hears (blocks). */
export const HEARING_DEPTH = 3;

type V3 = [number, number, number];

/** The mesh host's column query (engine.runtime.host). */
export interface SurfaceHost {
  stacks(
    dim: number,
    min: V3,
    max: V3,
    skip: number[],
    depth: number,
    above?: number,
    maxHits?: number,
  ): Promise<number[]>;
}

/**
 * Looked through: logs, leaves, plants, crops, snow (the same ids as tools.ts NOT_GROUND_IDS, kept equal by a test) and
 * the three blocks the viewer never draws (36, 166, 217). Not imported from tools.ts: the pawn-control route loads this
 * module too.
 */
export const SURFACE_SKIP: readonly number[] = [
  17, 162, 18, 161, 6, 31, 32, 37, 38, 39, 40, 175, 78, 59, 83, 106, 111, 141, 142, 207, 81, 115, 36, 166, 217,
];

/** Over the top of any column; the query itself stops at the column's own height. */
const WORLD_TOP = 319;

/**
 * The Y of the highest drawn ground block of a column, under the cut when there is one (the cut hides every block at or
 * over it); null when the column is not loaded or holds nothing there. One small worker query.
 */
export async function visibleSurface(
  host: SurfaceHost,
  dim: number,
  x: number,
  z: number,
  cutY: number | null,
): Promise<number | null> {
  const cx = Math.floor(x);
  const cz = Math.floor(z);
  const top = cutY === null ? WORLD_TOP : Math.min(WORLD_TOP, Math.ceil(cutY) - 1);
  if (top < 0) return null;
  // One cell deep, nothing over the top, one hit: the answer is the top alone, [x, y, z, id, meta, below], or empty.
  const hit = await host.stacks(dim, [cx, 0, cz], [cx, top, cz], [...SURFACE_SKIP], 1, 0, 1);
  return hit.length >= 6 ? hit[1]! : null;
}

/**
 * The last reading for the map centre. Re-read when the column or the cut changes (at most every `gapMs`) and every
 * `ttlMs` otherwise (blocks change, chunks arrive). The previous reading is served meanwhile; one query in flight.
 */
export class CentreSurface {
  private host: SurfaceHost | null = null;
  private key = '';
  private value: number | null = null;
  /** When the last query started. */
  private at = -Infinity;
  /** The query being waited for (0 = none). */
  private flight = 0;
  private seq = 0;

  constructor(
    private readonly now: () => number = () => performance.now(),
    private readonly ttlMs = 500,
    private readonly gapMs = 100,
    private readonly lostMs = 2000,
  ) {}

  read(host: SurfaceHost, dim: number, x: number, z: number, cutY: number | null): number | null {
    if (host !== this.host) {
      // Another viewer's world: nothing read from the old one applies, and its late answers are dropped below.
      this.host = host;
      this.key = '';
      this.value = null;
      this.at = -Infinity;
      this.flight = 0;
    }
    const key = `${dim}|${Math.floor(x)}|${Math.floor(z)}|${cutY ?? ''}`;
    const t = this.now();
    const age = t - this.at;
    // A disposed worker never answers, so a query older than `lostMs` stops holding the next one back.
    const idle = this.flight === 0 || age > this.lostMs;
    if (idle && age >= (key === this.key ? this.ttlMs : this.gapMs)) {
      const id = (this.flight = ++this.seq);
      this.key = key;
      this.at = t;
      void visibleSurface(host, dim, x, z, cutY)
        .then(
          (y) => {
            if (this.flight === id) this.value = y;
          },
          () => undefined, // a failed query keeps the last reading
        )
        .finally(() => {
          if (this.flight === id) this.flight = 0;
        });
    }
    return this.value;
  }
}
