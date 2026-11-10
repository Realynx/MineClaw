/**
 * The redstone studio's own input rules: the Configure tool (what a builder sets on a part after placing it: a repeater's
 * delay, a comparator's mode, a lever's start state), the pointer while a simulation is live (clicks use the circuit
 * instead of editing the canvas), placed components under the mouse and the keys (select, drag, turn, mirror), and the
 * ghost that places a Library component with the mouse free.
 */
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget } from '@mineclaw/world-viewer';
import { isTyping } from '@/features/world/directives';
import { inside, plotMode, type V3 } from '../canvas';
import { ComponentDrag, nextFrame, type ComponentPress, type FrameScheduler } from '../components/dragSession';
import { levelPoint, type PlacementRay } from '../components/levelRay';
import { prepareOutline, showDragPreview, type PreparedOutline, type PreviewOverlay } from '../components/outline';
import { FACE_NORMAL } from '../facing';
import type { Overlay3D } from '../overlay3d';
import type { PressCtx } from '../studioInput';
import type { Hit } from '../tools';
import { quarter, type Mirror } from './ports';
import { mirrorAsSeen, type RedstoneComponentController } from './RedstoneComponentController';

const LEVER = 69;
const isRepeater = (id: number) => id === 93 || id === 94;
const isComparator = (id: number) => id === 149 || id === 150;

/**
 * What the Configure tool makes of a block: its meta after one click and the words for the undo list, or null for a block
 * with nothing to set. A repeater's delay goes 1, 2, 3, 4 redstone ticks and round again (meta bits 4 and 8), a
 * comparator switches between compare and subtract (bit 4), a lever between off and on (bit 8); the facing stays.
 */
export function configured(id: number, meta: number): { meta: number; label: string } | null {
  if (isRepeater(id)) {
    const next = (meta & 3) | ((meta + 4) & 12);
    const ticks = (next >> 2) + 1;
    return { meta: next, label: `set a repeater to ${ticks} tick${ticks === 1 ? '' : 's'}` };
  }
  if (isComparator(id)) {
    const next = meta ^ 4;
    return { meta: next, label: `set a comparator to ${next & 4 ? 'subtract' : 'compare'}` };
  }
  if (id === LEVER) {
    const next = meta ^ 8;
    return { meta: next, label: `switched a lever ${next & 8 ? 'on' : 'off'}` };
  }
  return null;
}

/** The block the aim is on: the hit one, or the ground cell behind the face it stopped at. */
function aimedBlock(h: Hit): V3 | null {
  if (h.hit) return h.hit;
  const n = FACE_NORMAL[h.face];
  if (!h.place || !n) return null;
  const at: V3 = [h.place[0] - n[0], h.place[1] - n[1], h.place[2] - n[2]];
  return inside(...at) ? at : null;
}

/**
 * The Configure tool, in a redstone design only: a right click sets the part (one edit, so one undo step, and the bot
 * hears of it like any other), a left click shows the cell in the inspector.
 */
export function configurePress(c: Pick<PressCtx, 'studio' | 'h' | 'button' | 'setNotice' | 'inspect'>): void {
  const { studio, h } = c;
  if (studio.session.kind !== 'redstone') return;
  if (c.button === 0) {
    const at = aimedBlock(h);
    if (at) c.inspect?.(at);
    return;
  }
  if (c.button !== 2 || !h.hit) return;
  const [x, y, z] = h.hit;
  const [id, meta] = studio.canvas.get(x, y, z);
  const next = configured(id, meta);
  if (!next) return c.setNotice('Nothing to set here: right-click a repeater, a comparator or a lever.');
  studio.edit([[x, y, z, id, next.meta]], 'configure', next.label);
}

/** The cell the aim is on, in canvas coordinates: any block inside the plot, the ground too. */
export function aimedCell(t: AimTarget | null): Pos | null {
  if (!t || t.kind !== 'block') return null;
  const y = t.y - plotMode().baseY;
  return inside(t.x, y, t.z) ? [t.x, y, t.z] : null;
}

export interface SimPointer {
  /** A simulation is paused or running: only then are the clicks the circuit's. */
  live: () => boolean;
  /** The cell under the crosshair now. */
  aim: () => Promise<Pos | null>;
  /** Uses the lever, button or plate at a cell; false when there is none. */
  click: (at: Pos) => boolean;
  /** Shows a cell in the inspector. */
  inspect: (at: Pos) => void;
}

/**
 * While a simulation is live, a click in the world belongs to the circuit: the left or right button uses an input, on
 * anything else it shows the cell in the inspector, and the middle button always inspects. Bound on the stage, above the
 * viewer's surface, so it comes before the studio's own pointer rules and they never see these clicks. A click with the
 * mouse not locked is left alone: it locks the mouse, as it does when building.
 */
export function bindSimPointer(stage: HTMLElement, p: SimPointer): () => void {
  const onDown = (event: PointerEvent) => {
    if (event.button > 2 || !p.live()) return;
    const lock = document.pointerLockElement;
    if (!lock || !stage.contains(lock)) return;
    event.preventDefault();
    event.stopPropagation();
    const use = event.button !== 1;
    void p
      .aim()
      .then((at) => {
        // The simulation may have ended while the aim was worked out; the click is nobody's then.
        if (!at || !p.live()) return;
        if (!use || !p.click(at)) p.inspect(at);
      })
      .catch(() => undefined);
  };
  stage.addEventListener('pointerdown', onDown, true);
  return () => stage.removeEventListener('pointerdown', onDown, true);
}

// ------------------------------------------------------------------ placed components under the mouse

/** What a press at a point of the view is on: a gizmo handle (the gizmo takes the press), a component, or nothing. */
export type ComponentHit =
  | { kind: 'handle' }
  | {
      kind: 'component';
      id: string;
      /** Its box's corner now. */
      origin: V3;
      /** Where the press met the level the component stands on; null when it cannot be dragged from there. */
      grab: V3 | null;
    };

export interface ComponentPointer {
  /** Components take presses now: not while a simulation is live or the Library asks for a cell. */
  ready: () => boolean;
  /** The Select tool is on: a click with the mouse locked selects the component under the crosshair. */
  selecting: () => boolean;
  /** What a press at a client point is on; `null` asks for the crosshair (the view's centre, the mouse locked). */
  hit: (at: { x: number; y: number } | null) => ComponentHit | null;
  select: (id: string) => void;
  /** A press on a component that may become a drag (RedstoneComponentDrag). */
  press: (press: ComponentPress) => void;
  /** The keys go to the view (the engine, which would focus it, never sees the press). */
  focus: () => void;
  /** The view's surface: only a press on it is a press in the world, not one on a control drawn over the view. */
  surface: () => Element | null | undefined;
}

/** The event is on the view itself, not on a toolbar, the hotbar or a panel the stage also holds. */
function onView(event: Event, surface: () => Element | null | undefined): boolean {
  const el = surface();
  return !!el && event.target instanceof Node && el.contains(event.target);
}

/**
 * The mouse on placed components. A plain left press on one, with the mouse not locked, selects it and may drag it; the
 * engine never sees that press, so the view does not lock. A press anywhere else, on a gizmo handle, with a modifier or
 * another button, is left alone: it locks the mouse as it always has, and the right and middle buttons still look
 * around. With the mouse locked and the Select tool, a click on a component under the crosshair selects it. Bound on the
 * stage in capture phase, so it comes before the viewer's surface and the studio's own pointer rules.
 */
export function bindComponentPointer(stage: HTMLElement, p: ComponentPointer): () => void {
  const onDown = (event: PointerEvent) => {
    if (event.button !== 0 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || !p.ready()) return;
    if (!onView(event, p.surface)) return;
    const lock = document.pointerLockElement;
    const locked = !!lock && stage.contains(lock);
    if (locked && !p.selecting()) return;
    const h = p.hit(locked ? null : { x: event.clientX, y: event.clientY });
    if (h?.kind !== 'component') return;
    event.preventDefault();
    event.stopPropagation();
    p.select(h.id);
    if (locked) return;
    p.focus();
    if (h.grab)
      p.press({
        pointerId: event.pointerId,
        x: event.clientX,
        y: event.clientY,
        id: h.id,
        origin: h.origin,
        grab: h.grab,
      });
  };
  stage.addEventListener('pointerdown', onDown, true);
  return () => stage.removeEventListener('pointerdown', onDown, true);
}

type DragController = Pick<
  RedstoneComponentController,
  'snapshot' | 'shapeOf' | 'snapMoveCorner' | 'check' | 'previewPorts' | 'move'
>;

export interface RedstoneDragDeps {
  controller: DragController;
  overlay: Pick<Overlay3D, 'setMinePlacementGeometry' | 'setComponentPorts' | 'setMinePlacement' | 'setSelection'> &
    Partial<Pick<Overlay3D, 'setPortArrows'>>;
  /** The world ray under a client point, or null before the view has a size. */
  ray: (x: number, y: number) => PlacementRay | null;
  /** The user may change the design now (not read-only, busy, or with a simulation live). */
  canEdit: () => boolean;
  /** Why the outline shown cannot stand where it is; null when it can, or when no drag is shown. */
  refused: (reason: string | null) => void;
  /** A move landed: the component's new box becomes the selection. */
  moved: (id: string) => void;
  /** A drag ended: the overlay shows the selection and the ports as they are again. */
  rest: () => void;
  notice: (text: string) => void;
  /** When a pointer move is previewed; the default is once per animation frame. */
  schedule?: FrameScheduler;
}

interface Held {
  id: string;
  turns: number;
  mirror: Mirror;
  outline: PreparedOutline;
  /** The corner the last pointer position asked for, and the joint its snap shares (null without a snap). */
  last: { at: V3; joint: Pos | null } | null;
  /** The component's corner when it was picked up. */
  origin: V3;
  /** Held by a gizmo handle (studioPointer moves it) rather than by a mouse drag of its body. */
  gizmo: boolean;
}

/** Said when a drag is released after editing stopped: the piece went back, and this is why. */
export const BUSY_MOVE =
  'The design is busy (the designer is replying, or a save is under way): the component went back.';

const plus = (a: V3, b: V3): V3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const same = (a: V3, b: V3) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

/**
 * Dragging a placed redstone component across the level it stands on (`ComponentDrag` does the pointer). Each frame the
 * outline, its ports and the selection box move in the overlay only (the canvas is not touched), snapped onto a port or
 * a dust end within reach, red with the controller's reason where it cannot stand. The release is one `move`: one undo
 * step and one edit for the bot. A turn or a mirror while dragging changes the piece in hand, and the release keeps it.
 */
export class RedstoneComponentDrag {
  private held: Held | null = null;
  private readonly drag: ComponentDrag;

  constructor(private readonly d: RedstoneDragDeps) {
    this.drag = new ComponentDrag(
      {
        start: (press) => this.start(press),
        target: (x, y, press) => this.target(x, y, press),
        preview: (at) => this.preview(at),
        commit: (at) => this.commit(at),
        cancel: () => this.end(),
      },
      d.schedule ? { schedule: d.schedule } : {},
    );
  }

  /** A press on a component is held (a click or a drag). */
  get active(): boolean {
    return this.drag.active;
  }

  /** The press has become a drag: Escape and the turn keys are its. */
  get moved(): boolean {
    return this.drag.moved;
  }

  bind(win?: Window): void {
    this.drag.bind(win);
  }

  unbind(): void {
    this.drag.unbind();
    this.gizmoCancel();
  }

  press(press: ComponentPress): void {
    this.drag.press(press);
  }

  /** Puts a dragged piece back where it was (a gizmo drag's too). */
  cancel(): void {
    this.drag.cancel();
    this.gizmoCancel();
  }

  /** A quarter turn of the piece in hand (1 clockwise, 3 counter-clockwise); false when nothing is being dragged. */
  turn(by: number): boolean {
    return this.reshape((h) => ({ turns: quarter(h.turns + by), mirror: h.mirror }));
  }

  /** Mirrors the piece in hand across x or z as seen now; false when nothing is being dragged. */
  mirror(axis: 'x' | 'z'): boolean {
    return this.reshape((h) => mirrorAsSeen(h.turns, h.mirror, axis));
  }

  private reshape(next: (h: Held) => { turns: number; mirror: Mirror }): boolean {
    const h = this.held;
    if (!h || !this.drag.moved) return false;
    const n = next(h);
    const shape = this.d.controller.shapeOf(h.id, n.turns, n.mirror);
    if (!shape) return false;
    h.turns = n.turns;
    h.mirror = n.mirror;
    h.outline = prepareOutline(shape.cells);
    this.drag.refresh();
    return true;
  }

  private start(press: ComponentPress): boolean {
    // A gizmo handle already holds a piece (another finger): it keeps it.
    return !this.held?.gizmo && this.hold(press.id, false);
  }

  private hold(id: string, gizmo: boolean): boolean {
    const c = this.d.controller.snapshot().components.find((x) => x.id === id);
    const shape = c && this.d.canEdit() ? this.d.controller.shapeOf(c.id) : null;
    if (!c || !shape) return false;
    // The outline is worked out once here; each frame then only moves it.
    const origin: V3 = [c.at[0], c.at[1], c.at[2]];
    this.held = {
      id: c.id,
      turns: c.turns,
      mirror: c.mirror,
      outline: prepareOutline(shape.cells),
      last: null,
      origin,
      gizmo,
    };
    return true;
  }

  /** A gizmo handle drag of a component is held: the keys leave the piece alone until it ends. */
  get holdingGizmo(): boolean {
    return !!this.held?.gizmo;
  }

  /**
   * A press on a gizmo handle of the selected component: from here the handle only previews the move in the overlay
   * (`gizmoPreview`) and the release is one move (`gizmoCommit`), as a mouse drag is. False when it cannot be held (a
   * mouse drag is on, or editing is not allowed).
   */
  gizmoStart(id: string): boolean {
    if (this.drag.active || this.held) return false;
    return this.hold(id, true);
  }

  /** Shows the held piece moved by `delta` from where the gizmo drag began; a refused spot is shown red. */
  gizmoPreview(delta: V3): boolean {
    const h = this.held;
    if (!h?.gizmo) return false;
    this.preview(plus(h.origin, delta));
    return true;
  }

  /** The gizmo's release: one move by `delta`, or nothing when it is back where it began. */
  gizmoCommit(delta: V3): void {
    const h = this.held;
    if (!h?.gizmo) return;
    if (same(delta, [0, 0, 0])) return this.end();
    this.commit(plus(h.origin, delta));
  }

  /** Escape or a lost pointer during a gizmo drag: the piece goes back. */
  gizmoCancel(): void {
    if (this.held?.gizmo) this.end();
  }

  private target(x: number, y: number, press: ComponentPress): V3 | null {
    const h = this.held;
    const ray = h ? this.d.ray(x, y) : null;
    const point = ray ? levelPoint(ray, press.origin[1]) : null;
    if (!h || !point) return null;
    const [x0, y0, z0] = press.origin;
    const at: V3 = [x0 + point[0] - press.grab[0], y0, z0 + point[2] - press.grab[2]];
    const snap = this.d.controller.snapMoveCorner(h.id, at, h.turns, h.mirror);
    h.last = snap ? { at: [snap.at[0], snap.at[1], snap.at[2]], joint: snap.joint } : { at, joint: null };
    return h.last.at;
  }

  private jointAt(h: Held, at: V3): Pos | null {
    return h.last && same(h.last.at, at) ? h.last.joint : null;
  }

  private preview(at: V3): void {
    const h = this.held;
    if (!h) return;
    const { controller, overlay } = this.d;
    const reason = controller.check(h.id, at, h.turns, h.mirror);
    this.d.refused(reason);
    const marks = controller.previewPorts(h.id, at, h.turns, h.mirror, this.jointAt(h, at));
    const ports = controller.shapeOf(h.id, h.turns, h.mirror)?.ports ?? [];
    showDragPreview(overlay, h.outline.positions, at, reason !== null, marks.open, marks.joined, ports);
    const b = h.outline.bounds;
    if (b) overlay.setSelection({ min: plus(at, b.min), max: plus(at, b.max) });
  }

  private commit(at: V3): void {
    const h = this.held;
    const joint = h ? this.jointAt(h, at) : null;
    this.end();
    if (!h) return;
    // Editing stopped during the drag (the designer is replying, a save or a version change is under way).
    if (!this.d.canEdit()) return this.d.notice(BUSY_MOVE);
    const { controller } = this.d;
    // A refused spot moves nothing and says why (the outline was red there).
    const reason = controller.check(h.id, at, h.turns, h.mirror);
    if (reason) return this.d.notice(reason);
    if (controller.move(h.id, at, { joint, turns: h.turns, mirror: h.mirror })) this.d.moved(h.id);
    else this.d.notice(controller.snapshot().error ?? 'The component could not move there.');
  }

  private end(): void {
    this.held = null;
    this.d.overlay.setMinePlacement(null);
    this.d.refused(null);
    this.d.rest();
  }
}

// ------------------------------------------------------------------ placing from the Library with the mouse free

/** What the Library asked to place: a definition, turned and mirrored, maybe snapped onto an output port. */
export interface PlaceRequest {
  def: string;
  turns: number;
  mirror: Mirror;
  snap: boolean;
}

export interface PlaceGhostDeps {
  controller: Pick<RedstoneComponentController, 'placeShape' | 'previewPlace' | 'place' | 'snapshot'>;
  overlay: PreviewOverlay & Pick<Overlay3D, 'setMinePlacement'>;
  /** The world ray under a client point, or null before the view has a size. */
  ray: (x: number, y: number) => PlacementRay | null;
  /** The level (canvas y) the ghost's box stands on. */
  level: () => number;
  /** The view's surface: the ghost follows and places only for the mouse on it, never on a control over the view. */
  surface: () => Element | null | undefined;
  /** The user may change the design now (not read-only, busy, or with a simulation live). */
  canEdit: () => boolean;
  /** Why the ghost cannot stand where it is shown; null when it can, or when it is hidden. */
  refused: (reason: string | null) => void;
  /** A component was placed: placing is over, and it is the selection. */
  placed: (id: string) => void;
  /** A right click or Escape: placing is over and nothing was placed. */
  cancel: () => void;
  /** The ghost is hidden: the overlay shows the selection and the ports as they are again. */
  rest: () => void;
  notice: (text: string) => void;
  /** When a pointer move is shown; the default is once per animation frame. */
  schedule?: FrameScheduler;
}

/**
 * Placing a Library component with the mouse not locked. Its outline and ports (a ghost) follow the mouse across a level,
 * at most once per frame and in the overlay only, red with the reason where the placement would be refused; a plain left
 * click places it there (one undo step, as a locked click does), and a right click or Escape stops placing. The engine
 * never sees those presses, so the view does not lock; the middle button and a modified press still go to it. With the
 * mouse locked the ghost hides and the locked click places as before. Bound on the stage in capture phase.
 */
export function bindPlaceGhost(
  stage: HTMLElement,
  req: PlaceRequest,
  d: PlaceGhostDeps,
  win: Window = window,
): () => void {
  const shape = d.controller.placeShape(req.def, req.turns, req.mirror);
  if (!shape) return () => undefined;
  // Worked out once; each frame then only moves it.
  const outline = prepareOutline(shape.cells);
  const schedule = d.schedule ?? nextFrame;
  const options = { turns: req.turns, mirror: req.mirror, snap: req.snap };
  /** The cell the ghost is shown for, or null while it is hidden. */
  let shown: V3 | null = null;
  let pointer: [number, number] | null = null;
  let pending = false;
  let bound = true;
  const locked = () => {
    const lock = document.pointerLockElement;
    return !!lock && stage.contains(lock);
  };
  const cellAt = (x: number, y: number): V3 | null => {
    const ray = d.ray(x, y);
    return ray ? levelPoint(ray, d.level()) : null;
  };
  const hide = () => {
    pointer = null;
    if (!shown) return;
    shown = null;
    d.overlay.setMinePlacement(null);
    d.refused(null);
    d.rest();
  };
  const show = (cell: V3) => {
    if (shown && same(shown, cell)) return;
    const p = d.controller.previewPlace(req.def, cell, options);
    if (!p) return hide();
    shown = cell;
    d.refused(p.reason);
    const at: V3 = [p.at[0], p.at[1], p.at[2]];
    showDragPreview(d.overlay, outline.positions, at, p.reason !== null, p.ports.open, p.ports.joined, shape.ports);
  };
  const flush = () => {
    pending = false;
    const at = pointer;
    pointer = null;
    if (!bound || !at) return;
    if (locked()) return hide();
    const cell = cellAt(at[0], at[1]);
    if (cell) show(cell);
    else hide();
  };
  const onMove = (event: PointerEvent) => {
    if (locked() || !onView(event, d.surface)) return hide();
    pointer = [event.clientX, event.clientY];
    if (pending) return;
    pending = true;
    schedule(flush);
  };
  const onDown = (event: PointerEvent) => {
    if (locked() || !onView(event, d.surface)) return;
    if (event.button === 2) {
      event.preventDefault();
      event.stopPropagation();
      return d.cancel();
    }
    if (event.button !== 0 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    event.preventDefault();
    event.stopPropagation();
    const cell = cellAt(event.clientX, event.clientY);
    if (!cell || !d.canEdit()) return;
    const placed = d.controller.place(req.def, cell, options);
    if (placed) d.placed(placed.id);
    else d.notice(d.controller.snapshot().error ?? 'The component could not go there.');
  };
  const onKey = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || isTyping(event.target)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    d.cancel();
  };
  stage.addEventListener('pointerdown', onDown, true);
  stage.addEventListener('pointermove', onMove, true);
  stage.addEventListener('pointerleave', hide);
  win.addEventListener('keydown', onKey, true);
  return () => {
    bound = false;
    stage.removeEventListener('pointerdown', onDown, true);
    stage.removeEventListener('pointermove', onMove, true);
    stage.removeEventListener('pointerleave', hide);
    win.removeEventListener('keydown', onKey, true);
    hide();
  };
}

export interface ComponentKeys {
  /** The selected component, or null. */
  selected: () => string | null;
  canEdit: () => boolean;
  /** The view has the keys: the focus is in it or the mouse is locked to it. */
  inWorld: () => boolean;
  drag: Pick<RedstoneComponentDrag, 'moved' | 'cancel' | 'turn' | 'mirror'>;
  turn: (id: string, by: number) => void;
  mirror: (id: string, axis: 'x' | 'z') => void;
  remove: (id: string) => void;
  duplicate: (id: string) => void;
}

/**
 * What a key does to the selected component (or the one being dragged), or null when the key is not the components'.
 * R or ] turns it clockwise, Shift+R or [ counter-clockwise, M mirrors it across x (Shift+M across z), Delete removes it
 * and Ctrl+D duplicates it; Escape puts a dragged one back. While editing is not allowed (a live simulation, a read-only
 * page) the key is still taken and does nothing, so the page never edits a component's cells as raw ones.
 */
export function componentKeyAction(e: KeyboardEvent, k: ComponentKeys): (() => void) | null {
  const dragging = k.drag.moved;
  if (e.key === 'Escape') return dragging ? () => k.drag.cancel() : null;
  if (e.altKey || isTyping(e.target) || !k.inWorld()) return null;
  const id = k.selected();
  if (!id && !dragging) return null;
  const ctrl = e.ctrlKey || e.metaKey;
  const back = e.key === '[' || (e.code === 'KeyR' && e.shiftKey);
  const turnBy = ctrl ? 0 : back ? 3 : e.key === ']' || e.code === 'KeyR' ? 1 : 0;
  const mirror: 'x' | 'z' | null = !ctrl && e.code === 'KeyM' ? (e.shiftKey ? 'z' : 'x') : null;
  const remove = !ctrl && (e.key === 'Delete' || e.key === 'Backspace');
  const duplicate = ctrl && e.key.toLowerCase() === 'd';
  if (!turnBy && !mirror && !remove && !duplicate) return null;
  if (!k.canEdit()) return () => undefined;
  if (dragging && turnBy) return () => void k.drag.turn(turnBy);
  if (dragging && mirror) return () => void k.drag.mirror(mirror);
  if (!id) return null;
  if (turnBy) return () => k.turn(id, turnBy);
  if (mirror) return () => k.mirror(id, mirror);
  return () => {
    // The piece goes back first: the copy or the removal is of the component where it stands.
    k.drag.cancel();
    if (remove) k.remove(id);
    else k.duplicate(id);
  };
}
