import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';
import { aimAt, beyondOwnCell, botAlong, leaveCell, outlineOf, rayBox, type BlockPick } from '../src/render/aim';
import { WorldViewerEngine } from '../src/render/engine';
import { pick, type PickHit, type V3 } from '../src/mesh/query';
import { buildStateTable } from '../src/mesh/states';
import { blockName } from '../src/world/blocks';
import { storeWith, type B } from './helpers';

/** A fixture world: a stone floor at y 63 around the origin and a wall of planks at z = 10 (x -2..2, y 64..66). */
async function fixture() {
  const blocks: B[] = [];
  for (let x = -8; x <= 8; x++) for (let z = -8; z <= 16; z++) blocks.push([x, 63, z, 1]);
  for (let x = -2; x <= 2; x++) for (let y = 64; y <= 66; y++) blocks.push([x, y, 10, 5]);
  const store = await storeWith(blocks);
  const table = buildStateTable('b1.8.1');
  const pickBlock = (o: V3, d: V3, max: number) => Promise.resolve(pick(store, table, 0, o, d, max));
  return { pickBlock, nameOf: (id: number) => blockName('b1.8.1', id) };
}

// The camera at the screen centre: eye at (0.5, 65.62, 0.5) looking along +z (yaw 0 = south in Minecraft terms).
const eye: V3 = [0.5, 65.62, 0.5];
const south: V3 = [0, 0, 1];

describe('crosshair aim', () => {
  it('hits the block at the screen centre: the wall in front, with its name and the face it entered', async () => {
    const { pickBlock, nameOf } = await fixture();
    const t = await aimAt(eye, south, [], pickBlock, nameOf);
    expect(t).toMatchObject({ kind: 'block', x: 0, y: 65, z: 10, id: 5 });
    expect(t?.kind === 'block' && t.name).toMatch(/plank|wood/i);
    expect(t?.distance).toBeCloseTo(9.5, 1);
  });

  it('a bot standing between the camera and the wall is what the crosshair selects', async () => {
    const { pickBlock, nameOf } = await fixture();
    const bots = [
      { name: 'maid_chan_102', x: 0.5, y: 64, z: 5.5 },
      { name: 'maid_chan_114', x: 4.5, y: 64, z: 5.5 }, // off to the side: not under the crosshair
    ];
    const t = await aimAt(eye, south, bots, pickBlock, nameOf);
    expect(t).toMatchObject({ kind: 'bot', name: 'maid_chan_102' });
    expect(t?.distance).toBeCloseTo(4.7, 1);
  });

  it('a bot behind the wall is hidden by it; looking down picks the floor', async () => {
    const { pickBlock, nameOf } = await fixture();
    const hidden = await aimAt(eye, south, [{ name: 'maid_chan_124', x: 0.5, y: 64, z: 13.5 }], pickBlock, nameOf);
    expect(hidden).toMatchObject({ kind: 'block', z: 10 });
    const down = await aimAt(eye, [0, -1, 0.2], [], pickBlock, nameOf);
    expect(down).toMatchObject({ kind: 'block', y: 63, id: 1 });
  });

  it('looking at the sky aims at nothing', async () => {
    const { pickBlock, nameOf } = await fixture();
    expect(await aimAt(eye, [0, 1, 0], [], pickBlock, nameOf)).toBeNull();
  });

  it('ray/box and bot hit boxes', () => {
    expect(rayBox([0, 0, 0], [1, 0, 0], [2, -1, -1], [3, 1, 1])).toBeCloseTo(2);
    expect(rayBox([0, 0, 0], [-1, 0, 0], [2, -1, -1], [3, 1, 1])).toBeNull();
    expect(botAlong([0, 1, 0], [1, 0, 0], [{ name: 'a', x: 5, y: 0, z: 0 }], 4)).toBeNull(); // beyond reach
    expect(botAlong([0, 1, 0], [1, 0, 0], [{ name: 'a', x: 5, y: 0, z: 0 }], 10)?.bot.name).toBe('a');
    expect(
      outlineOf({ kind: 'block', x: 1, y: 2, z: 3, id: 1, meta: 0, face: 0, name: 'stone', distance: 1 }).min,
    ).toEqual([0.998, 1.998, 2.998]);
  });
});

const LADDER = 65;
const STONE = 1;

/** A corridor for first-person picks: a stone floor at y 63 and whatever else the test stands in it. */
async function corridor(extra: B[]): Promise<BlockPick> {
  const blocks: B[] = [...extra];
  for (let x = -2; x <= 2; x++) for (let z = -2; z <= 8; z++) blocks.push([x, 63, z, STONE]);
  const store = await storeWith(blocks);
  const table = buildStateTable('b1.8.1');
  return (o, d, max) => Promise.resolve(pick(store, table, 0, o, d, max));
}

describe('a first-person pick that starts outside the cell of the eye', () => {
  it('looks past a ladder in that cell and reports the face the ray enters by', async () => {
    const pickBlock = await corridor([
      [0, 65, 0, LADDER, 3],
      [0, 65, 3, STONE],
    ]);
    // The whole-cell pick answers the cell the eye is in for every direction, with no face.
    expect(await pickBlock(eye, south, 4.25)).toMatchObject({ x: 0, y: 65, z: 0, id: LADDER, face: -1 });
    expect(await pickBlock(eye, [1, 0, 0], 4.25)).toMatchObject({ x: 0, y: 65, z: 0, id: LADDER, face: -1 });
    const beyond = beyondOwnCell(pickBlock);
    expect(await beyond(eye, south, 4.25)).toMatchObject({ x: 0, y: 65, z: 3, id: STONE, face: 2 });
    expect(await beyond(eye, [1, 0, 0], 4.25)).toBeNull();
    expect(await beyond(eye, [0, -1, 0.2], 4.25)).toMatchObject({ x: 0, y: 63, z: 0, id: STONE, face: 1 });
    const t = await aimAt(eye, south, [], beyond, () => 'stone', 4.25);
    expect(t).toMatchObject({ kind: 'block', x: 0, y: 65, z: 3, face: 2 });
    expect(t?.distance).toBeCloseTo(2.5, 9); // from the eye, not from where the pick began
  });

  it('gives the neighbouring cell its entry face in all six directions', async () => {
    const beyond = beyondOwnCell(
      await corridor([
        [0, 65, 0, LADDER, 3],
        [1, 65, 0, STONE],
        [-1, 65, 0, STONE],
        [0, 66, 0, STONE],
        [0, 64, 0, STONE],
        [0, 65, 1, STONE],
        [0, 65, -1, STONE],
      ]),
    );
    const faces: Array<[V3, Partial<PickHit>]> = [
      [[1, 0, 0], { x: 1, face: 4 }],
      [[-1, 0, 0], { x: -1, face: 5 }],
      [[0, 1, 0], { y: 66, face: 0 }],
      [[0, -1, 0], { y: 64, face: 1 }],
      [[0, 0, 1], { z: 1, face: 2 }],
      [[0, 0, -1], { z: -1, face: 3 }],
    ];
    for (const [d, want] of faces) expect(await beyond(eye, d, 4.25), d.join()).toMatchObject({ id: STONE, ...want });
  });

  it('leaves at once from an eye that sits exactly on a cell boundary', async () => {
    const beyond = beyondOwnCell(
      await corridor([
        [1, 65, 0, LADDER, 3],
        [0, 65, 0, STONE],
      ]),
    );
    // x = 1 belongs to cell 1; looking west the ray is in cell 0 after no distance at all.
    expect(leaveCell([1, 65.62, 0.5], [-1, 0, 0])).toMatchObject({ travelled: 0, face: 5 });
    expect(await beyond([1, 65.62, 0.5], [-1, 0, 0], 4.25)).toMatchObject({ x: 0, y: 65, z: 0, id: STONE, face: 5 });
    expect(leaveCell(eye, [0, 0, 0])).toBeNull();
    expect(await beyond(eye, [0, 0, 0], 4.25)).toBeNull();
  });

  it('takes the cells in the order of the worker when the ray leaves through an edge', async () => {
    // From the middle of a cell along a diagonal, x and y cross at the same distance. The worker's walk steps y first.
    const middle: V3 = [0.5, 65.5, 0.5];
    const blocks: B[] = [
      [0, 66, 0, STONE],
      [1, 65, 0, 5],
      [1, 66, 0, 4],
    ];
    const above = { x: 0, y: 66, z: 0, id: STONE, face: 0 };
    const plain = await corridor(blocks);
    expect(await plain(middle, [1, 1, 0], 4.25)).toMatchObject(above);
    expect(await beyondOwnCell(plain)(middle, [1, 1, 0], 4.25)).toMatchObject(above);
    // With that cell empty, both go on into the diagonal one, never into the cell beside the eye.
    const diagonal = { x: 1, y: 66, z: 0, id: 4, face: 4 };
    const open = await corridor(blocks.slice(1));
    expect(await open(middle, [1, 1, 0], 4.25)).toMatchObject(diagonal);
    expect(await beyondOwnCell(open)(middle, [1, 1, 0], 4.25)).toMatchObject(diagonal);
  });

  it('measures the reach from the eye, exactly as the plain pick does', async () => {
    const pickBlock = await corridor([[0, 65, 5, STONE]]);
    const beyond = beyondOwnCell(pickBlock);
    // The near face of the wall is 4.2 blocks from the first eye and 4.3 from the second.
    for (const [z, hit] of [
      [0.8, true],
      [0.7, false],
    ] as const) {
      const from: V3 = [0.5, 65.62, z];
      expect((await pickBlock(from, south, 4.25)) !== null, `plain pick from z ${z}`).toBe(hit);
      expect((await beyond(from, south, 4.25)) !== null, `pick beyond the cell from z ${z}`).toBe(hit);
    }
    expect(await beyond([0.5, 65.62, 0.8], south, 4.25)).toMatchObject({ x: 0, y: 65, z: 5, face: 2 });
  });
});

type Picked = { o: V3; max: number };

/** An engine with just enough behind it to aim: its camera at `from` looking south, over the given block pick. */
function aimingEngine(
  controls: 'player' | 'default',
  from: V3,
  pickBlock: BlockPick,
  picked: Picked[] = [],
): WorldViewerEngine {
  const camera = new THREE.PerspectiveCamera();
  camera.position.set(...from);
  camera.lookAt(from[0], from[1], from[2] + 10);
  return Object.assign(Object.create(WorldViewerEngine.prototype), {
    opts: { controls },
    camera,
    dim: 0,
    layers: { bots: false },
    levelCut: null,
    followName: null,
    disposed: false,
    aimRequest: 0,
    aimed: null,
    drawCap: null,
    runtime: {
      version: 'b1.8.1',
      viewerQuality: { drawDistance: 96 },
      store: { bots: () => [] },
      host: {
        pick: (_dim: number, o: V3, d: V3, max: number) => {
          picked.push({ o, max });
          return pickBlock(o, d, max);
        },
      },
    },
  }) as WorldViewerEngine;
}

describe('the takeover crosshair (player controls)', () => {
  it('reaches 4.25 blocks unless the session says otherwise, and never targets the cell of the eye', async () => {
    const pickBlock = await corridor([
      [0, 65, 0, LADDER, 3],
      [0, 65, 5, STONE],
    ]);
    const picked: Picked[] = [];
    const near = aimingEngine('player', [0.5, 65.62, 0.8], pickBlock, picked); // the wall is 4.2 blocks away
    expect(await near.aimNow()).toMatchObject({ kind: 'block', x: 0, y: 65, z: 5, id: STONE, face: 2 });
    expect(near.aim()).toMatchObject({ z: 5 });
    // The worker was asked from the far side of the eye's cell, for what is left of the reach.
    expect(picked[0].o[2]).toBe(1);
    expect(picked[0].max).toBeCloseTo(4.05, 9);
    const far = aimingEngine('player', [0.5, 65.62, 0.7], pickBlock); // 4.3 blocks away
    expect(await far.aimNow()).toBeNull();
    far.setPlayerReach(4.5);
    expect(await far.aimNow()).toMatchObject({ kind: 'block', z: 5 });
    far.setPlayerReach(undefined);
    expect(await far.aimNow()).toBeNull();
  });

  it('leaves the other views picking from the camera itself, out to the draw distance', async () => {
    const pickBlock = await corridor([
      [0, 65, 0, LADDER, 3],
      [0, 65, 5, STONE],
    ]);
    const picked: Picked[] = [];
    const engine = aimingEngine('default', [0.5, 65.62, 0.7], pickBlock, picked);
    engine.setPlayerReach(2);
    expect(await engine.aimNow()).toMatchObject({ kind: 'block', x: 0, y: 65, z: 0, id: LADDER, face: -1 });
    expect(picked).toHaveLength(1);
    expect(picked[0].o[2]).toBeCloseTo(0.7, 12);
    expect(picked[0].max).toBe(96 + 32);
  });

  it('is re-aimed about thirty times a second, and the other views still ten', async () => {
    const aims = async (controls: 'player' | 'default') => {
      const aimNow = vi.fn(() => Promise.resolve(null));
      const engine = Object.assign(Object.create(WorldViewerEngine.prototype), {
        opts: { controls },
        rig: { mode: 'fly' },
        locked: () => true,
        aimNow,
        aimed: null,
        aimAtMs: 0,
        aimBusy: false,
      }) as { updateAim(now: number): void };
      // One second of frames at 60 fps; each pick has answered before the next frame.
      for (let frame = 0; frame < 60; frame++) {
        engine.updateAim(1000 + (frame * 1000) / 60);
        await Promise.resolve();
        await Promise.resolve();
      }
      return aimNow.mock.calls.length;
    };
    const player = await aims('player');
    expect(player).toBeGreaterThanOrEqual(28);
    expect(player).toBeLessThanOrEqual(31);
    const other = await aims('default');
    expect(other).toBeGreaterThanOrEqual(9);
    expect(other).toBeLessThanOrEqual(10);
  });
});
