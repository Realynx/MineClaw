'use client';

/**
 * The Design Studio's chrome around the 3D stage (StudioPage owns the state): the header (version, kind, name, save, revisions),
 * the saved bar, the kind / version dialogs, the HUD, the tool rail, and the tool options with the selection's actions.
 */
import type { DesignerCompatReport, DesignerKind, DesignerSession, DesignRevision } from '@mineclaw/contracts';
import {
  Copy,
  FlipHorizontal2,
  FlipVertical2,
  Grid3x3,
  History as HistoryIcon,
  Maximize2,
  Redo2,
  RotateCcw,
  RotateCw,
  Shovel,
  Trash2,
  Undo2,
} from 'lucide-react';
import Link from 'next/link';
import type { ReactNode, RefObject } from 'react';
import { Badge, Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import type { Cell, Selection, V3 } from './canvas';
import { invert, selectAll, sizeOf } from './canvas';
import type { Dir, Version } from './facing';
import type { PaletteEntry } from './palette';
import { RedstoneSavedBar } from './redstone/RedstoneSavedBar';
import type { Studio, StudioSnapshot } from './studio';
import type { Notices, NoticeTone, StudioNotice } from './studioSave';
import {
  type BoxMode,
  type BrushMode,
  type BrushShape,
  deleteSelection,
  type Hit,
  mirrorSel,
  raiseLower,
  replaceIn,
  rotate,
  selectionShape,
  underRange,
} from './tools';

export type Tool = 'build' | 'box' | 'line' | 'select' | 'brush' | 'face' | 'extrude' | 'anchor' | 'configure';
export type ToolDef = { id: Tool; label: string; key: string; icon: ReactNode };
type Bounds = { min: V3; max: V3 };
type Transform = (label: string, f: (s: Selection) => { cells: Cell[]; selection: Selection | null }) => void;

// ------------------------------------------------------------------ header

export function StudioHeader(p: {
  snap: StudioSnapshot;
  session: DesignerSession;
  version: Version;
  locked: boolean;
  changeVersion: (v: Version) => void;
  changeKind: (k: DesignerKind) => void;
  saveName: string;
  setSaveName: (v: string) => void;
  saveSummary: string;
  setSaveSummary: (v: string) => void;
  canSave: boolean;
  saving: boolean;
  doSave: (asNew: boolean) => void;
  revisions: DesignRevision[];
  /** Sends the waiting edits now (after a refusal, or instead of waiting for the next try). */
  retrySync: () => void;
}) {
  const { snap, session, version } = p;
  return (
    <header className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-3 py-2">
      <Link href="/designs" className="text-sm text-muted hover:text-fg">
        Designs
      </Link>
      <span className="text-subtle">/</span>
      <h1 className="text-sm font-semibold text-fg">Design Studio</h1>
      {snap.designId ? <Badge tone="accent">#{snap.designId}</Badge> : <Badge>unsaved</Badge>}
      <label className="flex items-center gap-1 text-xs text-muted" title="The game version this design is drawn for">
        Version
        <select
          aria-label="Game version"
          value={version}
          disabled={p.locked}
          onChange={(e) => p.changeVersion(e.target.value as Version)}
          className="h-7 rounded-md border border-border bg-surface px-1.5 font-mono text-xs text-fg"
          data-testid="version-select"
        >
          {(session.versions?.length ? session.versions : ['b1.8.1', '1.12.2']).map((v) => (
            <option key={v} value={v}>
              {v}
              {v === session.serverVersion ? ' (server)' : ''}
            </option>
          ))}
        </select>
      </label>
      <label
        className="flex items-center gap-1 text-xs text-muted"
        title="Build: on flat grass. Mine: dug into generated terrain, kept at absolute y (pawns mine it out). Redstone: a circuit on flat grass, with a simulator and a debugger."
      >
        Kind
        <select
          aria-label="Design kind"
          value={session.kind ?? 'build'}
          disabled={p.locked}
          onChange={(e) => p.changeKind(e.target.value as DesignerKind)}
          className="h-7 rounded-md border border-border bg-surface px-1.5 text-xs text-fg"
          data-testid="kind-select"
        >
          <option value="build">Build</option>
          <option value="mine">Mine</option>
          <option value="redstone">Redstone</option>
        </select>
      </label>
      <ServerVersionNote version={version} serverVersion={session.serverVersion} />
      <div className="ml-auto flex flex-wrap items-center gap-2">
        <div className="w-40">
          <Input
            value={p.saveName}
            onChange={(e) => p.setSaveName(e.target.value)}
            placeholder="Design name"
            aria-label="Design name"
            className="h-8"
            maxLength={48}
          />
        </div>
        <div className="w-56">
          <Input
            value={p.saveSummary}
            onChange={(e) => p.setSaveSummary(e.target.value)}
            placeholder="What changed (revision message)"
            aria-label="Revision message"
            className="h-8"
            maxLength={120}
          />
        </div>
        <SyncChip snap={snap} retry={p.retrySync} />
        {snap.dirty ? (
          <Badge tone="warn" data-testid="unsaved-chip" title="Changes since the last save. Ctrl+S saves.">
            Unsaved changes
          </Badge>
        ) : null}
        <Button
          size="sm"
          variant={snap.dirty ? 'primary' : 'secondary'}
          onClick={() => p.doSave(false)}
          disabled={!p.canSave}
          loading={p.saving}
        >
          {snap.designId ? 'Save as new revision' : 'Save'}
        </Button>
        {snap.designId ? (
          <Button size="sm" variant="secondary" onClick={() => p.doSave(true)} disabled={!p.canSave}>
            Save as new design
          </Button>
        ) : null}
        {p.revisions.length ? <RevisionList revisions={p.revisions} /> : null}
      </div>
    </header>
  );
}

/** "Synced", "Sending 120 cells", "Retrying: <why>" or "Not syncing: <why>": whether the bot's canvas follows this page. */
export function syncText(snap: Pick<StudioSnapshot, 'pending' | 'sync' | 'syncError'>): string {
  const why = snap.syncError ?? 'the designer did not answer';
  if (snap.sync === 'blocked') return `Not syncing: ${why}`;
  if (snap.sync === 'retrying') return `Retrying: ${why}`;
  if (snap.sync === 'sending' || snap.pending)
    return snap.pending ? `Sending ${snap.pending} cell${snap.pending === 1 ? '' : 's'}` : 'Sending';
  return 'Synced';
}

const SYNC_TONE = { idle: 'neutral', sending: 'info', retrying: 'warn', blocked: 'danger' } as const;

export function SyncChip({
  snap,
  retry,
}: {
  snap: Pick<StudioSnapshot, 'pending' | 'sync' | 'syncError'>;
  retry: () => void;
}) {
  const failing = snap.sync === 'blocked' || snap.sync === 'retrying';
  return (
    <span className="flex items-center gap-1" data-testid="sync-chip" data-sync={snap.sync}>
      <Badge
        tone={SYNC_TONE[snap.sync]}
        className="max-w-72 truncate"
        title={failing ? 'The bot saves the canvas it holds: edits it has not got are not saved.' : undefined}
        role={failing ? 'alert' : 'status'}
      >
        {syncText(snap)}
      </Badge>
      {failing ? (
        <button type="button" className="text-xs text-fg underline" onClick={retry}>
          Retry
        </button>
      ) : null}
    </span>
  );
}

function ServerVersionNote({ version, serverVersion }: { version: Version; serverVersion?: string | null }) {
  if (!serverVersion || serverVersion === version) return null;
  return (
    <span
      className="rounded bg-warn-soft px-1.5 py-0.5 text-[11px] text-warn"
      title={`Drawn for ${version}; the server runs ${serverVersion}. A build there converts what maps and refuses blocks it doesn't have.`}
    >
      server is {serverVersion}
    </span>
  );
}

function RevisionList({ revisions }: { revisions: DesignRevision[] }) {
  return (
    <details className="relative">
      <summary
        className="flex h-8 cursor-pointer list-none items-center gap-1 rounded-md px-2 text-xs text-muted hover:bg-surface-2"
        aria-label="Revision history"
      >
        <HistoryIcon className="size-3.5" /> r{revisions[0]!.rev} · {revisions.length}
      </summary>
      <ol
        className="absolute right-0 z-20 mt-1 max-h-72 w-80 overflow-y-auto rounded-lg border border-border bg-surface p-2 shadow-card"
        aria-label="Revisions"
      >
        {revisions.map((r) => (
          <li key={r.rev} className="border-b border-border py-1.5 text-xs last:border-0">
            <span className="font-medium text-fg">r{r.rev}</span>{' '}
            <span className="text-muted">
              “{r.message}” · {r.n} blocks
            </span>
            <div className="text-subtle">{r.author}</div>
          </li>
        ))}
      </ol>
    </details>
  );
}

// ------------------------------------------------------------------ bars and dialogs

export function SavedBar(p: {
  saved: { designId: number; rev: number; text: string };
  mineRange: string | null;
  canChat: boolean;
  mainBot: string | null;
  askBot: () => void;
  /** Changed since that save. */
  dirty?: boolean;
  /** A redstone design: neither the pawns nor the bot's builder can build a working circuit yet, so neither is offered. */
  redstone?: boolean;
}) {
  const { saved } = p;
  if (p.redstone) return <RedstoneSavedBar saved={saved} dirty={p.dirty} />;
  return (
    <div
      className="flex flex-wrap items-center gap-2 border-b border-border bg-ok-soft px-3 py-1.5 text-xs text-ok"
      data-testid="saved-bar"
    >
      <span>
        {(!p.dirty && saved.text) || `Design #${saved.designId}${saved.rev ? ` r${saved.rev}` : ''}`}
        {p.dirty ? ' · edited since' : ''}
      </span>
      {p.mineRange !== null ? (
        <span className="rounded bg-surface px-1.5 py-0.5 font-mono text-fg" data-testid="mine-range">
          {p.mineRange}
        </span>
      ) : null}
      <span className="ml-auto" />
      <Link
        href={`/story?design=${saved.designId}`}
        className="rounded-md bg-surface px-2 py-1 text-fg hover:bg-surface-2"
        title="Opens Controller with a build placement for this design"
      >
        Build in Controller
      </Link>
      <Button
        size="sm"
        variant="secondary"
        onClick={p.askBot}
        disabled={!p.canChat || !p.mainBot}
        title={p.canChat ? `Ask ${p.mainBot ?? 'the bot'} in chat to build it` : 'Your account cannot chat with the bots'}
      >
        Ask the bot to build it
      </Button>
      <Link href={`/designs?id=${saved.designId}`} className="text-ok underline">
        Open in Designs
      </Link>
    </div>
  );
}

const NOTICE_TONE: Record<NoticeTone, string> = {
  info: 'bg-info-soft text-info',
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  error: 'bg-danger-soft text-danger',
};

function NoticeLine({ notice, testId, dismiss }: { notice: StudioNotice; testId: string; dismiss?: () => void }) {
  return (
    <div
      className={cn(
        'flex flex-wrap items-center gap-2 border-b border-border px-3 py-1 text-xs',
        NOTICE_TONE[notice.tone],
      )}
      role={notice.tone === 'error' ? 'alert' : 'status'}
      data-testid={testId}
      data-tone={notice.tone}
    >
      <span>{notice.text}</span>
      {notice.action ? (
        <button type="button" className="font-medium underline" onClick={notice.action.run}>
          {notice.action.label}
        </button>
      ) : null}
      {dismiss ? (
        <button type="button" className="ml-auto underline" onClick={dismiss}>
          Dismiss
        </button>
      ) : null}
    </div>
  );
}

/** The notice line: what stays until dismissed (failures, how a mine's layout opened) above the latest passing notice. */
export function NoticeLines({ notices, dismiss }: { notices: Notices; dismiss: () => void }) {
  return (
    <>
      {notices.sticky ? <NoticeLine notice={notices.sticky} testId="studio-alert" dismiss={dismiss} /> : null}
      {notices.passing ? <NoticeLine notice={notices.passing} testId="studio-notice" /> : null}
    </>
  );
}

/** A mine's saved range: "y lo..hi · anchor absolute[ · preferred for v]". */
export function mineRangeText(bounds: Bounds, preferred: string | null): string {
  return `y ${bounds.min[1]}..${bounds.max[1]} · anchor absolute${preferred ? ` · preferred for ${preferred}` : ''}`;
}

export function KindDialog(p: {
  kind: DesignerKind;
  blocks: number;
  dug: number;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const mine = p.kind === 'mine';
  // Only a switch to or from a mine clears the canvas, so the other target is a build or a redstone circuit.
  const onGrass = p.kind === 'redstone' ? 'a redstone circuit' : 'a build';
  return (
    <div
      role="dialog"
      aria-label="Change the kind"
      className="flex flex-col gap-2 border-b border-border bg-warn-soft px-3 py-2 text-xs text-fg"
      data-testid="kind-dialog"
    >
      <p>
        Switch this design to <b>{mine ? 'a mine' : onGrass}</b>? The canvas is cleared ({p.blocks} blocks,{' '}
        {p.dug} dug cells):{' '}
        {mine ? 'a mine is dug into generated terrain at absolute y' : `${onGrass} stands on flat grass`}. Save it
        first to keep it.
      </p>
      <div className="flex gap-2">
        <Button size="sm" onClick={p.onConfirm} loading={p.busy}>
          Clear and switch
        </Button>
        <Button size="sm" variant="secondary" onClick={p.onCancel} disabled={p.busy}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

const counted = (list: Array<{ count: number; name: string }>) => list.map((s) => `${s.count} ${s.name}`).join(', ');

export function VersionDialog(p: {
  version: Version;
  target: Version;
  report: DesignerCompatReport;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const { report } = p;
  return (
    <div
      role="dialog"
      aria-label="Change the version"
      className="flex flex-col gap-2 border-b border-border bg-warn-soft px-3 py-2 text-xs text-fg"
      data-testid="version-dialog"
    >
      <p>
        Convert this design from <b>{p.version}</b> to <b>{p.target}</b>?{' '}
        <span className="text-muted">
          {report.same} block{report.same === 1 ? '' : 's'} stay the same.
        </span>
      </p>
      {report.substituted.length ? <p>Becomes: {counted(report.substituted)}</p> : null}
      {report.missing.length ? (
        <p className="text-warn">
          Not in {p.target} (left out): {counted(report.missing)}
        </p>
      ) : null}
      <div className="flex gap-2">
        <Button size="sm" onClick={p.onConfirm} loading={p.busy}>
          Convert to {p.target}
        </Button>
        <Button size="sm" variant="secondary" onClick={p.onCancel} disabled={p.busy}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ HUD

/** The aimed cell (y absolute in a mine, "underground" below a build's grass), the ground spot, or a dash. */
export function aimText(h: Hit | null, mine: boolean): string {
  if (h?.hit) return `${h.hit.join(', ')}${mine ? ' (y absolute)' : h.hit[1] < 0 ? ' (underground)' : ''}`;
  return h?.place ? `ground · ${h.place.join(', ')}` : '—';
}

export interface ToolSettings {
  tool: Tool;
  boxMode: BoxMode;
  brushMode: BrushMode;
  brushShape: BrushShape;
  brushSize: number;
  extrudeN: number;
}

/** The active tool's settings after its name: " (fill)", " (add sphere 3)", " (2)". */
export function toolSuffix(t: ToolSettings): string {
  if (t.tool === 'box') return ` (${t.boxMode})`;
  if (t.tool === 'brush') return ` (${t.brushMode} ${t.brushShape} ${t.brushSize})`;
  return t.tool === 'extrude' ? ` (${t.extrudeN})` : '';
}

/** "12 blocks · 3 dug · 5×4×6 · r9 · 2 unsent · 40 fps". */
export function countsText(snap: StudioSnapshot, bounds: Bounds | null): string {
  const dug = snap.dug ? ` · ${snap.dug} dug` : '';
  const size = bounds ? ` · ${sizeOf(bounds).join('×')}` : '';
  return `${snap.blocks} blocks${dug}${size} · r${snap.rev}`;
}

export function StudioHud(p: {
  aim: string;
  facing: string;
  toolLabel: string;
  counts: string;
  pending: number;
  fps: number;
  over: boolean;
  selection: string | null;
  pasting: boolean;
}) {
  return (
    <div className="pointer-events-none absolute bottom-36 left-14 max-w-[calc(100%-4.5rem)] space-y-1 font-mono text-[11px] text-white" data-testid="studio-hud">
      <div className="rounded bg-black/55 px-2 py-0.5">
        {p.aim} · facing {p.facing} · {p.toolLabel}
      </div>
      <div className="rounded bg-black/55 px-2 py-0.5">
        {p.counts}
        {p.pending ? ` · ${p.pending} unsent` : ''} · {p.fps} fps
      </div>
      {p.over ? <div className="rounded bg-danger px-2 py-0.5">over 32 a side: too big to save as one design</div> : null}
      {p.selection !== null ? (
        <div className="rounded bg-[#0369a1]/80 px-2 py-0.5" data-testid="selection-hud">
          {p.selection}
        </div>
      ) : null}
      {p.pasting ? (
        <div className="rounded bg-[#a16207]/80 px-2 py-0.5">Pasting: click to place (Shift keeps pasting) · Esc cancels</div>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ tool rail

export function ToolRail(p: {
  tools: ToolDef[];
  tool: Tool;
  setTool: (t: Tool) => void;
  studio: Studio;
  snap: StudioSnapshot;
  grid: boolean;
  toggleGrid: () => void;
  underground: boolean;
  toggleUnder: () => void;
  stage: RefObject<HTMLDivElement | null>;
}) {
  const { snap, studio } = p;
  return (
    <div className="absolute top-3 left-3 flex flex-col gap-1" role="toolbar" aria-label="Tools">
      {p.tools.map((t) => (
        <button
          key={t.id}
          type="button"
          onClick={() => p.setTool(t.id)}
          aria-pressed={p.tool === t.id}
          aria-label={t.label}
          title={`${t.label} [${t.key}]`}
          className={cn(
            'grid size-8 place-items-center rounded text-white',
            p.tool === t.id ? 'bg-accent' : 'bg-black/55 hover:bg-black/75',
          )}
        >
          {t.icon}
        </button>
      ))}
      <div className="my-1 h-px bg-white/20" />
      <button
        type="button"
        onClick={() => studio.undo()}
        disabled={!snap.canUndo}
        aria-label="Undo"
        title={`Undo ${snap.undoLabel ?? ''} [Ctrl+Z]`}
        className="grid size-8 place-items-center rounded bg-black/55 text-white disabled:opacity-40"
      >
        <Undo2 className="size-4" />
      </button>
      <button
        type="button"
        onClick={() => studio.redo()}
        disabled={!snap.canRedo}
        aria-label="Redo"
        title={`Redo ${snap.redoLabel ?? ''} [Ctrl+Y]`}
        className="grid size-8 place-items-center rounded bg-black/55 text-white disabled:opacity-40"
      >
        <Redo2 className="size-4" />
      </button>
      <button
        type="button"
        onClick={p.toggleGrid}
        aria-pressed={p.grid}
        aria-label="Grid"
        title="Grid [H]"
        className="grid size-8 place-items-center rounded bg-black/55 text-white"
      >
        <Grid3x3 className="size-4" />
      </button>
      <button
        type="button"
        onClick={p.toggleUnder}
        aria-pressed={p.underground}
        aria-label="Underground"
        title="Underground: dig the ground and see below it [K]"
        className={cn(
          'grid size-8 place-items-center rounded text-white',
          p.underground ? 'bg-accent' : 'bg-black/55 hover:bg-black/75',
        )}
      >
        <Shovel className="size-4" />
      </button>
      <button
        type="button"
        onClick={() => void p.stage.current?.requestFullscreen?.().catch(() => undefined)}
        aria-label="Full screen"
        className="grid size-8 place-items-center rounded bg-black/55 text-white"
      >
        <Maximize2 className="size-4" />
      </button>
    </div>
  );
}

// ------------------------------------------------------------------ tool options

export function UnderSlider({ under, setUnder }: { under: number; setUnder: (y: number) => void }) {
  return (
    <label className="flex items-center gap-1.5 rounded bg-black/55 px-2 py-1" title="Layers above this one are cut away">
      Underground: show to y
      <input
        type="range"
        min={underRange().min}
        max={underRange().max}
        value={under}
        onChange={(e) => setUnder(Number(e.target.value))}
        aria-label="Underground level"
      />
      <span className="w-6 text-right tabular-nums">{under}</span>
    </label>
  );
}

export function ToolSettingsBar(p: ToolSettings & {
  mine?: boolean;
  setBoxMode: (v: BoxMode) => void;
  setBrushMode: (v: BrushMode) => void;
  setBrushShape: (v: BrushShape) => void;
  setBrushSize: (v: number) => void;
  setExtrudeN: (v: number) => void;
}) {
  if (p.tool === 'box' && p.mine) return <span className="rounded bg-black/55 px-2 py-1">Drag to excavate a box · PgUp / PgDn adjusts height</span>;
  if (p.tool === 'box')
    return (
      <Seg label="Box" value={p.boxMode} options={['fill', 'hollow', 'walls', 'outline']} onChange={(v) => p.setBoxMode(v as BoxMode)} />
    );
  if (p.tool === 'brush')
    return (
      <div className="flex max-w-full items-center gap-1.5 overflow-x-auto whitespace-nowrap" aria-label="Brush settings">
        {!p.mine ? (
        <Seg
          label="Brush"
          value={p.brushMode}
          options={['add', 'remove', 'paint', 'replace', 'smooth']}
          onChange={(v) => p.setBrushMode(v as BrushMode)}
        />
        ) : <span className="shrink-0 rounded bg-black/55 px-2 py-1">Excavate</span>}
        <Seg label="Shape" value={p.brushShape} options={['sphere', 'cube']} onChange={(v) => p.setBrushShape(v as BrushShape)} />
        <label className="flex shrink-0 items-center gap-1 rounded bg-black/55 px-2 py-1">
          Size
          <input
            type="range"
            min={1}
            max={8}
            value={p.brushSize}
            onChange={(e) => p.setBrushSize(Number(e.target.value))}
            aria-label="Brush size"
          />
          {p.brushSize}
        </label>
      </div>
    );
  if (p.tool === 'extrude')
    return (
      <label className="flex items-center gap-1 rounded bg-black/55 px-2 py-1">
        By
        <input
          type="number"
          min={1}
          max={16}
          value={p.extrudeN}
          onChange={(e) => p.setExtrudeN(Math.max(1, Math.min(16, Number(e.target.value) || 1)))}
          className="w-12 bg-transparent"
          aria-label="Extrude by"
        />
      </label>
    );
  return null;
}

// ------------------------------------------------------------------ the selection's actions

const AXES = ['x', 'y', 'z'] as const;
const unitMove = (a: 'x' | 'y' | 'z', sgn: number): V3 => [a === 'x' ? sgn : 0, a === 'y' ? sgn : 0, a === 'z' ? sgn : 0];

/** Rotate / mirror (the selection, or the whole design), moves, copy / duplicate / delete, paste. */
export function TransformBar(p: {
  studio: Studio;
  sel: Selection | null;
  version: Version;
  hasClip: boolean;
  applyTransform: Transform;
  moveSel: (d: V3) => void;
  doCopy: () => unknown;
  startPaste: () => void;
  /** False hides the whole design's turn and mirror (a mine with components: they would move its cells under them). */
  whole?: boolean;
}) {
  const { studio, sel, version, applyTransform } = p;
  if (!sel && p.whole === false)
    return p.hasClip ? (
      <Act label="Paste [Ctrl+V]" onClick={p.startPaste}>
        <span>paste</span>
      </Act>
    ) : null;
  const what = sel ? 'Selection' : 'Whole design';
  return (
    <div className="flex flex-wrap justify-end gap-1" role="toolbar" aria-label={what}>
      <span className="rounded bg-black/55 px-2 py-1 text-white/70">{what}</span>
      <Act
        label="Rotate 90° counter-clockwise [ [ ]"
        onClick={() => applyTransform('rotate counter-clockwise', (s) => rotate(studio.canvas, s, 3, version))}
      >
        <RotateCcw className="size-3.5" />
      </Act>
      <Act label="Rotate 90° clockwise [ ] ]" onClick={() => applyTransform('rotate clockwise', (s) => rotate(studio.canvas, s, 1, version))}>
        <RotateCw className="size-3.5" />
      </Act>
      <Act label="Mirror east–west" onClick={() => applyTransform('mirror x', (s) => mirrorSel(studio.canvas, s, 'x'))}>
        <FlipHorizontal2 className="size-3.5" />
      </Act>
      <Act label="Mirror north–south" onClick={() => applyTransform('mirror z', (s) => mirrorSel(studio.canvas, s, 'z'))}>
        <FlipVertical2 className="size-3.5" />
      </Act>
      {sel ? <SelectionMoves studio={studio} sel={sel} moveSel={p.moveSel} doCopy={p.doCopy} startPaste={p.startPaste} /> : null}
      {p.hasClip ? (
        <Act label="Paste [Ctrl+V]" onClick={p.startPaste}>
          <span>paste</span>
        </Act>
      ) : null}
    </div>
  );
}

function SelectionMoves(p: {
  studio: Studio;
  sel: Selection;
  moveSel: (d: V3) => void;
  doCopy: () => unknown;
  startPaste: () => void;
}) {
  return (
    <>
      {AXES.map((a) =>
        [-1, 1].map((sgn) => (
          <Act key={a + sgn} label={`Move ${sgn > 0 ? '+' : '−'}${a}`} onClick={() => p.moveSel(unitMove(a, sgn))}>
            <span className="font-mono">
              {sgn > 0 ? '+' : '−'}
              {a}
            </span>
          </Act>
        )),
      )}
      <Act label="Copy [Ctrl+C]" onClick={() => p.doCopy()}>
        <Copy className="size-3.5" />
      </Act>
      <Act label="Duplicate [Ctrl+D]" onClick={() => p.doCopy() && p.startPaste()}>
        <span>dup</span>
      </Act>
      <Act
        label="Delete [Delete]"
        onClick={() => p.studio.edit(deleteSelection(p.studio.canvas, p.sel), 'remove', 'deleted the selection')}
      >
        <Trash2 className="size-3.5" />
      </Act>
    </>
  );
}

/** Fill / walls with the current block, hollow, raise / lower, replace, invert, deselect. */
export function SelectionEdits(p: {
  studio: Studio;
  sel: Selection;
  current: PaletteEntry | null;
  materials: Array<[number, string]>;
  replaceFrom: string;
  setReplaceFrom: (v: string) => void;
}) {
  const { studio, sel, current } = p;
  return (
    <div className="flex flex-wrap justify-end gap-1" role="toolbar" aria-label="Selection edits">
      {current ? <ShapeEdits studio={studio} sel={sel} current={current} /> : null}
      <Act label="Hollow it out" onClick={() => studio.edit(selectionShape(sel, 0, 0, 'hollow'), 'hollow', 'hollowed the selection')}>
        <span>hollow</span>
      </Act>
      <Act label="Raise the top surface" onClick={() => studio.edit(raiseLower(studio.canvas, sel, true), 'raise', 'raised the top surface')}>
        <span>raise</span>
      </Act>
      <Act label="Lower the top surface" onClick={() => studio.edit(raiseLower(studio.canvas, sel, false), 'lower', 'lowered the top surface')}>
        <span>lower</span>
      </Act>
      <ReplaceControl {...p} />
      <Act label="Invert [Ctrl+I]" onClick={() => studio.setSelection(invert(studio.canvas, sel))}>
        <span>invert</span>
      </Act>
      <Act label="Deselect" onClick={() => studio.setSelection(null)}>
        <span>none</span>
      </Act>
    </div>
  );
}

function ShapeEdits({ studio, sel, current }: { studio: Studio; sel: Selection; current: PaletteEntry }) {
  return (
    <>
      <Act
        label={`Fill with ${current.name}`}
        onClick={() =>
          studio.edit(selectionShape(sel, current.id, current.meta, 'fill'), 'fill', `filled the selection with ${current.name}`)
        }
      >
        <span>fill</span>
      </Act>
      <Act
        label={`Walls of ${current.name}`}
        onClick={() => studio.edit(selectionShape(sel, current.id, current.meta, 'walls'), 'walls', `walls of ${current.name}`)}
      >
        <span>walls</span>
      </Act>
    </>
  );
}

function ReplaceControl(p: {
  studio: Studio;
  sel: Selection;
  current: PaletteEntry | null;
  materials: Array<[number, string]>;
  replaceFrom: string;
  setReplaceFrom: (v: string) => void;
}) {
  const { studio, sel, current, replaceFrom, materials } = p;
  const fromName =
    replaceFrom === 'any' ? 'everything' : (materials.find(([i]) => String(i) === replaceFrom)?.[1] ?? replaceFrom);
  const replace = () =>
    current &&
    studio.edit(
      replaceIn(studio.canvas, sel, replaceFrom === 'any' ? 'any' : Number(replaceFrom), current.id, current.meta),
      'replace',
      `replaced ${fromName} with ${current.name}`,
    );
  return (
    <label className="flex items-center gap-1 rounded bg-black/55 px-1.5 py-0.5">
      Replace
      <select
        value={replaceFrom}
        onChange={(e) => p.setReplaceFrom(e.target.value)}
        aria-label="Replace which block"
        className="bg-black/40 text-white"
      >
        <option value="any">all</option>
        {materials.map(([id, name]) => (
          <option key={id} value={String(id)}>
            {name}
          </option>
        ))}
      </select>
      → {current?.name ?? '—'}
      <button type="button" className="rounded bg-white/15 px-1 hover:bg-white/30" disabled={!current} onClick={replace}>
        go
      </button>
    </label>
  );
}

export function SelectAllBar({ studio }: { studio: Studio }) {
  return (
    <div className="flex gap-1">
      <Act label="Select all [Ctrl+A]" onClick={() => studio.setSelection(selectAll(studio.canvas))}>
        <span>select all</span>
      </Act>
    </div>
  );
}

export function AnchorNote({ studio, anchor }: { studio: Studio; anchor: V3 }) {
  return (
    <span className="rounded bg-black/55 px-2 py-1">
      Anchor {anchor.join(', ')}{' '}
      {/* A clear that could not be sent now stays queued and is sent again with the edits (studio.setAnchor). */}
      <button type="button" className="underline" onClick={() => void studio.setAnchor(null).catch(() => undefined)}>
        clear
      </button>
    </span>
  );
}

/** The facing shown in the HUD: the chosen one, or the look's "(look)". */
export function facingText(facing: Dir | null, lookNow: Dir): string {
  return facing ?? `${lookNow} (look)`;
}

// ------------------------------------------------------------------ small controls

export function Seg({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: string[];
  onChange: (v: string) => void;
}) {
  return (
    <div className="flex shrink-0 items-center gap-0.5 rounded bg-black/55 p-0.5" role="radiogroup" aria-label={label}>
      <span className="px-1 text-white/70">{label}</span>
      {options.map((o) => (
        <button
          key={o}
          type="button"
          role="radio"
          aria-checked={value === o}
          onClick={() => onChange(o)}
          className={cn('rounded px-1.5 py-0.5', value === o ? 'bg-accent' : 'hover:bg-white/15')}
        >
          {o}
        </button>
      ))}
    </div>
  );
}

export function Act({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      title={label}
      className="grid h-7 min-w-7 place-items-center rounded bg-black/55 px-1.5 text-white hover:bg-black/75"
    >
      {children}
    </button>
  );
}
