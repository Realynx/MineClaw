'use client';

/**
 * The Design Studio (docs/web/architecture.md "Design Studio"): a creative-mode build canvas the user co-designs with a model of
 * their choice. The big 3D canvas is the world viewer over an in-memory flat grassland (./studioWorld); the AI builds through
 * the bot's sandbox canvas, the user with Minecraft-style controls and a manual toolkit (selection, transforms, brushes, face
 * tools); every change lands in one history and one op stream. A save is an ordinary design revision.
 *
 * Controls (mouse locked in the world): left breaks / acts, right places / acts, middle picks the block; 1–9 the hotbar, B the
 * palette, R turns the placement facing, T or Enter the chat, Ctrl+Z / Ctrl+Y undo / redo, V select, X box, L line, G brush,
 * F face fill, U extrude, O anchor, P build, K underground; with a selection: arrows / PgUp / PgDn move it (Shift ×5), [ ]
 * rotate, Delete erases, Ctrl+C / Ctrl+V / Ctrl+D copy, paste, duplicate, Ctrl+A select all, Ctrl+I invert.
 *
 * Underground (K): the plot's ground (canvas y -16..-1, grass at -1) becomes editable: breaking, box / line removal, the
 * brush and Delete dig it out (the build digs those cells), and the view cuts away everything above a level (the slider) so
 * the basement can be seen and built from above. Off, the grassland behaves as before (only blocks and dug cells are edited).
 *
 * Leaving with unsaved changes asks first (lib/navGuard: in-app links, back / forward, the command palette; the browser's
 * prompt on reload or close).
 */
import type { AimTarget, WorldViewerHandle } from '@mineclaw/world-viewer';
import type { DesignerCompatReport, DesignerModel, DesignerSession, DesignRevision } from '@mineclaw/contracts';
import { useQueryClient } from '@tanstack/react-query';
import {
  Anchor,
  ArrowUpFromLine,
  Box,
  BoxSelect,
  Copy,
  FlipHorizontal2,
  FlipVertical2,
  Grid3x3,
  Hammer,
  History as HistoryIcon,
  Maximize2,
  PaintBucket,
  Paintbrush,
  Redo2,
  RotateCcw,
  RotateCw,
  Shovel,
  Slash,
  Trash2,
  Undo2,
} from 'lucide-react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { Badge, Button, ErrorState, Input, Spinner } from '@/components/ui/primitives';
import { api, errorText } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { cn, newRequestId } from '@/lib/utils';
import { useNavigationGuard } from '@/lib/navGuard';
import { isTyping } from '@/features/world/directives';
import { Crosshair } from '@/features/world/InWorld';
import { Hotbar, PaletteDrawer, type IconOf, type IconSheet } from './BlockBar';
import {
  boxOf,
  connected,
  DEPTH,
  extend,
  invert,
  maskSelection,
  type Selection,
  selectAll,
  sizeOf,
  type V3,
  type Cell,
} from './canvas';
import { ChatPanel, type ChatPanelHandle } from './ChatPanel';
import { dirOfMcYaw, type Dir, hasFacing, placeMeta, turn, type Version } from './facing';
import { defaultHotbar, entryKey, type PaletteEntry, pushRecent } from './palette';
import { Studio, type StudioApi, type StudioSnapshot } from './studio';
import type * as ViewerMod from './studio-viewer';
import {
  aimHit,
  type Hit,
  boxCells,
  brush,
  type BrushMode,
  type BrushShape,
  brushCells,
  type BoxMode,
  type Clip,
  copy,
  erase,
  extrude,
  floodFace,
  lineCells,
  mirrorSel,
  move,
  pasteCells,
  raiseLower,
  replaceIn,
  rotate,
  selectionShape,
  toggleUnder,
  underCut,
} from './tools';

type Mod = typeof ViewerMod;
type Tool = 'build' | 'box' | 'line' | 'select' | 'brush' | 'face' | 'extrude' | 'anchor';

const TOOLS: Array<{ id: Tool; label: string; key: string; icon: ReactNode }> = [
  { id: 'build', label: 'Build (left breaks, right places)', key: 'P', icon: <Hammer className="size-4" /> },
  {
    id: 'select',
    label: 'Select (drag a box; right-click: connected, Ctrl: whole structure)',
    key: 'V',
    icon: <BoxSelect className="size-4" />,
  },
  { id: 'box', label: 'Box (right-drag fills, left-drag removes)', key: 'X', icon: <Box className="size-4" /> },
  { id: 'line', label: 'Line (right-drag)', key: 'L', icon: <Slash className="size-4" /> },
  { id: 'brush', label: 'Brush (sculpt)', key: 'G', icon: <Paintbrush className="size-4" /> },
  {
    id: 'face',
    label: 'Face fill (right: paint the face area, left: remove it)',
    key: 'F',
    icon: <PaintBucket className="size-4" />,
  },
  {
    id: 'extrude',
    label: 'Extrude (right pushes, left pulls the face by N)',
    key: 'U',
    icon: <ArrowUpFromLine className="size-4" />,
  },
  {
    id: 'anchor',
    label: 'Move origin (click the design’s anchor block)',
    key: 'O',
    icon: <Anchor className="size-4" />,
  },
];

const LAYERS = {
  bots: false,
  entities: false,
  claims: false,
  places: false,
  points: false,
  plans: false,
  dangers: false,
  notes: false,
  weather: false,
  cutaway: false,
  staleness: false,
  changes: false,
  aids: false,
};

/** The API the studio uses, over the typed client. */
const realApi: StudioApi = {
  message: (id, body) => api('designer.message', { params: { id }, body }),
  canvas: (id, body) => api('designer.canvas', { params: { id }, body }),
  save: (id, body) => api('designer.save', { params: { id }, body }),
};

const hitOf = aimHit;

export function StudioPage() {
  const params = useSearchParams();
  const router = useRouter();
  const qc = useQueryClient();
  const { me, can, canChat } = useAuth();
  const canWrite = can('design');
  const designParam = params.get('id') ? Number(params.get('id')) : null;
  const sessionParam = params.get('session') ? Number(params.get('session')) : null;
  const models = useApi('designer.models');
  const [session, setSession] = useState<DesignerSession | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [mod, setMod] = useState<Mod | null>(null);
  const started = useRef(false);

  // Session: reopen ?session=, else a new one (a design's newest revision with ?id=).
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void (async () => {
      try {
        const s = sessionParam
          ? await api('designer.get', { params: { id: sessionParam } })
          : await api('designer.create', { body: designParam ? { designId: designParam } : {} });
        setSession(s);
        if (!sessionParam)
          router.replace(`/designs/studio?session=${s.id}${designParam ? `&id=${designParam}` : ''}`, {
            scroll: false,
          });
      } catch (e) {
        setLoadError(e);
      }
    })();
  }, [designParam, sessionParam, router]);

  useEffect(() => {
    let live = true;
    void import('./studio-viewer').then((m) => live && setMod(m)).catch((e: unknown) => live && setLoadError(e));
    return () => {
      live = false;
    };
  }, []);

  if (loadError) return <ErrorState error={loadError} title="The Design Studio could not open" />;
  if (!session || !mod)
    return (
      <div className="grid h-full place-items-center text-sm text-muted">
        <span className="flex items-center gap-2">
          <Spinner className="size-4" /> Opening the Design Studio…
        </span>
      </div>
    );
  return (
    <StudioBody
      key={`${session.id}:${session.version}`}
      session={session}
      onSession={setSession}
      mod={mod}
      models={models.data?.models ?? []}
      defaultModel={models.data?.default ?? null}
      canWrite={canWrite}
      canChat={canChat}
      player={me.player ?? me.username}
      invalidate={() => void qc.invalidateQueries({ predicate: (q) => q.queryKey[0] === 'designs.list' })}
    />
  );
}

function StudioBody({
  session,
  onSession,
  mod,
  models,
  defaultModel,
  canWrite,
  canChat,
  player,
  invalidate,
}: {
  session: DesignerSession;
  /** A new session state (another version: the body remounts on it). */
  onSession: (s: DesignerSession) => void;
  mod: Mod;
  models: DesignerModel[];
  defaultModel: string | null;
  canWrite: boolean;
  canChat: boolean;
  player: string;
  invalidate: () => void;
}) {
  const version: Version = session.version === '1.12.2' ? '1.12.2' : 'b1.8.1';
  const studio = useMemo(() => new Studio(session, realApi), [session]);
  const world = useMemo(() => new mod.StudioWorld(version, studio.canvas), [mod, version, studio]);
  const overlay = useMemo(() => new mod.Overlay3D(64), [mod]);
  const snap: StudioSnapshot = useSyncExternalStore(studio.subscribe, studio.snapshot, studio.snapshot);
  const viewer = useRef<WorldViewerHandle>(null);
  const chat = useRef<ChatPanelHandle>(null);
  const stage = useRef<HTMLDivElement>(null);
  const [ready, setReady] = useState(false);
  const [aim, setAim] = useState<AimTarget | null>(null);
  const [locked, setLocked] = useState(false);
  const [tool, setTool] = useState<Tool>('build');
  const [boxMode, setBoxMode] = useState<BoxMode>('fill');
  const [brushShape, setBrushShape] = useState<BrushShape>('sphere');
  const [brushSize, setBrushSize] = useState(3);
  const [brushMode, setBrushMode] = useState<BrushMode>('add');
  const [extrudeN, setExtrudeN] = useState(1);
  const [slots, setSlots] = useState<Array<PaletteEntry | null>>(() => defaultHotbar(session.palette));
  const [active, setActive] = useState(0);
  const [facing, setFacing] = useState<Dir | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [hud, setHud] = useState(true);
  const [grid, setGrid] = useState(true);
  /** Underground mode: null = off, else the highest layer still shown (canvas y; everything above it is cut away). */
  const [under, setUnder] = useState<number | null>(null);
  const [withSel, setWithSel] = useState(true);
  const [model, setModel] = useState(session.model || defaultModel || 'main');
  const [clip, setClip] = useState<Clip | null>(null);
  const [pasting, setPasting] = useState(false);
  const [drag, setDrag] = useState<{ button: number; a: V3; dy: number } | null>(null);
  const [fps, setFps] = useState(0);
  const [iconTick, setIconTick] = useState(0);
  const [replaceFrom, setReplaceFrom] = useState<string>('any');
  const [saveName, setSaveName] = useState(session.name === 'new design' ? '' : session.name);
  const [saveSummary, setSaveSummary] = useState('');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<{ designId: number; rev: number; text: string } | null>(
    session.designId ? { designId: session.designId, rev: session.revisions[0]?.rev ?? 0, text: '' } : null,
  );
  const [revisions, setRevisions] = useState<DesignRevision[]>(session.revisions);
  const [notice, setNotice] = useState<string | null>(null);
  const bots = useApi('bots.list', {}, { enabled: canChat });
  const mainBot = bots.data?.bots.find((b) => b.main)?.name ?? bots.data?.bots[0]?.name ?? null;

  // Icons from the bundled textures (loaded once per version); the whole palette drawn into one sheet once the page is idle.
  const bundle = useRef<Awaited<ReturnType<Mod['loadBundle']>> | null>(null);
  const [sheet, setSheet] = useState<IconSheet | null>(null);
  useEffect(() => {
    let live = true;
    let timer: ReturnType<typeof setTimeout> | null = null;
    void mod
      .loadBundle(version)
      .then((b) => {
        if (!live) return;
        bundle.current = b;
        setIconTick((n) => n + 1);
        timer = setTimeout(() => {
          if (!live) return;
          try {
            setSheet(mod.blockIconAtlas(b, session.palette, 32));
          } catch {
            /* no 2D canvas: the cells show placeholders */
          }
        }, 50);
      })
      .catch(() => undefined);
    return () => {
      live = false;
      if (timer) clearTimeout(timer);
    };
  }, [mod, version, session.palette]);
  const hints = useMemo(() => new Map(session.palette.map((e) => [entryKey(e), e.icon ?? null] as const)), [session.palette]);
  const icon: IconOf = useCallback(
    (id, meta, hint) => {
      void iconTick;
      return bundle.current ? mod.blockIconUrl(bundle.current, id, meta, 32, hint ?? hints.get(`${id}:${meta}`) ?? null) : null;
    },
    [mod, iconTick, hints],
  );

  // The game version: a dropdown; an empty canvas switches, a full one is checked and converted on the user's word.
  const [versionAsk, setVersionAsk] = useState<{ target: Version; report: DesignerCompatReport } | null>(null);
  const [versionBusy, setVersionBusy] = useState(false);
  const changeVersion = async (target: Version, apply = false) => {
    if (target === version && !apply) return;
    setVersionBusy(true);
    try {
      await studio.flush();
      const r = await api('designer.version', { params: { id: session.id }, body: { version: target, ...(apply ? { apply } : {}) } });
      if (r.changed && r.session) {
        setVersionAsk(null);
        onSession(r.session);
      } else if (r.report) setVersionAsk({ target, report: r.report });
    } catch (e) {
      setNotice(`Couldn't change the version: ${errorText(e)}`);
    } finally {
      setVersionBusy(false);
    }
  };

  // Everything the input handlers read, current on every render (they are bound once).
  const st = useRef({
    tool,
    boxMode,
    brushShape,
    brushSize,
    brushMode,
    extrudeN,
    slots,
    active,
    facing,
    clip,
    pasting,
    drag,
    snap,
    aim,
    version,
    under,
  });
  useEffect(() => {
    st.current = {
      tool,
      boxMode,
      brushShape,
      brushSize,
      brushMode,
      extrudeN,
      slots,
      active,
      facing,
      clip,
      pasting,
      drag,
      snap,
      aim,
      version,
      under,
    };
  });
  const [lookNow, setLookNow] = useState<Dir>('north');

  useEffect(() => () => studio.dispose(), [studio]);

  // Mock mode only: the e2e reads the canvas and points the camera (the clicks themselves are real input).
  useEffect(() => {
    if (process.env.NEXT_PUBLIC_MOCK !== '1') return;
    (window as unknown as { __studio?: unknown }).__studio = { studio, engine: () => viewer.current?.engine() ?? null };
  }, [studio]);

  // The world follows the canvas; the AI's cells flash as they land.
  useEffect(
    () =>
      studio.onCells((cells, by) => {
        world.push(cells);
        if (by === 'ai') overlay.flashCells(cells, performance.now());
      }),
    [studio, world, overlay],
  );

  // The overlay joins the runtime's scene once the viewer is up; the flash fades on the animation frame.
  useEffect(() => {
    let raf = 0;
    let attached = false;
    const loop = () => {
      const e = viewer.current?.engine();
      if (e && !attached) {
        e.runtime.scene.add(overlay.group);
        attached = true;
        // Start over the plot's south-east corner, looking down at its centre.
        e.rig.flyYaw = 135;
        e.focus(31, 63, 31, { distance: 34, pitch: 38 });
        setReady(true);
      }
      overlay.tick(performance.now());
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => {
      cancelAnimationFrame(raf);
      overlay.dispose();
    };
  }, [overlay]);

  useEffect(() => overlay.setSelection(snap.selection), [overlay, snap.selection]);
  useEffect(() => overlay.setAnchor(snap.anchor), [overlay, snap.anchor]);
  useEffect(() => overlay.setGrid(grid), [overlay, grid]);
  // Underground: the view cuts away every layer above the chosen one (the pick looks through the cut too).
  useEffect(() => {
    const e = ready ? viewer.current?.engine() : null;
    e?.setLevelCut(underCut(under));
  }, [ready, under]);

  // Leaving with unsaved changes asks first (Save and leave / Leave / Stay).
  const saveRef = useRef<() => Promise<boolean>>(() => Promise.resolve(false));
  useNavigationGuard(snap.dirty, { save: () => saveRef.current(), what: "the design's unsaved changes" });

  useEffect(() => {
    const onLock = () =>
      setLocked(
        !!stage.current && !!document.pointerLockElement && stage.current.contains(document.pointerLockElement),
      );
    document.addEventListener('pointerlockchange', onLock);
    return () => document.removeEventListener('pointerlockchange', onLock);
  }, []);

  const current = slots[active] ?? null;
  const look = useCallback((): Dir => facing ?? dirOfMcYaw(viewer.current?.engine()?.rig.flyYaw ?? 0), [facing]);

  // ------------------------------------------------------------------ previews (drag box, brush, paste ghost)

  useEffect(() => {
    const s = st.current;
    const h = hitOf(aim, studio.canvas, s.under !== null);
    if (drag && h) {
      const b = (drag.button === 2 && (tool === 'box' || tool === 'line') ? h.place : (h.hit ?? h.place)) ?? drag.a;
      const box = boxOf(drag.a, b);
      overlay.setDrag(
        tool === 'select' ? { min: box.min, max: [box.max[0], Math.min(47, box.max[1] + drag.dy), box.max[2]] } : box,
      );
    } else overlay.setDrag(null);
    if (tool === 'brush' && h) {
      const c = (s.brushMode === 'add' ? h.place : h.hit) ?? h.place;
      if (c) {
        const cells = brushCells(c, brushSize, brushShape);
        const xs = cells.map((p) => p[0]),
          ys = cells.map((p) => p[1]),
          zs = cells.map((p) => p[2]);
        overlay.setBrush(
          cells.length
            ? {
                min: [Math.min(...xs), Math.min(...ys), Math.min(...zs)],
                max: [Math.max(...xs), Math.max(...ys), Math.max(...zs)],
              }
            : null,
        );
      } else overlay.setBrush(null);
    } else overlay.setBrush(null);
    if (pasting && clip && h?.place) overlay.setGhost(pasteCells(clip, h.place));
    else overlay.setGhost(null);
  }, [aim, drag, tool, brushSize, brushShape, pasting, clip, overlay, studio]);

  // ------------------------------------------------------------------ actions

  const sel = snap.selection;
  const selOrAll = useCallback((): Selection | null => studio.selection ?? selectAll(studio.canvas), [studio]);

  const applyTransform = useCallback(
    (label: string, f: (s: Selection) => { cells: Cell[]; selection: Selection | null }, group?: string) => {
      const s = selOrAll();
      if (!s) return;
      const t = f(s);
      studio.edit(t.cells, label.split(' ')[0]!, label, group);
      if (studio.selection) studio.setSelection(t.selection);
    },
    [studio, selOrAll],
  );

  const moveSel = useCallback(
    (d: V3, group?: string) => applyTransform(`move ${d.join(',')}`, (s) => move(studio.canvas, s, d), group),
    [applyTransform, studio],
  );

  const placeAt = useCallback(
    (p: V3, face: number) => {
      const e = st.current.slots[st.current.active];
      if (!e) return;
      const meta = e.facing || hasFacing(e.id) ? placeMeta(e.id, e.meta, look(), face, st.current.version) : e.meta;
      studio.edit([[p[0], p[1], p[2], e.id, meta]], 'place', `placed ${e.name}`);
    },
    [studio, look],
  );

  const pick = useCallback(
    (h: Hit | null) => {
      if (!h?.hit) return;
      const [id, meta] = studio.canvas.get(...h.hit);
      const entry =
        session.palette.find((e) => e.id === id && e.meta === meta) ?? session.palette.find((e) => e.id === id) ?? null;
      if (!entry) return;
      setSlots((cur) => {
        const at = cur.findIndex((s) => s?.id === entry.id && s.meta === entry.meta);
        if (at >= 0) {
          setActive(at);
          return cur;
        }
        const next = [...cur];
        next[st.current.active] = entry;
        return next;
      });
    },
    [studio, session.palette],
  );

  const stroke = useRef<{
    button: number;
    group: string;
    target?: number;
    timer: ReturnType<typeof setInterval> | null;
  } | null>(null);

  const brushAt = useCallback(
    (h: Hit | null, button: number, group: string, target?: number) => {
      if (!h) return;
      const s = st.current;
      const e = s.slots[s.active];
      const mode: BrushMode = button === 0 ? 'remove' : s.brushMode;
      const centre = (mode === 'add' ? h.place : h.hit) ?? h.place;
      if (!centre) return;
      studio.edit(
        brush(
          studio.canvas,
          centre,
          s.brushSize,
          s.brushShape,
          mode,
          e?.id ?? 4,
          e?.meta ?? 0,
          target,
          s.under !== null,
        ),
        'brush',
        `brush ${mode}`,
        group,
      );
    },
    [studio],
  );

  const down = useCallback(
    async (button: number, mods: { shift: boolean; ctrl: boolean }) => {
      const t = (await viewer.current?.engine()?.aimNow()) ?? null;
      const s = st.current;
      const h = hitOf(t, studio.canvas, s.under !== null);
      if (!h) return;
      if (button === 1) return pick(h);
      if (s.pasting && s.clip) {
        if (h.place) studio.edit(pasteCells(s.clip, h.place), 'paste', `pasted ${s.clip.cells.length} blocks`);
        if (!mods.shift) setPasting(false);
        return;
      }
      const e = s.slots[s.active];
      switch (s.tool) {
        case 'build':
          if (button === 0 && h.hit) studio.edit([[...h.hit, 0, 0]], 'remove', `broke ${studio.canvas.id(...h.hit)}`);
          if (button === 2 && h.place) placeAt(h.place, h.face);
          return;
        case 'box':
        case 'line':
        case 'select': {
          if (s.tool === 'select' && button === 2) {
            if (!h.hit) return;
            const mask = connected(studio.canvas, h.hit, !mods.ctrl);
            studio.setSelection(mask.size ? maskSelection(mask) : null);
            return;
          }
          const a = button === 2 ? h.place : (h.hit ?? h.place);
          if (a) setDrag({ button, a, dy: 0 });
          return;
        }
        case 'brush': {
          const group = `brush-${Date.now()}`;
          const target = h.hit ? studio.canvas.id(...h.hit) : undefined;
          brushAt(h, button, group, target);
          const timer = setInterval(() => {
            void viewer.current
              ?.engine()
              ?.aimNow()
              .then((tt) => brushAt(hitOf(tt, studio.canvas, st.current.under !== null), button, group, target));
          }, 140);
          stroke.current = { button, group, target, timer };
          return;
        }
        case 'face':
          if (!h.hit) return;
          studio.edit(
            floodFace(studio.canvas, h.hit, h.face, button === 0 ? 0 : (e?.id ?? 4), button === 0 ? 0 : (e?.meta ?? 0)),
            'face',
            button === 0 ? 'removed a face area' : `painted a face area ${e?.name ?? ''}`,
          );
          return;
        case 'extrude':
          if (!h.hit) return;
          studio.edit(
            extrude(studio.canvas, h.hit, h.face, button === 0 ? -s.extrudeN : s.extrudeN),
            'extrude',
            `${button === 0 ? 'pulled' : 'pushed'} a face by ${s.extrudeN}`,
          );
          return;
        case 'anchor': {
          const p = h.hit ?? h.place;
          if (p) void studio.setAnchor(p).catch((err: unknown) => setNotice(errorText(err)));
          return;
        }
      }
    },
    [studio, pick, placeAt, brushAt],
  );

  const up = useCallback(
    async (button: number, mods: { shift: boolean }) => {
      if (stroke.current && stroke.current.button === button) {
        if (stroke.current.timer) clearInterval(stroke.current.timer);
        stroke.current = null;
      }
      const d = st.current.drag;
      if (!d || d.button !== button) return;
      setDrag(null);
      const t = (await viewer.current?.engine()?.aimNow()) ?? null;
      const s = st.current;
      const h = hitOf(t, studio.canvas, s.under !== null);
      const e = s.slots[s.active];
      const b = (button === 2 ? h?.place : (h?.hit ?? h?.place)) ?? d.a;
      if (s.tool === 'select') {
        const top: V3 = [b[0], Math.min(47, Math.max(b[1], d.a[1]) + d.dy), b[2]];
        studio.setSelection(mods.shift ? extend(studio.selection, d.a, top) : boxOf(d.a, top));
        return;
      }
      if (s.tool === 'box') {
        if (button === 2 && e)
          studio.edit(
            boxCells(d.a, b, e.id, e.meta, s.boxMode),
            s.boxMode,
            `${s.boxMode} ${sizeOf(boxOf(d.a, b)).join('×')} ${e.name}`,
          );
        if (button === 0)
          studio.edit(boxCells(d.a, b, 0, 0, 'fill'), 'remove', `removed ${sizeOf(boxOf(d.a, b)).join('×')}`);
      }
      if (s.tool === 'line') {
        if (button === 2 && e) studio.edit(lineCells(d.a, b, e.id, e.meta), 'line', `line of ${e.name}`);
        if (button === 0) studio.edit(lineCells(d.a, b, 0, 0), 'remove', 'removed a line');
      }
    },
    [studio],
  );

  // Mouse: with the mouse locked the studio takes every button (the viewer never picks); unlocked, the gizmo can be dragged.
  useEffect(() => {
    if (!ready) return;
    const e = viewer.current?.engine();
    if (!e) return;
    const c = e.surface;
    let giz: {
      axis: 'x' | 'y' | 'z';
      x0: number;
      y0: number;
      dir: [number, number];
      px: number;
      applied: number;
      group: string;
    } | null = null;
    const onDown = (ev: PointerEvent) => {
      if (document.pointerLockElement === c) {
        ev.stopPropagation();
        ev.preventDefault();
        void down(ev.button, { shift: ev.shiftKey, ctrl: ev.ctrlKey || ev.metaKey });
        return;
      }
      if (ev.button !== 0 || !studio.selection) return;
      const r = c.getBoundingClientRect();
      const ndc = new mod.Vector2(
        ((ev.clientX - r.left) / r.width) * 2 - 1,
        -((ev.clientY - r.top) / r.height) * 2 + 1,
      );
      const ray = new mod.Raycaster();
      ray.setFromCamera(ndc, e.camera);
      const axis = overlay.pickHandle(ray);
      if (!axis) return;
      ev.stopPropagation();
      ev.preventDefault();
      const o = overlay.gizmo.position.clone();
      const unit = new mod.Vector3(axis === 'x' ? 1 : 0, axis === 'y' ? 1 : 0, axis === 'z' ? 1 : 0);
      const p0 = o.clone().project(e.camera),
        p1 = o.clone().add(unit).project(e.camera);
      const dx = ((p1.x - p0.x) * r.width) / 2,
        dy = (-(p1.y - p0.y) * r.height) / 2;
      const len = Math.hypot(dx, dy) || 1;
      giz = {
        axis,
        x0: ev.clientX,
        y0: ev.clientY,
        dir: [dx / len, dy / len],
        px: len,
        applied: 0,
        group: `gizmo-${Date.now()}`,
      };
    };
    const onMove = (ev: PointerEvent) => {
      if (!giz) return;
      const steps = Math.round(((ev.clientX - giz.x0) * giz.dir[0] + (ev.clientY - giz.y0) * giz.dir[1]) / giz.px);
      const diff = steps - giz.applied;
      if (diff === 0) return;
      giz.applied = steps;
      const d: V3 = [giz.axis === 'x' ? diff : 0, giz.axis === 'y' ? diff : 0, giz.axis === 'z' ? diff : 0];
      moveSel(d, giz.group);
    };
    const onUp = (ev: PointerEvent) => {
      if (giz) {
        giz = null;
        return;
      }
      if (document.pointerLockElement === c) {
        ev.stopPropagation();
        void up(ev.button, { shift: ev.shiftKey });
      }
    };
    const onAux = (ev: MouseEvent) => ev.button === 1 && ev.preventDefault();
    const onWheel = (ev: WheelEvent) => {
      const s = st.current;
      if (s.tool !== 'select') return;
      if (s.drag) {
        ev.preventDefault();
        ev.stopPropagation();
        setDrag((d) => (d ? { ...d, dy: Math.max(-47, Math.min(47, d.dy + (ev.deltaY < 0 ? 1 : -1))) } : d));
      } else if (studio.selection) {
        ev.preventDefault();
        ev.stopPropagation();
        const cur = studio.selection;
        studio.setSelection({
          ...cur,
          mask: null,
          max: [cur.max[0], Math.max(cur.min[1], Math.min(47, cur.max[1] + (ev.deltaY < 0 ? 1 : -1))), cur.max[2]],
        });
      }
    };
    c.addEventListener('pointerdown', onDown, true);
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp, true);
    c.addEventListener('mousedown', onAux);
    c.addEventListener('wheel', onWheel, { capture: true, passive: false });
    return () => {
      c.removeEventListener('pointerdown', onDown, true);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp, true);
      c.removeEventListener('mousedown', onAux);
      c.removeEventListener('wheel', onWheel, { capture: true });
    };
  }, [ready, down, up, studio, overlay, mod, moveSel]);

  const openChat = useCallback(() => {
    if (document.pointerLockElement) document.exitPointerLock();
    chat.current?.focus();
  }, []);
  const backToWorld = useCallback(() => viewer.current?.engine()?.surface.focus(), []);

  const doCopy = useCallback(() => {
    const s = studio.selection;
    const c = s ? copy(studio.canvas, s) : null;
    if (c) setClip(c);
    return c;
  }, [studio]);

  // Keys (capture phase: arrows / PgUp / PgDn move the selection instead of the camera while there is one).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isTyping(e.target)) return;
      const el = stage.current;
      const inWorld =
        !!el &&
        (el.contains(document.activeElement) ||
          (!!document.pointerLockElement && el.contains(document.pointerLockElement)));
      if (!inWorld) return;
      const ctrl = e.ctrlKey || e.metaKey;
      const s = st.current;
      const key = e.key.toLowerCase();
      const stop = () => {
        e.preventDefault();
        e.stopPropagation();
      };
      if (ctrl) {
        const act: Record<string, (() => void) | undefined> = {
          z: e.shiftKey ? () => studio.redo() : () => studio.undo(),
          y: () => studio.redo(),
          c: () => void doCopy(),
          v: s.clip ? () => setPasting(true) : undefined,
          d: () => {
            if (doCopy()) setPasting(true);
          },
          a: () => studio.setSelection(selectAll(studio.canvas)),
          i: () => studio.setSelection(invert(studio.canvas, studio.selection)),
        };
        const f = act[key];
        if (f) {
          stop();
          f();
        }
        return;
      }
      if (studio.selection && !s.drag && (e.key.startsWith('Arrow') || e.key === 'PageUp' || e.key === 'PageDown')) {
        stop();
        const n = e.shiftKey ? 5 : 1;
        const d: V3 =
          e.key === 'ArrowLeft'
            ? [-n, 0, 0]
            : e.key === 'ArrowRight'
              ? [n, 0, 0]
              : e.key === 'ArrowUp'
                ? [0, 0, -n]
                : e.key === 'ArrowDown'
                  ? [0, 0, n]
                  : e.key === 'PageUp'
                    ? [0, n, 0]
                    : [0, -n, 0];
        moveSel(d);
        return;
      }
      if (s.drag && (e.key === 'PageUp' || e.key === 'PageDown')) {
        stop();
        setDrag((d) => (d ? { ...d, dy: d.dy + (e.key === 'PageUp' ? 1 : -1) } : d));
        return;
      }
      if (/^Digit[1-9]$/.test(e.code)) return setActive(Number(e.code.slice(5)) - 1);
      if (e.code === 'KeyT' || e.key === 'Enter' || e.key === '/') {
        stop();
        return openChat();
      }
      if (e.key === 'Tab') {
        stop();
        return setHud((v) => !v);
      }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (studio.selection) {
          stop();
          studio.edit(erase(studio.canvas, studio.selection, s.under !== null), 'remove', 'deleted the selection');
        }
        return;
      }
      if (e.key === 'Escape') {
        if (s.pasting) setPasting(false);
        else if (s.drag) setDrag(null);
        return;
      }
      if (e.key === '[' || e.key === ']')
        return applyTransform(`rotate ${e.key === ']' ? 'clockwise' : 'counter-clockwise'}`, (x) =>
          rotate(studio.canvas, x, e.key === ']' ? 1 : 3, s.version),
        );
      const tools: Record<string, Tool> = {
        KeyP: 'build',
        KeyV: 'select',
        KeyX: 'box',
        KeyL: 'line',
        KeyG: 'brush',
        KeyF: 'face',
        KeyU: 'extrude',
        KeyO: 'anchor',
      };
      if (tools[e.code]) return setTool(tools[e.code]!);
      if (e.code === 'KeyR')
        return setFacing((f) => turn(f ?? dirOfMcYaw(viewer.current?.engine()?.rig.flyYaw ?? 0), e.shiftKey ? -1 : 1));
      if (e.code === 'KeyB') {
        e.preventDefault(); // the "b" never lands in the palette's search box that opens with it
        if (document.pointerLockElement) document.exitPointerLock();
        return setPaletteOpen((v) => !v);
      }
      if (e.code === 'KeyH') return setGrid((v) => !v);
      if (e.code === 'KeyK') return setUnder(toggleUnder);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [studio, moveSel, applyTransform, openChat, doCopy]);

  // ------------------------------------------------------------------ chat, save, build

  const send = useCallback(
    (text: string) => {
      void studio.send(text, { model, withSelection: withSel }).catch(() => undefined);
    },
    [studio, model, withSel],
  );

  const doSave = useCallback(
    async (asNew: boolean): Promise<boolean> => {
      if (!saveName.trim()) {
        setNotice('Give the design a name first.');
        return false;
      }
      setSaving(true);
      setNotice(null);
      try {
        const r = await studio.save(saveName, saveSummary, asNew);
        setSaved({ designId: r.designId, rev: r.rev, text: r.text });
        setSaveSummary('');
        invalidate();
        const fresh = await api('designer.get', { params: { id: studio.id } }).catch(() => null);
        if (fresh) setRevisions(fresh.revisions);
        return true;
      } catch (e) {
        setNotice(errorText(e));
        return false;
      } finally {
        setSaving(false);
      }
    },
    [studio, saveName, saveSummary, invalidate],
  );
  useEffect(() => {
    saveRef.current = () => (canWrite ? doSave(false) : Promise.resolve(false));
  }, [canWrite, doSave]);

  const askBot = useCallback(async () => {
    if (!saved || !mainBot) return;
    try {
      const r = await api('bots.chat', {
        params: { bot: mainBot },
        body: {
          text: `please build a copy of design #${saved.designId} (${snap.name}) from the Design Studio near me`,
          requestId: newRequestId(),
        },
      });
      setNotice(
        r.accepted ? `Asked ${mainBot} to build design #${saved.designId}.` : `${mainBot} did not take the line.`,
      );
    } catch (e) {
      setNotice(errorText(e));
    }
  }, [saved, mainBot, snap.name]);

  // ------------------------------------------------------------------ render

  const h = hitOf(aim, studio.canvas, under !== null);
  const selSize = sel ? sizeOf(sel) : null;
  const selBlocks = useMemo(
    () =>
      sel
        ? studio.canvas
            .cells()
            .filter(
              (c) =>
                c[0] >= sel.min[0] &&
                c[0] <= sel.max[0] &&
                c[1] >= sel.min[1] &&
                c[1] <= sel.max[1] &&
                c[2] >= sel.min[2] &&
                c[2] <= sel.max[2],
            ).length
        : 0,
    [sel, studio, snap.rev, snap.blocks],
  ); // eslint-disable-line react-hooks/exhaustive-deps
  const bounds = useMemo(() => studio.canvas.bounds(), [studio, snap.blocks, snap.rev]); // eslint-disable-line react-hooks/exhaustive-deps
  const over = !!bounds && sizeOf(bounds).some((n) => n > 32);
  const selMaterials = useMemo(() => {
    const ids = new Map<number, string>();
    if (sel)
      for (const c of studio.canvas.cells())
        if (
          c[0] >= sel.min[0] &&
          c[0] <= sel.max[0] &&
          c[1] >= sel.min[1] &&
          c[1] <= sel.max[1] &&
          c[2] >= sel.min[2] &&
          c[2] <= sel.max[2]
        )
          ids.set(c[3], session.palette.find((p) => p.id === c[3])?.name ?? `#${c[3]}`);
    return [...ids];
  }, [sel, studio, session.palette, snap.rev]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div
      className="flex h-full min-h-0 flex-col"
      data-testid="studio"
      data-blocks={snap.blocks}
      data-rev={snap.rev}
      data-dirty={snap.dirty ? '1' : '0'}
      data-under={under ?? ''}
    >
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
            disabled={!canWrite || versionBusy || snap.busy}
            onChange={(e) => void changeVersion(e.target.value as Version)}
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
        {session.serverVersion && session.serverVersion !== version ? (
          <span
            className="rounded bg-warn-soft px-1.5 py-0.5 text-[11px] text-warn"
            title={`Drawn for ${version}; the server runs ${session.serverVersion}. A build there converts what maps and refuses blocks it doesn't have.`}
          >
            server is {session.serverVersion}
          </span>
        ) : null}
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <div className="w-40">
            <Input
              value={saveName}
              onChange={(e) => setSaveName(e.target.value)}
              placeholder="Design name"
              aria-label="Design name"
              className="h-8"
              maxLength={48}
            />
          </div>
          <div className="w-56">
            <Input
              value={saveSummary}
              onChange={(e) => setSaveSummary(e.target.value)}
              placeholder="What changed (revision message)"
              aria-label="Revision message"
              className="h-8"
              maxLength={120}
            />
          </div>
          <Button
            size="sm"
            onClick={() => void doSave(false)}
            disabled={!canWrite || saving || snap.blocks === 0}
            loading={saving}
          >
            {snap.designId ? 'Save as new revision' : 'Save'}
          </Button>
          {snap.designId ? (
            <Button
              size="sm"
              variant="secondary"
              onClick={() => void doSave(true)}
              disabled={!canWrite || saving || snap.blocks === 0}
            >
              Save as new design
            </Button>
          ) : null}
          {revisions.length ? (
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
          ) : null}
        </div>
      </header>
      {saved?.designId ? (
        <div
          className="flex flex-wrap items-center gap-2 border-b border-border bg-ok-soft px-3 py-1.5 text-xs text-ok"
          data-testid="saved-bar"
        >
          <span>{saved.text || `Design #${saved.designId}${saved.rev ? ` r${saved.rev}` : ''}`}</span>
          <span className="ml-auto" />
          <Link
            href={`/story?design=${saved.designId}`}
            className="rounded-md bg-surface px-2 py-1 text-fg hover:bg-surface-2"
            title="Opens Story Mode with a build placement for this design"
          >
            Build in Story Mode
          </Link>
          <Button
            size="sm"
            variant="secondary"
            onClick={() => void askBot()}
            disabled={!canChat || !mainBot}
            title={
              canChat ? `Ask ${mainBot ?? 'the bot'} in chat to build it` : 'Your account cannot chat with the bots'
            }
          >
            Ask the bot to build it
          </Button>
          <Link href={`/designs?id=${saved.designId}`} className="text-ok underline">
            Open in Designs
          </Link>
        </div>
      ) : null}
      {notice ? (
        <p className="border-b border-border bg-info-soft px-3 py-1 text-xs text-info" role="status">
          {notice}
        </p>
      ) : null}
      {versionAsk ? (
        <div
          role="dialog"
          aria-label="Change the version"
          className="flex flex-col gap-2 border-b border-border bg-warn-soft px-3 py-2 text-xs text-fg"
          data-testid="version-dialog"
        >
          <p>
            Convert this design from <b>{version}</b> to <b>{versionAsk.target}</b>?{' '}
            <span className="text-muted">
              {versionAsk.report.same} block{versionAsk.report.same === 1 ? '' : 's'} stay the same.
            </span>
          </p>
          {versionAsk.report.substituted.length ? (
            <p>
              Becomes:{' '}
              {versionAsk.report.substituted.map((s) => `${s.count} ${s.name}`).join(', ')}
            </p>
          ) : null}
          {versionAsk.report.missing.length ? (
            <p className="text-warn">
              Not in {versionAsk.target} (left out):{' '}
              {versionAsk.report.missing.map((s) => `${s.count} ${s.name}`).join(', ')}
            </p>
          ) : null}
          <div className="flex gap-2">
            <Button size="sm" onClick={() => void changeVersion(versionAsk.target, true)} loading={versionBusy}>
              Convert to {versionAsk.target}
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setVersionAsk(null)} disabled={versionBusy}>
              Cancel
            </Button>
          </div>
        </div>
      ) : null}
      <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <div ref={stage} className="relative min-h-[420px] min-w-0 flex-1 bg-black" data-testid="studio-stage">
          <mod.WorldViewer
            ref={viewer}
            source={world.source}
            cameraMode="fly"
            xray="off"
            drawDistance={96}
            layers={LAYERS}
            hideHint
            onAim={(t) => {
              setAim(t);
              const yaw = viewer.current?.engine()?.rig.flyYaw;
              if (yaw !== undefined) setLookNow(dirOfMcYaw(yaw));
            }}
            onStats={(s) => setFps(Math.round(s.fps))}
            className="absolute inset-0"
          />
          {locked ? <Crosshair /> : null}
          {!locked && ready ? (
            <div className="pointer-events-none absolute bottom-24 left-1/2 -translate-x-1/2 rounded bg-black/55 px-2.5 py-1 font-mono text-[11px] whitespace-nowrap text-white">
              Click to build · WASD fly · Space/E up · Q down · B blocks · T chat · Ctrl+Z undo
            </div>
          ) : null}
          {hud ? (
            <div
              className="pointer-events-none absolute top-3 left-14 space-y-1 font-mono text-[11px] text-white"
              data-testid="studio-hud"
            >
              <div className="rounded bg-black/55 px-2 py-0.5">
                {h?.hit
                  ? `${h.hit.join(', ')}${h.hit[1] < 0 ? ' (underground)' : ''}`
                  : h?.place
                    ? `ground · ${h.place.join(', ')}`
                    : '—'}{' '}
                · facing {facing ?? `${lookNow} (look)`} · {TOOLS.find((t) => t.id === tool)?.label.split(' (')[0]}
                {tool === 'box'
                  ? ` (${boxMode})`
                  : tool === 'brush'
                    ? ` (${brushMode} ${brushShape} ${brushSize})`
                    : tool === 'extrude'
                      ? ` (${extrudeN})`
                      : ''}
              </div>
              <div className="rounded bg-black/55 px-2 py-0.5">
                {snap.blocks} blocks{bounds ? ` · ${sizeOf(bounds).join('×')}` : ''} · r{snap.rev}
                {snap.pending ? ` · ${snap.pending} unsent` : ''} · {fps} fps
              </div>
              {over ? (
                <div className="rounded bg-danger px-2 py-0.5">over 32 a side: too big to save as one design</div>
              ) : null}
              {sel && selSize ? (
                <div className="rounded bg-[#0369a1]/80 px-2 py-0.5" data-testid="selection-hud">
                  Selection {selSize[0]}×{selSize[1]}×{selSize[2]} · {sel.mask ? sel.mask.size : selBlocks} blocks
                </div>
              ) : null}
              {pasting ? (
                <div className="rounded bg-[#a16207]/80 px-2 py-0.5">
                  Pasting: click to place (Shift keeps pasting) · Esc cancels
                </div>
              ) : null}
            </div>
          ) : null}
          {/* Tools */}
          <div className="absolute top-3 left-3 flex flex-col gap-1" role="toolbar" aria-label="Tools">
            {TOOLS.map((t) => (
              <button
                key={t.id}
                type="button"
                onClick={() => setTool(t.id)}
                aria-pressed={tool === t.id}
                aria-label={t.label}
                title={`${t.label} [${t.key}]`}
                className={cn(
                  'grid size-8 place-items-center rounded text-white',
                  tool === t.id ? 'bg-accent' : 'bg-black/55 hover:bg-black/75',
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
              onClick={() => setGrid((v) => !v)}
              aria-pressed={grid}
              aria-label="Grid"
              title="Grid [H]"
              className="grid size-8 place-items-center rounded bg-black/55 text-white"
            >
              <Grid3x3 className="size-4" />
            </button>
            <button
              type="button"
              onClick={() => setUnder(toggleUnder)}
              aria-pressed={under !== null}
              aria-label="Underground"
              title="Underground: dig the ground and see below it [K]"
              className={cn(
                'grid size-8 place-items-center rounded text-white',
                under !== null ? 'bg-accent' : 'bg-black/55 hover:bg-black/75',
              )}
            >
              <Shovel className="size-4" />
            </button>
            <button
              type="button"
              onClick={() => void stage.current?.requestFullscreen?.().catch(() => undefined)}
              aria-label="Full screen"
              className="grid size-8 place-items-center rounded bg-black/55 text-white"
            >
              <Maximize2 className="size-4" />
            </button>
          </div>
          {/* Tool options and the selection's actions */}
          <div className="absolute top-3 right-3 flex max-w-[52%] flex-col items-end gap-1.5 text-[11px] text-white">
            {under !== null ? (
              <label
                className="flex items-center gap-1.5 rounded bg-black/55 px-2 py-1"
                title="Layers above this one are cut away"
              >
                Underground: show to y
                <input
                  type="range"
                  min={-DEPTH}
                  max={0}
                  value={under}
                  onChange={(e) => setUnder(Number(e.target.value))}
                  aria-label="Underground level"
                />
                <span className="w-6 text-right tabular-nums">{under}</span>
              </label>
            ) : null}
            {tool === 'box' ? (
              <Seg
                label="Box"
                value={boxMode}
                options={['fill', 'hollow', 'walls', 'outline']}
                onChange={(v) => setBoxMode(v as BoxMode)}
              />
            ) : null}
            {tool === 'brush' ? (
              <div className="flex flex-wrap items-center justify-end gap-1.5">
                <Seg
                  label="Brush"
                  value={brushMode}
                  options={['add', 'remove', 'paint', 'replace', 'smooth']}
                  onChange={(v) => setBrushMode(v as BrushMode)}
                />
                <Seg
                  label="Shape"
                  value={brushShape}
                  options={['sphere', 'cube']}
                  onChange={(v) => setBrushShape(v as BrushShape)}
                />
                <label className="flex items-center gap-1 rounded bg-black/55 px-2 py-1">
                  Size
                  <input
                    type="range"
                    min={1}
                    max={8}
                    value={brushSize}
                    onChange={(e) => setBrushSize(Number(e.target.value))}
                    aria-label="Brush size"
                  />
                  {brushSize}
                </label>
              </div>
            ) : null}
            {tool === 'extrude' ? (
              <label className="flex items-center gap-1 rounded bg-black/55 px-2 py-1">
                By
                <input
                  type="number"
                  min={1}
                  max={16}
                  value={extrudeN}
                  onChange={(e) => setExtrudeN(Math.max(1, Math.min(16, Number(e.target.value) || 1)))}
                  className="w-12 bg-transparent"
                  aria-label="Extrude by"
                />
              </label>
            ) : null}
            <div
              className="flex flex-wrap justify-end gap-1"
              role="toolbar"
              aria-label={sel ? 'Selection' : 'Whole design'}
            >
              <span className="rounded bg-black/55 px-2 py-1 text-white/70">{sel ? 'Selection' : 'Whole design'}</span>
              <Act
                label="Rotate 90° counter-clockwise [ [ ]"
                onClick={() => applyTransform('rotate counter-clockwise', (s) => rotate(studio.canvas, s, 3, version))}
              >
                <RotateCcw className="size-3.5" />
              </Act>
              <Act
                label="Rotate 90° clockwise [ ] ]"
                onClick={() => applyTransform('rotate clockwise', (s) => rotate(studio.canvas, s, 1, version))}
              >
                <RotateCw className="size-3.5" />
              </Act>
              <Act
                label="Mirror east–west"
                onClick={() => applyTransform('mirror x', (s) => mirrorSel(studio.canvas, s, 'x'))}
              >
                <FlipHorizontal2 className="size-3.5" />
              </Act>
              <Act
                label="Mirror north–south"
                onClick={() => applyTransform('mirror z', (s) => mirrorSel(studio.canvas, s, 'z'))}
              >
                <FlipVertical2 className="size-3.5" />
              </Act>
              {sel ? (
                <>
                  {(['x', 'y', 'z'] as const).map((a) =>
                    [-1, 1].map((sgn) => (
                      <Act
                        key={a + sgn}
                        label={`Move ${sgn > 0 ? '+' : '−'}${a}`}
                        onClick={() => moveSel([a === 'x' ? sgn : 0, a === 'y' ? sgn : 0, a === 'z' ? sgn : 0])}
                      >
                        <span className="font-mono">
                          {sgn > 0 ? '+' : '−'}
                          {a}
                        </span>
                      </Act>
                    )),
                  )}
                  <Act label="Copy [Ctrl+C]" onClick={() => doCopy()}>
                    <Copy className="size-3.5" />
                  </Act>
                  <Act label="Duplicate [Ctrl+D]" onClick={() => doCopy() && setPasting(true)}>
                    <span>dup</span>
                  </Act>
                  <Act
                    label="Delete [Delete]"
                    onClick={() => studio.edit(erase(studio.canvas, sel), 'remove', 'deleted the selection')}
                  >
                    <Trash2 className="size-3.5" />
                  </Act>
                </>
              ) : null}
              {clip ? (
                <Act label="Paste [Ctrl+V]" onClick={() => setPasting(true)}>
                  <span>paste</span>
                </Act>
              ) : null}
            </div>
            {sel ? (
              <div className="flex flex-wrap justify-end gap-1" role="toolbar" aria-label="Selection edits">
                {current ? (
                  <>
                    <Act
                      label={`Fill with ${current.name}`}
                      onClick={() =>
                        studio.edit(
                          selectionShape(sel, current.id, current.meta, 'fill'),
                          'fill',
                          `filled the selection with ${current.name}`,
                        )
                      }
                    >
                      <span>fill</span>
                    </Act>
                    <Act
                      label={`Walls of ${current.name}`}
                      onClick={() =>
                        studio.edit(
                          selectionShape(sel, current.id, current.meta, 'walls'),
                          'walls',
                          `walls of ${current.name}`,
                        )
                      }
                    >
                      <span>walls</span>
                    </Act>
                  </>
                ) : null}
                <Act
                  label="Hollow it out"
                  onClick={() => studio.edit(selectionShape(sel, 0, 0, 'hollow'), 'hollow', 'hollowed the selection')}
                >
                  <span>hollow</span>
                </Act>
                <Act
                  label="Raise the top surface"
                  onClick={() => studio.edit(raiseLower(studio.canvas, sel, true), 'raise', 'raised the top surface')}
                >
                  <span>raise</span>
                </Act>
                <Act
                  label="Lower the top surface"
                  onClick={() => studio.edit(raiseLower(studio.canvas, sel, false), 'lower', 'lowered the top surface')}
                >
                  <span>lower</span>
                </Act>
                <label className="flex items-center gap-1 rounded bg-black/55 px-1.5 py-0.5">
                  Replace
                  <select
                    value={replaceFrom}
                    onChange={(e) => setReplaceFrom(e.target.value)}
                    aria-label="Replace which block"
                    className="bg-black/40 text-white"
                  >
                    <option value="any">all</option>
                    {selMaterials.map(([id, name]) => (
                      <option key={id} value={String(id)}>
                        {name}
                      </option>
                    ))}
                  </select>
                  → {current?.name ?? '—'}
                  <button
                    type="button"
                    className="rounded bg-white/15 px-1 hover:bg-white/30"
                    disabled={!current}
                    onClick={() =>
                      current &&
                      studio.edit(
                        replaceIn(
                          studio.canvas,
                          sel,
                          replaceFrom === 'any' ? 'any' : Number(replaceFrom),
                          current.id,
                          current.meta,
                        ),
                        'replace',
                        `replaced ${replaceFrom === 'any' ? 'everything' : (selMaterials.find(([i]) => String(i) === replaceFrom)?.[1] ?? replaceFrom)} with ${current.name}`,
                      )
                    }
                  >
                    go
                  </button>
                </label>
                <Act label="Invert [Ctrl+I]" onClick={() => studio.setSelection(invert(studio.canvas, sel))}>
                  <span>invert</span>
                </Act>
                <Act label="Deselect" onClick={() => studio.setSelection(null)}>
                  <span>none</span>
                </Act>
              </div>
            ) : (
              <div className="flex gap-1">
                <Act label="Select all [Ctrl+A]" onClick={() => studio.setSelection(selectAll(studio.canvas))}>
                  <span>select all</span>
                </Act>
              </div>
            )}
            {snap.anchor ? (
              <span className="rounded bg-black/55 px-2 py-1">
                Anchor {snap.anchor.join(', ')}{' '}
                <button type="button" className="underline" onClick={() => void studio.setAnchor(null)}>
                  clear
                </button>
              </span>
            ) : null}
          </div>
          <Hotbar slots={slots} active={active} onPick={setActive} icon={icon} />
          {paletteOpen ? (
            <PaletteDrawer
              palette={session.palette}
              version={version}
              current={current}
              sheet={sheet}
              onChoose={(e) => {
                pushRecent(version, e);
                setSlots((cur) => {
                  const next = [...cur];
                  next[active] = e;
                  return next;
                });
              }}
              onClose={() => {
                setPaletteOpen(false);
                backToWorld();
              }}
            />
          ) : null}
          {!canWrite ? (
            <div className="absolute inset-x-0 bottom-20 mx-auto w-fit rounded bg-black/70 px-3 py-1.5 text-xs text-white">
              View only: designing needs the design (or admin) role.
            </div>
          ) : null}
        </div>
        <ChatPanel
          ref={chat}
          messages={snap.messages}
          models={models}
          model={model}
          onModel={setModel}
          costUsd={snap.costUsd}
          busy={snap.busy}
          canWrite={canWrite}
          hasSelection={!!sel}
          withSelection={withSel}
          onWithSelection={setWithSel}
          onSend={send}
          onClose={backToWorld}
          error={snap.error}
        />
      </div>
      {models.length === 0 ? (
        <p className="border-t border-border bg-warn-soft px-3 py-1 text-xs text-warn">
          The bot has no usable model configured (Agent:Llm), so the AI can't answer.
        </p>
      ) : null}
      <span hidden data-testid="player">
        {player}
      </span>
    </div>
  );
}

function Seg({
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
    <div className="flex items-center gap-0.5 rounded bg-black/55 p-0.5" role="radiogroup" aria-label={label}>
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

function Act({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
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
