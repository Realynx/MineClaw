import { describe, expect, it } from 'vitest';
import { designItems, placeDesign, type DesignBlock } from '@/features/story/tools';

describe('Story design placement rotation', () => {
  it.each([
    ['n', 0, 1, 2],
    ['e', -1, 0, 1],
    ['s', 0, -1, 3],
    ['w', 1, 0, 0],
  ] as const)('keeps a spruce staircase rising along its rotated run (%s)', (facing, dx, dz, meta) => {
    const stairs: DesignBlock[] = [
      [0, 0, 0, 134, 2],
      [0, 1, 1, 134, 2],
    ];
    const placed = placeDesign(stairs, [100, 64, 200], facing, false, '1.12.2');
    expect(placed).toEqual([
      [100, 64, 200, 134, meta],
      [100 + dx, 65, 200 + dz, 134, meta],
    ]);
    const [build] = designItems(placed, 'StoreHouse');
    expect(build?.material).toEqual([134, meta]);
    expect(build?.blocks).toEqual(placed.map((b) => b.slice(0, 3)));
    expect(stairs).toEqual([
      [0, 0, 0, 134, 2],
      [0, 1, 1, 134, 2],
    ]);
  });

  it('rotates directional state together while retaining half, variant, and upper-cell bits', () => {
    const states: DesignBlock[] = [
      [0, 0, 0, 134, 6], // upside-down spruce stairs south -> west
      [1, 0, 0, 61, 2], // furnace front north -> east
      [2, 0, 0, 17, 5], // spruce log x -> z
      [3, 0, 0, 64, 3], // door north -> east
      [3, 1, 0, 64, 11], // its upper half
      [4, 0, 0, 50, 1], // torch on west wall -> north wall
      [5, 0, 0, 66, 2], // rail ascending east -> south
      [6, 0, 0, 5, 2], // birch planks keep their variant
    ];
    expect(placeDesign(states, [0, 0, 0], 'e', false, '1.12.2').map((b) => b[4])).toEqual([5, 5, 9, 0, 8, 3, 5, 2]);
  });

  it('uses the server version for log axes and keeps mine elevations absolute', () => {
    const blocks: DesignBlock[] = [
      [1, 20, 2, 17, 5],
      [1, 21, 2, 65, 2],
    ];
    expect(placeDesign(blocks, [100, 64, 200], 'e', true, 'b1.8.1')).toEqual([
      [98, 20, 201, 17, 5],
      [98, 21, 201, 65, 5],
    ]);
    expect(placeDesign(blocks, [100, 64, 200], 'e', true, '1.12.2')[0]).toEqual([98, 20, 201, 17, 9]);
  });
});
