'use client';
import { Move, RotateCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';
import { defaultModule, encodeModule, MINE_MODULE_MIME, MINE_MODULES, moduleName, type MineModuleInstance, type MineModuleKind, type MineModuleSpec } from './modules';

export interface ModulePaletteProps {
  modules?: readonly MineModuleInstance[];
  selectedModule?: string | null;
  placingModule?: MineModuleSpec | null;
  placementY?: number;
  onPlacementY?: (y: number) => void;
  onPickModule?: (spec: MineModuleSpec, options?: { drag?: boolean }) => void;
  onUpdatePlacement?: (spec: MineModuleSpec) => void;
  onSelectModule?: (id: string) => void;
  onUpdateModule?: (id: string, spec: MineModuleSpec) => void;
  onMoveModule?: (id: string) => void;
  onRemoveModule?: (id: string) => void;
}
const icons: Record<MineModuleKind, string> = {
  tunnel: 'M6 19h52v10H6z',
  branches: 'M6 21h52v6H6z M18 7v34 M32 7v34 M46 7v34',
  chamber: 'M13 8h38v32H13z M6 24h7 M51 24h7 M32 2v6 M32 40v6',
  stairs: 'M6 10h10v7h10v7h10v7h10v7h12 M6 16h6v7h10v7h10v7h10v7h16',
  spiral: 'M9 9h46v30H9V17h38v14H17v-6h22',
  junction: 'M27 4h10v15h21v10H37v15H27V29H6V19h21z',
};
function Glyph({ kind }: { kind: MineModuleKind }) {
  return <svg viewBox="0 0 64 48" aria-hidden className="h-11 w-full text-sky-400"><path d={icons[kind]} fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinejoin="round" /></svg>;
}
function Field({ label, value, min, max, onChange, disabled }: { label: string; value: number; min: number; max: number; onChange: (value: number) => void; disabled: boolean }) {
  const [draft, setDraft] = useState<string | null>(null);
  return <label className="flex items-center justify-between gap-2 text-[11px] text-muted">{label}<input type="number" value={draft ?? value} min={min} max={max} disabled={disabled} aria-label={label}
    onChange={(e) => setDraft(e.target.value)}
    onBlur={(e) => { const raw = e.target.value.trim(), number = Number(raw); if (raw && Number.isFinite(number)) onChange(Math.max(min, Math.min(max, Math.round(number)))); setDraft(null); }}
    onKeyDown={(e) => { if (e.key === 'Escape') { e.currentTarget.value = String(value); setDraft(null); e.currentTarget.blur(); } if (e.key === 'Enter') e.currentTarget.blur(); }}
    className="h-7 w-16 rounded border border-border bg-surface px-1.5 text-right font-mono text-fg" /></label>;
}

export function ModulePalette(p: ModulePaletteProps & { disabled: boolean }) {
  const modules = p.modules ?? [];
  const selected = modules.find((m) => m.id === p.selectedModule);
  const spec = p.placingModule ?? selected?.spec;
  const update = (patch: Partial<MineModuleSpec>) => {
    if (!spec) return;
    if (p.placingModule) p.onUpdatePlacement?.({ ...spec, ...patch });
    else if (selected) p.onUpdateModule?.(selected.id, { ...spec, ...patch });
  };
  return <>
    <div className="mb-2 flex items-center justify-between gap-2">
      <h3 className="text-xs font-semibold text-fg">Mine layouts</h3>
      <span className="text-[10px] text-subtle">Drag into the mine</span>
    </div>
    <div className="grid grid-cols-2 gap-1.5" aria-label="Mine layout palette">
      {MINE_MODULES.map((m) => <button key={m.kind} type="button" draggable={!p.disabled} disabled={p.disabled}
        title={m.hint} aria-label={`Place ${m.name}`} aria-pressed={p.placingModule?.kind === m.kind}
        className={cn('rounded-lg border p-2 text-left transition-colors disabled:opacity-40', p.placingModule?.kind === m.kind ? 'border-accent bg-accent/10' : 'border-border bg-surface-2/40 hover:border-sky-500/60 hover:bg-surface-2')}
        onClick={() => p.onPickModule?.(defaultModule(m.kind))}
        onDragStart={(e) => { const next = defaultModule(m.kind); e.dataTransfer.setData(MINE_MODULE_MIME, encodeModule(next)); e.dataTransfer.effectAllowed = 'copy'; p.onPickModule?.(next, { drag: true }); }}>
        <Glyph kind={m.kind} /><span className="mt-1 block text-[11px] font-medium text-fg">{m.name}</span><span className="block text-[10px] text-subtle">{m.detail}</span>
      </button>)}
    </div>
    <div className="mt-2 rounded-md border border-border bg-surface-2/40 px-2 py-1.5">
      <Field label="Entrance Y" value={p.placementY ?? 11} min={1} max={125} disabled={p.disabled} onChange={(y) => p.onPlacementY?.(y)} />
      <p className="mt-1 text-[10px] text-subtle">Click a layout, then click the mine. Nearby entrances snap together.</p>
    </div>
    {spec ? <section aria-label="Layout settings" className="mt-2 rounded-md border border-sky-500/40 p-2">
      <div className="mb-2 flex items-center justify-between"><h4 className="text-[11px] font-semibold">{p.placingModule ? 'Placing' : 'Selected'} · {moduleName(spec.kind)}</h4>
        <button type="button" disabled={p.disabled} title="Rotate layout 90°" aria-label="Rotate layout 90°" onClick={() => update({ rotation: (spec.rotation + 1) % 4 })} className="flex items-center gap-1 rounded border border-border px-1.5 py-1 text-[10px]"><RotateCw className="size-3" />{spec.rotation * 90}°</button></div>
      <div className="grid gap-1.5">
        {['tunnel', 'branches', 'chamber', 'junction'].includes(spec.kind) ? <Field label={spec.kind === 'chamber' ? 'Room length' : 'Length'} value={spec.length} min={3} max={48} disabled={p.disabled} onChange={(length) => update({ length })} /> : null}
        {spec.kind !== 'spiral' ? <Field label="Width" value={spec.width} min={1} max={spec.kind === 'chamber' ? 24 : 2} disabled={p.disabled} onChange={(width) => update({ width })} /> : null}
        {spec.kind !== 'spiral' && spec.kind !== 'stairs' ? <Field label="Clearance" value={spec.height} min={2} max={spec.kind === 'chamber' ? 6 : 3} disabled={p.disabled} onChange={(height) => update({ height })} /> : null}
        {spec.kind === 'branches' ? <><Field label="Branch reach" value={spec.branchLength} min={2} max={24} disabled={p.disabled} onChange={(branchLength) => update({ branchLength })} /><Field label="Solid gap" value={spec.spacing} min={1} max={8} disabled={p.disabled} onChange={(spacing) => update({ spacing })} /></> : null}
        {spec.kind === 'spiral' || spec.kind === 'stairs' ? <Field label="Descent" value={spec.depth} min={1} max={96} disabled={p.disabled} onChange={(depth) => update({ depth })} /> : null}
        {spec.kind === 'spiral' ? <Field label="Shaft size" value={spec.size} min={3} max={6} disabled={p.disabled} onChange={(size) => update({ size })} /> : null}
      </div>
      {selected && !p.placingModule ? <><p className="mt-2 font-mono text-[10px] text-subtle">Centre {selected.at.join(', ')} · entrance Y{selected.at[1]}</p><div className="mt-2 flex gap-1.5">
        <button type="button" disabled={p.disabled} onClick={() => p.onMoveModule?.(selected.id)} className="flex flex-1 items-center justify-center gap-1 rounded border border-border py-1 text-[11px]"><Move className="size-3" />Move</button>
        <button type="button" disabled={p.disabled} onClick={() => p.onRemoveModule?.(selected.id)} className="flex items-center gap-1 rounded border border-border px-2 py-1 text-[11px] text-danger"><Trash2 className="size-3" />Remove</button>
      </div></> : null}
    </section> : null}
    {modules.length ? <details className="mt-2" open={modules.length <= 4}><summary className="cursor-pointer text-[11px] text-muted">Layouts in this mine · {modules.length}</summary>
      <div className="mt-1 max-h-40 space-y-1 overflow-y-auto">{modules.map((m, index) => <button key={m.id} type="button" aria-pressed={p.selectedModule === m.id} onClick={() => p.onSelectModule?.(m.id)}
        className={cn('flex w-full justify-between rounded px-2 py-1.5 text-[11px]', p.selectedModule === m.id ? 'bg-accent/15 text-fg' : 'bg-surface-2/50 text-muted hover:bg-surface-2')}>
        <span>{index + 1}. {moduleName(m.spec.kind)}</span><span className="font-mono text-[10px]">Y{m.at[1]}</span>
      </button>)}</div></details> : null}
  </>;
}
