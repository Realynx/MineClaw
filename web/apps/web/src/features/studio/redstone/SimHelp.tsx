'use client';

/** The simulator's help: how faithful it is, and the nine things it does differently from the game (./simplifications). */
import { CircleHelp } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { FIDELITY, SIMPLIFICATIONS } from './simplifications';

export function SimHelp() {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLSpanElement>(null);
  const id = useId();

  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onDown, true);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onDown, true);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <span ref={root} className="relative inline-flex">
      <button
        type="button"
        aria-label="What the simulator leaves out"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((o) => !o)}
        className="rounded p-0.5 text-muted hover:bg-surface-2 hover:text-fg"
      >
        <CircleHelp className="size-3.5" />
      </button>
      {open ? (
        <div
          id={id}
          role="dialog"
          aria-label="What the simulator leaves out"
          data-testid="sim-help"
          className="absolute top-full right-0 z-30 mt-1 max-h-[60vh] w-64 overflow-y-auto rounded border border-border bg-surface p-2 text-[11px] text-muted shadow-card"
        >
          <p className="text-fg">{FIDELITY}</p>
          <p className="mt-1">These are not done the way the game does them:</p>
          <ol className="mt-1 list-decimal space-y-1 pl-4">
            {SIMPLIFICATIONS.map((text) => (
              <li key={text}>{text}</li>
            ))}
          </ol>
        </div>
      ) : null}
    </span>
  );
}
