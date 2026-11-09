import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SoundListener, WorldViewerHandle } from '@mineclaw/world-viewer';
import { afterEach, expect, it, vi } from 'vitest';
import { StoryAudio } from '@/features/story/StoryAudio';

const { players } = vi.hoisted(() => ({ players: [] as Array<{
  listener: () => SoundListener | null;
  onReady?: (ready: boolean) => void;
  setVolume: ReturnType<typeof vi.fn>;
  enable: ReturnType<typeof vi.fn>;
  dispose: ReturnType<typeof vi.fn>;
}> }));
vi.mock('@/features/world/integration', () => ({
  loadWorldViewer: () => Promise.resolve({ ControllerAudio: class {
    constructor(public listener: () => SoundListener | null, public onReady?: (ready: boolean) => void) { players.push(this); }
    setVolume = vi.fn();
    enable = vi.fn(() => { this.onReady?.(true); return Promise.resolve(); });
    receive() {}
    dispose = vi.fn();
  } }),
}));
afterEach(() => { localStorage.clear(); players.length = 0; });

it('shows volume controls immediately, persists mute and restores the previous level after a refresh', async () => {
  localStorage.setItem('mineclaw.controller.volume', '0.35');
  const qc = new QueryClient();
  const engine = {
    surface: { isConnected: true }, runtime: { version: '1.12.2' },
    camera: { matrixWorld: { elements: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0] }, position: { x: 3, y: 70, z: 4 } },
    rig: { target: { x: 3, y: 64, z: 4 } },
  };
  const viewer = { current: { engine: () => engine } as unknown as WorldViewerHandle };
  const tree = <QueryClientProvider client={qc}><StoryAudio viewer={viewer} view="top" /></QueryClientProvider>;
  const page = render(tree);
  await waitFor(() => expect(players).toHaveLength(1));
  expect(screen.getByRole('slider', { name: 'Controller sound volume' })).toHaveValue('0.35');
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('35%');
  fireEvent.click(screen.getByRole('button', { name: 'Enable sound' }));
  expect(screen.queryByRole('button', { name: 'Enable sound' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Volume up' }));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('40%');
  fireEvent.click(screen.getByRole('button', { name: 'Mute sound' }));
  expect(localStorage.getItem('mineclaw.controller.volume')).toBe('0');
  page.unmount();
  expect(players[0]!.dispose).toHaveBeenCalledOnce();
  render(tree);
  await waitFor(() => expect(players).toHaveLength(2));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('0%');
  expect(players[1]!.setVolume).toHaveBeenLastCalledWith(0);
  expect(players[1]!.enable).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Unmute sound' }));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('40%');
  expect(players[1]!.setVolume).toHaveBeenLastCalledWith(0.4);
  expect(players[1]!.listener()).toMatchObject({ position: [3, 64, 4], topDown: true });
  engine.surface.isConnected = false;
  expect(players[1]!.listener()).toBeNull();
  act(() => players[1]!.onReady?.(false));
  expect(screen.getByRole('button', { name: 'Enable sound' })).toBeVisible();
});
