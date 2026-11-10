import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SoundListener, WorldViewerHandle } from '@mineclaw/world-viewer';
import { afterEach, expect, it, vi } from 'vitest';
import { SURFACE_SKIP } from '@/features/story/audioSurface';
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

type View = 'top' | 'angled' | 'fly';
type Cut = { x: number; y: number; z: number; r: number };
/** What the listener reads of the viewer engine: the map centre is column (3, 4), whose top block is at y 70, and the
 * look-at point sits one block over working level 63. `cut` is the level cut in force. */
function fakeEngine(cut: Cut | null = null) {
  const stacks = vi.fn((..._query: unknown[]) => Promise.resolve([3, 70, 4, 2, 0, 3]));
  return {
    surface: { isConnected: true }, posed: true, runtime: { version: '1.12.2', host: { stacks } },
    camera: { matrixWorld: { elements: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0] }, position: { x: 3, y: 70, z: 4 } },
    rig: { target: { x: 3, y: 64, z: 4 } }, pickCut: () => cut,
  };
}
/** The sound control over a fake engine: its listener callback, the worker's column query, and a view switch. */
async function mount(view: View, cut: Cut | null = null) {
  const engine = fakeEngine(cut), qc = new QueryClient();
  const viewer = { current: { engine: () => engine } as unknown as WorldViewerHandle };
  const tree = (v: View) => <QueryClientProvider client={qc}><StoryAudio viewer={viewer} view={v} /></QueryClientProvider>;
  const page = render(tree(view));
  await waitFor(() => expect(players).toHaveLength(1));
  return { stacks: engine.runtime.host.stacks, listener: () => players[0]!.listener(), show: (v: View) => page.rerender(tree(v)) };
}
/** Lets the worker's answer land: a macrotask turn runs all the queued microtasks first. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

it('keeps sound compact until clicked, persists mute and restores the previous level after a refresh', async () => {
  localStorage.setItem('mineclaw.controller.volume', '0.35');
  const qc = new QueryClient();
  const engine = fakeEngine();
  const viewer = { current: { engine: () => engine } as unknown as WorldViewerHandle };
  const tree = <QueryClientProvider client={qc}><StoryAudio viewer={viewer} view="top" /></QueryClientProvider>;
  const page = render(tree);
  await waitFor(() => expect(players).toHaveLength(1));
  expect(screen.queryByRole('slider', { name: 'Controller sound volume' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Sound settings' })).toHaveAttribute('aria-expanded', 'false');
  fireEvent.click(screen.getByRole('button', { name: 'Sound settings' }));
  expect(screen.getByRole('slider', { name: 'Controller sound volume' })).toHaveValue('0.35');
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('35%');
  fireEvent.click(screen.getByRole('button', { name: 'Enable sound' }));
  expect(screen.queryByRole('button', { name: 'Enable sound' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Volume up' }));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('40%');
  fireEvent.keyDown(screen.getByRole('slider', { name: 'Controller sound volume' }), { key: 'Escape' });
  expect(screen.queryByRole('slider', { name: 'Controller sound volume' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Sound settings' }));
  fireEvent.click(screen.getByRole('button', { name: 'Mute sound' }));
  expect(localStorage.getItem('mineclaw.controller.volume')).toBe('0');
  page.unmount();
  expect(players[0]!.dispose).toHaveBeenCalledOnce();
  render(tree);
  await waitFor(() => expect(players).toHaveLength(2));
  expect(screen.queryByRole('slider', { name: 'Controller sound volume' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Sound settings' }));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('0%');
  expect(players[1]!.setVolume).toHaveBeenLastCalledWith(0);
  expect(players[1]!.enable).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Unmute sound' }));
  expect(screen.getByLabelText('Sound volume')).toHaveTextContent('40%');
  expect(players[1]!.setVolume).toHaveBeenLastCalledWith(0.4);
  expect(players[1]!.listener()).toMatchObject({ position: [3, 64, 4], topDown: true });
  engine.posed = false; // camera not yet placed by a frame: still at the world origin
  expect(players[1]!.listener()).toBeNull();
  engine.posed = true;
  engine.surface.isConnected = false;
  expect(players[1]!.listener()).toBeNull();
  act(() => players[1]!.onReady?.(false));
  expect(screen.getByRole('button', { name: 'Enable sound' })).toBeVisible();
});

it('top view: the hearing floor is 3 blocks under the visible surface at the map centre, the working level until it is read', async () => {
  const { stacks, listener, show } = await mount('top');
  expect(listener()).toMatchObject({ topDown: true, position: [3, 64, 4], floorY: 60 }); // working level 63 stands in
  expect(stacks).toHaveBeenCalledOnce();
  expect(stacks).toHaveBeenCalledWith(0, [3, 0, 4], [3, 319, 4], [...SURFACE_SKIP], 1, 0, 1);
  await settle();
  expect(listener()).toMatchObject({ topDown: true, floorY: 67 }); // the surface is at 70
  show('angled');
  expect(listener()).toMatchObject({ topDown: true, floorY: null });
  show('top');
  expect(listener()).toMatchObject({ topDown: true, floorY: 67 });
});

it('top view with x-ray: the surface is looked for under the level cut', async () => {
  const { stacks, listener } = await mount('top', { x: 3, y: 68, z: 4, r: 512 });
  listener();
  expect(stacks).toHaveBeenCalledWith(0, [3, 0, 4], [3, 67, 4], [...SURFACE_SKIP], 1, 0, 1);
});

it('top view: a cut that does not reach the map centre hides nothing there', async () => {
  const { stacks, listener } = await mount('top', { x: 600, y: 68, z: 4, r: 512 });
  listener();
  expect(stacks).toHaveBeenCalledWith(0, [3, 0, 4], [3, 319, 4], [...SURFACE_SKIP], 1, 0, 1);
});

it.each(['angled', 'fly'] as const)('%s view: no hearing floor and no worker query', async (view) => {
  const { stacks, listener } = await mount(view);
  expect(listener()).toMatchObject({ topDown: view === 'angled', floorY: null });
  expect(stacks).not.toHaveBeenCalled();
});

it('a muted Controller asks the worker nothing; unmuting brings the floor back', async () => {
  localStorage.setItem('mineclaw.controller.volume', '0');
  const { stacks, listener } = await mount('top');
  expect(listener()).toMatchObject({ topDown: true, floorY: null });
  expect(stacks).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Sound settings' }));
  fireEvent.click(screen.getByRole('button', { name: 'Unmute sound' }));
  expect(listener()).toMatchObject({ topDown: true, floorY: 60 });
  expect(stacks).toHaveBeenCalledOnce();
});
