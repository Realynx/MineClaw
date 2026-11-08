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
  Hammer,
  PaintBucket,
  Paintbrush,
  Slash,
} from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { ErrorState, Spinner } from '@/components/ui/primitives';
import { api, errorText } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { newRequestId } from '@/lib/utils';
import { useNavigationGuard } from '@/lib/navGuard';
import { Crosshair } from '@/features/world/InWorld';
import { Hotbar, PaletteDrawer, type IconOf, type IconSheet } from './BlockBar';
import { EARTH, plotMode, type Selection, selectAll, sizeOf, type V3, type Cell } from './canvas';
import { PictureControls } from './PictureControls';
import { ChatPanel, type ChatPanelHandle } from './ChatPanel';
import { MinePanel } from './MinePanel';
import { minePreview } from './mine/minePreview';
import { useMinePlacement } from './useMinePlacement';
import type { MineCells } from './mine/mineTemplates';
import type { MinePreset } from './mine/presets';
import { dirOfMcYaw, type Dir, hasFacing, placeMeta, turn, type Version } from './facing';
import { defaultHotbar, entryKey, type PaletteEntry, pushRecent } from './palette';
import { Studio, type StudioApi, type StudioSnapshot } from './studio';
import type * as ViewerMod from './studio-viewer';
import { aimHit, type Hit, brush, type BrushMode, type BrushShape, type BoxMode, type Clip, copy, move, pasteCells, underCut, toggleUnder, UNDER_DEFAULT } from './tools';
import {
  AnchorNote,
  aimText,
  countsText,
  facingText,
  KindDialog,
  mineRangeText,
  SavedBar,
  SelectAllBar,
  SelectionEdits,
  StudioHeader,
  StudioHud,
  type Tool,
  type ToolDef,
  type ToolSettings,
  ToolRail,
  ToolSettingsBar,
  toolSuffix,
  TransformBar,
  UnderSlider,
  VersionDialog,
} from './StudioChrome';
import { brushPreview, type Drag, dragPreview, groundEditable, studioKey, studioPress, studioRelease } from './studioInput';
import { bindStudioPointer, type PointerPosition, type StudioPointerActions } from './studioPointer';

type Mod = typeof ViewerMod;

const TOOLS: ToolDef[] = [
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

const MINE_TOOLS: ToolDef[] = TOOLS.filter((t) => ['build', 'select', 'box', 'brush'].includes(t.id)).map((t) => ({
  ...t,
  label: t.id === 'build' ? 'Dig / fill back (left digs, right fills)' : t.id === 'box' ? 'Excavate box (left-drag digs)' : t.id === 'brush' ? 'Excavate brush' : t.label,
}));

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

/** A mine's first hotbar slot: "fill back" (a dug cell becomes ground again; anything else is left alone). */
const GROUND: PaletteEntry = {
  id: EARTH,
  meta: 0,
  name: 'Ground (fill back)',
  facing: false,
  spec: 'earth',
  cat: 'Misc',
  family: 'none',
  icon: null,
};

/** A numeric search param, or null when it is missing or empty. */
const numberParam = (v: string | null) => (v ? Number(v) : null);

/** The body remounts on another session, version, kind or seed. */
const sessionKey = (s: DesignerSession) => `${s.id}:${s.version}:${s.kind ?? 'build'}:${s.seed ?? ''}`;

function StudioOpening() {
  return (
    <div className="grid h-full place-items-center text-sm text-muted">
      <span className="flex items-center gap-2">
        <Spinner className="size-4" /> Opening the Design Studio…
      </span>
    </div>
  );
}

/** The models the designer offers, its default, and the server's seed (none before they load). */
function modelChoice(m: ReturnType<typeof useApi<'designer.models'>>['data']) {
  return { models: m?.models ?? [], defaultModel: m?.default ?? null, serverSeed: m?.serverSeed ?? null };
}

export function StudioPage() {
  const params = useSearchParams();
  const [pictureRequested] = useState(() => params.get('picture') === '1');
  const router = useRouter();
  const qc = useQueryClient();
  const { me, can, canChat } = useAuth();
  const canWrite = can('design');
  const designParam = numberParam(params.get('id'));
  const sessionParam = numberParam(params.get('session'));
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
  if (!session || !mod) return <StudioOpening />;
  return (
    <StudioBody
      key={sessionKey(session)}
      session={session}
      pictureTools={pictureRequested}
      onSession={setSession}
      mod={mod}
      {...modelChoice(models.data)}
      canWrite={canWrite}
      canChat={canChat}
      player={me.player ?? me.username}
      invalidate={() => void qc.invalidateQueries({ predicate: (q) => q.queryKey[0] === 'designs.list' || q.queryKey[0] === 'designs.get' })}
    />
  );
}

function StudioBody({
  session,
  pictureTools,
  onSession,
  mod,
  models,
  defaultModel,
  serverSeed,
  canWrite,
  canChat,
  player,
  invalidate,
}: {
  serverSeed: string | null;
  pictureTools: boolean;
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
  const version = versionOf(session);
  const studio = useMemo(() => new Studio(session, realApi), [session]);
  /** A mine session: generated terrain at absolute y (its ground always editable, the side panel, the X-ray). */
  const mine = studio.terrain !== null;
  const surface = useMemo(() => studio.terrain?.surfaceRange() ?? null, [studio]);
  const world = useMemo(
    () => new mod.StudioWorld(version, studio.canvas, undefined, studio.terrain),
    [mod, version, studio],
  );
  const overlay = useMemo(() => new mod.Overlay3D(64, surface ? surface.max + 1 : undefined), [mod, surface]);
  const snap: StudioSnapshot = useSyncExternalStore(studio.subscribe, studio.snapshot, studio.snapshot);
  const viewer = useRef<WorldViewerHandle>(null);
  const chat = useRef<ChatPanelHandle>(null);
  const stage = useRef<HTMLDivElement>(null);
  const [ready, setReady] = useState(false);
  const [aim, setAim] = useState<AimTarget | null>(null);
  const [locked, setLocked] = useState(false);
  const [tool, setTool] = useState<Tool>(mine ? 'select' : 'build');
  const chooseTool = useCallback((next: Tool) => {
    if (!mine || MINE_TOOLS.some((t) => t.id === next)) setTool(next);
  }, [mine]);
  const [boxMode, setBoxMode] = useState<BoxMode>('fill');
  const [brushShape, setBrushShape] = useState<BrushShape>('sphere');
  const [brushSize, setBrushSize] = useState(3);
  const [brushMode, setBrushMode] = useState<BrushMode>(mine ? 'remove' : 'add');
  const [extrudeN, setExtrudeN] = useState(1);
  const [slots, setSlots] = useState<Array<PaletteEntry | null>>(() =>
    session.kind === 'mine' ? [GROUND, ...defaultHotbar(session.palette).slice(0, 8)] : defaultHotbar(session.palette),
  );
  const [active, setActive] = useState(0);
  const [facing, setFacing] = useState<Dir | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [hud, setHud] = useState(true);
  // A mine's hills would hide under a grid floating over its highest ground: off there until asked for.
  const [grid, setGrid] = useState(session.kind !== 'mine');
  /** Underground mode: null = off, else the highest layer still shown (canvas y; everything above it is cut away). */
  const [under, setUnder] = useState<number | null>(null);
  const [withSel, setWithSel] = useState(true);
  const [model, setModel] = useState(() => session.model || defaultModel || 'main');
  const [clip, setClip] = useState<Clip | null>(null);
  const [pasting, setPasting] = useState(false);
  const [drag, setDrag] = useState<Drag | null>(null);
  const [fps, setFps] = useState(0);
  const [iconTick, setIconTick] = useState(0);
  const [replaceFrom, setReplaceFrom] = useState<string>('any');
  const [saveName, setSaveName] = useState(() => (session.name === 'new design' ? '' : session.name));
  const [saveSummary, setSaveSummary] = useState('');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<{ designId: number; rev: number; text: string } | null>(() =>
    session.designId ? { designId: session.designId, rev: session.revisions[0]?.rev ?? 0, text: '' } : null,
  );
  const [revisions, setRevisions] = useState<DesignRevision[]>(session.revisions);
  const [notice, setNotice] = useState<string | null>(null);
  const [canvasTick, setCanvasTick] = useState(0);
  const minePlacement = useMinePlacement({ studio, mod, viewer, overlay, ready, canEdit: canWrite && !snap.busy && !saving,
    selecting: tool === 'select', notice: setNotice });
  const { moveSelected: moveMineSelection, rememberDesign } = minePlacement;
  /** Underground's first level: a mine's highest ground, a build's grass layer. */
  const underFirst = useRef(firstUnderLevel(surface));
  const [xray, setXray] = useState(mine);
  const [preferred, setPreferred] = useState<string | null>(() => session.preferredFor ?? null);
  /** A kind switch of a full canvas waits for the user's word (the canvas is cleared). */
  const [kindAsk, setKindAsk] = useState<'build' | 'mine' | null>(null);
  const bots = useApi('bots.list', {}, { enabled: canChat });
  const mainBot = mainBotOf(bots.data?.bots);

  // Icons from the bundled textures (loaded once per version); the whole palette drawn into one sheet once the page is idle.
  const bundle = useRef<Awaited<ReturnType<Mod['loadBundle']>> | null>(null);
  const [sheet, setSheet] = useState<IconSheet | null>(null);
  useEffect(() => {
    let live = true;
    void mod
      .loadBundle(version)
      .then(async (b) => {
        if (!live) return;
        bundle.current = b;
        setIconTick((n) => n + 1);
        const atlas = await mod.blockIconAtlasAsync(b, session.palette, 32);
        if (live) setSheet(atlas);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [mod, version, session.palette]);
  const hints = useMemo(() => new Map(session.palette.map((e) => [entryKey(e), e.icon ?? null] as const)), [session.palette]);
  const icon: IconOf = useCallback(
    (id, meta, hint) => {
      void iconTick;
      // The mine's "fill back" slot shows dirt.
      const [iid, im] = id === EARTH ? [3, 0] : [id, meta];
      return bundle.current
        ? mod.blockIconUrl(bundle.current, iid, im, 32, hint ?? hints.get(`${iid}:${im}`) ?? null)
        : null;
    },
    [mod, iconTick, hints],
  );

  // The game version: a dropdown; an empty canvas switches, a full one is checked and converted on the user's word.
  const [versionAsk, setVersionAsk] = useState<{ target: Version; report: DesignerCompatReport } | null>(null);
  const [versionBusy, setVersionBusy] = useState(false);
  // Build <-> mine: an empty canvas switches at once, a full one asks first (it is cleared). A mine's seed: a new terrain.
  const changeKind = async (kind: 'build' | 'mine', apply = false) => {
    if (kind === (session.kind ?? 'build') && !apply) return;
    setVersionBusy(true);
    try {
      await studio.flush();
      const r = await api('designer.version', {
        params: { id: session.id },
        body: { version, kind, ...(apply ? { apply } : {}) },
      });
      if (r.changed && r.session) {
        setKindAsk(null);
        onSession(r.session);
      } else if (r.needsClear) setKindAsk(kind);
    } catch (e) {
      setNotice(`Couldn't switch to ${kind}: ${errorText(e)}`);
    } finally {
      setVersionBusy(false);
    }
  };
  const changeSeed = async (seed: string) => {
    setVersionBusy(true);
    try {
      await studio.flush();
      const r = await api('designer.version', { params: { id: session.id }, body: { version, seed } });
      if (r.session) onSession(r.session);
    } catch (e) {
      setNotice(`Couldn't change the seed: ${errorText(e)}`);
    } finally {
      setVersionBusy(false);
    }
  };
  /** A template's cells as one undoable edit; a shaft's entry becomes the design's anchor (its top). */
  const insertMine = (m: MineCells, kind = 'template') => {
    const changed = studio.edit(m.cells, kind, m.label);
    if (changed.length === 0) return setNotice(`${m.label}: nothing to dig there`);
    if (m.label.startsWith('spiral') || kind === 'preset') void studio.setAnchor(m.entry).catch(() => undefined);
    setNotice(`${m.label}: ${changed.length} cells`);
  };
  const applyPreset = async (p: MinePreset, m: MineCells) => {
    insertMine(m, 'preset');
    try {
      await studio.flush();
      await api('designer.version', { params: { id: session.id }, body: { version, preferredFor: p.version } });
      setPreferred(p.version);
    } catch (e) {
      setNotice(`Couldn't tag it preferred for ${p.version}: ${errorText(e)}`);
    }
  };

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
    (window as unknown as { __studio?: unknown }).__studio = {
      studio,
      world,
      overlay,
      engine: () => viewer.current?.engine() ?? null,
    };
  }, [studio, world, overlay]);

  // The world follows the canvas; the AI's cells flash as they land.
  useEffect(
    () =>
      studio.onCells((cells, by) => {
        world.push(cells);
        setCanvasTick((tick) => tick + 1);
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
        if (mine) e.rig.orbitYaw = 135;
        else e.rig.flyYaw = 135;
        e.focus(31, surface ? surface.max : 63, 31, { distance: surface ? 44 : 34, pitch: 38 });
        setReady(true);
      }
      if (e && mine) overlay.updateGizmoScale(e.camera, e.surface.clientHeight);
      overlay.tick(performance.now());
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => {
      cancelAnimationFrame(raf);
      overlay.dispose();
    };
  }, [overlay, surface, mine]);

  useEffect(() => overlay.setSelection(snap.selection), [overlay, snap.selection]);
  // Only authored excavations and veins exposed by those routes; seed caves never create outlines.
  useEffect(() => {
    if (!studio.terrain || !xray) { overlay.setMinePreview(null); return; }
    let frame = 0;
    const update = () => {
      frame = 0;
      overlay.setMinePreview(minePreview(studio.terrain!, studio.canvas.holes(), studio.canvas.cells()));
    };
    update();
    const stop = studio.onCells(() => { if (!frame) frame = requestAnimationFrame(update); });
    return () => { stop(); if (frame) cancelAnimationFrame(frame); };
  }, [overlay, studio, xray]);
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
  const look = useCallback((): Dir => {
    const rig = viewer.current?.engine()?.rig;
    return facing ?? dirOfMcYaw((mine ? rig?.orbitYaw : rig?.flyYaw) ?? 0);
  }, [facing, mine]);

  // ------------------------------------------------------------------ previews (drag box, brush, paste ghost)

  useEffect(() => {
    const s = st.current;
    const h = hitOf(aim, studio.canvas, s.under !== null);
    overlay.setDrag(dragPreview(drag, h, tool));
    overlay.setBrush(brushPreview(h, tool, s.brushMode, brushSize, brushShape));
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
    (d: V3, group?: string) => {
      if (moveMineSelection(d, group)) return;
      applyTransform(`move ${d.join(',')}`, (s) => move(studio.canvas, s, d), group);
    },
    [applyTransform, studio, moveMineSelection],
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
  const pointerActions = useRef<StudioPointerActions>(null!);

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
          groundEditable(s),
        ),
        'brush',
        `brush ${mode}`,
        group,
      );
    },
    [studio],
  );

  /** A brush stroke: one dab now, then one every 140 ms at the aim until the button is released. */
  const startBrush = useCallback(
    (h: Hit, button: number) => {
      const group = `brush-${Date.now()}`;
      const target = h.hit ? studio.canvas.id(...h.hit) : undefined;
      brushAt(h, button, group, target);
      const timer = setInterval(() => {
        void viewer.current
          ?.engine()
          ?.aimNow()
          .then((tt) => {
            if (stroke.current?.group === group && pointerActions.current.canEdit)
              brushAt(hitOf(tt, studio.canvas, st.current.under !== null), button, group, target);
          });
      }, 140);
      stroke.current = { button, group, target, timer };
    },
    [studio, brushAt],
  );

  const down = useCallback(
    async (button: number, mods: { shift: boolean; ctrl: boolean }, at?: PointerPosition, signal?: AbortSignal) => {
      const engine = viewer.current?.engine();
      const t = (await (at ? engine?.aimAtPointer(at.x, at.y) : engine?.aimNow())) ?? null;
      const s = st.current;
      if (signal?.aborted || (!pointerActions.current.canEdit && (s.tool !== 'select' || s.pasting))) return;
      const h = hitOf(t, studio.canvas, s.under !== null);
      if (!h) return;
      studioPress({
        studio,
        h,
        button,
        mods,
        s,
        setDrag: (next) => { st.current.drag = next; setDrag(next); },
        setPasting,
        pick,
        placeAt,
        startBrush,
        setNotice: (text) => setNotice(text),
      });
    },
    [studio, pick, placeAt, startBrush],
  );

  const up = useCallback(
    async (button: number, mods: { shift: boolean }, at?: PointerPosition, signal?: AbortSignal) => {
      if (stroke.current && stroke.current.button === button) {
        if (stroke.current.timer) clearInterval(stroke.current.timer);
        stroke.current = null;
      }
      const d = st.current.drag;
      if (!d || d.button !== button) return;
      st.current.drag = null;
      setDrag(null);
      const engine = viewer.current?.engine();
      const t = (await (at ? engine?.aimAtPointer(at.x, at.y) : engine?.aimNow())) ?? null;
      const s = st.current;
      if (signal?.aborted || (!pointerActions.current.canEdit && s.tool !== 'select')) return;
      studioRelease(studio, s, d, hitOf(t, studio.canvas, s.under !== null), button, mods.shift);
    },
    [studio],
  );

  useEffect(() => {
    pointerActions.current = {
      canEdit: canWrite && !snap.busy && !saving && !versionBusy,
      down, up, move: moveSel, setDrag,
      cancel: () => {
        if (stroke.current?.timer) clearInterval(stroke.current.timer);
        stroke.current = null;
        st.current.drag = null;
        setDrag(null);
      },
      error: (error) => setNotice(errorText(error)),
    };
  }, [canWrite, snap.busy, saving, versionBusy, down, up, moveSel]);
  useEffect(() => {
    const engine = ready ? viewer.current?.engine() : null;
    if (!engine) return;
    return bindStudioPointer({ surface: engine.surface, camera: engine.camera, studio, overlay, mod, mine,
      state: () => st.current, actions: () => pointerActions.current });
  }, [ready, studio, overlay, mod, mine]);

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
    const inWorld = () => {
      const el = stage.current;
      return (
        !!el &&
        (el.contains(document.activeElement) ||
          (!!document.pointerLockElement && el.contains(document.pointerLockElement)))
      );
    };
    const onKey = (e: KeyboardEvent) =>
      studioKey(e, {
        studio,
        state: () => st.current,
        inWorld,
        doCopy,
        setPasting,
        setDrag,
        moveSel,
        setActive,
        openChat,
        toggleHud: () => setHud((v) => !v),
        applyTransform,
        setTool: chooseTool,
        turnFacing: (by) => setFacing(turn(look(), by)),
        togglePalette: () => {
          if (document.pointerLockElement) document.exitPointerLock();
          setPaletteOpen((v) => !v);
        },
        toggleGrid: () => setGrid((v) => !v),
        toggleUnder: () => setUnder((u) => toggleUnder(u, underFirst.current)),
      });
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [studio, moveSel, applyTransform, openChat, doCopy, chooseTool, look]);

  // ------------------------------------------------------------------ chat, save, build

  const send = useCallback(
    (text: string) => {
      void studio.send(text, { model, withSelection: withSel }).catch(() => undefined);
    },
    [studio, model, withSel],
  );

  const capturePicture = useCallback(async () => {
    const engine = viewer.current?.engine();
    if (!engine) throw new Error('The designer is still loading. Try again when the view is ready.');
    return overlay.withoutGuides(() => engine.capturePng());
  }, [overlay]);

  const doSave = useCallback(
    async (asNew: boolean): Promise<boolean> => {
      if (!saveName.trim()) {
        setNotice('Give the design a name first.');
        return false;
      }
      setSaving(true);
      setNotice(null);
      try {
        const previewPng = await capturePicture();
        const r = await studio.save(saveName, saveSummary, asNew, previewPng);
        rememberDesign(r.designId, r.rev);
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
    [studio, saveName, saveSummary, invalidate, capturePicture, rememberDesign],
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

  const choosePaletteEntry = useCallback((entry: PaletteEntry) => {
    pushRecent(version, entry);
    setSlots((cur) => { const next = [...cur]; next[active] = entry; return next; });
  }, [active, version]);
  const closePalette = useCallback(() => { setPaletteOpen(false); backToWorld(); }, [backToWorld]);

  // ------------------------------------------------------------------ render

  const h = hitOf(aim, studio.canvas, under !== null);
  const selBlocks = useMemo(
    () => (sel ? studio.canvas.cells().filter((c) => inBox(c, sel)).length : 0),
    // The canvas mutates in place; local edits invalidate derived data before the server acknowledges them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [sel, studio, canvasTick],
  );
  const bounds = useMemo(() => studio.canvas.bounds(), [studio, canvasTick]); // eslint-disable-line react-hooks/exhaustive-deps
  const selMaterials = useMemo(() => {
    const ids = new Map<number, string>();
    if (sel)
      for (const c of studio.canvas.cells())
        if (inBox(c, sel)) ids.set(c[3], session.palette.find((p) => p.id === c[3])?.name ?? `#${c[3]}`);
    return [...ids];
  }, [sel, studio, session.palette, canvasTick]); // eslint-disable-line react-hooks/exhaustive-deps

  const toolSettings = { tool, boxMode, brushMode, brushShape, brushSize, extrudeN };
  const busy = snap.busy || versionBusy;

  return (
    <div
      className="flex h-full min-h-0 flex-col"
      data-testid="studio"
      data-blocks={snap.blocks}
      data-rev={snap.rev}
      data-dirty={Number(snap.dirty)}
      data-under={under ?? ''}
    >
      <StudioHeader
        snap={snap}
        session={session}
        version={version}
        locked={!canWrite || busy}
        changeVersion={(v) => void changeVersion(v)}
        changeKind={(k) => void changeKind(k)}
        saveName={saveName}
        setSaveName={setSaveName}
        saveSummary={saveSummary}
        setSaveSummary={setSaveSummary}
        canSave={canSaveNow(canWrite, saving, snap)}
        saving={saving}
        doSave={(asNew) => void doSave(asNew)}
        revisions={revisions}
      />
      <StudioBars
        saved={saved}
        mineRange={mineRangeOf(mine, bounds, preferred)}
        canChat={canChat}
        mainBot={mainBot}
        askBot={() => void askBot()}
        notice={notice}
        kindAsk={kindAsk}
        versionAsk={versionAsk}
        snap={snap}
        version={version}
        busy={versionBusy}
        changeKind={(k) => void changeKind(k, true)}
        cancelKind={() => setKindAsk(null)}
        changeVersion={(v) => void changeVersion(v, true)}
        cancelVersion={() => setVersionAsk(null)}
      />
      <PictureControls viewer={viewer} bounds={bounds} baseY={plotMode().baseY} defaultOpen={pictureTools} canWrite={canWrite} busy={busy || saving}
        designId={saved?.designId ?? null} dirty={snap.dirty}
        onCapture={async () => {
          const previewPng = await capturePicture();
          const result = await api('designer.save', { params: { id: studio.id }, body: { name: snap.name, previewPng, previewOnly: true } });
          setNotice(result.text);
          invalidate();
        }} />
      <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <div ref={stage} className="relative min-h-[420px] min-w-0 flex-1 bg-black" data-testid="studio-stage"
          onDragOver={minePlacement.dragOver} onDrop={minePlacement.drop}>
          <mod.WorldViewer
            ref={viewer}
            source={world.source}
            cameraMode={mine ? 'follow' : 'fly'}
            controls={mine ? 'editor' : undefined}
            xray="off"
            drawDistance={96}
            layers={LAYERS}
            hideHint
            onAim={(t) => {
              setAim(t);
              const rig = viewer.current?.engine()?.rig;
              const yaw = mine ? rig?.orbitYaw : rig?.flyYaw;
              if (yaw !== undefined) setLookNow(dirOfMcYaw(yaw));
            }}
            onStats={(s) => setFps(Math.round(s.fps))}
            className="absolute inset-0"
          />
          <StageHints locked={locked} ready={ready} mine={mine} />
          {minePlacement.placing ? <div className="absolute top-3 left-14 rounded border border-purple-400/50 bg-black/80 px-3 py-2 text-xs text-white">
            {minePlacement.hover ? `Place at ${minePlacement.hover.join(', ')}` : `Point inside the plot at Y ${minePlacement.placementY}`}
            <button type="button" onClick={minePlacement.cancel} className="ml-3 underline">Cancel</button>
          </div> : null}
          {hud ? (
            <StudioHud
              aim={aimText(h, mine)}
              facing={facingText(facing, lookNow)}
              toolLabel={toolLabelOf(toolSettings)}
              counts={countsText(snap, bounds)}
              pending={snap.pending}
              fps={fps}
              over={tooBig(mine, bounds)}
              selection={selectionText(sel, selBlocks)}
              pasting={pasting}
            />
          ) : null}
          {/* Tools */}
          <ToolRail
            tools={mine ? MINE_TOOLS : TOOLS}
            tool={tool}
            setTool={chooseTool}
            studio={studio}
            snap={snap}
            grid={grid}
            toggleGrid={() => setGrid((v) => !v)}
            underground={under !== null}
            toggleUnder={() => setUnder((u) => toggleUnder(u, underFirst.current))}
            stage={stage}
          />
          {/* Tool options and the selection's actions */}
          <ToolColumn
            mine={mine}
            moduleSelected={minePlacement.selectedId !== null}
            under={under}
            setUnder={setUnder}
            settings={toolSettings}
            setters={{ setBoxMode, setBrushMode, setBrushShape, setBrushSize, setExtrudeN }}
            studio={studio}
            sel={sel}
            version={version}
            hasClip={!!clip}
            applyTransform={applyTransform}
            moveSel={moveSel}
            doCopy={doCopy}
            startPaste={() => setPasting(true)}
            current={current}
            materials={selMaterials}
            replaceFrom={replaceFrom}
            setReplaceFrom={setReplaceFrom}
            anchor={snap.anchor}
          />
          <Hotbar slots={slots} active={active} onPick={setActive} icon={icon} />
          {paletteOpen ? (
            <PaletteDrawer
              palette={session.palette}
              version={version}
              current={current}
              sheet={sheet}
              onChoose={choosePaletteEntry}
              onClose={closePalette}
            />
          ) : null}
          <ViewOnlyNote canWrite={canWrite} />
        </div>
        {studio.terrain ? (
          <MinePanel
            terrain={studio.terrain}
            version={version}
            seed={session.seed ?? studio.terrain.seed}
            serverSeed={serverSeed}
            preferredFor={preferred}
            dug={() => studio.canvas.holes()}
            rev={canvasTick}
            level={under}
            onLevel={(y) => setUnder(y)}
            xray={xray}
            onXray={setXray}
            at={selMin(sel)}
            canWrite={canWrite}
            busy={busy || saving}
            onSeed={(seed) => void changeSeed(seed)}
            onInsert={(m) => insertMine(m)}
            onPreset={(p, m) => void applyPreset(p, m)}
            modules={minePlacement.modules}
            selectedModule={minePlacement.selectedId}
            placingModule={minePlacement.placing}
            placementY={minePlacement.placementY}
            onPlacementY={minePlacement.setPlacementY}
            onPickModule={(spec, options) => { chooseTool('select'); minePlacement.arm(spec, null, options); }}
            onUpdatePlacement={minePlacement.updatePlacing}
            onSelectModule={(id) => { chooseTool('select'); minePlacement.cancel(); minePlacement.select(id); }}
            onFrameModule={minePlacement.frame}
            onPositionModule={minePlacement.position}
            onUpdateModule={minePlacement.update}
            onMoveModule={minePlacement.move}
            onRemoveModule={minePlacement.remove}
          />
        ) : null}
        <ChatPanel
          ref={chat}
          kind={mine ? 'mine' : 'build'}
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
      <NoModelNote models={models} />
      <span hidden data-testid="player">
        {player}
      </span>
    </div>
  );
}

const versionOf = (session: DesignerSession): Version => (session.version === '1.12.2' ? '1.12.2' : 'b1.8.1');

/** A cell inside the selection's box. */
function inBox(c: Cell, sel: Selection): boolean {
  const inX = c[0] >= sel.min[0] && c[0] <= sel.max[0];
  return inX && c[1] >= sel.min[1] && c[1] <= sel.max[1] && c[2] >= sel.min[2] && c[2] <= sel.max[2];
}

function ViewOnlyNote({ canWrite }: { canWrite: boolean }) {
  if (canWrite) return null;
  return (
    <div className="absolute inset-x-0 bottom-20 mx-auto w-fit rounded bg-black/70 px-3 py-1.5 text-xs text-white">
      View only: designing needs the design (or admin) role.
    </div>
  );
}

function NoModelNote({ models }: { models: DesignerModel[] }) {
  if (models.length !== 0) return null;
  return (
    <p className="border-t border-border bg-warn-soft px-3 py-1 text-xs text-warn">
      The bot has no usable model configured (Agent:Llm), so the AI can't answer.
    </p>
  );
}

/** Underground's first level: a mine's highest ground, a build's grass layer. */
const firstUnderLevel = (surface: { max: number } | null) => (surface ? surface.max : UNDER_DEFAULT);

/** The bot "Ask the bot to build it" asks: the main body, else the first. */
function mainBotOf(bots: ReadonlyArray<{ name: string; main?: boolean }> | undefined): string | null {
  return bots?.find((b) => b.main)?.name ?? bots?.[0]?.name ?? null;
}

/** Saving needs the role, no save in flight and something on the canvas. */
function canSaveNow(canWrite: boolean, saving: boolean, snap: StudioSnapshot): boolean {
  return canWrite && !saving && !(snap.blocks === 0 && snap.dug === 0);
}

type Bounds = { min: V3; max: V3 };

const mineRangeOf = (mine: boolean, bounds: Bounds | null, preferred: string | null) =>
  mine && bounds ? mineRangeText(bounds, preferred) : null;

/** A build over 32 a side can't be saved as one design (a mine can). */
const tooBig = (mine: boolean, bounds: Bounds | null) => !mine && !!bounds && sizeOf(bounds).some((n) => n > 32);

const selMin = (sel: Selection | null) => (sel ? sel.min : null);

/** The HUD's tool: its name without the hint, then its settings. */
const toolLabelOf = (t: ToolSettings) => `${TOOLS.find((x) => x.id === t.tool)?.label.split(' (')[0]}${toolSuffix(t)}`;

/** The column right of the stage: the underground slider, the tool's settings, transforms, selection edits, the anchor. */
function ToolColumn(p: {
  mine: boolean;
  moduleSelected: boolean;
  under: number | null;
  setUnder: (y: number) => void;
  settings: ToolSettings;
  setters: Pick<
    Parameters<typeof ToolSettingsBar>[0],
    'setBoxMode' | 'setBrushMode' | 'setBrushShape' | 'setBrushSize' | 'setExtrudeN'
  >;
  studio: Studio;
  sel: Selection | null;
  version: Version;
  hasClip: boolean;
  applyTransform: Parameters<typeof TransformBar>[0]['applyTransform'];
  moveSel: (d: V3) => void;
  doCopy: () => unknown;
  startPaste: () => void;
  current: PaletteEntry | null;
  materials: Array<[number, string]>;
  replaceFrom: string;
  setReplaceFrom: (v: string) => void;
  anchor: V3 | null;
}) {
  const { studio, sel, mine } = p;
  return (
    <div
      className="absolute top-3 right-3 flex max-w-[calc(100%-4.5rem)] flex-col items-end gap-1.5 text-[11px] text-white"
    >
      {p.under !== null && !mine ? <UnderSlider under={p.under} setUnder={p.setUnder} /> : null}
      <ToolSettingsBar {...p.settings} {...p.setters} mine={mine} />
      {!p.moduleSelected ? <TransformBar
        studio={studio}
        sel={sel}
        version={p.version}
        hasClip={p.hasClip}
        applyTransform={p.applyTransform}
        moveSel={(d) => p.moveSel(d)}
        doCopy={p.doCopy}
        startPaste={p.startPaste}
      /> : <span className="rounded bg-black/55 px-2 py-1">Drag an axis · Arrow keys move · PgUp / PgDn changes Y</span>}
      {sel && !mine ? (
        <SelectionEdits
          studio={studio}
          sel={sel}
          current={p.current}
          materials={p.materials}
          replaceFrom={p.replaceFrom}
          setReplaceFrom={p.setReplaceFrom}
        />
      ) : !sel ? (
        <SelectAllBar studio={studio} />
      ) : <button type="button" onClick={() => studio.setSelection(null)} className="rounded bg-black/55 px-2 py-1 hover:bg-black/75">Deselect</button>}
      {p.anchor ? <AnchorNote studio={studio} anchor={p.anchor} /> : null}
    </div>
  );
}

/** The crosshair while the mouse is locked; the controls hint once the world is up and it isn't. */
function StageHints({ locked, ready, mine }: { locked: boolean; ready: boolean; mine: boolean }) {
  if (locked) return <Crosshair />;
  if (!ready) return null;
  return (
    <div className="pointer-events-none absolute right-3 bottom-24 left-14 mx-auto w-fit max-w-full rounded bg-black/55 px-2.5 py-1 text-center font-mono text-[11px] text-white">
      {mine ? 'Middle drag: orbit · Shift + middle: pan · Wheel: zoom · Alt + left drag also orbits · F: frame selection'
        : 'Click to build · WASD fly · Space/E up · Q down · B blocks · T chat · Ctrl+Z undo'}
    </div>
  );
}

/** "Selection 3×2×4 · 17 blocks" (a mask counts its cells); null without a selection. */
function selectionText(sel: Selection | null, blocks: number): string | null {
  if (!sel) return null;
  const size = sizeOf(sel);
  return `Selection ${size[0]}×${size[1]}×${size[2]} · ${sel.mask ? sel.mask.size : blocks} blocks`;
}

/** The saved bar, the notice line and the kind / version dialogs under the header. */
function StudioBars(p: {
  saved: { designId: number; rev: number; text: string } | null;
  mineRange: string | null;
  canChat: boolean;
  mainBot: string | null;
  askBot: () => void;
  notice: string | null;
  kindAsk: 'build' | 'mine' | null;
  versionAsk: { target: Version; report: DesignerCompatReport } | null;
  snap: StudioSnapshot;
  version: Version;
  busy: boolean;
  changeKind: (k: 'build' | 'mine') => void;
  cancelKind: () => void;
  changeVersion: (v: Version) => void;
  cancelVersion: () => void;
}) {
  const { kindAsk, versionAsk } = p;
  return (
    <>
      {p.saved?.designId ? (
        <SavedBar saved={p.saved} mineRange={p.mineRange} canChat={p.canChat} mainBot={p.mainBot} askBot={p.askBot} />
      ) : null}
      {p.notice ? (
        <p className="border-b border-border bg-info-soft px-3 py-1 text-xs text-info" role="status">
          {p.notice}
        </p>
      ) : null}
      {kindAsk ? (
        <KindDialog
          kind={kindAsk}
          blocks={p.snap.blocks}
          dug={p.snap.dug}
          busy={p.busy}
          onConfirm={() => p.changeKind(kindAsk)}
          onCancel={p.cancelKind}
        />
      ) : null}
      {versionAsk ? (
        <VersionDialog
          version={p.version}
          target={versionAsk.target}
          report={versionAsk.report}
          busy={p.busy}
          onConfirm={() => p.changeVersion(versionAsk.target)}
          onCancel={p.cancelVersion}
        />
      ) : null}
    </>
  );
}

