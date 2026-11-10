'use client';

/**
 * The Design Studio's redstone side panel (a redstone design only): whether the circuit is being drawn or simulated, with
 * the way in and out of the simulation; what the simulator has to say about the design; and the debugger's tabs. The
 * debugger (./debugger) is mounted here with its keys, and reads the simulation through the controller only.
 */
import type { Diagnostic, Pos } from '@mineclaw/redstone';
import { Play, Square, TriangleAlert } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
import { Button } from '@/components/ui/primitives';
import { isTyping } from '@/features/world/directives';
import { cn } from '@/lib/utils';
import { bindDebuggerKeys, DebuggerPanel } from './debugger';
import { DISPLAY_RATES, type DisplayHz, type SimControllerApi, type SimSnapshot } from './simController.types';
import { SimHelp } from './SimHelp';
import type { RedstoneSim } from './useRedstoneSim';
import { useSimSnapshot } from './useSimSnapshot';

export interface RedstonePanelProps {
  sim: RedstoneSim;
  /** The game version the server runs, when it is known. */
  serverVersion?: string | null;
  /** The stage: the debugger's keys apply while the focus is in it or in this panel. */
  stage: RefObject<HTMLElement | null>;
  /** The gate library's tab, once there is one. */
  library?: ReactNode;
}

type Tab = 'library' | 'simulate' | 'probes' | 'inspector';

const TABS: ReadonlyArray<{ id: Tab; label: string; title: string }> = [
  { id: 'library', label: 'Library', title: 'Proven gates and your own components' },
  { id: 'simulate', label: 'Simulate', title: 'Run, pause and step the circuit' },
  { id: 'probes', label: 'Probes', title: 'Probes, breakpoints and the timeline' },
  { id: 'inspector', label: 'Inspector', title: 'The cell you picked: its state, its power and what drives it' },
];

/** The most checks listed; the rest are counted. */
const MAX_CHECKS = 30;

const MODE_WORD: Record<SimSnapshot['mode'], string> = { edit: 'Editing', paused: 'Paused', running: 'Running' };

export function RedstonePanel(p: RedstonePanelProps) {
  return p.sim.api ? <Panel {...p} api={p.sim.api} /> : null;
}

function Panel({ sim, api, serverVersion, stage, library }: RedstonePanelProps & { api: SimControllerApi }) {
  const snap = useSimSnapshot(api);
  const [tab, setTab] = useState<Tab>(library ? 'library' : 'simulate');
  const root = useRef<HTMLElement>(null);
  const { selected, aimed } = sim;

  // A cell picked in the world is what the user wants to read next.
  useEffect(() => {
    if (selected) setTab('inspector');
  }, [selected]);

  useEffect(() => {
    const within = (el: Element | null, node: Element | null) => !!el && !!node && el.contains(node);
    const inWorld = () => {
      const active = document.activeElement;
      if (isTyping(active)) return false;
      if (!active || active === document.body) return true;
      return (
        within(stage.current, active) ||
        within(root.current, active) ||
        within(stage.current, document.pointerLockElement)
      );
    };
    return bindDebuggerKeys(document.body, api, { inWorld, aimed });
  }, [api, stage, aimed]);

  const pick = (at: Pos) => {
    sim.jump(at);
    sim.inspect(at);
  };
  const tabs = TABS.filter((t) => t.id !== 'library' || !!library);
  return (
    <aside
      ref={root}
      className="flex min-h-0 w-full shrink-0 flex-col overflow-hidden border-l border-border bg-surface text-xs lg:w-72"
      aria-label="Redstone designer"
      data-testid="redstone-panel"
    >
      <ModeStrip api={api} snap={snap} />
      {sim.failure ? (
        <p className="shrink-0 border-b border-border bg-danger-soft px-3 py-1.5 text-[11px] text-danger" role="alert">
          The simulator could not run this design: {sim.failure}
        </p>
      ) : null}
      <VersionWarning version={snap.version} serverVersion={serverVersion} />
      <Checks list={snap.diagnostics} onPick={pick} />
      <div
        className="flex shrink-0 gap-1 border-b border-border px-2 py-1.5"
        role="tablist"
        aria-label="Redstone tools"
      >
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={tab === t.id}
            title={t.title}
            onClick={() => setTab(t.id)}
            className={cn(
              'rounded px-2 py-0.5',
              tab === t.id ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
            )}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto" role="tabpanel" data-testid="redstone-tab" data-tab={tab}>
        {tab === 'library' ? (
          library
        ) : (
          <DebuggerPanel api={api} tab={tab} aimed={aimed} selected={selected} onJump={sim.jump} />
        )}
      </div>
    </aside>
  );
}

/** Drawing or simulating, the way between the two, and how often the simulated states are redrawn. */
function ModeStrip({ api, snap }: { api: SimControllerApi; snap: SimSnapshot }) {
  const live = snap.mode !== 'edit';
  return (
    <section className="shrink-0 border-b border-border px-3 py-2" data-testid="redstone-mode" data-mode={snap.mode}>
      <div className="flex items-center gap-2">
        <span
          className={cn(
            'rounded px-1.5 py-0.5 text-[11px] font-semibold',
            live ? 'bg-accent text-white' : 'bg-surface-2 text-muted',
          )}
        >
          {MODE_WORD[snap.mode]}
        </span>
        <span className="font-mono text-[11px] text-muted">
          {snap.version}
          {live ? ` · tick ${snap.tick}` : ''}
        </span>
        <span className="ml-auto" />
        <SimHelp />
        {live ? (
          <Button
            size="sm"
            onClick={() => api.stop()}
            title="Back to drawing: the canvas is shown as it was drawn again"
          >
            <Square className="size-3" /> Stop and edit
          </Button>
        ) : (
          <Button size="sm" variant="primary" onClick={() => api.start()} title="Run the circuit as the game would">
            <Play className="size-3" /> Simulate
          </Button>
        )}
      </div>
      <p className="mt-1.5 text-[11px] text-muted">
        {live
          ? 'The canvas is locked. In the world, click a lever, a button or a pressure plate to use it and any other block to inspect it (the middle button always inspects). Any edit ends the simulation.'
          : 'Draw the circuit, then simulate it. The Configure tool (C) sets a repeater’s delay, a comparator’s mode and whether a lever starts on.'}
      </p>
      <label
        className="mt-1.5 flex flex-wrap items-center gap-1 text-[11px] text-muted"
        title="How often the simulated states are redrawn. The simulation runs at its own speed, and the timeline keeps every tick."
      >
        Redraw
        <select
          aria-label="Redraw rate"
          value={snap.displayHz}
          onChange={(e) => api.setDisplayHz(Number(e.target.value) as DisplayHz)}
          className="h-6 rounded border border-border bg-surface px-1 text-[11px] text-fg"
        >
          {DISPLAY_RATES.map((hz) => (
            <option key={hz} value={hz}>
              {hz} Hz
            </option>
          ))}
        </select>
        {snap.displayHzNow !== snap.displayHz ? (
          <span className="text-warn" data-testid="redstone-rate-note">
            now {snap.displayHzNow} Hz while the frame rate is low
          </span>
        ) : null}
      </label>
    </section>
  );
}

/** A circuit drawn for another game version than the server runs: said, never refused. */
function VersionWarning({ version, serverVersion }: { version: string; serverVersion?: string | null }) {
  if (!serverVersion || serverVersion === version) return null;
  return (
    <p
      className="shrink-0 border-b border-border bg-warn-soft px-3 py-1.5 text-[11px] text-warn"
      data-testid="redstone-version-warning"
    >
      Drawn and simulated for {version}; the server runs {serverVersion}. Redstone differs between the two (how dust
      links, torch timing, and b1.8.1 has no comparators, lamps or observers), so the circuit may not work the same
      there.
    </p>
  );
}

/** What the simulator found wrong or could not simulate faithfully; a click shows the cell. */
function Checks({ list, onPick }: { list: readonly Diagnostic[]; onPick: (at: Pos) => void }) {
  if (list.length === 0) return null;
  const shown = list.slice(0, MAX_CHECKS);
  return (
    <section
      className="shrink-0 border-b border-border bg-warn-soft px-3 py-1.5"
      aria-label="Simulator checks"
      data-testid="redstone-checks"
    >
      <h3 className="flex items-center gap-1 text-[11px] font-semibold text-warn">
        <TriangleAlert className="size-3" /> {list.length} to check
      </h3>
      <ul className="mt-1 max-h-28 space-y-0.5 overflow-y-auto">
        {shown.map((d, i) => (
          <li key={`${d.code}:${d.at.join(',')}:${i}`}>
            <button
              type="button"
              onClick={() => onPick(d.at)}
              title="Show this cell"
              className="w-full rounded px-1 py-0.5 text-left text-[11px] text-fg hover:bg-surface-2"
            >
              <span className="font-mono text-muted">{d.at.join(', ')}</span> {d.text}
            </button>
          </li>
        ))}
      </ul>
      {list.length > shown.length ? (
        <p className="mt-0.5 text-[10px] text-muted">and {list.length - shown.length} more</p>
      ) : null}
    </section>
  );
}

/** Over the stage while a simulation is live: the pointer cannot edit there, so it says what a click does instead. */
export function SimStageNote({ api }: { api: SimControllerApi | null }) {
  return api ? <StageNote api={api} /> : null;
}

function StageNote({ api }: { api: SimControllerApi }) {
  const snap = useSimSnapshot(api);
  if (snap.mode === 'edit') return null;
  return (
    <div
      className="pointer-events-none absolute top-3 left-1/2 w-max max-w-[calc(100%-8rem)] -translate-x-1/2 rounded bg-black/70 px-2.5 py-1 text-center font-mono text-[11px] text-white"
      data-testid="sim-stage-note"
    >
      {MODE_WORD[snap.mode]} · tick {snap.tick} · the canvas is locked: clicks use the circuit
    </div>
  );
}
