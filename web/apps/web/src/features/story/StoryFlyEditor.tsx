'use client';

import { useCallback, useEffect, useLayoutEffect, useMemo, useState } from 'react';
import { SlidersHorizontal, X } from 'lucide-react';
import type { AimRay, AimTarget } from '@mineclaw/world-viewer';
import type { StoryArchitectEntry, StoryColonyState } from '@mineclaw/contracts';
import { loadWorldViewer } from '@/features/world/integration';
import { PaletteDrawer, type IconSheet } from '../studio/BlockBar';
import { rotateMeta, type Version } from '../studio/facing';
import { pushRecent, type PaletteEntry } from '../studio/palette';
import { hologramCells } from './bodyLook';
import {
  aimEdit,
  editCells,
  editHolograms,
  editPalette,
  editVariants,
  FlyEditQueue,
  type EditSend,
  type PlanOwner,
} from './flyEdit';

export function useStoryFlyEditor(
  state: StoryColonyState | undefined,
  palette: readonly StoryArchitectEntry[] | undefined,
  send: EditSend | undefined,
) {
  const [revision, redraw] = useState(0);
  const colonyId = state?.id;
  // Each queue captures its colony's writer; switching colonies cannot redirect a queued edit into another colony.
  const queue = useMemo(
    () =>
      new FlyEditQueue(
        (cells, id) => (send ? send(cells, id) : Promise.reject(new Error('Plan editing is unavailable'))),
        () => redraw((n) => n + 1),
      ),
    [colonyId], // eslint-disable-line react-hooks/exhaustive-deps
  );
  useLayoutEffect(() => {
    if (send) queue.setSend(send);
  }, [queue, send]);
  const [enabled, setEnabled] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const closePalette = useCallback(() => setPaletteOpen(false), []);
  const [pinned, setPinned] = useState('');
  const version: Version = state?.version === '1.12.2' ? '1.12.2' : 'b1.8.1';
  const entries = useMemo(() => editPalette(palette, version), [palette, version]);
  const [chosen, setChosen] = useState<PaletteEntry | null>(null);
  const entry =
    entries.find((e) => e.id === chosen?.id && e.meta === chosen.meta) ??
    entries.find((e) => e.id === 4) ??
    entries[0] ??
    null;
  const variants = useMemo(() => (entry ? editVariants(entry, version) : []), [entry, version]);
  const [variant, setVariant] = useState(0);
  const meta = variants.find((v) => v.meta === variant)?.meta ?? variants[0]?.meta ?? 0;
  const [aimed, setAimed] = useState<AimTarget | null>(null);
  const [sheet, setSheet] = useState<IconSheet | null>(null);
  useEffect(() => {
    queue.reconcile(state);
  }, [queue, state, revision]);
  useEffect(() => {
    if (pinned && queue.resolve(pinned) !== pinned) setPinned(queue.resolve(pinned));
  }, [queue, revision, pinned]);
  useEffect(() => {
    setEnabled(false);
    setPinned('');
    setAimed(null);
  }, [colonyId]);
  useEffect(() => {
    let alive = true;
    // Warm the shared atlas in small idle slices, before the user opens the palette.
    // Opening the drawer never synchronously renders every block model.
    void Promise.resolve()
      .then(() => loadWorldViewer())
      .then(async (mod) => {
        if (!mod) return;
        const bundle = await mod.loadBundle(version);
        if (!alive) return;
        const atlas = await mod.blockIconAtlasAsync(bundle, entries, 32);
        if (alive) setSheet(atlas);
      })
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, [version, entries]);

  const overlay = useMemo(
    () => editHolograms(hologramCells(state).cells, queue.overlays),
    // The queue mutates in place; its notification revision invalidates this snapshot.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [state, queue, revision],
  );
  // Read the queue directly: a second click can arrive before React commits the first optimistic render.
  const resolve = (physical: AimTarget | null, ray: AimRay) =>
    enabled ? aimEdit(physical, ray, editHolograms(hologramCells(state).cells, queue.overlays)) : physical;
  const ownerOf = (target: Extract<AimTarget, { kind: 'block' }>, extension = false): PlanOwner | undefined => {
    if (extension && pinned) return { id: queue.resolve(pinned), token: pinned };
    const at = [target.x, target.y, target.z];
    const pending = queue.owner(at);
    if (pending) return pending;
    const ghost = state?.ghosts?.blocks.find((g) => g[0] === at[0] && g[1] === at[1] && g[2] === at[2]);
    const id = ghost?.[5] ?? target.plan ?? pinned;
    // Preserve repair items are transient; the edit creates intentional ownership of only these cells.
    if (state?.builds?.some((b) => String(b.id) === String(id) && b.kind === 'preserve')) return undefined;
    return id ? { id: String(id), token: String(id) } : undefined;
  };
  const click = (target: AimTarget | null, button: number) => {
    if (!enabled || !entry || target?.kind !== 'block' || ![0, 2].includes(button)) return;
    const cells = editCells(target, button, entry, meta);
    const height = version === '1.12.2' ? 256 : 128;
    if (cells.some((c) => c[1] < 1 || c[1] >= height)) return;
    queue.enqueue(cells, ownerOf(target, button === 2));
  };
  const preview = enabled && entry && aimed?.kind === 'block' ? editCells(aimed, 2, entry, meta) : [];
  const choose = useCallback(
    (e: PaletteEntry) => {
      setChosen(e);
      setVariant(editVariants(e, version)[0]?.meta ?? e.meta);
      pushRecent(version, e);
      setPaletteOpen(false);
    },
    [version],
  );
  const rotate = () => {
    const turned = entry ? rotateMeta(entry.id, meta, 1, version) : meta;
    setVariant(
      turned !== meta && variants.some((v) => v.meta === turned)
        ? turned
        : (variants[(variants.findIndex((v) => v.meta === meta) + 1) % variants.length]?.meta ?? meta),
    );
  };
  const owned = aimed?.kind === 'block' ? ownerOf(aimed, true) : undefined;
  return {
    enabled,
    setEnabled,
    paletteOpen,
    setPaletteOpen,
    closePalette,
    pinned,
    setPinned,
    entries,
    entry,
    meta,
    setVariant,
    variants,
    aimed,
    setAimed,
    sheet,
    version,
    queue,
    overlay,
    resolve,
    click,
    preview,
    choose,
    rotate,
    owned,
  };
}

export type StoryFlyEditorState = ReturnType<typeof useStoryFlyEditor>;

export function StoryFlyEditor({
  editor: e,
  state,
  flying,
  canEdit,
  onStart,
}: {
  editor: StoryFlyEditorState;
  state: StoryColonyState | undefined;
  flying: boolean;
  canEdit: boolean;
  onStart?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [locked, setLocked] = useState(false);
  useEffect(() => {
    const changed = () => {
      const next = !!document.pointerLockElement;
      setLocked(next);
      if (next) setOpen(false);
    };
    document.addEventListener('pointerlockchange', changed);
    changed();
    return () => document.removeEventListener('pointerlockchange', changed);
  }, []);
  useEffect(() => {
    const key = (ev: KeyboardEvent) => {
      if (!flying || !canEdit || !e.enabled || !document.pointerLockElement || !['KeyR', 'KeyB'].includes(ev.code))
        return;
      ev.preventDefault();
      ev.stopImmediatePropagation();
      if (ev.code === 'KeyR') e.rotate();
      else {
        document.exitPointerLock?.();
        e.setPaletteOpen(true);
      }
    };
    window.addEventListener('keydown', key, true);
    return () => window.removeEventListener('keydown', key, true);
  }, [flying, canEdit, e]);
  if (!flying || !canEdit) return null;
  const unlock = () => document.exitPointerLock?.();
  const title = (id: string) => {
    const b = state?.builds?.find((x) => String(x.id) === id);
    return b?.label || b?.name || (b ? `${b.kind} · ${id}` : id);
  };
  return (
    <>
      {!open && !locked ? (
        <button
          type="button"
          aria-label="Freecam tools"
          aria-expanded={false}
          title={`${e.enabled ? 'Edit plan' : 'Inspect'} · Freecam tools`}
          className="pointer-events-auto absolute top-24 right-3 z-20 grid size-8 place-items-center rounded-lg border border-border bg-surface/95 text-muted shadow-pop hover:text-fg"
          onClick={() => setOpen(true)}
        >
          <SlidersHorizontal className="size-4" />
        </button>
      ) : null}
      {open && !locked ? (
        <div
          className="pointer-events-auto absolute top-24 right-3 z-20 max-w-80 space-y-2 rounded-lg border border-border bg-surface/95 p-3 text-xs shadow-pop"
          data-testid="fly-editor"
        >
          <div className="flex gap-1" role="group" aria-label="Freecam interaction">
            <button
              className="rounded border border-border px-2 py-1"
              aria-pressed={!e.enabled}
              onClick={() => {
                e.setEnabled(false);
                unlock();
              }}
            >
              Inspect
            </button>
            <button
              className="rounded border border-border px-2 py-1"
              aria-pressed={e.enabled}
              onClick={() => {
                e.setEnabled(true);
                onStart?.();
                unlock();
              }}
            >
              Edit plan
            </button>
            <button
              type="button"
              aria-label="Hide freecam tools"
              onClick={() => setOpen(false)}
              className="ml-auto px-1 text-muted hover:text-fg"
            >
              <X className="size-4" />
            </button>
          </div>
          {e.enabled ? (
            <>
              <p>Left removes · Right places · R rotates · B opens blocks. Pawns build these holograms.</p>
              <p className="text-muted">Doors, beds, signs and rails use the Architect tools.</p>
              <button
                className="w-full rounded border border-border bg-surface-2 px-2 py-1 text-left"
                onClick={() => {
                  unlock();
                  setOpen(false);
                  e.setPaletteOpen(true);
                }}
                aria-label="Choose plan material"
              >
                {e.entry?.name ?? 'Choose material'} ▾
              </button>
              <label className="flex items-center gap-2">
                Orientation
                <select
                  className="min-w-0 flex-1 rounded bg-surface-2 p-1"
                  aria-label="Plan block orientation"
                  value={e.meta}
                  onChange={(ev) => e.setVariant(Number(ev.target.value))}
                >
                  {e.variants.map((v) => (
                    <option key={v.meta} value={v.meta}>
                      {v.label}
                    </option>
                  ))}
                </select>
              </label>
              <label className="block">
                Additions belong to
                <select
                  className="mt-1 w-full rounded bg-surface-2 p-1"
                  aria-label="Plan for additions"
                  value={e.pinned}
                  onChange={(ev) => e.setPinned(ev.target.value)}
                >
                  <option value="">Automatic: aimed plan or a new plan</option>
                  {e.pinned && !state?.builds?.some((b) => String(b.id) === e.pinned) ? (
                    <option value={e.pinned}>Selected plan · {e.pinned}</option>
                  ) : null}
                  {(state?.builds ?? [])
                    .filter((b) => b.kind !== 'mine' && b.kind !== 'preserve')
                    .map((b) => (
                      <option key={b.id} value={String(b.id)}>
                        {title(String(b.id))}
                      </option>
                    ))}
                </select>
              </label>
              <p className="text-muted">
                {e.owned?.id
                  ? `Editing ${title(e.owned.id)}`
                  : 'Aim at a block or hologram. Click the world to look around; Esc opens the controls.'}
              </p>
            </>
          ) : (
            <p className="text-muted">Click a chest, furnace or crafting table to inspect it.</p>
          )}
        </div>
      ) : null}
      {e.queue.pending > 0 || e.queue.error ? (
        <div
          className="pointer-events-auto absolute top-36 right-3 z-20 max-w-80 rounded-lg border border-border bg-surface/95 p-2 text-xs shadow-pop"
          style={open && !locked ? { top: 'auto', bottom: '5rem' } : undefined}
        >
          {e.queue.pending > 0 ? (
            <p role="status">
              {e.queue.pending} edit{e.queue.pending === 1 ? '' : 's'} {e.queue.error ? 'waiting' : 'saving…'}
            </p>
          ) : null}
          {e.queue.error ? (
            <div role="alert" className="space-y-1 text-danger">
              <p>Edits are kept locally: {e.queue.error}</p>
              <button className="mr-2 underline" onClick={() => e.queue.retry()}>
                Retry edits
              </button>
              <button className="underline" onClick={() => e.queue.discard()}>
                Discard unsaved edits
              </button>
            </div>
          ) : null}
        </div>
      ) : null}
      {e.enabled && e.paletteOpen ? (
        <PaletteDrawer
          palette={e.entries}
          version={e.version}
          current={e.entry}
          sheet={e.sheet}
          onChoose={e.choose}
          onClose={e.closePalette}
        />
      ) : null}
    </>
  );
}
