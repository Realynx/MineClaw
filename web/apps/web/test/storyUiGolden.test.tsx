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
import type { BuildOpts, BuildToolId } from '@/features/story/tools';
import { storyUiStates, type StoryUiStates } from './storyUiFixture';

afterEach(cleanup);

let S: StoryUiStates;
beforeAll(() => {
  S = storyUiStates();
});

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
