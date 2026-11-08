'use client';

/**
 * The Design Studio's mine side panel (a mine session only): the terrain's seed, the ore-height guide (a ruler of the version's
 * ore bands with the current level, and what that level holds), the X-ray toggle, the templates (spiral shaft, strip mine,
 * room; "at ore band" sets the level to the ore's best), the version-preferred presets, and the dig estimate (blocks to mine
 * by material, ores mined and exposed per band). Everything is computed from the generated terrain: estimates, the real
 * world differs.
 */
import { Dices, Eye, Server } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { levelOres, mineStats, oreColour, seenPer100 } from './mine/mineStats';
import type { MineCells } from './mine/mineTemplates';
import { ModulePalette, ModuleSettings, type ModulePaletteProps } from './mine/ModulePalette';
import { MineComponents } from './mine/MineComponents';
import type { MineTerrain } from './mine/mineTerrain';
import { presetCells, presetsFor, type MinePreset } from './mine/presets';
import type { V3 } from './canvas';

export interface MinePanelProps extends ModulePaletteProps {
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

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="border-b border-border px-3 py-2">
      <h3 className="mb-1.5 text-[11px] font-semibold tracking-wide text-muted uppercase">{title}</h3>
      {children}
    </section>
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
  const h = 160;
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
        onKeyDown={(e) => {
          if (e.key === 'ArrowUp' || e.key === 'ArrowDown') { e.preventDefault(); onLevel(Math.max(1, Math.min(top, (level ?? 11) + (e.key === 'ArrowUp' ? 1 : -1)))); }
          if (e.key === 'Home' || e.key === 'End') { e.preventDefault(); onLevel(e.key === 'Home' ? 1 : top); }
        }}
        onClick={(e) => {
          const r = e.currentTarget.getBoundingClientRect();
          onLevel(Math.max(1, Math.min(top, Math.round(((r.bottom - e.clientY) / r.height) * top))));
        }}
      >
        {[0, 16, 32, 48, 64, 96, 127].filter((y) => y <= top).map((y) => (
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
  const top = 127;
  const [seedText, setSeedText] = useState(p.seed);
  const properties = useRef<HTMLDivElement>(null);
  const inspectedId = p.placingModule ? null : p.selectedModule;
  useEffect(() => {
    // A different component brings its inspector into view, without moving the page or jumping during edits.
    if (inspectedId && properties.current) properties.current.scrollTop = 0;
  }, [inspectedId]);
  const readout = useMemo(() => p.level === null ? null : levelOres(terrain, p.level), [terrain, p.level]);
  const stats = useMemo(() => mineStats(terrain, p.dug()), [terrain, p.rev]); // eslint-disable-line react-hooks/exhaustive-deps
  const disabled = !p.canWrite || p.busy;
  return <aside className="flex min-h-0 w-full shrink-0 flex-col overflow-hidden border-l border-border bg-surface text-xs lg:w-72" aria-label="Mine designer" data-testid="mine-panel">
    <MineComponents modules={p.modules} selectedModule={p.selectedModule} onSelectModule={p.onSelectModule} onFrameModule={p.onFrameModule} />
    <div ref={properties} className="min-h-0 flex-1 overflow-y-auto" data-testid="mine-properties">
    {p.selectedModule && !p.placingModule ? <section className="border-b border-border px-3 pb-3"><ModuleSettings key={p.selectedModule} {...p} disabled={disabled} /></section> : null}
    <section className="border-b border-border p-3"><ModulePalette {...p} disabled={disabled} /></section>
    <Section title={`Ore levels · ${version}`}>
      <p className="mb-2 text-[10px] text-subtle">Choose an entrance height. The cutaway below controls only the view.</p>
      <div className="mb-2 grid grid-cols-3 gap-1">
        {terrain.table.ores.filter((o) => o.veins > 0).map((o) => <button key={o.id} type="button" title={`${o.name}: preferred feet Y ${o.best}`} disabled={disabled}
          onClick={() => p.onPlacementY?.(o.best)} className={cn('rounded border px-1.5 py-1 text-left', (p.placementY ?? 11) === o.best ? 'border-accent bg-accent/10' : 'border-border hover:bg-surface-2')}>
          <span className="block text-[10px] capitalize"><span className="mr-1 inline-block size-1.5 rounded-full" style={{ background: o.color ?? '#888' }} />{o.id}</span><span className="font-mono text-xs">Y{o.best}</span>
        </button>)}
      </div>
      <p className="mb-2 text-[10px] text-subtle">Y11: diamond, redstone and gold above lava lakes. Y16: lapis and iron. Preferred levels are practical targets, not a density peak for every ore.</p>
      <details open><summary className="cursor-pointer text-[11px] text-muted">Ore bands &amp; cutaway</summary><div className="mt-2">
        <OreHeights terrain={terrain} top={top} level={p.level} readout={readout} xray={p.xray} onLevel={p.onLevel} onXray={p.onXray} />
      </div></details>
    </Section>
    <Section title="Dig estimate"><DigEstimate stats={stats} /></Section>
    <section className="border-b border-border px-3 py-2"><details><summary className="cursor-pointer text-[11px] font-medium text-muted">Version presets · {version}</summary>
      <p className="mt-2 text-[10px] text-subtle">Complete reference mines. These add ordinary excavation cells; use the layouts above for movable pieces.</p>
      <ul className="mt-2 space-y-2">{presetsFor(version).map((pr) => <li key={pr.id}><div className="flex items-center gap-2"><span className="flex-1 text-[11px]">{pr.name}</span>
        <Button size="sm" variant="secondary" disabled={disabled} data-testid={`preset-${pr.id}`} onClick={() => p.onPreset(pr, presetCells(pr, terrain))}>Use</Button></div><p className="mt-0.5 text-[10px] text-subtle">{pr.notes}</p></li>)}</ul>
      <PreferredNote preferredFor={p.preferredFor} />
    </details></section>
    <section className="px-3 py-2"><details><summary className="cursor-pointer text-[11px] font-medium text-muted">Terrain &amp; seed</summary>
      <p className="my-2 text-[10px] text-subtle">Simulated {version} terrain, ground Y{range.min}–{range.max}. Real-world ore locations differ; mine plans keep absolute heights.</p>
      <form className="flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); if (seedText.trim() && seedText.trim() !== p.seed) p.onSeed(seedText.trim()); }}>
        <Input value={seedText} onChange={(e) => setSeedText(e.target.value)} aria-label="Seed" maxLength={32} className="h-7 min-w-0 font-mono text-[11px]" data-testid="mine-seed" />
        <Button size="sm" variant="secondary" type="submit" disabled={disabled} title="Use this seed">Set</Button>
        <button type="button" aria-label="Random seed" disabled={disabled} className="rounded border border-border p-1.5" onClick={() => { const seed = String(Math.floor(Math.random() * 9e15) + 1); setSeedText(seed); p.onSeed(seed); }}><Dices className="size-3.5" /></button>
        {p.serverSeed ? <button type="button" aria-label="Use the server's seed" disabled={disabled} className="rounded border border-border p-1.5" onClick={() => { setSeedText(p.serverSeed!); p.onSeed(p.serverSeed!); }}><Server className="size-3.5" /></button> : null}
      </form>
    </details></section>
    </div>
  </aside>;
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
        title="Show planned excavation and exposed veins through the terrain"
      >
        <Eye className="size-3" /> Plan X-ray
      </button>
    </div>
    <p className="mt-2 flex flex-wrap gap-2 text-[10px]" aria-label="Mine preview legend"><span className="text-sky-400">■ Planned excavation</span><span className="text-amber-400">■ Exposed veins</span></p>
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
    <p className="mt-1 text-[10px] text-subtle" data-testid="mine-vein-follow">
      Miners automatically follow connected exposed ore beyond the plan, while keeping access routes and protected blocks intact.
      These estimates show the planned tunnels and their walls; extra vein mining adds to the yield.
    </p>
    </>
  );
}

function PreferredNote({ preferredFor }: { preferredFor: string | null | undefined }) {
  if (!preferredFor) return null;
  return <p className="mt-1 text-[11px] text-ok">This design is tagged preferred for {preferredFor}.</p>;
}
