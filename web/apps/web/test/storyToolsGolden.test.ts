/**
 * Goldens for the Story tools' pure functions (tools.ts): the right-click menu for every target kind and selection, the build item
 * of every tool over drags and options, the height-mode targets over synthetic columns, failed cells, design parsing and the
 * design / mine build requests. They pin the requests and labels so the functions can be restructured.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  buildItem,
  contextMenu,
  designBlocks,
  designItems,
  failedCells,
  FURNITURE_TOOLS,
  heightTargets,
  HEIGHT_MODES,
  mineItems,
  NOT_GROUND_IDS,
  STRUCTURE_TOOLS,
  WATER_TOOLS,
  type BuildOpts,
  type BuildToolId,
  type DesignBlock,
  type Target,
} from '@/features/story/tools';

type V3 = [number, number, number];

beforeEach(() => {
  vi.spyOn(Date, 'now').mockImplementation(() => 1_762_000_000_000);
});
afterEach(() => vi.restoreAllMocks());

describe('contextMenu', () => {
  it('every target kind and selection', () => {
    const targets: Record<string, Target> = {
      ghost: { kind: 'ghost', build: 'b7', label: 'Hut wall', x: 1.5, y: 64.2, z: -3.7 },
      animal: { kind: 'mob', entity: 8, name: 'Cow', animal: true, x: 6.5, y: 64, z: 6.5 },
      monster: { kind: 'mob', entity: 9, name: 'Zombie', animal: false, x: -6.5, y: 64, z: 6.5 },
      player: { kind: 'player', entity: 13, name: 'Steve', bot: false, x: 30.5, y: 64, z: 30.5 },
      bot: { kind: 'player', entity: null, name: 'maid_chan_2', bot: true, x: 3, y: 64, z: 3 },
      item: { kind: 'item', entity: 7, name: 'Cobblestone', x: 5.5, y: 64, z: 5.5 },
      ground: { kind: 'ground', x: -0.5, y: 63, z: 0.5 },
    };
    for (const [id, name] of [
      [17, 'Log'],
      [18, 'Leaves'],
      [161, 'Acacia leaves'],
      [162, 'Acacia log'],
      [54, 'Chest'],
      [146, 'Trapped chest'],
      [58, 'Crafting table'],
      [61, 'Furnace'],
      [62, 'Lit furnace'],
      [1, 'Stone'],
    ] as const)
      targets[`block ${id}`] = { kind: 'block', x: 2, y: 63, z: -1, id, name };
    const out: Record<string, unknown> = {};
    for (const [name, t] of Object.entries(targets))
      for (const [hasPawns, hasAi] of [
        [true, true],
        [true, false],
        [false, true],
        [false, false],
      ] as const)
        out[`${name} ${hasPawns ? 'pawns' : '-'} ${hasAi ? 'ai' : '-'}`] = contextMenu(t, hasPawns, hasAi);
    expect(out).toMatchSnapshot();
  });
});

describe('buildItem', () => {
  it('every tool over drags and options', () => {
    const base: BuildOpts = { height: 3, roof: 'gable', overhang: false, facing: 'e', material: 'oak_planks' };
    const opts: Record<string, BuildOpts> = {
      base,
      tall: { ...base, height: 40, overhang: true, roofMaterial: 'cobblestone', material: [61, 0], facing: 'n' },
      low: { ...base, height: -2, roof: 'flat', line: true, material: [5, 1], facing: 'w' },
      dig: { ...base, dig: true, material: [53, 0], facing: 's' },
      noDig: { ...base, dig: false },
    };
    const drags: Record<string, [V3, V3]> = {
      point: [
        [1, 64, 2],
        [1, 64, 2],
      ],
      xLine: [
        [1, 64, 2],
        [6, 64, 4],
      ],
      zLine: [
        [1, 64, 2],
        [2, 66, 8],
      ],
      box: [
        [-3, 64, -3],
        [3, 67, 3],
      ],
    };
    const tools: BuildToolId[] = [
      ...STRUCTURE_TOOLS.map((s) => s.id),
      ...FURNITURE_TOOLS.map((f) => `furniture:${f.id}` as BuildToolId),
      ...WATER_TOOLS.map((w) => w.id),
    ];
    const out: Record<string, unknown> = {};
    for (const tool of tools)
      for (const [dn, [a, b]] of Object.entries(drags))
        for (const [on, o] of Object.entries(opts)) out[`${tool} ${dn} ${on}`] = buildItem(tool, a, b, o);
    expect(out).toMatchSnapshot();
  });
});

describe('heightTargets', () => {
  it('every mode, tool and filter over synthetic columns', () => {
    // [x, y, z, id, meta, below] per cell, each column from the top down.
    const col = (x: number, z: number, cells: Array<[number, number, number?, number?]>) =>
      cells.flatMap(([y, id, meta = 0, below = 1]) => [x, y, z, id, meta, below]);
    const stacks = [
      ...col(0, 0, [
        [70, 0],
        [66, 31],
        [65, 2],
        [64, 3],
        [63, 1],
        [62, 14],
        [61, 1, 0, 0],
      ]),
      ...col(0, 1, [
        [68, 18],
        [67, 17],
        [66, 17],
        [65, 2],
        [64, 3],
        [63, 13],
      ]),
      ...col(1, 0, [
        [66, 78],
        [65, 12],
        [64, 24],
        [63, 15],
        [60, 7],
      ]),
      ...col(1, 1, [[64, 0]]),
      ...col(2, 0, [
        [64, 4],
        [63, 4],
        [62, 4],
        [61, 4],
        [60, 4],
        [59, 4],
      ]),
      ...col(2, 2, [
        [65, NOT_GROUND_IDS[0]!],
        [64, 2],
        [63, 56],
      ]),
    ];
    const out: Record<string, unknown> = {};
    for (const tool of ['mine', 'deconstruct'] as const)
      for (const h of HEIGHT_MODES)
        for (const filter of ['all', 'ores', 'stone', 'soil'] as const)
          for (const level of [63, 65])
            out[`${tool} ${h.id} ${filter} ${level}`] = heightTargets(tool, h.id, stacks, level, filter);
    out.empty = heightTargets('mine', 'surface', [], 64);
    out.defaultFilter = heightTargets('mine', 'surface', stacks, 64);
    out.ragged = heightTargets('mine', 'surface', stacks.slice(0, 9), 64);
    expect(out).toMatchSnapshot();
  });
});

describe('failedCells', () => {
  it('states, reasons and rects', () => {
    const ghosts = [
      [0, 64, 0, 1, 0, 'b1', 'blocked'],
      [1, 64, 0, 1, 0, 'b1', 'blocked', null, 'no path'],
      [2, 64, 5, 1, 0, 'b2', 'waiting', null, "can't reach"],
      [3, 64, 5, 1, 0, 'b2', 'waiting'],
      [4, 64, -5, 1, 0, 3, 'done', null, 'old'],
      [5, 64, -5, 1, 0, 3, 'unsupported', null, 'floating'],
      [-6, 64, 9, 1, 0, 4, 'blocked', 'x', null],
    ] as const;
    expect({
      all: failedCells(ghosts as never),
      rect: failedCells(ghosts as never, [0, 0, 3, 5]),
      flipped: failedCells(ghosts as never, [3, 5, 0, 0]),
      none: failedCells(ghosts as never, [100, 100, 101, 101]),
      left: failedCells(ghosts as never, [-10, 0, -1, 10]),
    }).toMatchSnapshot();
  });
});

describe('designs', () => {
  it('designBlocks over shapes of JSON', () => {
    const inputs: Record<string, unknown> = {
      null: null,
      number: 4,
      empty: [],
      tuples: [
        [5, 70, 5, 1],
        [6, 71, 5, 4, 2],
        [7, 70, 6, 0, 0],
      ],
      shortTuples: [
        [1, 2, 3],
        [4, 5, 6],
      ],
      objects: [
        { x: 1, y: 2, z: 3, id: 5, meta: 1 },
        { x: 2, y: 2, z: 3, block: 4, data: 2 },
        { x: 3, y: 2, z: 3, id: 0 },
      ],
      objectsBad: [{ x: 1, y: 2, z: 3 }],
      nested: { meta: { name: 'Hut' }, design: { layers: [[], { blocks: [[0, 0, 0, 17]] }] } },
      deep: { a: { b: { c: { d: { e: { f: { g: { h: [[0, 0, 0, 1]] } } } } } } } },
      mixed: [[1, 2, 3, 4], { x: 1, y: 2, z: 3, id: 4 }],
      allAir: [[0, 0, 0, 0]],
    };
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(inputs)) out[k] = designBlocks(v);
    expect(out).toMatchSnapshot();
  });

  it('mineItems and designItems', () => {
    const placed: DesignBlock[] = [];
    for (let i = 0; i < 9000; i++) placed.push([i % 50, 60 - Math.floor(i / 2500), Math.floor(i / 50) % 50, 0, 0]);
    for (let i = 0; i < 300; i++) placed.push([i % 7, 61, i % 11, i % 3 ? 50 : 65, i % 4]);
    const small: DesignBlock[] = [
      [0, 64, 0, 4, 0],
      [1, 64, 0, 4, 0],
      [0, 65, 0, 5, 2],
      [0, 63, 0, 0, 0],
      [1, 62, 0, 0, 0],
    ];
    const blocksOnly: DesignBlock[] = small.filter((b) => b[3] !== 0);
    const longName = 'A very long design name that goes past forty characters';
    const shape = (items: ReturnType<typeof mineItems>) =>
      items.map((it) => ({
        ...it,
        blocks: it.blocks?.length,
        place: it.place?.length,
        first: it.blocks?.[0],
        firstPlace: it.place?.[0],
      }));
    const many: DesignBlock[] = [];
    for (let i = 0; i < 70; i++) many.push([i, 64, 0, 1 + i, i % 3]); // replacement must never silently truncate a design
    expect(() => designItems(many)).toThrow('more than 64 build items');
    expect({
      mineBig: shape(mineItems(placed, longName, 'g'.repeat(50))),
      mineSmall: mineItems(small),
      minePutOnly: mineItems(blocksOnly, 'Hut'),
      mineFull: shape(mineItems(placed.slice(0, 4096).concat(blocksOnly), undefined, 'grp')),
      designSmall: designItems(small),
      designNamed: designItems(small, longName, 'grp'),
      designBlocksOnly: designItems(blocksOnly, 'Hut'),
      designBig: shape(designItems(placed, 'Big')),
    }).toMatchSnapshot();
  });
});
