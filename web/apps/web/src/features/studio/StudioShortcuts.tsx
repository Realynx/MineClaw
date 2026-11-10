'use client';

/**
 * A mine's keys: the shortcut sheet (the ? button or the ? key) and the hint line under the view, which follows what the
 * user is doing (nothing selected, a component or a box selected, a piece in hand). Every key listed here is one the studio
 * handles (studioInput.ts, useMinePlacement.ts' keyAction).
 */
import { useEffect, useRef } from 'react';

export type MineHintState = 'idle' | 'component' | 'selection' | 'placing';

/** What the hint line follows: a piece in hand first, then a selected component, then a box selection. */
export function mineHintState(p: { placing: boolean; component: boolean; selection: boolean }): MineHintState {
  if (p.placing) return 'placing';
  if (p.component) return 'component';
  return p.selection ? 'selection' : 'idle';
}

const MINE_HINTS: Record<MineHintState, string> = {
  idle: 'Middle drag: orbit · Shift + middle: pan · Wheel: zoom · Drag a piece in from the panel · PgUp / PgDn: working level · ?: all keys',
  component:
    'Arrows, PgUp / PgDn: move (Shift ×5) · [ ]: turn · F: frame · Ctrl+D: duplicate · Delete: remove · Esc: deselect · ?: all keys',
  selection: 'Delete: remove the plan inside · Ctrl+C: copy · Esc: deselect · ?: all keys',
  placing: 'Click to place · [ ] or R: turn · PgUp / PgDn: working level · Right-click or Esc: cancel · ?: all keys',
};

/** The hint line's text in a mine. */
export const mineHint = (state: MineHintState): string => MINE_HINTS[state];

/** The sheet: [keys, what they do] by section. */
export const MINE_SHORTCUTS: ReadonlyArray<{ title: string; keys: ReadonlyArray<readonly [string, string]> }> = [
  {
    title: 'View',
    keys: [
      ['Middle drag', 'Orbit (Alt + left drag too)'],
      ['Shift + middle drag', 'Pan'],
      ['Wheel', 'Zoom'],
      ['K', 'Cut the view away above a level, or show it all'],
      ['H', 'Grid on or off'],
      ['Tab', 'Hide or show the readouts'],
    ],
  },
  {
    title: 'A piece in hand',
    keys: [
      ['Click', 'Place it'],
      ['[ ] or R', 'Turn it (Shift+R turns back)'],
      ['PgUp / PgDn', 'Working level up or down (Shift ×5)'],
      ['Right-click or Esc', 'Put it down'],
    ],
  },
  {
    title: 'A selected component',
    keys: [
      ['Arrows', 'Move across (Shift ×5); a held key is one undo step'],
      ['PgUp / PgDn', 'Move up or down'],
      ['Drag the piece', 'Move it across its level, snapping to an opening of another piece'],
      ['Drag a handle', 'Move it; Esc while dragging puts it back'],
      ['[ ]', 'Turn it'],
      ['F', 'Frame it'],
      ['Ctrl+C / Ctrl+V / Ctrl+D', 'Copy, paste (at the working level when one is set), duplicate'],
      ['Delete', 'Remove it'],
      ['Ctrl+G', 'Combine the pieces ticked under Combine'],
      ['Ctrl / Shift + click in the list', 'Tick pieces to combine (Shift: a range)'],
      ['Ctrl+Shift+G', 'Ungroup a combined component'],
      ['Esc', 'Deselect'],
    ],
  },
  {
    title: 'Tools and selections',
    keys: [
      ['P', 'Dig (left) or fill back (right)'],
      ['X', 'Excavate box (left drag digs)'],
      ['G', 'Excavate brush'],
      ['V', 'Select a box'],
      ['Delete', 'Remove the plan inside the selection (never digs)'],
      ['Ctrl+A / Ctrl+I', 'Select all, invert'],
      ['PgUp / PgDn', 'With nothing selected: working level (Shift ×5)'],
    ],
  },
  {
    title: 'Design',
    keys: [
      ['Ctrl+Z', 'Undo'],
      ['Ctrl+Y or Ctrl+Shift+Z', 'Redo'],
      ['Ctrl+S', 'Save'],
      ['T or Enter', 'Chat with the designer'],
      ['?', 'This sheet'],
    ],
  },
];

/** The ? button that opens the sheet. */
export function ShortcutsButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label="Keyboard shortcuts"
      title="Keyboard shortcuts (?)"
      data-testid="shortcuts-button"
      className="absolute right-3 bottom-24 size-7 rounded-full bg-black/55 font-mono text-sm text-white hover:bg-black/75"
    >
      ?
    </button>
  );
}

/**
 * The sheet. It takes the focus while open, so the studio's own keys (which act only with the focus in the world) leave
 * Esc and ? to it; closing gives the focus back through `onClose`.
 */
export function StudioShortcuts({ onClose }: { onClose: () => void }) {
  const close = useRef<HTMLButtonElement>(null);
  useEffect(() => close.current?.focus(), []);
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Mine shortcuts"
        data-testid="studio-shortcuts"
        className="max-h-full w-full max-w-2xl overflow-y-auto rounded border border-border bg-surface p-4 text-xs text-fg shadow-lg"
        onKeyDown={(e) => {
          if (e.key !== 'Escape' && e.key !== '?') return;
          e.preventDefault();
          e.stopPropagation();
          onClose();
        }}
      >
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-sm font-semibold">Mine shortcuts</h2>
          <button ref={close} type="button" onClick={onClose} className="rounded border border-border px-2 py-1">
            Close
          </button>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          {MINE_SHORTCUTS.map((section) => (
            <section key={section.title}>
              <h3 className="mb-1 font-semibold text-muted">{section.title}</h3>
              <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                {section.keys.map(([k, what]) => (
                  <div key={k} className="contents">
                    <dt className="font-mono whitespace-nowrap">{k}</dt>
                    <dd>{what}</dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
      </div>
    </div>
  );
}
