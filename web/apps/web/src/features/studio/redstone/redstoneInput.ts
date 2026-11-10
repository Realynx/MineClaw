/**
 * The redstone studio's own input rules: the Configure tool (what a builder sets on a part after placing it: a repeater's
 * delay, a comparator's mode, a lever's start state) and the pointer while a simulation is live (clicks use the circuit
 * instead of editing the canvas).
 */
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget } from '@mineclaw/world-viewer';
import { inside, plotMode, type V3 } from '../canvas';
import { FACE_NORMAL } from '../facing';
import type { PressCtx } from '../studioInput';
import type { Hit } from '../tools';

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
