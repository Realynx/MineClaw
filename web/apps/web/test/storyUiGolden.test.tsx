/**
 * Goldens for the Story UI panels: each component rendered over colony states from the mock director (storyUiFixture.ts) and
 * hand-made variants that reach every branch, the resulting DOM snapshotted. They pin the markup — text, attributes, classes,
 * `data-testid`s — so the components can be split into smaller ones without changing what the page shows.
 */
import { cleanup, fireEvent, render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { MaterialBar } from '@/features/story/MaterialBar';
import { materialsFor } from '@/features/story/materials';
import type { StoryBuild, StoryColonyState, StoryZone } from '@mineclaw/contracts';
import { GrowingZonesPanel } from '@/features/story/GrowingZones';
import type { MapTool } from '@/features/story/StoryMap';
import { ArchitectPanel, ColonyPanel, QueuePanel } from '@/features/story/StoryPanels';
import type { BuildOpts, BuildToolId } from '@/features/story/tools';
import { storyUiStates, type StoryUiStates } from './storyUiFixture';

afterEach(cleanup);

let S: StoryUiStates;
beforeAll(() => {
  S = storyUiStates();
});

/** Each case rendered on its own: its markup by name. */
function variants(cases: Record<string, () => ReactElement>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [name, el] of Object.entries(cases)) {
    out[name] = render(el()).container.innerHTML;
    cleanup();
  }
  return out;
}

/** Renders `el` and returns its markup (and the container, for clicks before a second look). */
function html(el: ReactElement): { out: () => string; root: HTMLElement } {
  const { container } = render(el);
  return { out: () => container.innerHTML, root: container };
}

describe('MaterialBar', () => {
  const build: BuildOpts = { height: 3, roof: 'gable', overhang: true, facing: 'e', material: [61, 0] };
  const props = (toolId: BuildToolId, extra: Partial<Parameters<typeof MaterialBar>[0]> = {}) => {
    const options = toolId.startsWith('furniture:') ? null : materialsFor(toolId as 'wall', S.architect.entries);
    return {
      toolId,
      options,
      current: options?.[1]?.key ?? null,
      recent: (options ?? []).slice(0, 3),
      query: '',
      onQuery: vi.fn(),
      onPick: vi.fn(),
      onHover: vi.fn(),
      build,
      onBuild: vi.fn(),
      onClose: vi.fn(),
      ...extra,
    };
  };

  it('every tool, wide', () => {
    const out: Record<string, string> = {};
    for (const tool of [
      'wall',
      'floor',
      'roof',
      'door',
      'stairs',
      'block',
      'fence',
      'furniture:bed',
      'furniture:chest',
    ] as BuildToolId[]) {
      out[tool] = html(<MaterialBar {...props(tool)} />).out();
      cleanup();
    }
    out['block, no turn'] = html(<MaterialBar {...props('block', { build: { ...build, material: [5, 0] } })} />).out();
    cleanup();
    out['no match'] = html(<MaterialBar {...props('wall', { options: [], recent: [], query: 'zzz' })} />).out();
    cleanup();
    out['unknown current'] = html(<MaterialBar {...props('floor', { current: 'nope' })} />).out();
    expect(out).toMatchSnapshot();
  });

  it('narrow: the chip, then the opened bar', () => {
    const out: Record<string, string> = {};
    for (const tool of ['wall', 'roof', 'furniture:bed'] as BuildToolId[]) {
      const r = html(
        <div data-testid="story-mode">
          <MaterialBar {...props(tool)} />
        </div>,
      );
      out[`${tool} chip`] = r.out();
      const chip = r.root.querySelector('[data-testid="material-chip"]');
      if (chip) fireEvent.click(chip);
      out[`${tool} open`] = r.out();
      cleanup();
    }
    const none = html(
      <div data-testid="story-mode">
        <MaterialBar {...props('floor', { current: null })} />
      </div>,
    );
    out['floor no choice'] = none.out();
    expect(out).toMatchSnapshot();
  });
});

describe('ColonyPanel', () => {
  const colonies = [
    { id: 'c1', name: 'Golden', pawns: ['a', 'b'], paused: false },
    { id: 'c2', name: 'Old', pawns: [], paused: true },
  ];
  const panel = (extra: Partial<Parameters<typeof ColonyPanel>[0]>) => (
    <ColonyPanel
      state={undefined}
      colonies={colonies}
      current="c1"
      canWrite
      onSwitch={vi.fn()}
      onCreate={vi.fn()}
      centre={null}
      migrate={null}
      onMoveHome={vi.fn()}
      onConfirmMove={vi.fn()}
      onCancelMove={vi.fn()}
      onPickHome={vi.fn()}
      onDrawHouse={vi.fn()}
      onShowZone={vi.fn()}
      onRenameZone={vi.fn()}
      onDeleteZone={vi.fn()}
      onPause={vi.fn()}
      onRelease={vi.fn()}
      onResume={vi.fn()}
      {...extra}
    />
  );

  it('every state', () => {
    const o = S.ordered;
    const out = variants({
      none: () => panel({ current: null }),
      founded: () => panel({ state: S.founded, centre: [3, 64, -2], onOptions: vi.fn(), onBringAll: vi.fn() }),
      migrate: () =>
        panel({
          state: o,
          centre: [3, 64, -2],
          migrate: [5, 63, 7],
          onBringAll: vi.fn(),
          displayOf: (a) => a.toUpperCase(),
        }),
      readOnly: () => panel({ state: S.worked, canWrite: false, onOptions: vi.fn(), onBringAll: vi.fn() }),
      pausedInactive: () =>
        panel({
          state: {
            ...o,
            paused: true,
            active: false,
            home: undefined,
            seats: { ...o.seats!, capFree: 1, keptForRegulars: undefined },
          },
          onOptions: vi.fn(),
          onBringAll: vi.fn(),
        }),
      noCap: () =>
        panel({
          state: { ...o, seats: { ...o.seats!, capFree: null }, options: { ...o.options!, idle: undefined as never } },
          onOptions: vi.fn(),
          onBringAll: vi.fn(),
          blocked: [{ pawn: o.pawns[1]!.name, reason: 'server full' }] as never,
        }),
      noneOnline: () => panel({ state: { ...o, pawns: o.pawns.map((p) => ({ ...p, online: false })) } }),
    });
    expect(out).toMatchSnapshot();
  });

  it('renaming a house', () => {
    const r = html(panel({ state: S.ordered }));
    fireEvent.click(r.root.querySelector('[aria-label^="Rename"]')!);
    expect(r.out()).toMatchSnapshot();
  });
});

describe('ArchitectPanel', () => {
  const tools: Record<string, MapTool> = {
    select: { kind: 'select' },
    mine: { kind: 'designate', id: 'mine' },
    farm: { kind: 'zone', id: 'farm', op: 'add' },
    quarry: { kind: 'zone', id: 'mine', op: 'add' },
    erase: { kind: 'zone', id: 'any', op: 'remove' },
    extend: { kind: 'zone', id: 'farm', op: 'add', into: '__first__' },
    extendGone: { kind: 'zone', id: 'farm', op: 'add', into: 'nope' },
    wall: { kind: 'build', id: 'wall' },
    bed: { kind: 'build', id: 'furniture:bed' },
    channel: { kind: 'build', id: 'water_channel' },
    storage: { kind: 'storage' },
    home: { kind: 'home' },
  };
  const arch = (tool: MapTool, withPalette: boolean, lava = false) => {
    const into = tool.kind === 'zone' && tool.into === '__first__' ? { ...tool, into: S.ordered.zones[0]!.id } : tool;
    const entries = lava
      ? [...S.architect.entries, { ...S.architect.entries[0]!, id: 'lava', kind: 'lava' }]
      : S.architect.entries;
    return (
      <ArchitectPanel
        tool={into}
        onTool={vi.fn()}
        palette={withPalette ? { ...S.architect, entries } : undefined}
        onPick={withPalette ? vi.fn() : undefined}
        plants={S.ordered.plants}
        zones={S.ordered.zones}
      />
    );
  };

  it('opens on the tool’s category', () => {
    const cases: Record<string, () => ReactElement> = {};
    for (const [name, t] of Object.entries(tools)) cases[name] = () => arch(t, true);
    expect(variants(cases)).toMatchSnapshot();
  });

  it('every category, with and without the palette', () => {
    const out: Record<string, string> = {};
    for (const [mode, withPalette, lava] of [
      ['palette', true, false],
      ['lava', true, true],
      ['none', false, false],
    ] as const) {
      const r = html(arch(tools.wall!, withPalette, lava));
      for (const b of [...r.root.querySelectorAll<HTMLButtonElement>('section[data-testid="architect"] > div:first-child > button')]) {
        fireEvent.click(b);
        out[`${mode} ${b.textContent}`] = r.out();
      }
      cleanup();
    }
    expect(out).toMatchSnapshot();
  });
});

describe('GrowingZonesPanel', () => {
  const grow = (state: StoryColonyState | undefined, canWrite = true, irrigate = true) => (
    <GrowingZonesPanel
      state={state}
      canWrite={canWrite}
      onUpdate={vi.fn()}
      onBill={vi.fn()}
      onExtend={vi.fn()}
      onSeek={vi.fn()}
      onShow={vi.fn()}
      onIrrigate={irrigate ? vi.fn() : undefined}
    />
  );
  /** Hand-made farm zones over the sim's first one: every status line, seed state and bill state. */
  const handZones = (): StoryZone[] => {
    const z = S.ordered.zones.find((x) => x.kind === 'farm')!;
    const bill = z.bills![0]!;
    return [
      {
        ...z,
        id: 'h1',
        name: null,
        label: null,
        status: '3 too far from water',
        needsHoe: true,
        sow: false,
        harvest: false,
        seeds: { have: 4, item: 'wheat seeds', neededForEmpty: 1, sourceHint: 'tall grass', missing: 'no seeds', action: 'seek_seeds' },
        bills: [
          { ...bill, id: 'b1', active: true, paused: false, met: false, mode: 'forever', label: null },
          { ...bill, id: 'b2', active: false, paused: true, met: false, mode: 'until', stored: undefined, plant: 'nothing' },
          { ...bill, id: 'b3', active: false, paused: false, met: true, mode: 'count', seeds: { missing: 'short of seeds' } },
          { ...bill, id: 'b4', active: false, paused: false, met: false, seeds: { missing: 'short of seeds' } },
        ],
      },
      {
        ...z,
        id: 'h2',
        label: 'Cane',
        name: null,
        activePlant: 'sugar_cane',
        status: 'no water beside it',
        seeds: { have: 0, item: 'sugar cane', neededForEmpty: 5, action: 'craft' },
      },
      { ...z, id: 'h3', name: null, label: null, plant: null, bills: undefined, status: 'no water within 4 blocks', seeds: null },
      { ...z, id: 'h4', activePlant: 'nothing', status: null, seeds: { have: 2, item: 'carrot', neededForEmpty: 0 } },
    ] as never;
  };

  it('every zone state', () => {
    const hand = { ...S.ordered, zones: handZones() };
    expect(
      variants({
        none: () => grow(undefined),
        noFarms: () => grow(S.founded),
        ordered: () => grow(S.ordered),
        worked: () => grow(S.worked),
        hand: () => grow(hand),
        handReadOnly: () => grow(hand, false),
        handNoIrrigate: () => grow(hand, true, false),
      }),
    ).toMatchSnapshot();
  });

  it('adding a bill', () => {
    const r = html(grow(S.ordered));
    fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent === 'Add bill')!);
    expect(r.out()).toMatchSnapshot();
  });
});

describe('QueuePanel', () => {
  it('every build state', () => {
    const builds = S.worked.builds ?? [];
    const b0 = builds[0]!;
    const hand: StoryBuild[] = [
      ...builds,
      { ...b0, id: 'p', order: 90, status: 'paused', label: 'Paused one', reason: null, blocksTotal: 0 },
      { ...b0, id: 'k', order: 91, status: 'blocked', label: 'Blocked one', reason: null, kind: 'furniture' },
      {
        ...b0,
        id: 'w',
        order: 92,
        status: 'waiting',
        label: 'Waiting one',
        reason: 'waiting for planks',
        station: { kind: 'crafting_table', pos: [1, 64, 2] },
        materials: [
          { item: 'oak_planks', label: null, have: 2, need: 8, crafting: 3, via: 'craft', short: 3, chain: ['log', 'planks'], raw: ['log'] },
          { item: 'iron_ingot', label: 'Iron ingot', have: 0, need: 2, crafting: 2, via: 'smelt', short: 0, chain: [], raw: [] },
          { item: 'diamond', label: 'Diamond', have: 0, need: 1, crafting: 0, via: 'none', short: 1, chain: [], raw: [] },
        ] as never,
      },
      { ...b0, id: 'd', order: 93, status: 'done', label: 'Done one' },
    ];
    const failed = new Map([[String(b0.id), 4]]);
    expect(
      variants({
        empty: () => <QueuePanel builds={[]} canWrite onOp={vi.fn()} />,
        sim: () => <QueuePanel builds={builds} canWrite onOp={vi.fn()} failed={failed} onRetry={vi.fn()} />,
        hand: () => <QueuePanel builds={hand} canWrite onOp={vi.fn()} failed={failed} onRetry={vi.fn()} />,
        readOnly: () => <QueuePanel builds={hand} canWrite={false} onOp={vi.fn()} failed={failed} />,
      }),
    ).toMatchSnapshot();
  });
});
