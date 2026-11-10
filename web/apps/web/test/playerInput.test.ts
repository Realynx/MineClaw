import { afterEach, describe, expect, it, vi } from 'vitest';
import { bindPlayerInput, idleInput, movement, pickSlot } from '@/features/control/playerInput';

afterEach(() => { vi.useRealTimers(); Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null }); });

describe('pawn player input', () => {
  it('notifies input transitions immediately without keyboard-repeat movement spam, then explicitly stops on blur', () => {
    const surface = document.createElement('canvas'), input = idleInput();
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const changes: Array<{ kind: string; forward: number }> = [];
    const cleanup = bindPlayerInput(surface, { input, action: vi.fn(), inventory: vi.fn(), lockChanged: vi.fn(), hotbarChanged: vi.fn(),
      inputChanged: (kind) => changes.push({ kind, forward: input.forward }) });
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyW' }));
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyW', repeat: true }));
    window.dispatchEvent(new KeyboardEvent('keyup', { code: 'KeyW' }));
    expect(changes).toEqual([{ kind: 'movement', forward: 1 }, { kind: 'movement', forward: 0 }]);
    window.dispatchEvent(new Event('blur'));
    expect(changes.at(-1)).toEqual({ kind: 'stop', forward: 0 });
    cleanup();
  });
  it('uses survival movement: Space jumps, Shift sneaks, Ctrl sprints', () => {
    expect(movement(new Set(['KeyW', 'KeyA', 'Space', 'ShiftLeft', 'ControlLeft']), idleInput(42, -12, 3)))
      .toEqual({ forward: 1, strafe: 1, yaw: 42, pitch: -12, hotbar: 3, jump: true, sneak: true, sprint: true });
    expect(movement(new Set(['KeyW', 'KeyS', 'KeyA', 'KeyD']), idleInput()).forward).toBe(0);
  });
  it('never acts on a capture click and stops held movement and actions on unlock', () => {
    vi.useFakeTimers();
    const surface = document.createElement('canvas');
    const input = idleInput();
    const action = vi.fn(), lockChanged = vi.fn();
    const cleanup = bindPlayerInput(surface, { input, action, lockChanged, inventory: vi.fn(), hotbarChanged: vi.fn() });
    surface.dispatchEvent(new PointerEvent('pointerdown', { button: 0 }));
    expect(action).not.toHaveBeenCalled();
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    document.dispatchEvent(new Event('pointerlockchange'));
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyW' }));
    surface.dispatchEvent(new PointerEvent('pointerdown', { button: 0 }));
    vi.advanceTimersByTime(500);
    expect(input.forward).toBe(1);
    expect(action).toHaveBeenCalledTimes(3);
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
    document.dispatchEvent(new Event('pointerlockchange'));
    expect(input.forward).toBe(0);
    expect(action).toHaveBeenLastCalledWith('cancel');
    const calls = action.mock.calls.length;
    vi.advanceTimersByTime(1000);
    expect(action).toHaveBeenCalledTimes(calls);
    cleanup();
  });
  it('keeps immediate mouse look bounded, selects hotbar, and clears on window blur', () => {
    const surface = document.createElement('canvas');
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const input = idleInput();
    const action = vi.fn(), hotbarChanged = vi.fn();
    const cleanup = bindPlayerInput(surface, { input, action, hotbarChanged, inventory: vi.fn(), lockChanged: vi.fn() });
    const mouse = new MouseEvent('mousemove');
    Object.defineProperties(mouse, { movementX: { value: 100 }, movementY: { value: 1000 } });
    window.dispatchEvent(mouse);
    expect(input.yaw).toBe(15); expect(input.pitch).toBe(90);
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Digit9' }));
    surface.dispatchEvent(new WheelEvent('wheel', { deltaY: 10 }));
    expect(input.hotbar).toBe(0);
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space' }));
    window.dispatchEvent(new Event('blur'));
    expect(input.jump).toBe(false);
    cleanup();
  });
  it('marks the re-sends of a held attack as repeats, and only those', () => {
    vi.useFakeTimers();
    const surface = document.createElement('canvas');
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const action = vi.fn();
    const cleanup = bindPlayerInput(surface, { input: idleInput(), action, inventory: vi.fn(), lockChanged: vi.fn(), hotbarChanged: vi.fn() });
    try {
      surface.dispatchEvent(new PointerEvent('pointerdown', { button: 0 }));
      vi.advanceTimersByTime(500);
      window.dispatchEvent(new PointerEvent('pointerup', { button: 0 }));
      surface.dispatchEvent(new PointerEvent('pointerdown', { button: 0 }));
      expect(action.mock.calls).toEqual([['attack'], ['attack', undefined, true], ['attack', undefined, true], ['release'], ['attack']]);
      window.dispatchEvent(new PointerEvent('pointerup', { button: 0 }));
      action.mockClear();
      // A held use repeats as before: the server already keeps one running use going.
      surface.dispatchEvent(new PointerEvent('pointerdown', { button: 2 }));
      vi.advanceTimersByTime(250);
      expect(action.mock.calls).toEqual([['use'], ['use']]);
    } finally {
      cleanup(); vi.useRealTimers();
    }
  });
  it('releases a brief right click without cancelling its discrete use', () => {
    const surface = document.createElement('canvas');
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const action = vi.fn();
    const cleanup = bindPlayerInput(surface, { input: idleInput(), action, inventory: vi.fn(), lockChanged: vi.fn(), hotbarChanged: vi.fn() });
    surface.dispatchEvent(new PointerEvent('pointerdown', { button: 2 }));
    window.dispatchEvent(new PointerEvent('pointerup', { button: 2 }));
    expect(action.mock.calls).toEqual([['use'], ['release']]);
    window.dispatchEvent(new Event('blur'));
    expect(action).toHaveBeenLastCalledWith('cancel');
    cleanup();
  });
  it('opens inventory after one cancel even when pointer unlock and blur arrive later', () => {
    const surface = document.createElement('canvas');
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const previousExit = Object.getOwnPropertyDescriptor(document, 'exitPointerLock');
    Object.defineProperty(document, 'exitPointerLock', { configurable: true, value: vi.fn() });
    const events: string[] = [];
    const cleanup = bindPlayerInput(surface, { input: idleInput(), action: (kind) => events.push(kind),
      inventory: () => events.push('inventory'), lockChanged: vi.fn(), hotbarChanged: vi.fn() });
    try {
      window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyE' }));
      window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyE', repeat: true }));
      Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
      document.dispatchEvent(new Event('pointerlockchange'));
      window.dispatchEvent(new Event('blur'));
      expect(events).toEqual(['cancel', 'inventory']);
      Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
      document.dispatchEvent(new Event('pointerlockchange'));
      surface.dispatchEvent(new PointerEvent('pointerdown', { button: 2 }));
      window.dispatchEvent(new Event('blur'));
      expect(events).toEqual(['cancel', 'inventory', 'use', 'cancel']);
    } finally {
      cleanup();
      if (previousExit) Object.defineProperty(document, 'exitPointerLock', previousExit);
      else Reflect.deleteProperty(document, 'exitPointerLock');
    }
  });
});

describe('pick block', () => {
  it('selects the hotbar slot of the aimed block on a middle click, and nothing when no slot holds it', () => {
    const surface = document.createElement('canvas'), input = idleInput();
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const hotbarChanged = vi.fn(), inputChanged = vi.fn(), action = vi.fn();
    let slot: number | null = 4;
    const pick = vi.fn(() => slot);
    const cleanup = bindPlayerInput(surface, { input, action, hotbarChanged, inputChanged, pick, inventory: vi.fn(), lockChanged: vi.fn() });
    surface.dispatchEvent(new PointerEvent('pointerdown', { button: 1 }));
    expect(pick).toHaveBeenCalledOnce();
    expect(input.hotbar).toBe(4);
    expect(hotbarChanged).toHaveBeenCalledExactlyOnceWith(4);
    expect(inputChanged).toHaveBeenCalledExactlyOnceWith('hotbar');
    slot = null;
    surface.dispatchEvent(new PointerEvent('pointerdown', { button: 1 }));
    expect(input.hotbar).toBe(4);
    expect(hotbarChanged).toHaveBeenCalledOnce();
    expect(action).not.toHaveBeenCalled(); // neither a dig nor a use
    cleanup();
  });

  it('finds the item of the block, preferring the same meta', () => {
    const hotbar = [undefined, { id: 35, meta: 1 }, { id: 35, meta: 14 }, { id: 331, meta: 0 }, { id: 5, meta: 0 }];
    expect(pickSlot({ id: 35, meta: 14 }, hotbar)).toBe(2);
    expect(pickSlot({ id: 35, meta: 4 }, hotbar)).toBe(1);
    expect(pickSlot({ id: 55, meta: 9 }, hotbar)).toBe(3); // redstone wire → redstone dust
    expect(pickSlot({ id: 1, meta: 0 }, hotbar)).toBeNull();
  });
});
