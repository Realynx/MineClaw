/**
 * `expandBuildItem` over a grid of every build kind × materials × facings × shapes × heights / roofs / placements, pinned as
 * one digest per kind: a restructuring of the expansion must keep every block (and its order) the same.
 */
import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import {
  expandBuildItem,
  liquidOf,
  matchesTool,
  STORY_BUILD_KINDS,
  STORY_DESIGNATION_KINDS,
  targetPos,
  type StoryArea,
  type StoryBuildItem,
  type StoryDesignationKind,
  type StoryMineFilter,
  type StoryTargetAny,
} from '../src/index.js';

const MATERIALS: Array<StoryBuildItem['material']> = [
  undefined,
  'oak_planks',
  'cobblestone',
  'glass',
  'nonsense',
  'water',
  'lava',
  [64, 0],
  [85, 0],
  [53, 0],
  [4, 0],
  [5, 2],
  [10, 0],
];
const FACINGS: Array<StoryBuildItem['facing']> = [undefined, 'n', 's', 'e', 'w'];
const AREAS: Array<StoryArea | undefined> = [
  undefined,
  [0, 64, 0, 0, 64, 0],
  [0, 64, 0, 5, 64, 0],
  [3, 66, 4, 0, 64, 0],
  [0, 64, 0, 2, 64, 6],
];
const BLOCKS: Array<StoryBuildItem['blocks']> = [
  undefined,
  [
    [1, 64, 1],
    [1, 64, 1],
    [2, 65, 1],
  ],
];
const ROOFS: Array<StoryBuildItem['roof']> = [
  undefined,
  { style: 'flat' },
  { style: 'slab' },
  { style: 'gable' },
  { style: 'none' },
  { style: 'gable', overhang: 1 },
  { style: 'flat', overhang: 1, material: 'stone_bricks' },
];
const PLACES: Array<StoryBuildItem['place']> = [
  undefined,
  [
    [1, 64, 1, 50, 5],
    [9, 9, 9, 65, 2],
  ],
];

function* items(kind: StoryBuildItem['kind']): Generator<StoryBuildItem> {
  for (const material of MATERIALS)
    for (const facing of FACINGS)
      for (const area of AREAS)
        for (const blocks of BLOCKS)
          for (const height of [undefined, 3])
            for (const roof of kind === 'roof' ? ROOFS : [undefined])
              for (const place of kind === 'mine' ? PLACES : [undefined])
                yield { kind, material, facing, area, blocks, height, roof, place };
}

describe('designate tool matching, liquids and target positions over every input', () => {
  it('stays the same (digests)', () => {
    const h = createHash('sha256');
    for (const kind of [...STORY_DESIGNATION_KINDS, 'nonsense'] as StoryDesignationKind[])
      for (const filter of ['all', 'ores', 'stone', 'soil', undefined, 'odd'] as Array<StoryMineFilter | undefined>)
        for (let id = 0; id < 256; id++)
          for (let meta = 0; meta < 16; meta++)
            for (const below of [0, id, 3]) h.update(matchesTool(kind, id, meta, below, filter) ? '1' : '0');
    const liquids = [
      undefined,
      null,
      'water',
      ' Lava ',
      'still_water',
      'flowing_lava',
      'lava_bucket',
      'mud',
      [8, 0],
      [9, 0],
      [10, 0],
      [11, 0],
      [1, 0],
    ].map((m) => liquidOf(m as StoryBuildItem['material']));
    const targets = [
      null,
      undefined,
      '1,2,3',
      '1,2',
      '1,x,3',
      [4, 5, 6],
      { x: 1, z: 2 },
      { x: 1, y: 3, z: 2 },
      { x: 1 },
      { area: [0, 64, 0, 4, 66, 3] },
      { entity: 7 },
      {},
    ].map((t) => targetPos(t as StoryTargetAny));
    expect({ match: h.digest('hex').slice(0, 16), liquids, targets }).toMatchSnapshot();
  });
});

describe('build expansion: every kind over a grid of options', () => {
  it('stays block for block the same (one digest per kind)', () => {
    const digests: Record<string, string> = {};
    for (const kind of STORY_BUILD_KINDS) {
      const h = createHash('sha256');
      let n = 0;
      for (const item of items(kind)) {
        const out = expandBuildItem(item);
        n += out.length;
        h.update(JSON.stringify(out));
      }
      digests[kind] = `${n}:${h.digest('hex').slice(0, 16)}`;
    }
    expect(digests).toMatchSnapshot();
  });
});
