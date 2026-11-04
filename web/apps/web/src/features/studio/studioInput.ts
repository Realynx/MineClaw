/**
 * The Design Studio's input rules (StudioPage binds them once and hands them its current state): the keyboard (see the
 * controls in StudioPage's header comment), what a mouse press and release do with each tool, and the drag / brush previews.
 */
import type { Dispatch, SetStateAction } from 'react';
import { isTyping } from '@/features/world/directives';
import { errorText } from '@/lib/api/client';
import { boxOf, connected, extend, invert, maskSelection, plotMode, selectAll, sizeOf, type Selection, type V3 } from './canvas';
import type { Tool } from './StudioChrome';
import type { Version } from './facing';
import type { PaletteEntry } from './palette';
import type { Studio } from './studio';
import {
  boxCells,
  type BoxMode,
  brushCells,
  type BrushMode,
  type BrushShape,
  type Clip,
  erase,
  extrude,
  floodFace,
  type Hit,
  lineCells,
  pasteCells,
  rotate,
} from './tools';

export type Drag = { button: number; a: V3; dy: number };

/** The studio state the input handlers read (current on every event). */
export interface InputState {
  tool: Tool;
  boxMode: BoxMode;
  brushShape: BrushShape;
  brushSize: number;
  brushMode: BrushMode;
  extrudeN: number;
  slots: Array<PaletteEntry | null>;
  active: number;
  clip: Clip | null;
  pasting: boolean;
  drag: Drag | null;
  version: Version;
  under: number | null;
}

/** Breaking / digging reaches the ground: underground mode, or a mine (its ground is always editable). */
export const groundEditable = (s: Pick<InputState, 'under'>) => s.under !== null || plotMode().kind === 'mine';

// ------------------------------------------------------------------ keyboard

export interface KeyActions {
  studio: Studio;
  state: () => InputState;
  /** The stage has the focus or the locked pointer. */
  inWorld: () => boolean;
  doCopy: () => Clip | null;
  setPasting: (v: boolean) => void;
  setDrag: Dispatch<SetStateAction<Drag | null>>;
  moveSel: (d: V3) => void;
  setActive: (i: number) => void;
  openChat: () => void;
  toggleHud: () => void;
  applyTransform: (label: string, f: (s: Selection) => ReturnType<typeof rotate>) => void;
  setTool: (t: Tool) => void;
  /** R / Shift+R: the placement facing a quarter clockwise / counter-clockwise. */
  turnFacing: (by: 1 | -1) => void;
  togglePalette: () => void;
  toggleGrid: () => void;
  toggleUnder: () => void;
}

const stopKey = (e: KeyboardEvent) => {
  e.preventDefault();
  e.stopPropagation();
};

/** Ctrl / Cmd: undo, redo, copy, paste, duplicate, select all, invert. */
function ctrlKey(e: KeyboardEvent, a: KeyActions, s: InputState): void {
  const { studio } = a;
  const act: Record<string, (() => void) | undefined> = {
    z: e.shiftKey ? () => studio.redo() : () => studio.undo(),
    y: () => studio.redo(),
    c: () => void a.doCopy(),
    v: s.clip ? () => a.setPasting(true) : undefined,
    d: () => {
      if (a.doCopy()) a.setPasting(true);
    },
    a: () => studio.setSelection(selectAll(studio.canvas)),
    i: () => studio.setSelection(invert(studio.canvas, studio.selection)),
  };
  const f = act[e.key.toLowerCase()];
  if (f) {
    stopKey(e);
    f();
  }
}

const MOVE_KEYS: Record<string, V3> = {
  ArrowLeft: [-1, 0, 0],
  ArrowRight: [1, 0, 0],
  ArrowUp: [0, 0, -1],
  ArrowDown: [0, 0, 1],
  PageUp: [0, 1, 0],
  PageDown: [0, -1, 0],
};
const isLevelKey = (key: string) => key === 'PageUp' || key === 'PageDown';

/** One plain-key rule: true when it took the key (later rules are not asked). */
type KeyRule = (e: KeyboardEvent, a: KeyActions, s: InputState) => boolean;

/** Arrows / PgUp / PgDn move the selection (Shift ×5) instead of the camera while there is one. */
const moveSelectionKey: KeyRule = (e, a, s) => {
  if (!a.studio.selection || s.drag || !(e.key.startsWith('Arrow') || isLevelKey(e.key))) return false;
  stopKey(e);
  const n = e.shiftKey ? 5 : 1;
  const unit = MOVE_KEYS[e.key] ?? [0, -1, 0];
  a.moveSel([unit[0] * n, unit[1] * n, unit[2] * n]);
  return true;
};

/** PgUp / PgDn while dragging a selection box: its height. */
const dragLevelKey: KeyRule = (e, a, s) => {
  if (!s.drag || !isLevelKey(e.key)) return false;
  stopKey(e);
  a.setDrag((d) => (d ? { ...d, dy: d.dy + (e.key === 'PageUp' ? 1 : -1) } : d));
  return true;
};

const hotbarKey: KeyRule = (e, a) => {
  if (!/^Digit[1-9]$/.test(e.code)) return false;
  a.setActive(Number(e.code.slice(5)) - 1);
  return true;
};

const chatKey: KeyRule = (e, a) => {
  if (e.code !== 'KeyT' && e.key !== 'Enter' && e.key !== '/') return false;
  stopKey(e);
  a.openChat();
  return true;
};

const hudKey: KeyRule = (e, a) => {
  if (e.key !== 'Tab') return false;
  stopKey(e);
  a.toggleHud();
  return true;
};

const deleteKey: KeyRule = (e, a, s) => {
  if (e.key !== 'Delete' && e.key !== 'Backspace') return false;
  const { studio } = a;
  if (studio.selection) {
    stopKey(e);
    studio.edit(erase(studio.canvas, studio.selection, groundEditable(s)), 'remove', 'deleted the selection');
  }
  return true;
};

const escapeKey: KeyRule = (e, a, s) => {
  if (e.key !== 'Escape') return false;
  if (s.pasting) a.setPasting(false);
  else if (s.drag) a.setDrag(null);
  return true;
};

const rotateKey: KeyRule = (e, a, s) => {
  if (e.key !== '[' && e.key !== ']') return false;
  const cw = e.key === ']';
  a.applyTransform(`rotate ${cw ? 'clockwise' : 'counter-clockwise'}`, (x) => rotate(a.studio.canvas, x, cw ? 1 : 3, s.version));
  return true;
};

const TOOL_KEYS: Record<string, Tool> = {
  KeyP: 'build',
  KeyV: 'select',
  KeyX: 'box',
  KeyL: 'line',
  KeyG: 'brush',
  KeyF: 'face',
  KeyU: 'extrude',
  KeyO: 'anchor',
};

const toolKey: KeyRule = (e, a) => {
  const t = Object.hasOwn(TOOL_KEYS, e.code) ? TOOL_KEYS[e.code] : undefined;
  if (!t) return false;
  a.setTool(t);
  return true;
};

const toggleKey: KeyRule = (e, a) => {
  switch (e.code) {
    case 'KeyR':
      a.turnFacing(e.shiftKey ? -1 : 1);
      return true;
    case 'KeyB':
      e.preventDefault(); // the "b" never lands in the palette's search box that opens with it
      a.togglePalette();
      return true;
    case 'KeyH':
      a.toggleGrid();
      return true;
    case 'KeyK':
      a.toggleUnder();
      return true;
    default:
      return false;
  }
};

/** The plain-key rules in order. */
const KEY_RULES: KeyRule[] = [
  moveSelectionKey,
  dragLevelKey,
  hotbarKey,
  chatKey,
  hudKey,
  deleteKey,
  escapeKey,
  rotateKey,
  toolKey,
  toggleKey,
];

/** A key in the world (capture phase): ignored while typing or with the focus elsewhere. */
export function studioKey(e: KeyboardEvent, a: KeyActions): void {
  if (isTyping(e.target) || !a.inWorld()) return;
  const s = a.state();
  if (e.ctrlKey || e.metaKey) return ctrlKey(e, a, s);
  for (const rule of KEY_RULES) if (rule(e, a, s)) return;
}

// ------------------------------------------------------------------ mouse

export interface PressCtx {
  studio: Studio;
  h: Hit;
  button: number;
  mods: { shift: boolean; ctrl: boolean };
  s: InputState;
  setDrag: (d: Drag) => void;
  setPasting: (v: boolean) => void;
  pick: (h: Hit) => void;
  placeAt: (p: V3, face: number) => void;
  startBrush: (h: Hit, button: number) => void;
  setNotice: (text: string) => void;
}

const entryOf = (s: InputState) => s.slots[s.active];

/** Select's right click: the connected structure (Ctrl: of any block, else of the same block). */
function selectConnected(c: PressCtx): void {
  if (!c.h.hit) return;
  const mask = connected(c.studio.canvas, c.h.hit, !c.mods.ctrl);
  c.studio.setSelection(mask.size ? maskSelection(mask) : null);
}

/** Box, line and select drags start at the placed cell (right) or the hit one (left). */
function startDrag(c: PressCtx): void {
  if (c.s.tool === 'select' && c.button === 2) return selectConnected(c);
  const a = c.button === 2 ? c.h.place : (c.h.hit ?? c.h.place);
  if (a) c.setDrag({ button: c.button, a, dy: 0 });
}

function buildPress({ studio, h, button, placeAt }: PressCtx): void {
  if (button === 0 && h.hit) studio.edit([[...h.hit, 0, 0]], 'remove', `broke ${studio.canvas.id(...h.hit)}`);
  if (button === 2 && h.place) placeAt(h.place, h.face);
}

/** Face fill: right paints the face area with the current block, left removes it. */
function facePress({ studio, h, button, s }: PressCtx): void {
  if (!h.hit) return;
  if (button === 0) {
    studio.edit(floodFace(studio.canvas, h.hit, h.face, 0, 0), 'face', 'removed a face area');
    return;
  }
  const e = entryOf(s);
  studio.edit(floodFace(studio.canvas, h.hit, h.face, e?.id ?? 4, e?.meta ?? 0), 'face', `painted a face area ${e?.name ?? ''}`);
}

/** Extrude: right pushes the face out by N, left pulls it in. */
function extrudePress({ studio, h, button, s }: PressCtx): void {
  if (!h.hit) return;
  const pull = button === 0;
  studio.edit(
    extrude(studio.canvas, h.hit, h.face, pull ? -s.extrudeN : s.extrudeN),
    'extrude',
    `${pull ? 'pulled' : 'pushed'} a face by ${s.extrudeN}`,
  );
}

function anchorPress({ studio, h, setNotice }: PressCtx): void {
  const p = h.hit ?? h.place;
  if (p) void studio.setAnchor(p).catch((err: unknown) => setNotice(errorText(err)));
}

const TOOL_PRESS: Record<Tool, (c: PressCtx) => void> = {
  build: buildPress,
  box: startDrag,
  line: startDrag,
  select: startDrag,
  brush: (c) => c.startBrush(c.h, c.button),
  face: facePress,
  extrude: extrudePress,
  anchor: anchorPress,
};

/** A mouse press in the world on the aimed hit: middle picks, a paste places (Shift keeps pasting), else the tool acts. */
export function studioPress(c: PressCtx): void {
  if (c.button === 1) return c.pick(c.h);
  const { s, studio, h } = c;
  if (s.pasting && s.clip) {
    if (h.place) studio.edit(pasteCells(s.clip, h.place), 'paste', `pasted ${s.clip.cells.length} blocks`);
    if (!c.mods.shift) c.setPasting(false);
    return;
  }
  TOOL_PRESS[s.tool](c);
}

/** Where a drag ends: the placed cell (right), the hit one (left), or its start when nothing is aimed. */
export function dragEnd(h: Hit | null, button: number, d: Drag): V3 {
  return (button === 2 ? h?.place : (h?.hit ?? h?.place)) ?? d.a;
}

/** A finished select drag: a box up to the wheel-raised top (Shift extends the selection). */
function endSelect(studio: Studio, d: Drag, b: V3, shift: boolean): void {
  const top: V3 = [b[0], Math.min(plotMode().maxY, Math.max(b[1], d.a[1]) + d.dy), b[2]];
  studio.setSelection(shift ? extend(studio.selection, d.a, top) : boxOf(d.a, top));
}

/** A finished box drag: right fills it (in the box mode) with the current block, left removes it. */
function endBox(studio: Studio, s: InputState, d: Drag, b: V3, button: number): void {
  const e = entryOf(s);
  const size = sizeOf(boxOf(d.a, b)).join('×');
  if (button === 2 && e) studio.edit(boxCells(d.a, b, e.id, e.meta, s.boxMode), s.boxMode, `${s.boxMode} ${size} ${e.name}`);
  if (button === 0) studio.edit(boxCells(d.a, b, 0, 0, 'fill'), 'remove', `removed ${size}`);
}

/** A finished line drag: right draws it with the current block, left removes it. */
function endLine(studio: Studio, s: InputState, d: Drag, b: V3, button: number): void {
  const e = entryOf(s);
  if (button === 2 && e) studio.edit(lineCells(d.a, b, e.id, e.meta), 'line', `line of ${e.name}`);
  if (button === 0) studio.edit(lineCells(d.a, b, 0, 0), 'remove', 'removed a line');
}

/** A mouse release that ends this button's drag, on the aimed hit. */
export function studioRelease(studio: Studio, s: InputState, d: Drag, h: Hit | null, button: number, shift: boolean): void {
  const b = dragEnd(h, button, d);
  if (s.tool === 'select') return endSelect(studio, d, b, shift);
  if (s.tool === 'box') endBox(studio, s, d, b, button);
  if (s.tool === 'line') endLine(studio, s, d, b, button);
}

// ------------------------------------------------------------------ previews

type Box = { min: V3; max: V3 };

/** The drag's box (a select box up to its wheel-raised top); null without a drag or an aim. */
export function dragPreview(drag: Drag | null, h: Hit | null, tool: Tool): Box | null {
  if (!drag || !h) return null;
  const end = (drag.button === 2 && (tool === 'box' || tool === 'line') ? h.place : (h.hit ?? h.place)) ?? drag.a;
  const box = boxOf(drag.a, end);
  if (tool !== 'select') return box;
  return { min: box.min, max: [box.max[0], Math.min(plotMode().maxY, box.max[1] + drag.dy), box.max[2]] };
}

/** The brush's bounding box at the aimed cell; null when not brushing or nothing is aimed. */
export function brushPreview(h: Hit | null, tool: Tool, mode: BrushMode, size: number, shape: BrushShape): Box | null {
  if (tool !== 'brush' || !h) return null;
  const c = (mode === 'add' ? h.place : h.hit) ?? h.place;
  if (!c) return null;
  const cells = brushCells(c, size, shape);
  if (!cells.length) return null;
  const xs = cells.map((p) => p[0]),
    ys = cells.map((p) => p[1]),
    zs = cells.map((p) => p[2]);
  return {
    min: [Math.min(...xs), Math.min(...ys), Math.min(...zs)],
    max: [Math.max(...xs), Math.max(...ys), Math.max(...zs)],
  };
}
