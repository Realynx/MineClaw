import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { BotStatusView, WorldBot } from '@mineclaw/contracts';
import type { WorldViewerHandle, WorldViewerProps } from '@mineclaw/world-viewer';
import { TooltipProvider } from '@/components/ui/overlays';
import { WorldPane } from '@/features/world/WorldPane';
import { takeControlHref } from '@/features/control/navigation';

const f = vi.hoisted(() => ({
  canControl: true, bots: [] as WorldBot[], status: [] as BotStatusView[], props: {} as WorldViewerProps,
  followBot: vi.fn(), setCameraMode: vi.fn(),
}));
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ canChat: false, can: (permission: string) => permission === 'story' && f.canControl }) }));
vi.mock('@/lib/prefs', () => ({ usePrefs: () => ({ devDetails: false }) }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/lib/live/hooks', () => ({
  useEntities: () => ({ data: { bots: f.bots, entities: [] } }),
  useStatus: () => ({ data: { bots: f.status } }),
  useFeed: () => ({ data: { lines: [] } }),
}));
vi.mock('@/features/settings/QuickSettings', () => ({ ViewerQualityButton: () => null }));
vi.mock('@/features/world/integration', async () => {
  const { forwardRef, useImperativeHandle } = await import('react');
  const WorldViewer = forwardRef<WorldViewerHandle, WorldViewerProps>((props, ref) => {
    f.props = props;
    useImperativeHandle(ref, () => ({
      followBot: f.followBot, setCameraMode: f.setCameraMode,
      bots: () => f.bots.map((bot) => ({ ...bot, dto: bot })),
    }) as unknown as WorldViewerHandle);
    return null;
  });
  WorldViewer.displayName = 'TestWorldViewer';
  return { useWorldHost: () => ({ ready: true, source: {}, mod: { WorldViewer } }) };
});

function pawn(number: number): WorldBot {
  return { name: `maid_chan_${number}`, short: String(number), main: false, kind: 'script', online: true,
    dim: 0, x: number, y: 64, z: 381, hp: 20, food: 20, claims: [], live: true, ageSeconds: 0 };
}
const back = '/live/maid_chan_109?pane=world';
function page(follow = 'maid_chan_109') {
  return render(<TooltipProvider><WorldPane follow={follow} returnTo={back} /></TooltipProvider>);
}
beforeEach(() => {
  f.canControl = true; f.bots = [pawn(109), pawn(110)]; f.status = [];
  f.followBot.mockClear(); f.setCameraMode.mockClear();
  window.history.replaceState(null, '', back);
});
afterEach(() => {
  window.history.replaceState(null, '', '/');
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, value: null });
});

it('places takeover beside Follow for the canonical pawn and returns to its World tab', () => {
  page('109');
  expect(screen.getByRole('link', { name: 'Take control of pawn 109' })).toHaveAttribute('href', takeControlHref('maid_chan_109', back));
  fireEvent.click(screen.getByRole('radio', { name: 'Fly' }));
  expect(screen.queryByRole('link', { name: /Take control/ })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('radio', { name: 'Follow' }));
  expect(screen.getByRole('link', { name: 'Take control of pawn 109' })).toBeInTheDocument();
});

it('targets the newly followed model or go-to pawn instead of the original page bot', async () => {
  page();
  act(() => f.props.onSelectBot?.('maid_chan_110'));
  expect(f.followBot).toHaveBeenLastCalledWith('maid_chan_110');
  expect(f.props.follow).toBe('maid_chan_110');
  expect(screen.queryByRole('link', { name: 'Take control of pawn 109' })).not.toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Take control of pawn 110' })).toHaveAttribute('href', takeControlHref('maid_chan_110', back));
  fireEvent.change(screen.getByRole('textbox', { name: 'Go to a place, a bot or coordinates' }), { target: { value: '109' } });
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Go' })); await Promise.resolve(); });
  expect(f.props.follow).toBe('maid_chan_109');
  expect(screen.getByRole('link', { name: 'Take control of pawn 109' })).toBeInTheDocument();
});

it.each(['permission', 'offline', 'status-offline'])('keeps takeover disabled when blocked by %s', (reason) => {
  if (reason === 'permission') f.canControl = false;
  else if (reason === 'offline') f.bots[0]!.online = false;
  else f.status = [{ name: 'maid_chan_109', kind: 'script', status: 'offline' } as BotStatusView];
  page();
  expect(screen.getByRole('button', { name: 'Take control of pawn 109' })).toBeDisabled();
  expect(screen.queryByRole('link', { name: /Take control/ })).not.toBeInTheDocument();
});

it.each(['main', 'hive', 'subhive', null] as const)('does not offer takeover for a %s body', (kind) => {
  f.bots[0]!.kind = kind;
  page();
  expect(screen.queryByRole('link', { name: /Take control/ })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /Take control/ })).not.toBeInTheDocument();
});

it('keeps takeover inside the fullscreen stage beside its Follow controls', () => {
  page();
  const stage = screen.getByTestId('world-stage');
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, value: stage });
  act(() => { document.dispatchEvent(new Event('fullscreenchange')); });
  expect(within(stage).getByRole('link', { name: 'Take control of pawn 109' })).toHaveAttribute('href', takeControlHref('maid_chan_109', back));
});
