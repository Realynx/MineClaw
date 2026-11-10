'use client';

/**
 * The picked cell, decoded: its block, facing, a repeater's delay and lock, a comparator's mode, a lever's state; its
 * power now; the scheduled ticks waiting for it; the chain that powers it back to its source; and, for a container or an
 * analog source, the level it holds.
 */
import { GROUND_ID, type Pos } from '@mineclaw/redstone';
import { useState } from 'react';
import { facingOf } from '../../facing';
import type { SimControllerApi, SimSnapshot } from '../simController.types';

const NAMES: Readonly<Record<number, string>> = {
  [GROUND_ID]: 'ground',
  0: 'air',
  1: 'stone',
  2: 'grass',
  3: 'dirt',
  4: 'cobblestone',
  7: 'bedrock',
  20: 'glass',
  23: 'dispenser',
  25: 'note block',
  27: 'powered rail',
  28: 'detector rail',
  29: 'sticky piston',
  33: 'piston',
  34: 'piston head',
  36: 'moving block',
  44: 'slab',
  46: 'TNT',
  49: 'obsidian',
  54: 'chest',
  55: 'redstone dust',
  61: 'furnace',
  62: 'furnace (lit)',
  64: 'wooden door',
  69: 'lever',
  70: 'stone pressure plate',
  71: 'iron door',
  72: 'wooden pressure plate',
  75: 'redstone torch (out)',
  76: 'redstone torch',
  77: 'stone button',
  89: 'glowstone',
  93: 'repeater',
  94: 'repeater (on)',
  96: 'trapdoor',
  107: 'fence gate',
  123: 'redstone lamp',
  124: 'redstone lamp (lit)',
  143: 'wooden button',
  146: 'trapped chest',
  147: 'light weighted plate',
  148: 'heavy weighted plate',
  149: 'comparator',
  150: 'comparator (powered)',
  151: 'daylight sensor',
  152: 'redstone block',
  154: 'hopper',
  158: 'dropper',
  165: 'slime block',
  178: 'inverted daylight sensor',
  218: 'observer',
};

/** Blocks whose level the user sets: containers a comparator reads, and analog sources. */
const LEVEL_IDS: ReadonlySet<number> = new Set([23, 54, 61, 62, 146, 147, 148, 151, 154, 158, 178]);

export const blockName = (id: number): string => NAMES[id] ?? `block ${id}`;

/** What the meta says beyond the facing, in words. */
export function configText(id: number, meta: number): string | null {
  if (id === 93 || id === 94) {
    const rt = ((meta >> 2) & 3) + 1;
    return `delay ${rt} redstone tick${rt === 1 ? '' : 's'} (${rt * 2} gt)`;
  }
  if (id === 149 || id === 150) return meta & 4 ? 'subtract mode' : 'compare mode';
  if (id === 69) return meta & 8 ? 'switched on' : 'switched off';
  if (id === 77 || id === 143) return meta & 8 ? 'pressed' : 'not pressed';
  if (id === 55) return `level ${meta}`;
  return null;
}

export function Inspector({
  api,
  snap,
  selected,
  onJump,
}: {
  api: SimControllerApi;
  snap: SimSnapshot;
  selected: Pos | null;
  onJump: (at: Pos) => void;
}) {
  // Read on every render: the panel renders again whenever the controller tells of a change.
  const info = selected ? api.inspect(selected) : null;
  if (!selected) {
    return (
      <p className="px-3 py-2 text-[11px] text-muted" data-testid="inspector">
        Pick a cell to read it here: the middle button in the world, or any click on a block while the simulation runs.
      </p>
    );
  }
  if (!info) {
    return (
      <p className="px-3 py-2 text-[11px] text-muted" data-testid="inspector">
        {selected.join(', ')}: the simulation is still being built.
      </p>
    );
  }
  const id = info.sample.state >> 4;
  const meta = info.sample.state & 15;
  const facing = id > 0 ? facingOf(id, meta) : null;
  const config = configText(id, meta);
  return (
    <section className="space-y-2 px-3 py-2 text-[11px]" aria-label="Inspector" data-testid="inspector">
      <div>
        <h3 className="font-semibold text-fg" data-testid="inspector-block">
          {blockName(id)}{' '}
          <span className="font-mono font-normal text-muted">
            {id}:{meta}
          </span>
        </h3>
        <p className="font-mono text-muted">{selected.join(', ')}</p>
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-0.5">
        {facing ? (
          <>
            <dt className="text-muted">Facing</dt>
            <dd className="text-fg">{facing}</dd>
          </>
        ) : null}
        {config ? (
          <>
            <dt className="text-muted">Set to</dt>
            <dd className="text-fg">{config}</dd>
          </>
        ) : null}
        <dt className="text-muted">Power</dt>
        <dd className="text-fg" data-testid="inspector-power">
          {info.sample.power} ({info.sample.powered ? 'on' : 'off'}) at tick {info.sample.tick}
        </dd>
        {info.locked ? (
          <>
            <dt className="text-muted">Lock</dt>
            <dd className="text-warn">held by a repeater or comparator at its side</dd>
          </>
        ) : null}
      </dl>
      {LEVEL_IDS.has(id) ? <LevelInput key={selected.join(',')} api={api} snap={snap} at={selected} /> : null}
      <div>
        <h4 className="font-semibold text-muted">Scheduled ticks</h4>
        {info.pending.length === 0 ? (
          <p className="text-muted">none</p>
        ) : (
          <ul data-testid="inspector-pending">
            {info.pending.map((p, i) => (
              <li key={`${p.dueTick}:${i}`} className="font-mono text-fg">
                tick {p.dueTick} (in {p.dueTick - snap.tick} gt)
                {snap.version === '1.12.2' ? `, priority ${p.priority}` : ''}
              </li>
            ))}
          </ul>
        )}
      </div>
      <div>
        <h4 className="font-semibold text-muted">What powers it</h4>
        {info.explain.length === 0 ? (
          <p className="text-muted">nothing</p>
        ) : (
          <ol className="space-y-0.5" data-testid="inspector-explain">
            {info.explain.map((s, i) => (
              <li key={`${s.at.join(',')}:${i}`}>
                <button
                  type="button"
                  onClick={() => onJump(s.at)}
                  title="Show this cell"
                  className="w-full rounded px-1 text-left text-fg hover:bg-surface-2"
                >
                  <span className="font-mono text-muted">{s.at.join(', ')}</span> {s.why}
                </button>
              </li>
            ))}
          </ol>
        )}
      </div>
    </section>
  );
}

/** What a container or an analog source holds: part of the design in edit mode, a poke while the circuit runs. */
function LevelInput({ api, snap, at }: { api: SimControllerApi; snap: SimSnapshot; at: Pos }) {
  const start = snap.levels.find((l) => l.at[0] === at[0] && l.at[1] === at[1] && l.at[2] === at[2])?.level ?? 0;
  const [draft, setDraft] = useState<string | null>(null);
  const live = snap.mode !== 'edit';
  const commit = () => {
    if (draft === null) return;
    const n = Number(draft);
    setDraft(null);
    if (draft.trim() !== '' && Number.isFinite(n)) api.setLevel(at, n);
  };
  return (
    <label
      className="flex items-center gap-2 text-muted"
      title={
        live
          ? 'Changes the running circuit only, like a lever flipped there'
          : 'What it holds when the circuit starts; saved with the design'
      }
    >
      {live ? 'Level now' : 'Start level'}
      <input
        type="number"
        min={0}
        max={15}
        aria-label={live ? 'Level now' : 'Start level'}
        value={draft ?? String(start)}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit();
        }}
        className="h-6 w-14 rounded border border-border bg-surface px-1 font-mono text-fg"
      />
    </label>
  );
}
