import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { getPreviewFps, setPreviewFps } from '@mineclaw/world-viewer/settings';
import { HiveTreeView } from '@/features/live/HiveTreeView';
import { BotCard } from '@/features/live/BotCard';
import { PreviewFpsSlider } from '@/features/live/PreviewFpsSlider';
import { initPreviewSettings, PREVIEW_FPS_KEY, savePreviewFps } from '@/features/live/previewSettings';
import { MAIN_MIND_COLOR, mindColor, nodeActivity } from '@/features/live/hiveLook';
import { workAppearance } from '@/features/story/workAppearance';
import { TooltipProvider } from '@/components/ui/overlays';

const fixtures = vi.hoisted(() => ({ task: 'Mining coal', tree: {} as HiveTreeNode }));
vi.mock('@/lib/live/hooks', () => ({
  useHiveTree: () => ({ data: fixtures.tree }),
  useStatus: () => ({ data: { bots: [{ name: 'maid_chan_109', task: fixtures.task }] } }),
  useNow: () => ({ data: undefined, isPending: false }),
}));
vi.mock('@/features/world/integration', () => ({ BotThumbnail: () => <div>preview</div> }));
vi.mock('@/lib/prefs', () => ({ usePrefs: () => ({ devDetails: false }) }));
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  setPreviewFps(3);
});

it('loads and persists one preview limit, applies cross-tab changes and works without local storage', () => {
  localStorage.setItem(PREVIEW_FPS_KEY, '12');
  initPreviewSettings();
  render(<PreviewFpsSlider />);
  const slider = screen.getByRole('slider', { name: 'Maximum preview FPS' });
  expect(slider).toHaveValue('12');
  fireEvent.change(slider, { target: { value: '24' } });
  expect(getPreviewFps()).toBe(24);
  expect(localStorage.getItem(PREVIEW_FPS_KEY)).toBe('24');
  act(() => { window.dispatchEvent(new StorageEvent('storage', { key: PREVIEW_FPS_KEY, newValue: '4' })); });
  expect(slider).toHaveValue('4');
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new Error('Blocked');
  });
  act(() => savePreviewFps(7));
  expect(slider).toHaveValue('7');
});

it('uses current pawn work and matching task colors; minds retain distinct identity colors', () => {
  const pawn: HiveTreeNode = {
    id: '109',
    name: 'maid_chan_109',
    kind: 'subhive',
    state: 'running',
    task: 'story colony',
  };
  const mind: HiveTreeNode = {
    id: '102',
    name: 'maid_chan_102',
    kind: 'hive',
    brain: 'opus',
    state: 'running',
    children: [pawn],
  };
  fixtures.tree = { id: 'main', name: 'maid_chan', kind: 'main', state: 'running', children: [mind] };
  const { rerender } = render(<HiveTreeView />);
  const icon = screen.getByTestId('hive-icon-maid_chan_109');
  expect(icon).toHaveAttribute('data-work', 'mine');
  expect(screen.getByText('Mining coal')).toHaveStyle({ color: workAppearance('mine').color });
  expect(mindColor(fixtures.tree)).toBe(MAIN_MIND_COLOR);
  expect(mindColor(mind)).not.toBe(MAIN_MIND_COLOR);
  fixtures.task = 'Hauling cobblestone';
  rerender(<HiveTreeView />);
  expect(icon).toHaveAttribute('data-work', 'haul');
  expect(screen.getByText('Hauling cobblestone')).toHaveStyle({ color: workAppearance('haul').color });
  expect(nodeActivity(pawn, { task: null } as BotStatusView).label).toBe('Idle');
});

it('opens a body’s World tab from its preview and keeps the details link separate', () => {
  const bot: BotStatusView = {
    name: 'maid_chan_109',
    status: 'online',
    at: '2026-11-08T00:00:00Z',
    dim: 0,
    hp: 20,
    food: 20,
    budgetMode: 'Normal',
    chat: [],
    task: 'Mining coal',
  };
  render(
    <TooltipProvider>
      <BotCard bot={bot} node={undefined} compact />
    </TooltipProvider>,
  );
  expect(screen.getByRole('link', { name: 'View maid_chan_109 in the world' })).toHaveAttribute(
    'href',
    '/live/maid_chan_109?pane=world',
  );
  expect(screen.getByRole('link', { name: 'maid_chan_109: Mining coal' })).toHaveAttribute(
    'href',
    '/live/maid_chan_109',
  );
});
