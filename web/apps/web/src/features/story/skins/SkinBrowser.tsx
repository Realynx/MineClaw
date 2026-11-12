'use client';

/**
 * The pawn inspector's "Skin Browser" tab: what the pawn wears, and a picker over the shared library (uploads and looked-up
 * players), the default skins (from the user's own imported game assets) and a new upload. A pick is previewed first and
 * applied with "Apply". Skins are a web-view cosmetic: the game server runs in offline mode, so Minecraft clients keep the
 * default appearance.
 */
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { DefaultSkin, PawnSkins, SkinLibraryEntry, SkinRef } from '@mineclaw/contracts';
import { Button, Input } from '@/components/ui/primitives';
import { api, errorText } from '@/lib/api/client';
import { qk, useApi } from '@/lib/api/query';
import { cn } from '@/lib/utils';
import { loadWorldViewer } from '@/features/world/integration';
import { loadDefaultSkin } from './defaultSkins';
import { checkSkinFile, SKIN_MAX_KIB, type CheckedSkin } from './skinFile';
import {
  DEFAULT_SKINS,
  entryOf,
  refLabel,
  sameRef,
  skinOf,
  versionNote,
  visibleLibrary,
  wearers,
} from './skinLibrary';
import { drawSkinFront, FIGURE_H, FIGURE_W, skinBitmap } from './skinPreview';

type Version = 'b1.8.1' | '1.12.2';
type Source = 'library' | 'defaults' | 'upload' | 'lookup';

/** What a figure is drawn from: a base64 PNG, or a default skin from the imported pack. */
type Look = { png: string; height: 32 | 64; slim: boolean } | { base: DefaultSkin; slim: boolean } | null;

function lookOf(skins: PawnSkins | undefined, ref: SkinRef | null): Look {
  if (!ref) return null;
  if (ref.kind === 'default') return { base: ref.id, slim: ref.id === 'alex' };
  const e = entryOf(skins, ref);
  return e ? { png: e.png, height: e.height, slim: e.slim } : null;
}

/** A flat front view on a 2D canvas; a dashed silhouette while loading or when nothing can be drawn. */
export function SkinFigure({ look, version, scale = 3, label }: { look: Look; version: Version; scale?: number; label: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const [drawn, setDrawn] = useState(false);
  useEffect(() => {
    let live = true;
    setDrawn(false);
    const ctx = ref.current?.getContext('2d');
    if (!ctx || !look) return;
    ctx.clearRect(0, 0, FIGURE_W * scale, FIGURE_H * scale);
    void (async () => {
      if ('png' in look) {
        const bitmap = await skinBitmap(look.png).catch(() => null);
        if (live && bitmap) drawSkinFront(ctx, bitmap, { height: look.height, slim: look.slim, scale });
        if (live && bitmap) setDrawn(true);
        return;
      }
      const d = await loadDefaultSkin(version, look.base);
      if (!live || !d) return;
      drawSkinFront(ctx, d.image, { height: d.height, slim: version === '1.12.2' && look.slim, scale });
      setDrawn(true);
    })();
    return () => {
      live = false;
    };
  }, [look, version, scale]);
  return (
    <span className="relative inline-block" style={{ width: FIGURE_W * scale, height: FIGURE_H * scale }}>
      <canvas
        ref={ref}
        width={FIGURE_W * scale}
        height={FIGURE_H * scale}
        role="img"
        aria-label={label}
        className="block [image-rendering:pixelated]"
      />
      {drawn ? null : (
        <span aria-hidden className="absolute inset-x-1 inset-y-0 rounded-sm border border-dashed border-border" />
      )}
    </span>
  );
}

/** Shows the gateway's new view and asks the open world views to pick the change up now. */
function useApplyView() {
  const qc = useQueryClient();
  return (view: PawnSkins) => {
    qc.setQueryData(qk('skins.list'), view);
    void loadWorldViewer().then((m) => m?.refreshHumanSkins());
  };
}

interface Pick {
  ref: SkinRef | null;
  look: Look;
  label: string;
}

export function SkinBrowser({
  pawn,
  display,
  canWrite,
  version,
}: {
  /** The pawn's account name (what world views key players by). */
  pawn: string;
  display: string;
  canWrite: boolean;
  version: Version;
}) {
  const skins = useApi('skins.list');
  const apply = useApplyView();
  const current = skinOf(skins.data, pawn);
  const [source, setSource] = useState<Source>('library');
  const [pick, setPick] = useState<Pick | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const run = async (what: () => Promise<PawnSkins>, done?: string) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      apply(await what());
      if (done) setNotice(done);
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    } finally {
      setBusy(false);
    }
  };
  const assign = (ref: SkinRef | null, label: string) =>
    run(() => api('skins.assign', { body: { pawns: [pawn], skin: ref } }), `${display} now wears ${label}`).then((ok) => {
      if (ok) setPick(null);
    });

  return (
    <div className="space-y-2 text-xs" data-testid="skin-browser">
      <p className="rounded border border-border bg-surface-2 px-2 py-1 text-[11px] text-muted">
        Skins show in MineClaw&apos;s web views only. The game server runs in offline mode, so other Minecraft players still
        see the default skin.
      </p>
      <CurrentSkin
        skins={skins.data}
        current={current}
        pick={pick}
        version={version}
        display={display}
        canWrite={canWrite}
        busy={busy}
        onApply={() => pick && void assign(pick.ref, pick.label)}
        onCancel={() => setPick(null)}
        onReset={() => void assign(null, 'the default appearance')}
      />
      {error ? (
        <p role="alert" className="text-danger">
          {error}
        </p>
      ) : null}
      {notice ? (
        <p role="status" className="text-muted">
          {notice}
        </p>
      ) : null}
      <SourceTabs value={source} onChange={setSource} />
      {skins.error ? <p className="text-danger">{errorText(skins.error)}</p> : null}
      {source === 'library' ? (
        <LibraryGrid
          skins={skins.data}
          current={current}
          pick={pick}
          version={version}
          canWrite={canWrite}
          busy={busy}
          onPick={(e) => setPick({ ref: { kind: 'library', id: e.id }, look: lookOf(skins.data, { kind: 'library', id: e.id }), label: e.label })}
          onDelete={(e) =>
            void run(() => api('skins.delete', { body: { id: e.id } }), `${e.label} removed from the library`).then(() => {
              if (pick?.ref?.kind === 'library' && pick.ref.id === e.id) setPick(null);
            })
          }
        />
      ) : null}
      {source === 'defaults' ? (
        <DefaultsGrid
          current={current}
          pick={pick}
          version={version}
          onPick={(id, label) => setPick({ ref: { kind: 'default', id }, look: { base: id, slim: id === 'alex' }, label })}
        />
      ) : null}
      {source === 'upload' ? (
        <UploadForm
          version={version}
          canWrite={canWrite}
          busy={busy}
          onUpload={(checked, andApply) =>
            void run(async () => {
              const out = await api('skins.upload', { body: { label: checked.label, slim: checked.slim, png: checked.png } });
              if (!andApply) {
                setPick({ ref: { kind: 'library', id: out.skin.id }, look: lookOf(out.skins, { kind: 'library', id: out.skin.id }), label: out.skin.label });
                return out.skins;
              }
              return api('skins.assign', { body: { pawns: [pawn], skin: { kind: 'library', id: out.skin.id } } });
            }, andApply ? `${display} now wears ${checked.label}` : `${checked.label} added to the library`).then((ok) => {
              if (ok) setSource('library');
            })
          }
        />
      ) : null}
      {source === 'lookup' ? (
        <LookupForm
          available={skins.data?.lookup ?? false}
          canWrite={canWrite}
          busy={busy}
          onLookup={(name) =>
            void run(async () => {
              const out = await api('skins.lookup', { body: { name } });
              setPick({ ref: { kind: 'library', id: out.skin.id }, look: lookOf(out.skins, { kind: 'library', id: out.skin.id }), label: out.skin.label });
              return out.skins;
            }, `${name}'s skin added to the library`).then((ok) => {
              if (ok) setSource('library');
            })
          }
        />
      ) : null}
      {!canWrite ? <p className="text-muted">Changing skins needs the Story Mode role.</p> : null}
    </div>
  );
}

function CurrentSkin({
  skins,
  current,
  pick,
  version,
  display,
  canWrite,
  busy,
  onApply,
  onCancel,
  onReset,
}: {
  skins: PawnSkins | undefined;
  current: SkinRef | null;
  pick: Pick | null;
  version: Version;
  display: string;
  canWrite: boolean;
  busy: boolean;
  onApply: () => void;
  onCancel: () => void;
  onReset: () => void;
}) {
  const wearing = refLabel(skins, current);
  const currentLook = useMemo(() => lookOf(skins, current), [skins, current]);
  const changed = pick && !sameRef(pick.ref, current);
  const shown = changed ? pick : null;
  const note = shown && shown.look && 'png' in shown.look ? versionNote(shown.look.height, version) : null;
  return (
    <div className="flex items-start gap-3" data-testid="skin-current">
      <SkinFigure look={currentLook} version={version} label={`${display} wears ${wearing}`} />
      <div className="min-w-0 flex-1 space-y-1">
        <div>
          <span className="text-muted">Wearing </span>
          <span className="font-semibold" data-testid="skin-wearing">
            {wearing}
          </span>
        </div>
        {current && canWrite && !changed ? (
          <Button size="sm" variant="ghost" disabled={busy} onClick={onReset}>
            Use default appearance
          </Button>
        ) : null}
        {shown ? (
          <div className="space-y-1 rounded border border-accent/60 p-1.5" data-testid="skin-preview">
            <div className="flex items-center gap-2">
              <SkinFigure look={shown.look} version={version} scale={2} label={`Preview of ${shown.label}`} />
              <div className="min-w-0">
                <div className="truncate font-semibold">{shown.label}</div>
                <div className="text-muted">Preview, not applied yet</div>
              </div>
            </div>
            {note ? <div className="text-[11px] text-muted">{note}</div> : null}
            <div className="flex gap-1">
              <Button size="sm" variant="primary" disabled={!canWrite || busy} onClick={onApply}>
                Apply to {display}
              </Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>
                Cancel
              </Button>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  );
}

const SOURCES: Array<[Source, string]> = [
  ['library', 'Library'],
  ['defaults', 'Defaults'],
  ['upload', 'Upload'],
  ['lookup', 'Look up'],
];

function SourceTabs({ value, onChange }: { value: Source; onChange: (s: Source) => void }) {
  return (
    <div className="flex gap-0.5 rounded bg-surface-2 p-0.5" role="group" aria-label="Skin sources">
      {SOURCES.map(([id, label]) => (
        <button
          key={id}
          type="button"
          aria-pressed={value === id}
          onClick={() => onChange(id)}
          className={cn(
            'flex-1 rounded px-1.5 py-0.5 font-medium',
            value === id ? 'bg-surface text-fg shadow-sm' : 'text-muted hover:text-fg',
          )}
        >
          {label}
        </button>
      ))}
    </div>
  );
}

function Tile({
  label,
  sub,
  selected,
  wearing,
  onClick,
  children,
  onDelete,
}: {
  label: string;
  sub?: string;
  selected: boolean;
  wearing: boolean;
  onClick: () => void;
  children: React.ReactNode;
  onDelete?: () => void;
}) {
  return (
    <div className="relative">
      <button
        type="button"
        aria-pressed={selected}
        aria-label={`Skin ${label}`}
        onClick={onClick}
        className={cn(
          'flex w-full flex-col items-center gap-0.5 rounded border p-1',
          selected ? 'border-accent bg-accent/10' : 'border-border hover:bg-surface-2',
        )}
      >
        {children}
        <span className="w-full truncate text-center text-[11px]">{label}</span>
        {sub || wearing ? (
          <span className="w-full truncate text-center text-[10px] text-muted" title={sub}>
            {wearing ? ['Wearing', sub?.match(/\d+ pawns$/)?.[0]].filter(Boolean).join(' · ') : sub}
          </span>
        ) : null}
      </button>
      {onDelete ? (
        <button
          type="button"
          aria-label={`Delete ${label}`}
          onClick={onDelete}
          className="absolute top-0.5 right-0.5 rounded px-1 text-[11px] text-muted hover:bg-surface-2 hover:text-danger"
        >
          ×
        </button>
      ) : null}
    </div>
  );
}

function LibraryGrid({
  skins,
  current,
  pick,
  version,
  canWrite,
  busy,
  onPick,
  onDelete,
}: {
  skins: PawnSkins | undefined;
  current: SkinRef | null;
  pick: Pick | null;
  version: Version;
  canWrite: boolean;
  busy: boolean;
  onPick: (e: SkinLibraryEntry) => void;
  onDelete: (e: SkinLibraryEntry) => void;
}) {
  const [search, setSearch] = useState('');
  const [confirm, setConfirm] = useState<string | null>(null);
  const list = useMemo(() => visibleLibrary(skins, search, current), [skins, search, current]);
  const worn = useMemo(() => wearers(skins), [skins]);
  if (!skins) return <p className="text-muted">Loading skins…</p>;
  if (!skins.library.length)
    return <p className="text-muted">No skins yet. Upload one, or look up a player, and it is kept here for every pawn.</p>;
  return (
    <div className="space-y-1.5">
      <Input
        aria-label="Search skins"
        placeholder="Search skins"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        className="h-7 text-xs"
      />
      <div className="grid grid-cols-4 gap-1" data-testid="skin-library">
        {list.map((e) => {
          const ref: SkinRef = { kind: 'library', id: e.id };
          const n = worn.get(e.id) ?? 0;
          return (
            <Tile
              key={e.id}
              label={e.label}
              sub={`${e.source === 'player' ? 'player · ' : ''}${e.height === 32 ? '64×32' : e.slim ? 'slim' : '64×64'}${n ? ` · ${n} pawn${n === 1 ? '' : 's'}` : ''}`}
              selected={sameRef(pick?.ref, ref)}
              wearing={sameRef(current, ref)}
              onClick={() => onPick(e)}
              onDelete={canWrite && !busy ? () => setConfirm(e.id) : undefined}
            >
              <SkinFigure look={{ png: e.png, height: e.height, slim: e.slim }} version={version} scale={2} label={e.label} />
            </Tile>
          );
        })}
      </div>
      {confirm ? (
        <div className="flex items-center gap-1 rounded border border-border p-1.5" role="alertdialog" aria-label="Delete skin">
          <span className="flex-1">
            Delete {skins.library.find((s) => s.id === confirm)?.label}?
            {worn.get(confirm) ? ' Pawns wearing it go back to the default appearance.' : ''}
          </span>
          <Button
            size="sm"
            variant="danger"
            onClick={() => {
              const e = skins.library.find((s) => s.id === confirm);
              setConfirm(null);
              if (e) onDelete(e);
            }}
          >
            Delete
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setConfirm(null)}>
            Keep
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function DefaultsGrid({
  current,
  pick,
  version,
  onPick,
}: {
  current: SkinRef | null;
  pick: Pick | null;
  version: Version;
  onPick: (id: DefaultSkin, label: string) => void;
}) {
  const [imported, setImported] = useState<boolean | null>(null);
  useEffect(() => {
    let live = true;
    void loadDefaultSkin(version, 'steve').then((d) => live && setImported(!!d));
    return () => {
      live = false;
    };
  }, [version]);
  return (
    <div className="space-y-1.5">
      <div className="grid grid-cols-4 gap-1" data-testid="skin-defaults">
        {DEFAULT_SKINS.map((d) => {
          const ref: SkinRef = { kind: 'default', id: d.id };
          return (
            <Tile
              key={d.id}
              label={d.label}
              sub={d.slim ? 'slim' : 'classic'}
              selected={sameRef(pick?.ref, ref)}
              wearing={sameRef(current, ref)}
              onClick={() => onPick(d.id, d.label)}
            >
              <SkinFigure look={{ base: d.id, slim: d.slim }} version={version} scale={2} label={d.label} />
            </Tile>
          );
        })}
      </div>
      <p className="text-[11px] text-muted">
        {imported === false
          ? 'Previews come from your own game assets: import your Minecraft jar in Settings to see them. Pawns still show them in the world views once imported.'
          : 'Drawn from the game assets imported in this browser.'}
        {version === 'b1.8.1' ? ' b1.8.1 has no Alex model, so its world views draw Alex as Steve.' : ''}
      </p>
    </div>
  );
}

function UploadForm({
  version,
  canWrite,
  busy,
  onUpload,
}: {
  version: Version;
  canWrite: boolean;
  busy: boolean;
  onUpload: (checked: CheckedSkin, andApply: boolean) => void;
}) {
  const [checked, setChecked] = useState<CheckedSkin | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const look = useMemo<Look>(() => (checked ? { png: checked.png, height: checked.height, slim: checked.slim } : null), [checked]);
  const note = checked ? versionNote(checked.height, version) : null;
  return (
    <div className="space-y-1.5" data-testid="skin-upload">
      <label className="block">
        <span className="mb-0.5 block text-muted">
          Skin file (PNG, 64×32 or 64×64, up to {SKIN_MAX_KIB} KiB)
        </span>
        <input
          type="file"
          accept="image/png"
          aria-label="Skin file"
          disabled={!canWrite || busy}
          className="block w-full text-xs file:mr-2 file:rounded file:border file:border-border file:bg-surface-2 file:px-2 file:py-0.5"
          onChange={(e) => {
            const file = e.target.files?.[0];
            setChecked(null);
            setProblem(null);
            if (file)
              void checkSkinFile(file).then(setChecked, (err: unknown) =>
                setProblem(err instanceof Error ? err.message : 'That file is not a skin'),
              );
          }}
        />
      </label>
      {problem ? (
        <p role="alert" className="text-danger" data-testid="skin-upload-error">
          {problem}
        </p>
      ) : null}
      {checked ? (
        <div className="flex items-start gap-2">
          <SkinFigure look={look} version={version} scale={3} label="Uploaded skin preview" />
          <div className="min-w-0 flex-1 space-y-1">
            <Input
              aria-label="Skin name"
              value={checked.label}
              maxLength={32}
              onChange={(e) => setChecked({ ...checked, label: e.target.value })}
              className="h-7 text-xs"
            />
            <label className="flex items-center gap-1.5">
              <input
                type="checkbox"
                checked={checked.slim}
                disabled={checked.height === 32}
                onChange={(e) => setChecked({ ...checked, slim: e.target.checked })}
              />
              Slim arms (Alex model)
            </label>
            <div className="text-[11px] text-muted">
              {checked.height === 32 ? '64×32 (b1.8.1 layout)' : '64×64 (1.12.2 layout)'}
              {note ? `. ${note}` : ''}
            </div>
            <div className="flex flex-wrap gap-1">
              <Button size="sm" variant="primary" disabled={busy || !checked.label.trim()} onClick={() => onUpload(checked, true)}>
                Upload and apply
              </Button>
              <Button size="sm" disabled={busy || !checked.label.trim()} onClick={() => onUpload(checked, false)}>
                Add to library
              </Button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function LookupForm({
  available,
  canWrite,
  busy,
  onLookup,
}: {
  available: boolean;
  canWrite: boolean;
  busy: boolean;
  onLookup: (name: string) => void;
}) {
  const [name, setName] = useState('');
  const valid = /^[A-Za-z0-9_]{1,16}$/.test(name);
  return (
    <form
      className="space-y-1.5"
      onSubmit={(e) => {
        e.preventDefault();
        if (valid) onLookup(name);
      }}
    >
      <p className="text-[11px] text-muted">
        Copies a Minecraft player&apos;s official skin into the library. The web server asks Mojang&apos;s profile service; your
        browser never contacts it.
      </p>
      {available ? null : (
        <p className="text-[11px] text-muted" data-testid="skin-lookup-off">
          Off: an admin can turn on Administration → Player skins to allow lookups.
        </p>
      )}
      <div className="flex gap-1">
        <Input
          aria-label="Player name"
          placeholder="Player name"
          value={name}
          maxLength={16}
          onChange={(e) => setName(e.target.value.trim())}
          className="h-7 text-xs"
        />
        <Button size="sm" type="submit" disabled={!available || !canWrite || busy || !valid}>
          Look up
        </Button>
      </div>
    </form>
  );
}
