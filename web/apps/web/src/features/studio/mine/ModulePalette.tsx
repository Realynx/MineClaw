'use client';
import { Copy, CopyPlus, Move, RotateCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';
import { plotMode, type V3 } from '../canvas';
import type { PlacementHeightMode } from './placementHeight';
import { defaultModule, encodeModule, MINE_MODULE_MAX_BRANCH, MINE_MODULE_MAX_LENGTH, MINE_MODULE_MAX_ROOM, MINE_MODULE_MIME, MINE_MODULES, moduleName, type MineModuleInstance, type MineModuleKind, type MineModuleSpec } from './modules';

export interface ModulePaletteProps {
  modules?: readonly MineModuleInstance[];
  selectedModule?: string | null;
  placingModule?: MineModuleSpec | null;
  placementY?: number;
  placementMode?: PlacementHeightMode;
  placementSurface?: boolean;
  placementHoverY?: number;
  onPlacementMode?: (mode: PlacementHeightMode) => void;
  onPlacementY?: (y: number) => void;
  onPickModule?: (spec: MineModuleSpec, options?: { drag?: boolean }) => void;
  onUpdatePlacement?: (spec: MineModuleSpec) => void;
  onSelectModule?: (id: string) => void;
  onUpdateModule?: (id: string, spec: MineModuleSpec) => void;
  onMoveModule?: (id: string) => void;
  onPositionModule?: (id: string, at: V3) => void;
  onFrameModule?: (id: string) => void;
  onRemoveModule?: (id: string) => void;
  canPasteModule?: boolean;
  onCopyModule?: (id: string) => void;
  onDuplicateModule?: (id: string) => void;
  onPasteModule?: () => void;
  onCombineModules?: (ids: string[]) => void;
}
const icons: Record<MineModuleKind, string> = {
  tunnel: 'M6 19h52v10H6z',
  branches: 'M6 21h52v6H6z M18 7v34 M32 7v34 M46 7v34',
  chamber: 'M13 8h38v32H13z M6 24h7 M51 24h7 M32 2v6 M32 40v6',
  stairs: 'M6 10h10v7h10v7h10v7h10v7h12 M6 16h6v7h10v7h10v7h10v7h16',
  spiral: 'M9 9h46v30H9V17h38v14H17v-6h22',
  junction: 'M27 4h10v15h21v10H37v15H27V29H6V19h21z',
  custom: 'M8 8h20v14H8z M36 26h20v14H36z M28 15h8v18 M16 22v11h20',
};
export const moduleLabel = (spec: MineModuleSpec): string => spec.label || moduleName(spec.kind);
export function ModuleGlyph({ kind, compact = false }: { kind: MineModuleKind; compact?: boolean }) {
  return <svg viewBox="0 0 64 48" aria-hidden className={cn('text-sky-400', compact ? 'size-5 shrink-0' : 'h-11 w-full')}><path d={icons[kind]} fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinejoin="round" /></svg>;
}
function Field({ label, compactLabel, value, min, max, onChange, disabled }: { label: string; compactLabel?: string; value: number; min: number; max: number; onChange: (value: number) => void; disabled: boolean }) {
  const [draft, setDraft] = useState<string | null>(null);
  return <label className={cn('flex min-w-0 items-center justify-between text-[11px] text-muted', compactLabel ? 'gap-1' : 'gap-2')}>{compactLabel ?? label}<input type="number" value={draft ?? value} min={min} max={max} disabled={disabled} aria-label={label}
    onChange={(e) => setDraft(e.target.value)}
    onBlur={(e) => { const raw = e.target.value.trim(), number = Number(raw); if (raw && Number.isFinite(number)) { const next = Math.max(min, Math.min(max, Math.round(number))); if (next !== value) onChange(next); } setDraft(null); }}
    onKeyDown={(e) => { if (e.key === 'Escape') { e.currentTarget.value = String(value); setDraft(null); e.currentTarget.blur(); } if (e.key === 'Enter') e.currentTarget.blur(); }}
    className={cn('h-7 min-w-0 rounded border border-border bg-surface px-1.5 text-right font-mono text-fg', compactLabel ? 'w-full' : 'w-16')} /></label>;
}

export function ModulePalette(p: ModulePaletteProps & { disabled: boolean }) {
  return <>
    <div className="mb-2 flex items-center justify-between gap-2">
      <h3 className="text-xs font-semibold text-fg">Mine layouts</h3>
      <span className="text-[10px] text-subtle">Drag into the mine</span>
    </div>
    {p.canPasteModule ? <button type="button" disabled={p.disabled} onClick={p.onPasteModule} className="mb-2 flex w-full items-center justify-center gap-1.5 rounded border border-accent/40 bg-accent/10 py-1.5 text-[11px]"><CopyPlus className="size-3.5" />Paste component</button> : null}
    <div className="grid grid-cols-2 gap-1.5" aria-label="Mine layout palette">
      {MINE_MODULES.map((m) => <button key={m.kind} type="button" draggable={!p.disabled} disabled={p.disabled}
        title={m.hint} aria-label={`Place ${m.name}`} aria-pressed={p.placingModule?.kind === m.kind}
        className={cn('rounded-lg border p-2 text-left transition-colors disabled:opacity-40', p.placingModule?.kind === m.kind ? 'border-accent bg-accent/10' : 'border-border bg-surface-2/40 hover:border-sky-500/60 hover:bg-surface-2')}
        onClick={() => p.onPickModule?.(defaultModule(m.kind))}
        onDragStart={(e) => { const next = defaultModule(m.kind); e.dataTransfer.setData(MINE_MODULE_MIME, encodeModule(next)); e.dataTransfer.effectAllowed = 'copy'; p.onPickModule?.(next, { drag: true }); }}>
        <ModuleGlyph kind={m.kind} /><span className="mt-1 block text-[11px] font-medium text-fg">{m.name}</span><span className="block text-[10px] text-subtle">{m.detail}</span>
      </button>)}
    </div>
    <div className="mt-2 rounded-md border border-border bg-surface-2/40 px-2 py-1.5">
      <label className="mb-2 flex items-center justify-between gap-2 text-[11px] text-muted">Placement height
        <select aria-label="Placement height" value={p.placementMode ?? 'auto'} disabled={p.disabled} onChange={(event) => p.onPlacementMode?.(event.target.value as PlacementHeightMode)} className="h-7 rounded border border-border bg-surface px-1 text-[11px] text-fg">
          <option value="auto">Automatic</option><option value="surface">Surface</option><option value="fixed">Fixed Y</option>
        </select>
      </label>
      {p.placementSurface ? <p className="text-[11px] text-muted">Entrance follows the surface{p.placementHoverY !== undefined ? <span className="ml-1 font-mono text-fg">· Y{p.placementHoverY}</span> : null}.</p> : <Field label="Entrance Y" value={p.placementY ?? 11} min={plotMode().minY + 1} max={plotMode().maxY - 2} disabled={p.disabled} onChange={(y) => p.onPlacementY?.(y)} />}
      {(p.placementMode ?? 'auto') === 'auto' ? <p className="mt-1 text-[10px] text-subtle">Access starts at the surface. Select a component to continue from its lower exit.</p> : null}
      <p className="mt-1 text-[10px] text-subtle">Click a layout, then click the mine. Nearby entrances snap together.</p>
    </div>
    {p.placingModule ? <ModuleSettings key={`placing-${p.placingModule.kind}`} {...p} /> : null}
  </>;
}

/** The active piece's inspector sits directly below the outliner; placement uses the same dimensions. */
export function ModuleSettings(p: ModulePaletteProps & { disabled: boolean }) {
  const selected = p.modules?.find((m) => m.id === p.selectedModule);
  const spec = p.placingModule ?? selected?.spec;
  if (!spec) return null;
  const update = (patch: Partial<MineModuleSpec>) => {
    if (p.placingModule) p.onUpdatePlacement?.({ ...spec, ...patch });
    else if (selected) p.onUpdateModule?.(selected.id, { ...spec, ...patch });
  };
  const position = (axis: number, value: number) => {
    if (!selected) return;
    const at: V3 = [...selected.at];
    at[axis] = value;
    p.onPositionModule?.(selected.id, at);
  };
  return <section aria-label="Layout settings" className="mt-2 rounded-md border border-sky-500/40 p-2">
      <div className="mb-2 flex items-center justify-between gap-1"><h4 className="min-w-0 text-[11px] font-semibold">{p.placingModule ? 'Placing' : 'Selected'} · {moduleLabel(spec)}</h4>
        <button type="button" disabled={p.disabled} title="Rotate layout 90°" aria-label="Rotate layout 90°" onClick={() => update({ rotation: (spec.rotation + 1) % 4 })} className="flex items-center gap-1 rounded border border-border px-1.5 py-1 text-[10px]"><RotateCw className="size-3" />{spec.rotation * 90}°</button></div>
      {selected && !p.placingModule ? <fieldset className="mb-3 space-y-1.5 border-b border-border pb-2">
        <legend className="mb-1 text-[10px] text-muted">Position · entrance centre</legend>
        <div className="grid grid-cols-3 gap-2">{(['X', 'Y', 'Z'] as const).map((axis, index) => <Field key={`${selected.id}-${axis}`} label={`Position ${axis}`} compactLabel={axis} value={selected.at[index]!} min={index === 1 ? plotMode().minY + 1 : 0} max={index === 1 ? plotMode().maxY : plotMode().size - 1} disabled={p.disabled || !p.onPositionModule} onChange={(value) => position(index, value)} />)}</div>
        <p className="text-[10px] text-subtle">Drag an axis or use arrow keys; PgUp/PgDn changes height.</p>
      </fieldset> : null}
      {spec.kind === 'custom' ? <><label className="mb-2 block text-[11px] text-muted">Component name<input key={spec.label} defaultValue={spec.label ?? 'Custom component'} aria-label="Component name" maxLength={48} disabled={p.disabled} className="mt-1 h-7 w-full rounded border border-border bg-surface px-1.5 text-fg" onBlur={(event) => { const label = event.target.value.trim() || 'Custom component'; if (label !== spec.label) update({ label }); }} onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur(); }} /></label><p className="mb-2 text-[10px] text-subtle">{spec.parts?.length ?? 0} combined parts. Move, rotate or copy them together.</p></> : null}
      <div className="grid gap-1.5">
        {['tunnel', 'branches', 'chamber', 'junction'].includes(spec.kind) ? <Field label={spec.kind === 'chamber' ? 'Room length' : 'Length'} value={spec.length} min={3} max={MINE_MODULE_MAX_LENGTH} disabled={p.disabled} onChange={(length) => update({ length })} /> : null}
        {spec.kind !== 'spiral' && spec.kind !== 'custom' ? <Field label="Width" value={spec.width} min={1} max={spec.kind === 'chamber' ? MINE_MODULE_MAX_ROOM : 2} disabled={p.disabled} onChange={(width) => update({ width })} /> : null}
        {spec.kind !== 'spiral' && spec.kind !== 'stairs' && spec.kind !== 'custom' ? <Field label="Clearance" value={spec.height} min={2} max={spec.kind === 'chamber' ? 6 : 3} disabled={p.disabled} onChange={(height) => update({ height })} /> : null}
        {spec.kind === 'branches' ? <><Field label="Branch reach" value={spec.branchLength} min={2} max={MINE_MODULE_MAX_BRANCH} disabled={p.disabled} onChange={(branchLength) => update({ branchLength })} /><Field label="Solid gap" value={spec.spacing} min={1} max={8} disabled={p.disabled} onChange={(spacing) => update({ spacing })} /></> : null}
        {spec.kind === 'spiral' || spec.kind === 'stairs' ? <Field label="Descent" value={spec.depth} min={1} max={plotMode().maxY - plotMode().minY - 1} disabled={p.disabled} onChange={(depth) => update({ depth })} /> : null}
        {spec.kind === 'spiral' ? <Field label="Shaft size" value={spec.size} min={3} max={6} disabled={p.disabled} onChange={(size) => update({ size })} /> : null}
      </div>
      {selected && !p.placingModule ? <><div className="mt-2 flex gap-1.5">
        <button type="button" disabled={!p.onCopyModule} onClick={() => p.onCopyModule?.(selected.id)} title="Copy component (Ctrl/Cmd+C)" className="flex flex-1 items-center justify-center gap-1 rounded border border-border py-1 text-[11px]"><Copy className="size-3" />Copy</button>
        <button type="button" disabled={p.disabled || !p.onDuplicateModule} onClick={() => p.onDuplicateModule?.(selected.id)} title="Duplicate component (Ctrl/Cmd+D)" className="flex flex-1 items-center justify-center gap-1 rounded border border-border py-1 text-[11px]"><CopyPlus className="size-3" />Duplicate</button>
      </div><div className="mt-2 flex gap-1.5">
        <button type="button" disabled={p.disabled} onClick={() => p.onMoveModule?.(selected.id)} className="flex flex-1 items-center justify-center gap-1 rounded border border-border py-1 text-[11px]"><Move className="size-3" />Move</button>
        <button type="button" disabled={p.disabled} onClick={() => p.onRemoveModule?.(selected.id)} className="flex items-center gap-1 rounded border border-border px-2 py-1 text-[11px] text-danger"><Trash2 className="size-3" />Remove</button>
      </div></> : null}
    </section>;
}
