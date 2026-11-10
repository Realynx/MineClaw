import { act, render, screen } from '@testing-library/react';
import { forwardRef, useImperativeHandle } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { WorldViewerHandle, WorldViewerProps } from '@mineclaw/world-viewer';
import { StoryMap, type StoryMapProps } from '@/features/story/StoryMap';
import type * as ChestBadges from '@/features/story/chestBadges';

/** What the fake viewer's runtime answers to `syncStatus()`. */
const viewer = vi.hoisted(() => ({
  status: { swept: true, sectionsQueued: 0, updating: false },
  syncStatus: undefined as unknown as () => { swept: boolean; sectionsQueued: number; updating: boolean },
  engine: {},
}));
vi.mock('@/features/story/chestBadges', async (importOriginal) => ({
  ...(await importOriginal<typeof ChestBadges>()),
  useContainerBadges: () => ({ current: new Map() }),
}));
vi.mock('@/features/story/StoryAudio', () => ({ StoryAudio: () => null }));
vi.mock('@/features/world/integration', () => ({
  useWorldHost: () => ({ ready: true, source: {}, mod: { WorldViewer: MockViewer } }),
}));
const MockViewer = forwardRef<WorldViewerHandle, WorldViewerProps>(function MockViewer(_props, ref) {
  useImperativeHandle(ref, () => ({ engine: () => viewer.engine, locked: () => false }) as WorldViewerHandle);
  return <div data-testid="fake-viewer" />;
});

beforeEach(() => {
  vi.useFakeTimers();
  viewer.status = { swept: true, sectionsQueued: 0, updating: false };
  viewer.syncStatus = vi.fn(() => viewer.status);
  viewer.engine = { runtime: { syncStatus: viewer.syncStatus } };
  vi.stubGlobal('requestAnimationFrame', () => 1);
  vi.stubGlobal('cancelAnimationFrame', () => undefined);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const mapProps = (over: Partial<StoryMapProps> = {}): StoryMapProps => ({
  state: undefined,
  selection: { bodies: [], chests: [], bench: null },
  tool: { kind: 'select' },
  build: { height: 3, facing: 'n', material: 'cobblestone', roof: 'flat', overhang: false },
  mineFilter: 'all',
  heightMode: 'surface',
  layers: 1,
  level: 63,
  cutY: 67,
  view: 'top',
  keys: { current: new Set() },
  home: [400, 63, 400],
  onSelect: vi.fn(),
  onSelectSame: vi.fn(),
  onTool: vi.fn(),
  onContext: vi.fn(),
  onDropTool: vi.fn(),
  ...over,
});

const chip = () => screen.queryByTestId('story-map-updating');
const advance = (ms: number) => act(() => void vi.advanceTimersByTime(ms));

it('says the map is updating while the viewer catches up, and stops once it has', () => {
  render(<StoryMap {...mapProps()} />);
  viewer.status = { swept: false, sectionsQueued: 40, updating: true };
  advance(500);
  expect(chip()).toHaveTextContent('Updating the map…');
  viewer.status = { swept: true, sectionsQueued: 0, updating: false };
  advance(250);
  expect(chip()).toBeNull();
});

it('keeps quiet for a brief re-mesh during live play (one poll)', () => {
  render(<StoryMap {...mapProps()} />);
  viewer.status = { swept: true, sectionsQueued: 2, updating: true };
  advance(250);
  viewer.status = { swept: true, sectionsQueued: 0, updating: false };
  advance(250);
  expect(chip()).toBeNull();
  // Two polls in a row: the queue is real work, not a blip.
  viewer.status = { swept: true, sectionsQueued: 30, updating: true };
  advance(500);
  expect(chip()).not.toBeNull();
});

it('polls the viewer a few times a second, not every frame', () => {
  render(<StoryMap {...mapProps()} />);
  advance(1000);
  expect(viewer.syncStatus).toHaveBeenCalledTimes(4);
});

it('shows nothing while the colony is still loading (the loading line says it), or with a viewer that has no status', () => {
  const { rerender } = render(<StoryMap {...mapProps({ home: null, loading: true })} />);
  viewer.status = { swept: false, sectionsQueued: 0, updating: true };
  advance(1000);
  expect(screen.getByTestId('story-map-loading')).toBeInTheDocument();
  expect(chip()).toBeNull();

  viewer.engine = { runtime: {} };
  rerender(<StoryMap {...mapProps()} />);
  advance(1000);
  expect(chip()).toBeNull();
});
