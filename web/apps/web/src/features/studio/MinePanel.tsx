'use client';

/**
 * The Design Studio's mine side panel (a mine session only): the terrain's seed, the ore-height guide (a ruler of the version's
 * ore bands with the current level, and what that level holds), the X-ray toggle, the templates (spiral shaft, strip mine,
 * room; "at ore band" sets the level to the ore's best), the version-preferred presets, and the dig estimate (blocks to mine
 * by material, ores mined and exposed per band). Everything is computed from the generated terrain: estimates, the real
 * world differs.
 */
import { Dices, Eye, Server } from 'lucide-react';
import { useMemo, useState, type ReactNode } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { levelOres, mineStats, oreColour, seenPer100 } from './mine/mineStats';
import { room, spiral, STRIP_DEFAULTS, strip, type MineCells } from './mine/mineTemplates';
import type { MineTerrain } from './mine/mineTerrain';
import { presetCells, presetsFor, type MinePreset } from './mine/presets';
import type { V3 } from './canvas';

export interface MinePanelProps {
  terrain: MineTerrain;
  version: string;
  seed: string;
  serverSeed: string | null;
  preferredFor: string | null;
  /** The dug cells now (recomputed on every canvas revision). */
  dug: () => Array<readonly [number, number, number]>;
  /** Changes whenever the canvas does (the studio's snapshot): the estimate is recomputed. */
  rev: unknown;
  /** The underground level (null: the cut is off). */
  level: number | null;
  onLevel: (y: number) => void;
  xray: boolean;
  onXray: (on: boolean) => void;
  /** Where a template goes: the selection's min corner, else the plot's middle. */
  at: V3 | null;
  canWrite: boolean;
  busy: boolean;
  onSeed: (seed: string) => void;
  onInsert: (cells: MineCells) => void;
  onPreset: (p: MinePreset, cells: MineCells) => void;
}

const num = (v: string, lo: number, hi: number, d: number): number => {
  const n = Math.trunc(Number(v));
  return Number.isFinite(n) && v.trim() !== '' ? Math.max(lo, Math.min(hi, n)) : d;
};

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="border-b border-border px-3 py-2">
      <h3 className="mb-1.5 text-[11px] font-semibold tracking-wide text-muted uppercase">{title}</h3>
      {children}
    </section>
  );
}

function NumField({
  label,
  value,
  onChange,
  min,
  max,
}: {
  label: string;
  value: number;
  onChange: (n: number) => void;
  min: number;
  max: number;
}) {
  return (
    <label className="flex items-center justify-between gap-1 text-[11px] text-muted">
      {label}
      <input
        type="number"
        min={min}
        max={max}
        value={value}
        onChange={(e) => onChange(num(e.target.value, min, max, value))}
        aria-label={label}
        className="h-6 w-14 rounded border border-border bg-surface px-1 text-right font-mono text-fg"
      />
    </label>
  );
}

/** The ore-height ruler: each band of the version as a coloured bar over y 0..top, best y ticked, the level marked. */
function OreRuler({
  terrain,
  top,
  level,
  onLevel,
}: {
  terrain: MineTerrain;
  top: number;
  level: number | null;
  onLevel: (y: number) => void;
}) {
  const ores = terrain.table.ores.filter((o) => o.veins > 0);
  const h = 220;
  const y2px = (y: number) => h - (y / top) * h;
  return (
    <div className="flex gap-2">
      <div
        className="relative w-full cursor-crosshair overflow-hidden rounded border border-border bg-surface-2"
        style={{ height: h }}
        role="slider"
        aria-label="Ore height guide"
        aria-valuemin={1}
        aria-valuemax={top}
        aria-valuenow={level ?? undefined}
        data-testid="ore-ruler"
        tabIndex={0}
        onClick={(e) => {
          const r = e.currentTarget.getBoundingClientRect();
          onLevel(Math.max(1, Math.min(top, Math.round(((r.bottom - e.clientY) / r.height) * top))));
        }}
      >
        {[0, 16, 32, 48, 64].filter((y) => y <= top).map((y) => (
          <div key={y} className="absolute right-0 left-0 border-t border-border/60" style={{ top: y2px(y) }}>
            <span className="absolute -top-2 left-0.5 font-mono text-[9px] text-subtle">{y}</span>
          </div>
        ))}
        <div
          className="absolute right-0 left-0 bg-danger/15"
          style={{ top: y2px(terrain.table.lava + 1), height: y2px(0) - y2px(terrain.table.lava + 1) }}
          title={`lava lakes at y ${terrain.table.lava} and below`}
        />
        {ores.map((o, i) => {
          const left = 16 + i * ((100 - 18) / ores.length);
          return (
            <div key={o.id} title={`${o.name}: y ${o.min}-${o.max}, best ${o.best}${o.note ? ` (${o.note})` : ''}`}>
              <div
                className="absolute w-2 rounded-sm opacity-80"
                style={{
                  left: `${left}%`,
                  top: y2px(Math.min(o.max, top) + 1),
                  height: y2px(o.min) - y2px(Math.min(o.max, top) + 1),
                  background: o.color ?? '#888',
                }}
              />
              <div className="absolute h-0.5 w-3.5 bg-fg" style={{ left: `calc(${left}% - 3px)`, top: y2px(o.best + 0.5) }} />
            </div>
          );
        })}
        {level !== null ? (
          <div className="absolute right-0 left-0 border-t-2 border-accent" style={{ top: y2px(level + 0.5) }}>
            <span className="absolute -top-3.5 right-0.5 rounded bg-accent px-1 font-mono text-[9px] text-white">y {level}</span>
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function MinePanel(p: MinePanelProps) {
  const { terrain, version } = p;
  const range = useMemo(() => terrain.surfaceRange(), [terrain]);
  // The ruler spans y 0 to 16 over the highest ground (bands above it are cut at its top).
  const top = Math.min(127, range.max + 16);
  const [seedText, setSeedText] = useState(p.seed);
  const ores = terrain.table.ores.filter((o) => o.veins > 0);
  const at: V3 = p.at ?? [30, 0, 30];
  // Spiral shaft.
  const [size, setSize] = useState(4);
  const [core, setCore] = useState(2);
  const [shaftTop, setShaftTop] = useState<number | null>(null);
  const [bottom, setBottom] = useState(11);
  const [cw, setCw] = useState(true);
  const [torches, setTorches] = useState(6);
  const [landings, setLandings] = useState('11');
  // Strip mine.
  const [stripY, setStripY] = useState(11);
  const [axis, setAxis] = useState<'x' | 'z'>('x');
  const [length, setLength] = useState(30);
  const [corridor, setCorridor] = useState<'1x2' | '2x3'>('1x2');
  // The layout study's best for both versions (docs/design/mine-blueprints.md).
  const [spacing, setSpacing] = useState<number>(STRIP_DEFAULTS.spacing);
  const [branch, setBranch] = useState(12);
  const [sides, setSides] = useState<'both' | 'left' | 'right' | 'none'>('both');
  const [stripTorches, setStripTorches] = useState(8);
  // Room.
  const [roomW, setRoomW] = useState(5);
  const [roomD, setRoomD] = useState(5);
  const [roomH, setRoomH] = useState(3);
  const [roomY, setRoomY] = useState(11);
  const surfaceTop = terrain.surfaceOver(at[0], at[2], at[0] + size - 1, at[2] + size - 1);
  const level = p.level;
  const readout = useMemo(() => (level === null ? null : levelOres(terrain, level)), [terrain, level]);
  const stats = useMemo(() => mineStats(terrain, p.dug()), [terrain, p.rev]); // eslint-disable-line react-hooks/exhaustive-deps
  const presets = presetsFor(version);
  const disabled = !p.canWrite || p.busy;

  const atBand = (set: (y: number) => void) => (
    <div className="mt-1 flex flex-wrap gap-1" aria-label="At ore band">
      {ores.map((o) => (
        <button
          key={o.id}
          type="button"
          className="rounded border border-border px-1.5 py-0.5 text-[10px] text-muted hover:bg-surface-2"
          title={`${o.name}: best at y ${o.best}`}
          onClick={() => {
            set(o.best);
            p.onLevel(o.best);
          }}
        >
          <span className="mr-1 inline-block size-2 rounded-full" style={{ background: o.color ?? '#888' }} />
          {o.id} {o.best}
        </button>
      ))}
    </div>
  );

  return (
    <aside
      className="flex w-full shrink-0 flex-col overflow-y-auto border-l border-border bg-surface text-xs lg:w-72"
      aria-label="Mine designer"
      data-testid="mine-panel"
    >
      <Section title="Terrain">
        <p className="mb-1 text-[11px] text-muted">
          Generated for {version} (MineClaw&apos;s own generator): ground y {range.min}–{range.max}, bedrock y 0–
          {terrain.table.bedrockTop}. The design keeps its absolute y.
        </p>
        <form
          className="flex items-center gap-1"
          onSubmit={(e) => {
            e.preventDefault();
            if (seedText.trim() && seedText.trim() !== p.seed) p.onSeed(seedText.trim());
          }}
        >
          <Input
            value={seedText}
            onChange={(e) => setSeedText(e.target.value)}
            aria-label="Seed"
            maxLength={32}
            className="h-7 font-mono text-[11px]"
            data-testid="mine-seed"
          />
          <Button size="sm" variant="secondary" type="submit" disabled={disabled} title="Use this seed">
            Set
          </Button>
          <button
            type="button"
            aria-label="Random seed"
            title="Random seed"
            disabled={disabled}
            className="grid size-7 place-items-center rounded border border-border hover:bg-surface-2"
            onClick={() => {
              const s = String(Math.floor(Math.random() * 9e15) + 1);
              setSeedText(s);
              p.onSeed(s);
            }}
          >
            <Dices className="size-3.5" />
          </button>
          {p.serverSeed ? (
            <button
              type="button"
              aria-label="Use the server's seed"
              title="Use the server's seed (our generator still makes its own world from it)"
              disabled={disabled}
              className="grid size-7 place-items-center rounded border border-border hover:bg-surface-2"
              onClick={() => {
                setSeedText(p.serverSeed!);
                p.onSeed(p.serverSeed!);
              }}
            >
              <Server className="size-3.5" />
            </button>
          ) : null}
        </form>
      </Section>
      <Section title="Ore heights">
        <OreHeights terrain={terrain} top={top} level={level} readout={readout} xray={p.xray} onLevel={p.onLevel} onXray={p.onXray} />
      </Section>
      <Section title="Spiral shaft">
        <div className="grid grid-cols-2 gap-x-2 gap-y-1">
          <NumField label="Size" value={size} onChange={setSize} min={3} max={6} />
          <NumField label="Core" value={core} onChange={setCore} min={1} max={2} />
          <NumField label="Top y" value={shaftTop ?? surfaceTop} onChange={setShaftTop} min={2} max={127} />
          <NumField label="Bottom y" value={bottom} onChange={setBottom} min={1} max={126} />
          <NumField label="Torch every" value={torches} onChange={setTorches} min={0} max={32} />
          <label className="flex items-center justify-between gap-1 text-[11px] text-muted">
            Turn
            <select
              value={cw ? 'cw' : 'ccw'}
              onChange={(e) => setCw(e.target.value === 'cw')}
              aria-label="Turn"
              className="h-6 rounded border border-border bg-surface px-1 text-fg"
            >
              <option value="cw">cw</option>
              <option value="ccw">ccw</option>
            </select>
          </label>
        </div>
        <label className="mt-1 flex items-center justify-between gap-1 text-[11px] text-muted">
          Landings (y)
          <input
            value={landings}
            onChange={(e) => setLandings(e.target.value)}
            aria-label="Landings"
            className="h-6 w-28 rounded border border-border bg-surface px-1 font-mono text-fg"
          />
        </label>
        {atBand(setBottom)}
        <Button
          size="sm"
          className="mt-1.5 w-full"
          disabled={disabled}
          data-testid="insert-spiral"
          onClick={() =>
            p.onInsert(
              spiral({
                x: at[0],
                z: at[2],
                top: shaftTop ?? surfaceTop,
                bottom,
                size,
                core,
                cw,
                torchEvery: torches,
                landings: landings
                  .split(/[\s,]+/)
                  .map(Number)
                  .filter((n) => Number.isInteger(n) && n > 0),
              }),
            )
          }
        >
          Insert spiral shaft at {at[0]}, {at[2]}
        </Button>
      </Section>
      <Section title="Strip mine">
        <div className="grid grid-cols-2 gap-x-2 gap-y-1">
          <NumField label="Feet y" value={stripY} onChange={setStripY} min={1} max={126} />
          <NumField label="Length" value={length} onChange={setLength} min={1} max={64} />
          <NumField label="Spacing" value={spacing} onChange={setSpacing} min={1} max={8} />
          <NumField label="Branch" value={branch} onChange={setBranch} min={0} max={64} />
          <NumField label="Torch every" value={stripTorches} onChange={setStripTorches} min={0} max={32} />
          <label className="flex items-center justify-between gap-1 text-[11px] text-muted">
            Along
            <select
              value={axis}
              onChange={(e) => setAxis(e.target.value as 'x' | 'z')}
              aria-label="Along"
              className="h-6 rounded border border-border bg-surface px-1 text-fg"
            >
              <option value="x">x</option>
              <option value="z">z</option>
            </select>
          </label>
          <label className="flex items-center justify-between gap-1 text-[11px] text-muted">
            Corridor
            <select
              value={corridor}
              onChange={(e) => setCorridor(e.target.value as '1x2' | '2x3')}
              aria-label="Corridor"
              className="h-6 rounded border border-border bg-surface px-1 text-fg"
            >
              <option value="1x2">1×2</option>
              <option value="2x3">2×3</option>
            </select>
          </label>
          <label className="flex items-center justify-between gap-1 text-[11px] text-muted">
            Sides
            <select
              value={sides}
              onChange={(e) => setSides(e.target.value as typeof sides)}
              aria-label="Sides"
              className="h-6 rounded border border-border bg-surface px-1 text-fg"
            >
              {['both', 'left', 'right', 'none'].map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
        </div>
        {atBand(setStripY)}
        <Button
          size="sm"
          className="mt-1.5 w-full"
          disabled={disabled}
          data-testid="insert-strip"
          onClick={() =>
            p.onInsert(
              strip({
                x: at[0],
                y: stripY,
                z: at[2],
                axis,
                length,
                width: corridor === '2x3' ? 2 : 1,
                height: corridor === '2x3' ? 3 : 2,
                spacing,
                branchLength: branch,
                sides,
                torchEvery: stripTorches,
              }),
            )
          }
        >
          Insert strip mine at {at[0]}, {stripY}, {at[2]}
        </Button>
      </Section>
      <Section title="Room">
        <div className="grid grid-cols-2 gap-x-2 gap-y-1">
          <NumField label="Width" value={roomW} onChange={setRoomW} min={1} max={32} />
          <NumField label="Depth" value={roomD} onChange={setRoomD} min={1} max={32} />
          <NumField label="Height" value={roomH} onChange={setRoomH} min={2} max={8} />
          <NumField label="Floor y" value={roomY} onChange={setRoomY} min={1} max={126} />
        </div>
        <Button
          size="sm"
          variant="secondary"
          className="mt-1.5 w-full"
          disabled={disabled}
          onClick={() => p.onInsert(room(at[0], roomY, at[2], roomW, roomD, roomH))}
        >
          Dig a room at {at[0]}, {roomY}, {at[2]}
        </Button>
      </Section>
      <Section title={`Preferred for ${version}`}>
        <ul className="space-y-1.5">
          {presets.map((pr) => (
            <li key={pr.id}>
              <div className="flex items-center gap-1">
                <span className="flex-1 font-medium text-fg">{pr.name}</span>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={disabled}
                  data-testid={`preset-${pr.id}`}
                  onClick={() => p.onPreset(pr, presetCells(pr, terrain))}
                >
                  Use
                </Button>
              </div>
              <p className="text-[10px] text-subtle">{pr.notes}</p>
            </li>
          ))}
        </ul>
        <PreferredNote preferredFor={p.preferredFor} />
      </Section>
      <Section title="Dig estimate">
        <DigEstimate stats={stats} />
      </Section>
    </aside>
  );
}

/** The ore ruler, the level slider, X-ray, and the ores at the level (band density, count here). */
function OreHeights(p: {
  terrain: MinePanelProps['terrain'];
  top: number;
  level: number | null;
  readout: ReturnType<typeof levelOres> | null;
  xray: boolean;
  onLevel: (y: number) => void;
  onXray: (on: boolean) => void;
}) {
  const { terrain, top, level, readout } = p;
  return (
    <>
    <OreRuler terrain={terrain} top={top} level={level} onLevel={p.onLevel} />
    <label className="mt-1.5 flex items-center gap-1.5 text-[11px] text-muted" title="Layers above this one are cut away">
      Show to y
      <input
        type="range"
        min={1}
        max={127}
        value={level ?? top}
        onChange={(e) => p.onLevel(Number(e.target.value))}
        aria-label="Underground level"
        className="min-w-0 flex-1"
      />
      <span className="w-7 text-right font-mono tabular-nums">{level ?? '-'}</span>
    </label>
    <div className="mt-1 flex items-center justify-between">
      <span className="text-[11px] text-muted">
        {level === null ? 'Click the ruler, drag the slider or press K' : `At y ${level}:`}
      </span>
      <button
        type="button"
        aria-pressed={p.xray}
        onClick={() => p.onXray(!p.xray)}
        className={cn(
          'flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px]',
          p.xray ? 'bg-accent text-white' : 'border border-border text-muted hover:bg-surface-2',
        )}
        title="X-ray: show the generated ores through the stone"
      >
        <Eye className="size-3" /> X-ray
      </button>
    </div>
    {readout ? (
      <ul className="mt-1 space-y-0.5" data-testid="level-readout">
        {readout.map((r) => (
          <li key={r.ore.id} className={cn('flex items-center gap-1.5', !r.inBand && 'opacity-40')}>
            <span className="inline-block size-2 rounded-full" style={{ background: r.ore.color ?? '#888' }} />
            <span className="flex-1">
              {r.ore.name}
              {r.best ? ' ★' : ''}
            </span>
            <span className="font-mono text-[10px] text-muted">
              {r.inBand ? (r.weight < 1 ? `${Math.round(r.weight * 100)}%` : 'in band') : 'out'} · {r.count}
            </span>
          </li>
        ))}
        <li className="text-[10px] text-subtle">band density here · generated ores in this layer of the plot</li>
      </ul>
    ) : null}
    </>
  );
}

/** The dig estimate: blocks to mine and their y range, materials, ores mined and exposed. */
function DigEstimate({ stats }: { stats: ReturnType<typeof mineStats> }) {
  return (
    <>
    <p className="text-[11px] text-fg" data-testid="mine-stats">
      {stats.toMine} blocks to mine
      {stats.yRange ? ` · y ${stats.yRange.min}–${stats.yRange.max}` : ''}
    </p>
    {stats.materials.length ? (
      <ul className="mt-1 space-y-0.5">
        {stats.materials.slice(0, 8).map((m) => (
          <li key={m.name} className="flex justify-between">
            <span>{m.name}</span>
            <span className="font-mono text-muted">{m.count}</span>
          </li>
        ))}
      </ul>
    ) : null}
    <table className="mt-1.5 w-full text-[11px]">
      <thead>
        <tr className="text-left text-subtle">
          <th className="font-normal">Ore (band)</th>
          <th className="text-right font-normal">mined</th>
          <th className="text-right font-normal">in walls</th>
          <th className="text-right font-normal" title="ore seen (mined + in the walls) per 100 blocks mined: compare layouts by it">
            per 100
          </th>
        </tr>
      </thead>
      <tbody>
        {stats.ores.map((o) => (
          <tr key={o.ore.id}>
            <td>
              <span
                className="mr-1 inline-block size-2 rounded-full"
                style={{ background: `#${oreColour(o.ore).toString(16).padStart(6, '0')}` }}
              />
              {o.ore.id} <span className="text-subtle">y{o.ore.min}–{o.ore.max}</span>
            </td>
            <td className="text-right font-mono">{o.mined}</td>
            <td className="text-right font-mono">{o.exposed}</td>
            <td className="text-right font-mono" data-testid={`mine-per100-${o.ore.id}`}>
              {seenPer100(stats, o.ore.id).toFixed(2)}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
    <p className="mt-1 text-[10px] text-subtle">Estimated from the generated terrain; the real world will differ.</p>
    </>
  );
}

function PreferredNote({ preferredFor }: { preferredFor: string | null | undefined }) {
  if (!preferredFor) return null;
  return <p className="mt-1 text-[11px] text-ok">This design is tagged preferred for {preferredFor}.</p>;
}
