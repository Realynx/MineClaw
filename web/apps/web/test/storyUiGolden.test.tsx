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
import { WorkPanel } from '@/features/story/roles/WorkPanel';
import { barBodies, ColonistBar, ControlCluster, OptionsBar } from '@/features/story/StoryHud';
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

describe('OptionsBar', () => {
  const build: BuildOpts = { height: 3, roof: 'flat', overhang: false, facing: 'w', material: [5, 0] };
  const bar = (tool: MapTool, extra: Partial<Parameters<typeof OptionsBar>[0]> = {}) => (
    <OptionsBar tool={tool} build={build} mineFilter="ores" onMineFilter={vi.fn()} onClose={vi.fn()} {...extra} />
  );
  it('every tool and height mode', () => {
    const cases: Record<string, () => ReactElement> = {
      select: () => bar({ kind: 'select' }),
      build: () => bar({ kind: 'build', id: 'wall' }),
      zoneAdd: () => bar({ kind: 'zone', id: 'farm', op: 'add' }),
      zoneRemove: () => bar({ kind: 'zone', id: 'chop', op: 'remove' }),
      zoneUnknown: () => bar({ kind: 'zone', id: 'nope', op: 'add' }),
      storage: () => bar({ kind: 'storage' }),
      home: () => bar({ kind: 'home' }),
      design: () => bar({ kind: 'design', id: 3, name: 'Hut', blocks: [[0, 0, 0, 1, 0]] as never }),
      retryNone: () => bar({ kind: 'designate', id: 'retry' }),
      retrySome: () => bar({ kind: 'designate', id: 'retry' }, { stalled: 7, onRetryAll: vi.fn() }),
    };
    for (const id of ['chop', 'cut', 'harvest', 'mine', 'haul', 'hunt', 'deconstruct', 'cancel', 'forbid', 'allow'])
      cases[`designate ${id}`] = () => bar({ kind: 'designate', id: id as never });
    for (const mode of ['surface', 'level', 'down', 'flatten', 'layers'] as const) {
      cases[`mine ${mode}`] = () =>
        bar({ kind: 'designate', id: 'mine' }, { heightMode: mode, onHeightMode: vi.fn(), layers: 3, onLayers: vi.fn(), level: 58 });
      cases[`deconstruct ${mode} no level`] = () => bar({ kind: 'designate', id: 'deconstruct' }, { heightMode: mode });
    }
    expect(variants(cases)).toMatchSnapshot();
  });
});

describe('ColonistBar', () => {
  const bodies = () => {
    const b = barBodies(S.worked.pawns, S.worked.ai ?? []);
    b[0] = { ...b[0]!, mood: 'danger', drafted: true };
    return b;
  };
  const colonistBar = (extra: Partial<Parameters<typeof ColonistBar>[0]>, log: unknown[] = []) => (
    <ColonistBar
      bodies={bodies()}
      selected={[]}
      order={[]}
      onOrder={(n) => log.push(['order', n])}
      onClick={(n, m) => log.push(['click', n, m])}
      onBox={(n, add) => log.push(['box', n, add])}
      onJump={(n) => log.push(['jump', n])}
      {...extra}
    />
  );

  it('barBodies over every state', () => {
    const hand = S.worked.pawns.map((p, i) => ({
      ...p,
      job: i === 0 ? null : p.job,
      drafted: i === 1,
      state: i === 2 ? 'sleeping' : p.state,
      hp: i === 3 ? undefined : p.hp,
      food: i === 3 ? undefined : p.food,
      offlineReason: undefined,
    }));
    const ai = (S.worked.ai ?? []).map((a, i) => ({
      ...a,
      kind: i ? a.kind : 'main',
      name: i ? 'maid_chan' : a.name,
      task: null,
      brainState: i ? null : a.brainState,
      online: i ? false : a.online,
      hp: undefined,
      pos: undefined,
    }));
    expect({
      founded: barBodies(S.founded.pawns, S.founded.ai ?? []),
      worked: barBodies(S.worked.pawns, S.worked.ai ?? []),
      hand: barBodies(hand as never, ai as never),
    }).toMatchSnapshot();
  });

  it('every bar state', () => {
    const names = S.worked.pawns.map((p) => p.name);
    expect(
      variants({
        plain: () => colonistBar({}),
        selected: () => colonistBar({ selected: [names[0]!, names[2]!] }),
        controls: () =>
          colonistBar({ selected: [names[0]!], pending: 2, onAdd: vi.fn(), onRemove: vi.fn(), onBringAll: vi.fn() }),
        removeOnly: () => colonistBar({ selected: names, onRemove: vi.fn(), pending: -1 }),
        addOnly: () => colonistBar({ onAdd: vi.fn(), onBringAll: null }),
        noAi: () => colonistBar({ bodies: bodies().filter((b) => b.kind === 'pawn'), onBringAll: vi.fn() }),
      }),
    ).toMatchSnapshot();
  });

  it('hover, add many, remove confirm', () => {
    const names = S.worked.pawns.map((p) => p.name);
    const onAdd = vi.fn();
    const onRemove = vi.fn();
    const r = html(colonistBar({ selected: [names[0]!], onAdd, onRemove }));
    const out: Record<string, string> = {};
    for (const el of [...r.root.querySelectorAll('[data-portrait]')]) {
      fireEvent.pointerEnter(el);
      out[`hover ${el.getAttribute('data-portrait')}`] = r.out();
      fireEvent.pointerLeave(el);
    }
    fireEvent.click(r.root.querySelector('[data-testid="bar-add-pawn"]')!, { shiftKey: true });
    out.addMany = r.out();
    fireEvent.change(r.root.querySelector('[aria-label="How many pawns"]')!, { target: { value: '40' } });
    out.addManyClamped = r.out();
    fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent === 'Add')!);
    fireEvent.click(r.root.querySelector('[data-testid="bar-add-pawn"]')!);
    fireEvent.click(r.root.querySelector('[data-testid="bar-remove-pawn"]')!);
    out.removeConfirm = r.out();
    fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent === 'Remove')!);
    out.after = r.out();
    expect({ out, add: onAdd.mock.calls, remove: onRemove.mock.calls }).toMatchSnapshot();
  });

  it('gestures: click, double click, box select, lift and reorder', () => {
    vi.useFakeTimers();
    const capture = vi.spyOn(HTMLElement.prototype, 'setPointerCapture').mockImplementation(() => undefined);
    const rect = vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      const all = [...(this.parentElement?.querySelectorAll('[data-portrait]') ?? [])];
      const i = all.indexOf(this);
      const left = i < 0 ? 0 : i * 60;
      const width = i < 0 ? 600 : 58;
      return { left, right: left + width, width, top: 100, bottom: 160, height: 60, x: left, y: 100, toJSON: () => ({}) };
    });
    try {
      const log: unknown[] = [];
      const r = html(colonistBar({}, log));
      const at = (i: number) => r.root.querySelectorAll('[data-portrait]')[i]!;
      const ptr = (el: Element, type: 'pointerDown' | 'pointerMove' | 'pointerUp', x: number, extra = {}) =>
        fireEvent[type](el, { button: 0, pointerId: 1, clientX: x, clientY: 120, ...extra });
      const out: Record<string, string> = {};
      // A click, a shift-click, a ctrl-click, then a double click (jump).
      ptr(at(0), 'pointerDown', 10);
      ptr(at(0), 'pointerUp', 10);
      vi.advanceTimersByTime(1000);
      ptr(at(1), 'pointerDown', 70, { shiftKey: true });
      ptr(at(1), 'pointerUp', 70);
      ptr(at(2), 'pointerDown', 130, { metaKey: true });
      ptr(at(2), 'pointerUp', 130);
      ptr(at(2), 'pointerDown', 130);
      ptr(at(2), 'pointerUp', 130);
      // A right button does nothing; a move with nothing pressed neither.
      ptr(at(0), 'pointerDown', 10, { button: 2 });
      ptr(at(0), 'pointerMove', 200);
      ptr(at(0), 'pointerUp', 10);
      // A quick drag box-selects (shift adds).
      ptr(at(0), 'pointerDown', 10, { shiftKey: true });
      ptr(at(0), 'pointerMove', 12);
      ptr(at(0), 'pointerMove', 140);
      out.box = r.out();
      ptr(at(0), 'pointerUp', 140);
      // Hold still: the portrait lifts; dragging it reorders.
      ptr(at(0), 'pointerDown', 10);
      vi.advanceTimersByTime(400);
      out.lifted = r.out();
      ptr(at(0), 'pointerMove', 150);
      out.dragging = r.out();
      ptr(at(0), 'pointerUp', 150);
      // Lifted and dropped in place: nothing; lifted onto itself: no target.
      ptr(at(1), 'pointerDown', 70);
      vi.advanceTimersByTime(400);
      ptr(at(1), 'pointerUp', 70);
      ptr(at(1), 'pointerDown', 70);
      vi.advanceTimersByTime(400);
      ptr(at(1), 'pointerMove', 80);
      ptr(at(1), 'pointerUp', 80);
      // Past the last portrait: to the end.
      ptr(at(1), 'pointerDown', 70);
      vi.advanceTimersByTime(400);
      ptr(at(1), 'pointerMove', 9000);
      ptr(at(1), 'pointerUp', 9000);
      // An AI body never lifts (a hold then a drag box-selects).
      const lastI = r.root.querySelectorAll('[data-portrait]').length - 1;
      ptr(at(lastI), 'pointerDown', lastI * 60 + 5);
      vi.advanceTimersByTime(400);
      ptr(at(lastI), 'pointerMove', 10);
      ptr(at(lastI), 'pointerUp', 10);
      out.end = r.out();
      cleanup();
      // With an order: the reorder keeps it and appends the pawns it lacks.
      const log2: unknown[] = [];
      const names = S.worked.pawns.map((p) => p.name);
      const r2 = html(colonistBar({ order: [names[2]!, names[0]!] }, log2));
      const at2 = (i: number) => r2.root.querySelectorAll('[data-portrait]')[i]!;
      ptr(at2(3), 'pointerDown', 190);
      vi.advanceTimersByTime(400);
      ptr(at2(3), 'pointerMove', 20);
      ptr(at2(3), 'pointerUp', 20);
      expect({ log, log2, out }).toMatchSnapshot();
    } finally {
      capture.mockRestore();
      rect.mockRestore();
      vi.useRealTimers();
    }
  });
});

describe('ControlCluster', () => {
  it('every state', () => {
    const cluster = (extra: Partial<Parameters<typeof ControlCluster>[0]>) => (
      <ControlCluster
        clock={null}
        name="Golden"
        paused={false}
        canWrite
        onPause={vi.fn()}
        view="top"
        onView={vi.fn()}
        level={64}
        ground={63}
        height={4}
        xray={false}
        onLevel={vi.fn()}
        onXray={vi.fn()}
        onHelp={vi.fn()}
        {...extra}
      />
    );
    expect(
      variants({
        plain: () => cluster({}),
        other: () =>
          cluster({
            paused: true,
            canWrite: false,
            view: 'angled',
            xray: true,
            level: 70,
            speed: 'fast',
            speedEffective: 'normal',
            onSpeed: vi.fn(),
            children: <span>extra</span>,
          }),
        speedOnly: () => cluster({ speed: 'faster' }),
      }),
    ).toMatchSnapshot();
  });
});

describe('WorkPanel', () => {
  const pawns = () => {
    const w = S.worked;
    return w.pawns.map((p, i) => ({
      ...p,
      role: i === 0 ? 'farmer' : p.role,
      skills: i === 1 ? { farm: { level: 12, passion: true }, mine: { level: 3 } } : p.skills,
    })) as typeof w.pawns;
  };
  const work = (extra: Partial<Parameters<typeof WorkPanel>[0]>, log: unknown[] = []) => (
    <WorkPanel
      pawns={pawns()}
      ai={S.worked.ai ?? []}
      canWrite
      onSet={(p, w) => log.push(['set', p, w])}
      roles={S.worked.roles ?? []}
      onRole={(p, r) => log.push(['role', p, r])}
      onRoleOp={(b) => log.push(['roleOp', b])}
      {...extra}
    />
  );

  it('every grid state', () => {
    const names = S.worked.pawns.map((p) => p.name);
    expect(
      variants({
        roles: () => work({}),
        picked: () => work({ selected: [names[0]!, 'nobody', names[2]!] }),
        allPicked: () => work({ selected: names }),
        noRoles: () => work({ onRole: undefined, onRoleOp: undefined }),
        readOnly: () => work({ canWrite: false }),
        noAi: () => work({ ai: [], roles: [] }),
      }),
    ).toMatchSnapshot();
  });

  it('sorting, editing, detaching, bulk roles and the roles editor', () => {
    const log: unknown[] = [];
    const r = html(work({}, log));
    const out: Record<string, string> = {};
    const names = S.worked.pawns.map((p) => p.name);
    const cell = (n: string, t: string) => r.root.querySelector(`[data-cell="${n}:${t}"]`)!;
    const head = (label: string) => [...r.root.querySelectorAll('th button')].find((b) => b.textContent?.startsWith(label))!;
    fireEvent.click(head('Pawn'));
    out.sortName = r.out();
    fireEvent.click(head('Farm'));
    fireEvent.click(head('Farm'));
    out.sortFarm = r.out();
    fireEvent.click(cell(names[1]!, 'farm'));
    fireEvent.click(cell(names[1]!, 'mine'), { shiftKey: true });
    fireEvent.contextMenu(cell(names[2]!, 'haul'));
    fireEvent.click(cell(names[0]!, 'farm'));
    out.detached = r.out();
    fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent?.includes('Undo'))!);
    fireEvent.click(r.root.querySelector('[aria-label="Select every pawn"]')!);
    out.bulk = r.out();
    fireEvent.change(r.root.querySelector('[data-testid="role-bulk-select"]')!, { target: { value: S.worked.roles![1]!.id } });
    fireEvent.change(r.root.querySelector(`[data-testid="role-select-${names[3]}"]`)!, { target: { value: '' } });
    fireEvent.click(r.root.querySelector(`[aria-label="Select ${S.worked.pawns[2]!.display}"]`)!);
    out.unticked = r.out();
    fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent === 'Clear')!);
    fireEvent.click(r.root.querySelector('[data-testid="roles-toggle"]')!);
    out.rolesOpen = r.out();
    const del = (role: string) => {
      fireEvent.click(r.root.querySelector(`[aria-label="Delete ${role}"]`)!);
      fireEvent.click([...r.root.querySelectorAll('button')].find((b) => b.textContent === 'Delete')!);
    };
    del('Miner');
    del('Cook');
    out.end = r.out();
    expect({ log, out }).toMatchSnapshot();
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
