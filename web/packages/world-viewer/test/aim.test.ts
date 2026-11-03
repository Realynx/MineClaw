import { describe, expect, it } from 'vitest';
import { aimAt, botAlong, outlineOf, rayBox } from '../src/render/aim';
import { pick, type V3 } from '../src/mesh/query';
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
    expect(outlineOf({ kind: 'block', x: 1, y: 2, z: 3, id: 1, meta: 0, face: 0, name: 'stone', distance: 1 }).min).toEqual([0.998, 1.998, 2.998]);
  });
});
