/** Human input only. The game client applies collision, gravity and survival rules. */
export interface PlayerInput {
  forward: number; strafe: number; yaw: number; pitch: number;
  jump: boolean; sneak: boolean; sprint: boolean; hotbar: number;
}

export function idleInput(yaw = 0, pitch = 0, hotbar = 0): PlayerInput {
  return { forward: 0, strafe: 0, yaw, pitch, hotbar, jump: false, sneak: false, sprint: false };
}

export function movement(keys: ReadonlySet<string>, look: Pick<PlayerInput, 'yaw' | 'pitch' | 'hotbar'>): PlayerInput {
  const held = (...codes: string[]) => codes.some((code) => keys.has(code));
  return {
    ...look,
    forward: Number(held('KeyW', 'ArrowUp')) - Number(held('KeyS', 'ArrowDown')),
    strafe: Number(held('KeyA', 'ArrowLeft')) - Number(held('KeyD', 'ArrowRight')),
    jump: held('Space'), sneak: held('ShiftLeft', 'ShiftRight'), sprint: held('ControlLeft', 'ControlRight'),
  };
}

export interface PlayerBindings {
  input: PlayerInput;
  /** `repeat` marks the 250 ms re-send of a held attack: the server digs on but hits a mob once per click. */
  action: (kind: 'attack' | 'use' | 'release' | 'cancel' | 'drop', wholeStack?: boolean, repeat?: boolean) => void;
  inventory: () => void;
  lockChanged: (locked: boolean) => void;
  hotbarChanged: (slot: number) => void;
  inputChanged?: (kind: PlayerInputChange) => void;
  /** Middle click: the hotbar slot to select for the aimed block (pick block), or null to change nothing. */
  pick?: () => number | null;
}
export type PlayerInputChange = 'movement' | 'look' | 'hotbar' | 'stop';

/** All held controls are cleared on unlock/blur. A capture click never swings a tool. */
export function bindPlayerInput(surface: HTMLElement, callbacks: PlayerBindings): () => void {
  const keys = new Set<string>();
  const heldButtons = new Set<number>();
  const input = callbacks.input;
  let cleared = false;
  const locked = () => document.pointerLockElement === surface;
  const sync = () => {
    const next = movement(keys, input);
    const changed = next.forward !== input.forward || next.strafe !== input.strafe || next.jump !== input.jump
      || next.sneak !== input.sneak || next.sprint !== input.sprint;
    Object.assign(input, next);
    if (changed) callbacks.inputChanged?.('movement');
  };
  const clear = () => {
    keys.clear(); heldButtons.clear(); sync();
    callbacks.inputChanged?.('stop');
    // E/Escape clear synchronously; the browser delivers pointerlockchange later. A second cancel would discard
    // the inventory request queued after E. Blur/unlock/visibility form one stop until a fresh locked interaction.
    if (!cleared) callbacks.action('cancel');
    cleared = true;
  };
  const onLock = () => {
    if (!locked()) clear();
    else cleared = false;
    callbacks.lockChanged(locked());
  };
  const choose = (slot: number) => { input.hotbar = (slot + 9) % 9; callbacks.hotbarChanged(input.hotbar); callbacks.inputChanged?.('hotbar'); };
  const down = (event: KeyboardEvent) => {
    if (!locked()) return;
    if (event.code === 'KeyE' && event.repeat) { event.preventDefault(); return; }
    cleared = false;
    if (event.code === 'Escape') { clear(); return; }
    if (/^(Key[WASDEQ]|Arrow|Space|Shift|Control|Digit[1-9])/.test(event.code) || event.ctrlKey) event.preventDefault();
    if (event.code === 'KeyE' && !event.repeat) {
      clear(); document.exitPointerLock(); callbacks.inventory(); return;
    }
    if (event.code === 'KeyQ' && !event.repeat) { callbacks.action('drop', event.ctrlKey); return; }
    if (/^Digit[1-9]$/.test(event.code)) { choose(Number(event.code.slice(5)) - 1); return; }
    keys.add(event.code); sync();
  };
  const up = (event: KeyboardEvent) => { keys.delete(event.code); sync(); };
  const mouse = (event: MouseEvent) => {
    if (!locked()) return;
    input.yaw = ((input.yaw + event.movementX * 0.15) % 360 + 360) % 360;
    input.pitch = Math.max(-90, Math.min(90, input.pitch + event.movementY * 0.15));
    callbacks.inputChanged?.('look');
  };
  const press = (event: PointerEvent) => {
    if (locked() && event.button === 1) {
      event.preventDefault();
      const slot = callbacks.pick?.();
      if (slot !== null && slot !== undefined && slot !== input.hotbar) choose(slot);
      return;
    }
    if (!locked() || (event.button !== 0 && event.button !== 2)) return;
    cleared = false;
    event.preventDefault(); heldButtons.add(event.button);
    callbacks.action(event.button === 0 ? 'attack' : 'use');
  };
  const release = (event: PointerEvent) => {
    if (heldButtons.delete(event.button)) callbacks.action('release');
  };
  const wheel = (event: WheelEvent) => {
    if (!locked()) return;
    event.preventDefault(); choose(input.hotbar + Math.sign(event.deltaY));
  };
  const hidden = () => { if (document.hidden) clear(); };
  // Repeated primary/use requests do not restart an in-progress dig/use on the client.
  const timer = setInterval(() => {
    if (!locked()) return;
    if (heldButtons.has(0)) callbacks.action('attack', undefined, true);
    else if (heldButtons.has(2)) callbacks.action('use');
  }, 250);
  surface.addEventListener('pointerdown', press);
  surface.addEventListener('wheel', wheel, { passive: false });
  window.addEventListener('pointerup', release);
  window.addEventListener('keydown', down);
  window.addEventListener('keyup', up);
  window.addEventListener('mousemove', mouse);
  window.addEventListener('blur', clear);
  document.addEventListener('pointerlockchange', onLock);
  document.addEventListener('visibilitychange', hidden);
  return () => {
    clearInterval(timer); clear();
    surface.removeEventListener('pointerdown', press);
    surface.removeEventListener('wheel', wheel);
    window.removeEventListener('pointerup', release);
    window.removeEventListener('keydown', down);
    window.removeEventListener('keyup', up);
    window.removeEventListener('mousemove', mouse);
    window.removeEventListener('blur', clear);
    document.removeEventListener('pointerlockchange', onLock);
    document.removeEventListener('visibilitychange', hidden);
  };
}

/** Blocks whose item has another id: the item a pick-block takes from the hotbar for them. */
const PICKED_ITEM: Readonly<Record<number, number>> = {
  26: 355, 55: 331, 59: 295, 62: 61, 63: 323, 64: 324, 68: 323, 71: 330, 74: 73, 75: 76, 83: 338, 92: 354, 93: 356, 94: 356,
  104: 361, 105: 362, 115: 372, 117: 379, 118: 380, 124: 123, 132: 287, 140: 390, 144: 397, 149: 404, 150: 404,
};

/** Pick block: the hotbar slot (0..8) holding the aimed block's item, preferring the same meta (wool colour, wood kind);
 * null when no slot holds it. */
export function pickSlot(block: { id: number; meta: number }, hotbar: ReadonlyArray<{ id: number; meta: number } | null | undefined>): number | null {
  const id = PICKED_ITEM[block.id] ?? block.id;
  let any: number | null = null;
  for (let i = 0; i < 9; i++) {
    const item = hotbar[i];
    if (!item || item.id !== id) continue;
    if (id === block.id && item.meta === block.meta) return i;
    any ??= i;
  }
  return any;
}
