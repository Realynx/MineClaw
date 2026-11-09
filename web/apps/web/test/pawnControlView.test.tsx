import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { PawnControlView } from '@/features/control/PawnControlView';

const f = vi.hoisted(() => ({
  engine: null as null | { surface: HTMLCanvasElement },
  bind: vi.fn(() => vi.fn()), capture: vi.fn(() => Promise.resolve(null)),
  handoff: null as null | { mount: ReturnType<typeof vi.fn>; transfer: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn> },
  control: {
    status: 'active', state: { dim: 0, hp: 20, food: 20 }, inventory: null, inventoryAt: 0, error: '',
    input: { current: { forward: 0, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: false, hotbar: 0 } },
    pose: { current: null }, action: vi.fn(), end: vi.fn(), retry: vi.fn(),
  },
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/features/world/integration', async () => {
  const React = await import('react');
  const mod = { WorldViewer: React.forwardRef((_props, ref) => {
    React.useImperativeHandle(ref, () => ({ engine: () => f.engine }));
    return null;
  }) };
  const source = {};
  return { useWorldHost: () => ({ mod, source }) };
});
vi.mock('@/features/world/InWorld', () => ({ Crosshair: () => null }));
vi.mock('@/features/live/inventory/InventoryPanel', () => ({ useBodyInventory: () => ({ dataUpdatedAt: 0 }), useItemIcons: () => ({}) }));
vi.mock('@/features/story/StoryAudio', () => ({ StoryAudio: () => null }));
vi.mock('@/features/story/controllerInput', () => ({ bindControllerKeyboard: () => () => {}, requestControllerCapture: f.capture }));
vi.mock('@/features/control/usePawnControl', () => ({ usePawnControl: () => f.control }));
vi.mock('@/features/control/playerInput', () => ({ bindPlayerInput: f.bind }));
vi.mock('@/features/control/captureHandoff', () => ({ takeoverCapture: () => f.handoff }));
vi.mock('@/features/control/PlayerInventory', () => ({ PlayerInventory: () => null, PlayerSlot: () => null }));

afterEach(() => { cleanup(); vi.useRealTimers(); vi.clearAllMocks(); f.engine = null; f.handoff = null; });

it('does not allow pointer capture until the actual canvas has its input listeners', async () => {
  vi.useFakeTimers();
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const play = screen.getByRole('button', { name: 'Click to play' });
  expect(play).toBeDisabled();
  fireEvent.click(play);
  expect(f.capture).not.toHaveBeenCalled();
  act(() => { vi.advanceTimersByTime(30); });
  expect(play).toBeDisabled();
  f.engine = { surface: document.createElement('canvas') };
  act(() => { vi.advanceTimersByTime(30); });
  expect(f.bind).toHaveBeenCalledOnce();
  expect(f.bind).toHaveBeenCalledWith(f.engine.surface, expect.any(Object));
  expect(play).toBeEnabled();
  await act(async () => { fireEvent.click(play); await Promise.resolve(); });
  expect(f.capture).toHaveBeenCalledWith(f.engine, screen.getByTestId('pawn-control'), expect.any(Function));
});

it('transfers the original click only after listeners are bound to the actual canvas', async () => {
  vi.useFakeTimers();
  const order: string[] = [];
  f.handoff = { mount: vi.fn(() => () => {}), transfer: vi.fn(() => { order.push('transfer'); return Promise.resolve(true); }), release: vi.fn() };
  f.bind.mockImplementationOnce(() => { order.push('bind'); return vi.fn(); });
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(f.handoff.transfer).not.toHaveBeenCalled();
  f.engine = { surface: document.createElement('canvas') };
  await act(async () => { vi.advanceTimersByTime(30); await Promise.resolve(); });
  expect(order).toEqual(['bind', 'transfer']);
  expect(f.handoff.transfer).toHaveBeenCalledWith(f.engine.surface, expect.any(Function));
  expect(screen.queryByRole('button', { name: 'Click to play' })).not.toBeInTheDocument();
  expect(f.capture).not.toHaveBeenCalled();
});
