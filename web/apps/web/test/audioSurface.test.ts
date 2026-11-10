import { encodeColumn, encodeMcwc, lightSections, MeshEngine } from '@mineclaw/world-viewer/testing';
import { describe, expect, it, vi } from 'vitest';
import { CentreSurface, SURFACE_SKIP, visibleSurface, type SurfaceHost } from '@/features/story/audioSurface';
import { NOT_GROUND_IDS } from '@/features/story/tools';

// The top view's hearing floor hangs on the visible surface under the map centre: which block that is, read through the
// viewer engine's real `stacks` query, and how often the mesh worker is asked for it.

type B = [number, number, number, number, number?];
const H = 128;
const cellIndex = (lx: number, y: number, lz: number) => ((y >> 7) << 15) | ((lx * 16 + lz) * 128 + (y & 127));
const skyLight = () => 15;
const blockLight = () => 0;

/** An engine holding these blocks (one or more chunk columns, everything else air). The same as in storyHeight.test.ts. */
async function engineWith(blocks: B[]): Promise<MeshEngine> {
  const cols = new Map<string, { cx: number; cz: number; ids: Uint8Array; meta: Uint8Array }>();
  for (const [x, y, z, id, meta] of blocks) {
    const k = `${x >> 4},${z >> 4}`;
    let c = cols.get(k);
    if (!c) cols.set(k, (c = { cx: x >> 4, cz: z >> 4, ids: new Uint8Array(256 * H), meta: new Uint8Array(256 * H) }));
    c.ids[cellIndex(x & 15, y, z & 15)] = id;
    c.meta[cellIndex(x & 15, y, z & 15)] = meta ?? 0;
  }
  const entries = [...cols.values()].map((c) => {
    const e = encodeColumn(c.ids, c.meta, lightSections(H, skyLight, blockLight));
    return { cx: c.cx, cz: c.cz, hash: e.blockHash, seenMs: 1, by: 't', live: true, data: e.data, light: e.light };
  });
  const engine = new MeshEngine();
  engine.configure('b1.8.1', null, false);
  await engine.apply(encodeMcwc(0, entries));
  return engine;
}

/**
 * A 4×4 patch (x 0..3, z 0..3): stone up to y 60, dirt 61..62, grass at 63 (a heightmap of 63) except a hill column (0,0)
 * up to 66 (dirt 64, 65, grass 66) and a pit column (3,3) whose grass is at 61 (air 62..63). Tall grass on (1,1) at 64, a
 * poppy on the hill top at 67, a tree trunk on (2,2) at 64..66 with leaves at 67, and a cave pocket at (1,2) y 62.
 */
function patch(): B[] {
  const out: B[] = [];
  for (let x = 0; x < 4; x++)
    for (let z = 0; z < 4; z++) {
      const top = x === 0 && z === 0 ? 66 : x === 3 && z === 3 ? 61 : 63;
      for (let y = 50; y <= top; y++) out.push([x, y, z, y === top ? 2 : y > 60 ? 3 : 1]);
    }
  out.push([1, 64, 1, 31, 1], [0, 67, 0, 38], [2, 64, 2, 17], [2, 65, 2, 17], [2, 66, 2, 17], [2, 67, 2, 18]);
  return out.map((b) => (b[0] === 1 && b[1] === 62 && b[2] === 2 ? ([1, 62, 2, 0] as B) : b)); // the cave pocket
}

/** The engine behind the host's asynchronous `stacks`, as the mesh worker answers it; `stacks` counts the queries. */
function hostOf(engine: MeshEngine) {
  const stacks = vi.fn<SurfaceHost['stacks']>((...query) => Promise.resolve(engine.stacks(...query)));
  const host: SurfaceHost = { stacks };
  return { host, stacks };
}

/** A host that answers only when the test says so, in the order it was asked. */
function manualHost() {
  const asked: Array<{ resolve: (hit: number[]) => void; reject: (error: Error) => void }> = [];
  const stacks = vi.fn<SurfaceHost['stacks']>(
    () => new Promise<number[]>((resolve, reject) => asked.push({ resolve, reject })),
  );
  const host: SurfaceHost = { stacks };
  return { host, stacks, asked };
}

/** A reading cache on a clock the test moves by hand. */
function clocked() {
  let now = 5000;
  const surface = new CentreSurface(() => now);
  return {
    surface,
    advance: (ms: number) => {
      now += ms;
    },
  };
}

/** Lets every pending answer land: a macrotask turn runs all the queued microtasks first. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('visibleSurface', () => {
  it('reads the ground under plants and trees, in a pit and on a hill', async () => {
    const { host } = hostOf(await engineWith(patch()));
    expect(await visibleSurface(host, 0, 1.5, 1.5, null)).toBe(63); // under the tall grass
    expect(await visibleSurface(host, 0, 2.5, 2.5, null)).toBe(63); // under the tree: not its trunk or leaves
    expect(await visibleSurface(host, 0, 0.5, 0.5, null)).toBe(66); // the hill's grass, not the poppy on it
    expect(await visibleSurface(host, 0, 3.5, 3.5, null)).toBe(61); // the pit's floor
    expect(await visibleSurface(host, 0, 1.5, 2.5, null)).toBe(63); // a cave under the column changes nothing
  });

  it('counts water, a roof and glass as the surface', async () => {
    const built: B[] = [...patch(), [3, 62, 3, 9], [3, 63, 3, 9], [1, 68, 0, 5], [2, 70, 0, 20]];
    const { host } = hostOf(await engineWith(built));
    expect(await visibleSurface(host, 0, 3.5, 3.5, null)).toBe(63); // the top water block of the flooded pit
    expect(await visibleSurface(host, 0, 1.5, 0.5, null)).toBe(68); // a plank roof over open air
    expect(await visibleSurface(host, 0, 2.5, 0.5, null)).toBe(70); // a glass roof
  });

  it('stays under the level cut, which hides every block at or over it', async () => {
    const { host } = hostOf(await engineWith([...patch(), [1, 68, 0, 5]]));
    expect(await visibleSurface(host, 0, 0.5, 0.5, 65)).toBe(64); // the hill, cut at 65
    expect(await visibleSurface(host, 0, 0.5, 0.5, 64.5)).toBe(64);
    expect(await visibleSurface(host, 0, 0.5, 0.5, 67)).toBe(66);
    expect(await visibleSurface(host, 0, 0.5, 0.5, 1e6)).toBe(66); // the cut that only dims the storeys below
    expect(await visibleSurface(host, 0, 1.5, 0.5, 69)).toBe(68); // the roof is still drawn
    expect(await visibleSurface(host, 0, 1.5, 0.5, 68)).toBe(63); // x-rayed away: the ground under it
  });

  it('gives null where the column is not loaded or nothing is left under the cut', async () => {
    const { host, stacks } = hostOf(await engineWith(patch()));
    expect(await visibleSurface(host, 0, 40.5, 40.5, null)).toBeNull(); // no such chunk
    expect(await visibleSurface(host, 1, 1.5, 1.5, null)).toBeNull(); // another dimension
    expect(await visibleSurface(host, 0, 1.5, 1.5, 50)).toBeNull(); // the lowest block is at 50
    expect(stacks).toHaveBeenCalledTimes(3);
    expect(await visibleSurface(host, 0, 1.5, 1.5, 0)).toBeNull(); // nothing can be under a cut at 0: not asked
    expect(await visibleSurface(host, 0, 1.5, 1.5, -8)).toBeNull();
    expect(stacks).toHaveBeenCalledTimes(3);
  });

  it('asks for the one column the point is in, also at negative coordinates, in a single small query', async () => {
    const { host, stacks } = hostOf(await engineWith([...patch(), [-1, 70, -1, 1]]));
    expect(await visibleSurface(host, 0, -0.5, -0.25, null)).toBe(70); // column (-1, -1), not (0, 0) with its hill
    expect(stacks).toHaveBeenLastCalledWith(0, [-1, 0, -1], [-1, 319, -1], [...SURFACE_SKIP], 1, 0, 1);
    expect(await visibleSurface(host, 0, 3.99, 0.01, 67)).toBe(63);
    expect(stacks).toHaveBeenLastCalledWith(0, [3, 0, 0], [3, 66, 0], [...SURFACE_SKIP], 1, 0, 1);
  });

  it('looks through exactly what the Mine tool calls not-ground, plus the three blocks the viewer never draws', () => {
    const ascending = (ids: readonly number[]) => [...ids].sort((a, b) => a - b);
    expect(ascending(SURFACE_SKIP)).toEqual(ascending([...NOT_GROUND_IDS, 36, 166, 217]));
  });
});

describe('CentreSurface', () => {
  it('serves nothing before the first answer, then the reading, and re-reads the same column every 500 ms', async () => {
    const engine = await engineWith(patch());
    const { host, stacks } = hostOf(engine);
    const { surface, advance } = clocked();
    expect(surface.read(host, 0, 1.5, 1.5, null)).toBeNull();
    expect(stacks).toHaveBeenCalledTimes(1);
    await settle();
    expect(surface.read(host, 0, 1.5, 1.5, null)).toBe(63);
    advance(499);
    expect(surface.read(host, 0, 1.1, 1.9, null)).toBe(63); // elsewhere in the same column
    expect(stacks).toHaveBeenCalledTimes(1);
    engine.patch(0, [[1, 63, 1, 0, 0]]); // a pawn digs the grass block away: dirt at 62 is the top now
    advance(1);
    expect(surface.read(host, 0, 1.5, 1.5, null)).toBe(63); // the previous reading until the answer lands
    expect(stacks).toHaveBeenCalledTimes(2);
    await settle();
    expect(surface.read(host, 0, 1.5, 1.5, null)).toBe(62);
    expect(stacks).toHaveBeenCalledTimes(2);
  });

  it('re-reads a new column or a new cut 100 ms after the last query started, serving the old reading meanwhile', async () => {
    const { host, stacks } = hostOf(await engineWith(patch()));
    const { surface, advance } = clocked();
    surface.read(host, 0, 1.5, 1.5, null);
    await settle();
    advance(99);
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(63); // panned onto the hill: too soon to ask again
    expect(stacks).toHaveBeenCalledTimes(1);
    advance(1);
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(63);
    expect(stacks).toHaveBeenCalledTimes(2);
    await settle();
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(66);

    advance(99);
    expect(surface.read(host, 0, 0.5, 0.5, 65)).toBe(66); // x-ray comes on over the same column
    expect(stacks).toHaveBeenCalledTimes(2);
    advance(1);
    expect(surface.read(host, 0, 0.5, 0.5, 65)).toBe(66);
    expect(stacks).toHaveBeenCalledTimes(3);
    await settle();
    expect(surface.read(host, 0, 0.5, 0.5, 65)).toBe(64);

    // Panned off the loaded world: no surface is known there, so the reading goes back to null.
    advance(100);
    expect(surface.read(host, 0, 40.5, 40.5, 65)).toBe(64);
    await settle();
    expect(surface.read(host, 0, 40.5, 40.5, 65)).toBeNull();
    expect(stacks).toHaveBeenCalledTimes(4);
  });

  it('keeps one query in flight', async () => {
    const { host, stacks, asked } = manualHost();
    const { surface, advance } = clocked();
    surface.read(host, 0, 0.5, 0.5, null);
    advance(600);
    surface.read(host, 0, 0.5, 0.5, null); // due again, but the first is unanswered
    advance(600);
    surface.read(host, 0, 9.5, 9.5, null); // and on a new column
    expect(stacks).toHaveBeenCalledTimes(1);
    asked[0]!.resolve([0, 70, 0, 1, 0, 1]);
    await settle();
    expect(surface.read(host, 0, 9.5, 9.5, null)).toBe(70); // the answer is served and the waiting column is asked
    expect(stacks).toHaveBeenCalledTimes(2);
    expect(stacks).toHaveBeenLastCalledWith(0, [9, 0, 9], [9, 319, 9], [...SURFACE_SKIP], 1, 0, 1);
  });

  it('keeps the last reading when a query fails, and asks again when it is next due', async () => {
    const { host, stacks, asked } = manualHost();
    const { surface, advance } = clocked();
    surface.read(host, 0, 0.5, 0.5, null);
    asked[0]!.resolve([0, 70, 0, 1, 0, 1]);
    await settle();
    advance(500);
    surface.read(host, 0, 0.5, 0.5, null);
    asked[1]!.reject(new Error('mesh worker failed'));
    await settle();
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(70);
    expect(stacks).toHaveBeenCalledTimes(2);

    stacks.mockImplementationOnce(() => {
      throw new Error('host gone');
    });
    advance(500);
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(70); // a host that throws outright does not reach the listener
    await settle();
    advance(500);
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(70);
    expect(stacks).toHaveBeenCalledTimes(4);
  });

  it("starts over on a new host and drops the old one's late answer", async () => {
    const old = manualHost();
    const fresh = manualHost();
    const { surface, advance } = clocked();
    surface.read(old.host, 0, 0.5, 0.5, null);
    old.asked[0]!.resolve([0, 70, 0, 1, 0, 1]);
    await settle();
    advance(500);
    expect(surface.read(old.host, 0, 0.5, 0.5, null)).toBe(70); // asked a second time, not answered yet
    expect(old.stacks).toHaveBeenCalledTimes(2);

    // The viewer was remounted: nothing is carried over and the new world is asked at once.
    expect(surface.read(fresh.host, 0, 0.5, 0.5, null)).toBeNull();
    expect(fresh.stacks).toHaveBeenCalledTimes(1);
    old.asked[1]!.resolve([0, 90, 0, 1, 0, 1]);
    await settle();
    expect(surface.read(fresh.host, 0, 0.5, 0.5, null)).toBeNull();
    fresh.asked[0]!.resolve([0, 64, 0, 1, 0, 1]);
    await settle();
    expect(surface.read(fresh.host, 0, 0.5, 0.5, null)).toBe(64);
    expect(fresh.stacks).toHaveBeenCalledTimes(1);
  });

  it('asks again after 2 s when an answer never comes', async () => {
    const { host, stacks, asked } = manualHost();
    const { surface, advance } = clocked();
    surface.read(host, 0, 0.5, 0.5, null);
    advance(2000);
    surface.read(host, 0, 0.5, 0.5, null);
    expect(stacks).toHaveBeenCalledTimes(1);
    advance(1);
    surface.read(host, 0, 0.5, 0.5, null);
    expect(stacks).toHaveBeenCalledTimes(2);
    asked[0]!.resolve([0, 90, 0, 1, 0, 1]); // the query that was given up on answers after all: superseded
    await settle();
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBeNull();
    asked[1]!.resolve([0, 70, 0, 1, 0, 1]);
    await settle();
    expect(surface.read(host, 0, 0.5, 0.5, null)).toBe(70);
    expect(stacks).toHaveBeenCalledTimes(2);
  });
});
