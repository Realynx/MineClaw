'use client';

/**
 * The debugger's run controls: run or pause, step a redstone tick or a game tick, step back, run until something changes,
 * back to tick 0, the speed, the clock in game and redstone ticks, whether the circuit is idle, what happened lately,
 * and the keys (which can be changed, in this browser).
 */
import type { Pos, SimEvent } from '@mineclaw/redstone';
import { FastForward, Pause, Play, RotateCcw, StepBack, StepForward, SkipForward } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { SIM_SPEEDS, type SimControllerApi, type SimSnapshot, type SimSpeed } from '../simController.types';
import {
  ACTIONS,
  chordLabel,
  chordOf,
  resetKeys,
  setKey,
  useKeyMap,
  withKey,
  type DebuggerAction,
} from './debuggerKeys';

/** The newest events listed. */
const MAX_EVENTS_SHOWN = 20;

const EVENT_WORD: Record<SimEvent['kind'], string> = {
  burnout: 'burned out',
  primed: 'TNT primed',
  fired: 'fired',
  played: 'played',
  destroyed: 'destroyed',
  blocked: 'could not move',
};

export const speedLabel = (s: SimSpeed): string => (s === 'max' ? 'Max' : `${s} gt/s`);

/** A game tick count in redstone ticks: two game ticks each, so an odd count ends in a half. */
export const redstoneTicks = (gameTicks: number): string => String(gameTicks / 2);

export function SimControls({
  api,
  snap,
  onJump,
}: {
  api: SimControllerApi;
  snap: SimSnapshot;
  onJump: (at: Pos) => void;
}) {
  const keys = useKeyMap();
  const running = snap.mode === 'running';
  const live = snap.mode !== 'edit';
  const speeds = SIM_SPEEDS.includes(snap.speed) ? SIM_SPEEDS : [...SIM_SPEEDS, snap.speed];
  return (
    <section className="space-y-2 px-3 py-2" aria-label="Run controls" data-testid="sim-controls">
      <div className="flex flex-wrap items-center gap-1">
        <Button
          size="sm"
          variant={running ? 'secondary' : 'primary'}
          onClick={() => (running ? api.pause() : api.start())}
          title={withKey(keys, 'runPause', running ? 'Pause' : 'Run')}
        >
          {running ? <Pause className="size-3" /> : <Play className="size-3" />} {running ? 'Pause' : 'Run'}
        </Button>
        <Button
          size="icon-sm"
          aria-label="Step a redstone tick"
          title={withKey(keys, 'stepRedstone', 'Step a redstone tick (two game ticks)')}
          onClick={() => api.stepRedstoneTick()}
        >
          <SkipForward className="size-3.5" />
        </Button>
        <Button
          size="icon-sm"
          aria-label="Step a game tick"
          title={withKey(keys, 'stepGame', 'Step a game tick')}
          onClick={() => api.stepGameTick()}
        >
          <StepForward className="size-3.5" />
        </Button>
        <Button
          size="icon-sm"
          aria-label="Step back"
          title={`Step back a game tick (${snap.canStepBack} in history)`}
          disabled={snap.canStepBack < 1}
          onClick={() => api.stepBack()}
        >
          <StepBack className="size-3.5" />
        </Button>
        <Button
          size="icon-sm"
          aria-label="Run until something changes"
          title={withKey(keys, 'runUntilChange', 'Run until something changes')}
          onClick={() => api.runUntilChange()}
        >
          <FastForward className="size-3.5" />
        </Button>
        <Button
          size="icon-sm"
          aria-label="Back to tick 0"
          title={withKey(keys, 'reset', 'Back to tick 0 (probes and breakpoints stay)')}
          disabled={!live}
          onClick={() => api.reset()}
        >
          <RotateCcw className="size-3.5" />
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2 text-[11px]">
        <span className="font-mono text-fg" data-testid="sim-clock" title="The clock: game ticks, and redstone ticks">
          tick {snap.tick} gt · {redstoneTicks(snap.tick)} rt
        </span>
        {live && snap.idle ? (
          <span
            className="rounded bg-surface-2 px-1.5 py-0.5 text-muted"
            data-testid="sim-idle"
            title="Nothing is scheduled, queued or moving: without an input nothing will change"
          >
            idle
          </span>
        ) : null}
        <label className="ml-auto flex items-center gap-1 text-muted" title="Game ticks a second; the game runs 20">
          Speed
          <select
            aria-label="Speed"
            value={String(snap.speed)}
            onChange={(e) => api.setSpeed(e.target.value === 'max' ? 'max' : Number(e.target.value))}
            className="h-6 rounded border border-border bg-surface px-1 text-[11px] text-fg"
          >
            {speeds.map((s) => (
              <option key={String(s)} value={String(s)}>
                {speedLabel(s)}
              </option>
            ))}
          </select>
        </label>
      </div>

      <Events list={snap.events} onJump={onJump} />
      <KeyBindings />
    </section>
  );
}

function Events({ list, onJump }: { list: readonly SimEvent[]; onJump: (at: Pos) => void }) {
  if (list.length === 0) return null;
  const shown = list.slice(-MAX_EVENTS_SHOWN).reverse();
  return (
    <div aria-label="What happened" data-testid="sim-events">
      <h3 className="text-[11px] font-semibold text-muted">What happened</h3>
      <ul className="mt-0.5 max-h-28 space-y-0.5 overflow-y-auto">
        {shown.map((e, i) => (
          <li key={`${e.tick}:${e.kind}:${e.at.join(',')}:${i}`}>
            <button
              type="button"
              onClick={() => onJump(e.at)}
              title="Show this cell"
              className="w-full rounded px-1 py-0.5 text-left text-[11px] text-fg hover:bg-surface-2"
            >
              <span className="font-mono text-muted">{e.tick}</span> {EVENT_WORD[e.kind]}{' '}
              <span className="font-mono text-muted">{e.at.join(', ')}</span>
              {e.note ? ` · ${e.note}` : ''}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The debugger's keys, each changed by pressing the new one. Kept in this browser. */
function KeyBindings() {
  const keys = useKeyMap();
  const [waiting, setWaiting] = useState<DebuggerAction | null>(null);

  useEffect(() => {
    if (!waiting) return;
    // Ahead of everything else, the debugger's own keys included: the press is the new binding, not a command.
    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape') return setWaiting(null);
      const chord = chordOf(e);
      if (!chord) return;
      setKey(waiting, chord);
      setWaiting(null);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [waiting]);

  return (
    <details className="text-[11px] text-muted" data-testid="debugger-keys">
      <summary className="cursor-pointer select-none">Keys</summary>
      <ul className="mt-1 space-y-0.5">
        {ACTIONS.map((a) => (
          <li key={a.id} className="flex items-center gap-2">
            <span className="flex-1 text-fg">{a.label}</span>
            <button
              type="button"
              aria-label={`Change the key for ${a.label}`}
              onClick={() => setWaiting(waiting === a.id ? null : a.id)}
              className={cn(
                'min-w-16 rounded border border-border px-1.5 py-0.5 font-mono',
                waiting === a.id ? 'bg-accent-soft text-fg' : 'bg-surface hover:bg-surface-2',
              )}
            >
              {waiting === a.id ? 'press a key…' : chordLabel(keys[a.id])}
            </button>
          </li>
        ))}
      </ul>
      <p className="mt-1">
        The keys work while the world or this panel has the focus. A key another action had takes this one’s old key.{' '}
        <button type="button" className="underline hover:text-fg" onClick={() => resetKeys()}>
          Back to the usual keys
        </button>
      </p>
    </details>
  );
}
