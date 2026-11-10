/**
 * Dragging a component with the mouse, framework-free and shared by the mine and redstone studios. A press on a component
 * becomes a drag past a few pixels; the outline follows the pointer through `preview` at most once per frame and the
 * release commits once. Escape, a cancelled pointer, a lost capture or the window losing focus put the piece back.
 */
import type { V3 } from '../canvas';

/** How far (CSS pixels) a press travels before it becomes a drag; less is a click. */
export const DRAG_THRESHOLD_PX = 5;

export interface ComponentPress {
  pointerId: number;
  /** Where the press began, in client pixels. */
  x: number;
  y: number;
  /** The component pressed. */
  id: string;
  /** Its corner when the press began. */
  origin: V3;
  /** Where the press met the plane the component is dragged across. */
  grab: V3;
}

export interface ComponentDragHandlers {
  /** Past the threshold: true when the drag may begin; false drops the press. */
  start(press: ComponentPress): boolean;
  /** The corner the piece would go to with the pointer at (x, y), snapped; null keeps the one shown. */
  target(x: number, y: number, press: ComponentPress): V3 | null;
  /** Show the piece at `at` (only called when the corner changes). */
  preview(at: V3, press: ComponentPress): void;
  /** The release, where the piece last previewed somewhere other than where it began. */
  commit(at: V3, press: ComponentPress): void;
  /** A drag that ends without a move: put the piece back as it was. */
  cancel(press: ComponentPress): void;
}

/** Runs `run` once, later; the default waits for the next animation frame. */
export type FrameScheduler = (run: () => void) => void;
export const nextFrame: FrameScheduler = (run) => {
  requestAnimationFrame(() => run());
};
/** No coalescing: each pointer move previews at once. */
export const immediately: FrameScheduler = (run) => run();

interface Session {
  press: ComponentPress;
  started: boolean;
  /** The corner shown now (the origin until a preview moves it). */
  at: V3;
  /** The latest pointer position not yet previewed. */
  pointer: [number, number] | null;
  pending: boolean;
  /** The latest pointer position of the drag, previewed or not. */
  last: [number, number];
  /** The piece in hand changed shape (a turn while dragging): the release commits even where it began. */
  changed: boolean;
}

const same = (a: V3, b: V3) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

export class ComponentDrag {
  private session: Session | null = null;
  private bound: Window | null = null;
  private readonly threshold: number;
  private readonly schedule: FrameScheduler;

  constructor(
    private readonly handlers: ComponentDragHandlers,
    options: { threshold?: number; schedule?: FrameScheduler } = {},
  ) {
    this.threshold = options.threshold ?? DRAG_THRESHOLD_PX;
    this.schedule = options.schedule ?? nextFrame;
  }

  /** A press is held (a click or a drag). */
  get active(): boolean {
    return this.session !== null;
  }

  /** The press has become a drag: Escape belongs to it. */
  get moved(): boolean {
    return this.session?.started ?? false;
  }

  /** A press on a component; one already held is cancelled first. */
  press(press: ComponentPress): void {
    this.cancel();
    this.session = {
      press,
      started: false,
      at: press.origin,
      pointer: null,
      pending: false,
      last: [press.x, press.y],
      changed: false,
    };
  }

  /**
   * The piece in hand changed shape (a turn or a mirror while dragging): shows it again where the pointer is now, even
   * on the same corner, and the release then commits even where the drag began. Nothing happens before the drag starts.
   */
  refresh(): void {
    const s = this.session;
    if (!s?.started) return;
    s.changed = true;
    s.pending = false;
    s.pointer = null;
    s.at = this.handlers.target(s.last[0], s.last[1], s.press) ?? s.at;
    this.handlers.preview(s.at, s.press);
  }

  /** Drops the press; a drag in progress puts the piece back. */
  cancel(): void {
    const s = this.session;
    this.session = null;
    if (s?.started) this.handlers.cancel(s.press);
  }

  /** Listens on the window, so the pointer may leave the view while it is held. */
  bind(win: Window = window): void {
    if (this.bound === win) return;
    this.unbind();
    this.bound = win;
    win.addEventListener('pointermove', this.onMove, true);
    win.addEventListener('pointerup', this.onUp, true);
    win.addEventListener('pointercancel', this.onLost, true);
    win.addEventListener('lostpointercapture', this.onLost, true);
    win.addEventListener('keydown', this.onKey, true);
    win.addEventListener('blur', this.onBlur);
  }

  unbind(): void {
    this.cancel();
    const win = this.bound;
    if (!win) return;
    this.bound = null;
    win.removeEventListener('pointermove', this.onMove, true);
    win.removeEventListener('pointerup', this.onUp, true);
    win.removeEventListener('pointercancel', this.onLost, true);
    win.removeEventListener('lostpointercapture', this.onLost, true);
    win.removeEventListener('keydown', this.onKey, true);
    win.removeEventListener('blur', this.onBlur);
  }

  private readonly onMove = (event: PointerEvent) => {
    const s = this.session;
    if (!s || event.pointerId !== s.press.pointerId) return;
    if (!s.started) {
      if (Math.hypot(event.clientX - s.press.x, event.clientY - s.press.y) < this.threshold) return;
      if (!this.handlers.start(s.press)) {
        if (this.session === s) this.session = null;
        return;
      }
      s.started = true;
    }
    s.pointer = [event.clientX, event.clientY];
    s.last = s.pointer;
    if (s.pending) return;
    s.pending = true;
    this.schedule(() => this.flush(s));
  };

  /** Previews the latest pointer position of `s`, unless the press has ended since it was scheduled. */
  private flush(s: Session): void {
    if (this.session !== s || !s.pending) return;
    s.pending = false;
    const pointer = s.pointer;
    s.pointer = null;
    if (!pointer) return;
    const at = this.handlers.target(pointer[0], pointer[1], s.press);
    if (!at || same(at, s.at)) return;
    s.at = at;
    this.handlers.preview(at, s.press);
  }

  private readonly onUp = (event: PointerEvent) => {
    const s = this.session;
    if (!s || event.pointerId !== s.press.pointerId) return;
    // A move whose frame has not come yet still counts: the release lands where the pointer is.
    this.flush(s);
    this.session = null;
    if (!s.started) return;
    if (same(s.at, s.press.origin) && !s.changed) this.handlers.cancel(s.press);
    else this.handlers.commit(s.at, s.press);
  };

  private readonly onLost = (event: PointerEvent) => {
    if (event.pointerId === this.session?.press.pointerId) this.cancel();
  };

  private readonly onKey = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || !this.session?.started) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    this.cancel();
  };

  private readonly onBlur = () => this.cancel();
}
