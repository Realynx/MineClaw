import { render } from '@testing-library/react';
import type { WorldViewerProps } from '@mineclaw/world-viewer';
import { afterEach, expect, it, vi } from 'vitest';
import { PawnControlView } from '@/features/control/PawnControlView';

type Pose = { x: number; y: number; z: number; dim: number };
const f = vi.hoisted(() => ({
  beforeFrame: undefined as WorldViewerProps['onBeforeFrame'],
  pose: null as Pose | null,
  input: { forward: 0, strafe: 0, yaw: 90, pitch: 10, jump: false, sneak: false, sprint: false, hotbar: 0 },
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/features/world/integration', () => {
  const WorldViewer = (props: WorldViewerProps) => {
    f.beforeFrame = props.onBeforeFrame;
    return null;
  };
  const mod = { WorldViewer };
  const source = {};
  return { useWorldHost: () => ({ mod, source }) };
});
vi.mock('@/features/world/InWorld', () => ({ Crosshair: () => null }));
vi.mock('@/features/live/inventory/InventoryPanel', () => ({
  useBodyInventory: () => ({ dataUpdatedAt: 0 }),
  useItemIcons: () => ({}),
}));
vi.mock('@/features/story/StoryAudio', () => ({ StoryAudio: () => null }));
vi.mock('@/features/story/controllerInput', () => ({
  bindControllerKeyboard: () => () => {},
  requestControllerCapture: () => Promise.resolve(null),
}));
vi.mock('@/features/control/usePawnControl', () => ({
  usePawnControl: () => ({
    status: 'active',
    state: { dim: 0, hp: 20, food: 20 },
    inventory: null,
    inventoryAt: 0,
    error: '',
    input: { current: f.input },
    inputChanged: vi.fn(),
    pose: { current: null },
    reach: { current: undefined },
    aimInput: { current: null },
    camera: { current: { position: () => f.pose, onGround: () => true } },
    action: vi.fn(),
    end: vi.fn(),
    retry: vi.fn(),
  }),
}));
vi.mock('@/features/control/playerInput', () => ({ bindPlayerInput: () => () => {} }));
vi.mock('@/features/control/captureHandoff', () => ({ takeoverCapture: () => null }));
vi.mock('@/features/control/PlayerInventory', () => ({ PlayerInventory: () => null, PlayerSlot: () => null }));

afterEach(() => {
  f.beforeFrame = undefined;
  f.pose = null;
});

it('holds the takeover view until its camera has a pose, then puts the eyes at the pawn', () => {
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const rig = { flyPos: { set: vi.fn() }, flyYaw: 0, flyPitch: 0 };
  const engine = {
    rig,
    playerCollision: vi.fn(),
    setPlayerReach: vi.fn(),
    setBreak: vi.fn(),
    setHologramPreview: vi.fn(),
    blockBurst: vi.fn(),
    blockAt: () => null,
    aim: () => null,
    runtime: { version: '1.12.2', blockListeners: new Set() },
  } as unknown as Parameters<
    NonNullable<WorldViewerProps['onBeforeFrame']>
  >[0];
  // No pose yet: the viewer holds the frame, so nothing is drawn (or asked of the world) at the rig's default camera.
  expect(f.beforeFrame!(engine, 16, 0.016)).toBe(false);
  expect(rig.flyPos.set).not.toHaveBeenCalled();
  f.pose = { x: 216.5, y: 64, z: 400.5, dim: 0 };
  expect(f.beforeFrame!(engine, 32, 0.016)).not.toBe(false);
  expect(rig.flyPos.set).toHaveBeenCalledExactlyOnceWith(216.5, expect.closeTo(65.62, 6), 400.5);
  expect(rig).toMatchObject({ flyYaw: 90, flyPitch: 10 });
});
