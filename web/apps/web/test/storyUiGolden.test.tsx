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
import { ColonyPanel } from '@/features/story/StoryPanels';
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
