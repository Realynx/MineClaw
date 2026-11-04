'use client';

/**
 * The Story Mode map: the shared world viewer in its `top` camera (or the angled `iso` view for 3D building) under one 2D
 * overlay canvas that takes every pointer event and draws the colony (zones, designation markers, build ghosts, chests and
 * workbenches, pawn tokens with names and job marks, AI bodies, selection and drag boxes, order targets, the placement
 * preview). The overlay redraws in one animation-frame loop that also pans (keys, screen edge) and keeps it ≥ 40 fps on
 * software WebGL: no blur, no DOM per pawn, a few hundred canvas primitives.
 */
import type { PickedBlock, WorldViewerEngine, WorldViewerHandle } from '@mineclaw/world-viewer';
import { getViewerQuality } from '@mineclaw/world-viewer/settings';
import { designationCells, targetPos, type StoryColonyState } from '@mineclaw/contracts';
import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef } from 'react';
import { useWorldHost } from '@/features/world/integration';
import { DotMap } from '@/features/world/DotMap';
import {
  assignAlong,
  cellAt,
  cellRect,
  edgePan,
  isDrag,
  lineSlots,
  project,
  rayAt,
  rayPlaneY,
  rectOf,
  snapCells,
  standFeet,
  unitsPerPx,
  zoomAt,
  type P2,
  type Pt,
  type Rect,
  type V3,
  type ViewMats,
} from './math';
import { anchorLift, bodyLook, hologramCells, type BodyLook } from './bodyLook';
import { chestBadge, drawChestBadge, useContainerBadges } from './chestBadges';
import { clusterFailed, placeLabels, type FailedCluster, type FailedMark, type PlacedLabel } from './failedLabels';
import { type Selection } from './model';
import {
  buildItem,
  DragGate,
  entitiesIn,
  hasHeight,
  heightLabel,
  heightQuery,
  heightTargets,
  MAX_DRAG_CELLS,
  NOT_GROUND_IDS,
  type HeightMode,
  type HeightTargets,
  faceCell,
  placeDesign,
  type DesignBlock,
  preview as buildPreview,
  previewTargets,
  scanIds,
  toolArea,
  toolYRange,
  DESIGNATE_TOOLS,
  zoneColor,
  type BuildOpts,
  type BuildToolId,
  type DesignateId,
  type EntityLite,
  type Target,
} from './tools';
import { COLORS, glyph, pawnColor } from './visuals';

export type MapTool =
  | { kind: 'select' }
  | { kind: 'designate'; id: DesignateId }
  | { kind: 'zone'; id: string; op: 'add' | 'remove' }
  | { kind: 'build'; id: BuildToolId }
  | { kind: 'design'; id: number; name: string; blocks: DesignBlock[] }
  | { kind: 'storage' }
  | { kind: 'home' };

export interface DragResult {
  rect: [number, number, number, number];
  /** First and last cells (build tools: the placement cells). */
  a: V3;
  b: V3;
  /** Preview of a designate drag: matching blocks and entities. */
  cells: V3[];
  entities: EntityLite[];
  count: number;
  /** The pointer travelled past the drag threshold (else it was a click). */
  moved?: boolean;
  /** A Mine or Deconstruct drag: exactly the cells its height mode designates (sent as `blocks`). */
  height?: HeightTargets;
}

export interface FormationMove {
  pawn: string;
  /** Feet position of the formation cell. */
  target: V3;
}

/** Columns around a formation slot whose standing height is read (snapping looks this far for a standable cell). */
const SNAP_R = 3;

export interface StoryMapHandle {
  focus(x: number, z: number, y?: number): void;
  center(): V3 | null;
  zoom(): number;
  /** World → page coordinates (tests, the context menu). */
  toPage(x: number, y: number, z: number): Pt | null;
  /** Is a world point inside the map's picture (with a small margin)? */
  onScreen(x: number, y: number, z: number): boolean;
  /** The top solid block of a column (trees and plants skipped), when its chunk is loaded. */
  surface(x: number, z: number): Promise<number | null>;
  /** Esc: cancels a drag in progress (nothing happens on release). True when there was one. */
  cancelDrag(): boolean;
}

export interface StoryMapProps {
  state: StoryColonyState | undefined;
  selection: Selection;
  tool: MapTool;
  build: BuildOpts;
  mineFilter: 'all' | 'ores' | 'stone' | 'soil';
  /** The active Mine / Deconstruct tool's height mode, and the Layers count. */
  heightMode: HeightMode;
  layers: number;
  level: number;
  cutY: number | null;
  view: 'top' | 'angled';
  /** Keys held (pan), owned by the page's key handler. */
  keys: React.RefObject<Set<string>>;
  home: V3 | null;
  /** No colony yet: the first placement is over the main bot (else the viewing user's player) instead of home. */
  settle?: boolean;
  /** The viewing user's Minecraft player (the settle fallback). */
  player?: string | null;
  /** The map centre's column (x, level, z), a few times a second while it changes. */
  onCentre?: (c: V3) => void;
  /** Settling with no colony: the floor height the map was placed at (the working level starts there). */
  onSettled?: (floorY: number) => void;
  onSelect: (sel: Selection) => void;
  /** Double-click on a body: every visible body of the same kind. */
  onSelectSame: (kind: 'pawn' | 'ai', visible: string[]) => void;
  onTool: (result: DragResult, shift: boolean) => void;
  onContext: (target: Target, page: Pt) => void;
  /** A right-drag line with drafted pawns selected: one move per drafted pawn to its formation cell (feet position);
   * `ignored` = selected pawns that are not drafted (they keep working, as in the genre). */
  onFormation?: (moves: FormationMove[], queue: boolean, ignored: number) => void;
  onDropTool: () => void;
  /** A drag was cancelled (Esc, the other mouse button, leaving full screen). */
  onCancelled?: () => void;
  onHover?(info: string | null): void;
  /** Drafted pawns are selected: an entity under the cursor (mob, animal, player, AI bot) shows the attack cursor. */
  attackMode?: boolean;
}

const TOP_ZOOM = 44;
/** Blocks a column's surface reading looks through: logs, leaves, plants, snow (both versions' ids). */
const NOT_GROUND = new Set([17, 18, 31, 32, 37, 38, 39, 40, 59, 78, 83, 106, 161, 162, 175]);
/** How long the first placement waits for the main bot or the player to show up before it stays where it is. */
const SETTLE_WAIT_MS = 6000;

/** Where a colony with no home yet settles: the main bot's column, else the viewing user's player, as [x, floor y, z]. */
function settleSpot(e: WorldViewerEngine, player: string | null | undefined): V3 | null {
  const at = (x: number, y: number, z: number): V3 => [Math.floor(x), Math.floor(y + 0.01) - 1, Math.floor(z)];
  const main = e.runtime.store.bots().find((b) => b.dim === 0 && b.dto.main);
  if (main) return at(main.x, main.y, main.z);
  const me = player
    ? e.runtime.store.entities(0).find((x) => x.kind === 'player' && x.name?.toLowerCase() === player.toLowerCase())
    : undefined;
  return me ? at(me.x, me.y, me.z) : null;
}
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

/** A map mark (its glyph on a dark disc) drawn once into a small canvas and stamped with drawImage after that: a few hundred
 * marks a frame cost a blit each instead of a path fill with save/restore (the Story perf profile's top item). */
const sprites = new Map<string, HTMLCanvasElement | null>();
function markSprite(id: string, color: string, size: number): HTMLCanvasElement | null {
  const key = `${id}|${color}|${size}`;
  const hit = sprites.get(key);
  if (hit !== undefined) return hit;
  let c: HTMLCanvasElement | null = null;
  const g = glyph(id);
  if (typeof document !== 'undefined') {
    const dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
    const full = size + 6;
    c = document.createElement('canvas');
    c.width = Math.ceil(full * dpr);
    c.height = Math.ceil(full * dpr);
    const x = c.getContext('2d');
    if (!x) c = null;
    else {
      x.scale(dpr, dpr);
      x.translate(3, 3);
      x.fillStyle = 'rgba(12,14,18,0.72)';
      x.beginPath();
      x.arc(size / 2, size / 2, size / 2 + 2, 0, Math.PI * 2);
      x.fill();
      if (g) {
        x.scale(size / 16, size / 16);
        x.fillStyle = color;
        x.strokeStyle = color;
        x.lineWidth = 1.4;
        x.fill(g, 'evenodd');
        if (id === 'hunt') x.stroke(g);
      }
    }
  }
  if (sprites.size > 256) sprites.clear();
  sprites.set(key, c);
  return c;
}

/** One designation as the map draws it: its mark's cell (a tree's top log), colour, ring, owner. */
interface DesMark {
  d: StoryColonyState['designations'][number];
  cell: V3;
  col: string;
  ring: string | null;
  failed: boolean;
}

/** The designations' marks and the failed ones' clusters, worked out once per state (not per frame). */
interface DesModel {
  marks: DesMark[];
  failed: FailedMark[];
  clusters: FailedCluster[];
  clusterOf: Map<string, Array<string | number>>;
}

const desModels = new WeakMap<object, DesModel>();
function designationModel(list: StoryColonyState['designations']): DesModel {
  const hit = desModels.get(list);
  if (hit) return hit;
  const marks: DesMark[] = [];
  const failed: FailedMark[] = [];
  for (const d of list) {
    const all = designationCells(d);
    const cells = d.kind === 'chop' && all.length > 1 ? [all.reduce((a, b) => (b[1] > a[1] ? b : a))] : all.slice(0, 1);
    const isFailed = d.status === 'failed';
    const ring = isFailed ? COLORS.danger : (d.fails ?? 0) > 0 ? '#ffb35c' : null;
    for (const b of cells) {
      const cell: V3 = [b[0], b[1], b[2]];
      marks.push({ d, cell, col: COLORS.designation[d.kind] ?? '#ffffff', ring, failed: isFailed });
      if (isFailed) failed.push({ id: d.id, kind: d.kind, reason: d.reason ?? '', cell });
      else if (d.wait) failed.push({ id: d.id, kind: d.kind, reason: d.wait, cell, tone: 'wait' });
    }
  }
  const clusters = clusterFailed(failed);
  const clusterOf = new Map<string, Array<string | number>>();
  for (const cl of clusters) for (const id of cl.ids) clusterOf.set(String(id), cl.ids);
  const m = { marks, failed, clusters, clusterOf };
  desModels.set(list, m);
  return m;
}

/** One column of a height-mode preview: x, z, its highest and lowest designated y, how many cells. */
type ColumnSpan = [number, number, number, number, number];

/** Groups a height drag's cells by column (once per scan, not per frame). */
function columnSpans(cells: readonly V3[]): ColumnSpan[] {
  const by = new Map<string, ColumnSpan>();
  for (const [x, y, z] of cells) {
    const k = `${x},${z}`;
    const c = by.get(k);
    if (!c) by.set(k, [x, z, y, y, 1]);
    else {
      c[2] = Math.max(c[2], y);
      c[3] = Math.min(c[3], y);
      c[4]++;
    }
  }
  return [...by.values()];
}

/** The pointer-event `buttons` bit of a `button` (0 left → 1, 1 middle → 4, 2 right → 2). */
const buttonBit = (b: number) => (b === 0 ? 1 : b === 1 ? 4 : b === 2 ? 2 : 1 << b);

type KeyboardLock = { lock?: (codes: string[]) => Promise<void>; unlock?: () => void };
/** In full screen, Esc is the browser's way out: while a drag is held it is locked (Keyboard Lock, Chromium) so Esc cancels
 * the drag instead; holding Esc still leaves full screen. Elsewhere (or where the API is missing) leaving full screen
 * cancels the drag instead (see the fullscreenchange listener). */
function lockEsc(on: boolean) {
  const kb = (navigator as unknown as { keyboard?: KeyboardLock }).keyboard;
  try {
    if (on && document.fullscreenElement) void kb?.lock?.(['Escape'])?.catch(() => undefined);
    else if (!on) kb?.unlock?.();
  } catch {
    /* not supported */
  }
}

interface Formation {
  a: P2;
  b: P2;
  moves: FormationMove[];
  ignored: number;
}

interface DragState {
  start: Pt;
  now: Pt;
  button: number;
  shift: boolean;
  alt: boolean;
  startCell: V3 | null;
  endCell: V3 | null;
  moved: boolean;
}

export const StoryMap = forwardRef<StoryMapHandle, StoryMapProps>(function StoryMap(props, ref) {
  const { mod, source, ready } = useWorldHost();
  const viewer = useRef<WorldViewerHandle | null>(null);
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const wrap = useRef<HTMLDivElement | null>(null);
  const p = useRef(props);
  p.current = props;
  const drag = useRef<DragState | null>(null);
  /** Esc / the other button cancel a drag; the release after a cancel does nothing (tools.ts `DragGate`). */
  const gate = useRef(new DragGate());
  const hover = useRef<{ at: Pt; cell: V3 | null; face: V3 | null; pickAt: number } | null>(null);
  const shown = useRef(new Map<string, V3>());
  const scan = useRef<{ key: string; busy: boolean; result: (DragResult & { cols?: ColumnSpan[] }) | null }>({
    key: '',
    busy: false,
    result: null,
  });
  const chests = useRef<{ at: number; cells: Array<{ pos: V3; id: number }> }>({ at: 0, cells: [] });
  // What the hive knows is in those chests (their fill, for the badges drawn when zoomed in).
  const chestCells = useMemo(() => () => chests.current.cells, []);
  const badges = useContainerBadges(chestCells);
  const mats = useRef<ViewMats | null>(null);
  const lastClick = useRef<{ t: number; name: string } | null>(null);
  const placed = useRef(false);
  const appliedCut = useRef<string | undefined>(undefined);
  const lastEngine = useRef<WorldViewerEngine | null>(null);
  /** Standing heights per column at one level (`x,z` -> feet y, null = nowhere to stand); refilled for each right-drag. */
  const stand = useRef<{ level: number; cols: Map<string, number | null>; busy: boolean }>({
    level: 0,
    cols: new Map(),
    busy: false,
  });
  const formationShown = useRef<Formation | null>(null);
  /** The failed designations' labels (DOM over the canvas: crisp text in the theme's colours, hover titles, clickable). */
  const labelsEl = useRef<HTMLDivElement | null>(null);
  /** Where each failed designation's ring was drawn this frame, for clicks (id, the ids its cluster label stands for). */
  const failedAt = useRef<Array<{ id: string | number; cluster: Array<string | number>; x: number; y: number }>>([]);
  /** What the viewer was last given for the holograms: the state's ghosts and builds (by identity) and the placement key. */
  const holo = useRef<{ ghosts: unknown; builds: unknown; preview: string }>({ ghosts: null, builds: null, preview: '' });

  const engine = (): WorldViewerEngine | null => viewer.current?.engine() ?? null;

  const toPage = (x: number, y: number, z: number): Pt | null => {
    const m = mats.current,
      c = canvas.current;
    if (!m || !c) return null;
    const s = project(m, x, y, z);
    if (!s) return null;
    const r = c.getBoundingClientRect();
    return { x: r.left + s.x, y: r.top + s.y };
  };

  useImperativeHandle(ref, () => ({
    focus: (x, z, y) => {
      const e = engine();
      if (!e) return;
      e.rig.beginTransition(e.camera, performance.now(), 500);
      e.rig.target.set(x + 0.5, y ?? p.current.level + 1, z + 0.5);
    },
    center: () => {
      const t = engine()?.rig.target;
      return t ? [Math.floor(t.x), Math.floor(t.y), Math.floor(t.z)] : null;
    },
    zoom: () => engine()?.rig.isoZoom ?? TOP_ZOOM,
    toPage,
    onScreen: (x, y, z) => {
      const m = mats.current;
      const s = m ? project(m, x, y, z) : null;
      return !!m && !!s && s.x >= 24 && s.y >= 24 && s.x <= m.w - 24 && s.y <= m.h - 24;
    },
    surface: async (x, z) => {
      const e = engine();
      if (!e) return null;
      let y = 320;
      for (let i = 0; i < 32; i++) {
        const hit = await e.runtime.host.pick(0, [x + 0.5, y, z + 0.5], [0, -1, 0], y + 70, null).catch(() => null);
        if (!hit) return null;
        if (!NOT_GROUND.has(hit.id)) return hit.y;
        y = hit.y - 0.001;
      }
      return null;
    },
    cancelDrag: () => {
      if (!gate.current.escape()) return false;
      dropDrag();
      return true;
    },
  }));

  // First placement over the colony's home; with no colony yet, over the main bot (else the viewing user's player) so a new
  // colony settles where the hive is rather than at the world origin. The target height follows the level.
  useEffect(() => {
    let raf = 0;
    const t0 = performance.now();
    const tryPlace = () => {
      const e = engine();
      if (!e || placed.current) return;
      const home = p.current.home;
      let at: V3 | null = home;
      if (!at) {
        if (!p.current.settle) return;
        at = settleSpot(e, p.current.player);
        if (!at && performance.now() - t0 < SETTLE_WAIT_MS) return;
      }
      placed.current = true;
      e.rig.isoZoom = TOP_ZOOM;
      if (!at) return;
      if (!home) p.current.onSettled?.(at[1]);
      e.rig.target.set(at[0] + 0.5, (home ? p.current.level : at[1]) + 1, at[2] + 0.5);
    };
    const loop = () => {
      tryPlace();
      if (!placed.current) raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [props.home, props.settle, ready]);

  // ------------------------------------------------------------------ the frame loop: pan, project, draw
  useEffect(() => {
    let raf = 0;
    let last = performance.now();
    let centreAt = 0,
      centreKey = '',
      centreCb: StoryMapProps['onCentre'] = undefined;
    const loop = (now: number) => {
      raf = requestAnimationFrame(loop);
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      const e = engine(),
        c = canvas.current;
      if (!e || !c) return;
      // The level cut (x-ray above the working level) is a per-view uniform: no re-meshing when it moves. The camera looks
      // at the level's floor.
      if (lastEngine.current !== e) {
        lastEngine.current = e;
        appliedCut.current = undefined;
        holo.current = { ghosts: null, builds: null, preview: '' };
        // The pawns and AI bodies are drawn as their player models; the overlay draws their names (no second label).
        e.setBotLabels(false);
      }
      // Above the ground storey, the storeys below are dimmed for reference.
      const home = p.current.home;
      const dim = home && p.current.level > home[1] ? p.current.level + 1 : null;
      const cutKey = `${p.current.cutY}|${dim}`;
      if (appliedCut.current !== cutKey) {
        appliedCut.current = cutKey;
        e.setLevelCut(p.current.cutY !== null || dim !== null ? { y: p.current.cutY ?? 1e6, dimBelow: dim } : null);
      }
      if (placed.current) e.rig.target.y = p.current.level + 1;
      syncHolograms(e);
      const w = c.clientWidth,
        h = c.clientHeight;
      if (c.width !== w || c.height !== h) {
        c.width = w;
        c.height = h;
      }
      // Pan: keys, then the screen edge (only while the pointer is over the map and not dragging a box).
      const keys = p.current.keys.current;
      let px = 0,
        pz = 0;
      for (const code of keys ?? []) {
        if (code === 'KeyW' || code === 'ArrowUp') pz -= 1;
        if (code === 'KeyS' || code === 'ArrowDown') pz += 1;
        if (code === 'KeyA' || code === 'ArrowLeft') px -= 1;
        if (code === 'KeyD' || code === 'ArrowRight') px += 1;
      }
      const hv = hover.current;
      if (hv && !drag.current && document.hasFocus()) {
        const [ex, ey] = edgePan(hv.at, w, h, 8);
        px += ex;
        pz += ey;
      }
      if (px || pz) {
        const speed = e.rig.isoZoom * 0.9 * dt;
        if (e.rig.mode === 'top') {
          e.rig.target.x += px * speed;
          e.rig.target.z += pz * speed;
        } else e.rig.pan(-px * speed * (h / e.rig.isoZoom), -pz * speed * (h / e.rig.isoZoom), h);
      }
      const cam = e.camera;
      mats.current = {
        view: cam.matrixWorldInverse.elements,
        proj: cam.projectionMatrix.elements,
        world: cam.matrixWorld.elements,
        projInv: cam.projectionMatrixInverse.elements,
        w,
        h,
      };
      // Chests and workbenches near the view (a worker scan every few seconds).
      if (now - chests.current.at > 4000) {
        chests.current.at = now;
        const t = e.rig.target,
          r = Math.min(64, e.rig.isoZoom);
        const lv = p.current.level;
        void e.runtime.host
          .scan(0, [t.x - r, lv - 12, t.z - r], [t.x + r, lv + 16, t.z + r], [54, 146, 58, 61, 62], 400)
          .then((s) => {
            const cells: Array<{ pos: V3; id: number }> = [];
            for (let i = 0; i + 5 < s.length + 1; i += 6)
              cells.push({ pos: [s[i]!, s[i + 1]!, s[i + 2]!], id: s[i + 3]! });
            chests.current.cells = cells;
          })
          .catch(() => undefined);
      }
      // The centre column for the panels ("settles at", "move home here"), at most five times a second, once placed, and
      // only while a panel wants it (the page passes onCentre then: a re-render of the whole HUD per pan step otherwise).
      if (p.current.onCentre !== centreCb) {
        centreCb = p.current.onCentre;
        centreKey = '';
      }
      if (placed.current && centreCb && now - centreAt > 200) {
        centreAt = now;
        const t = e.rig.target,
          cc: V3 = [Math.floor(t.x), p.current.level, Math.floor(t.z)];
        const key = cc.join(',');
        if (key !== centreKey) {
          centreKey = key;
          p.current.onCentre?.(cc);
        }
      }
      draw(c, dt, now);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  /** [holograms] Hands the viewer the queued cells (only when the state's ghosts or builds changed) and the design being
   * placed (only when its cell or turn changed). */
  const syncHolograms = (e: WorldViewerEngine) => {
    const st = p.current.state,
      h = holo.current;
    if (h.ghosts !== st?.ghosts || h.builds !== st?.builds) {
      h.ghosts = st?.ghosts;
      h.builds = st?.builds;
      const { cells, rev } = hologramCells(st);
      e.setHolograms(cells, rev);
    }
    const tool = p.current.tool,
      hv = hover.current;
    const a = tool.kind === 'design' && hv?.cell && !drag.current ? startCellOf(hv) : null;
    if (a && tool.kind === 'design') {
      const key = `${tool.id}|${a.join(',')}|${p.current.build.facing}`;
      if (key !== h.preview) {
        h.preview = key;
        e.setHologramPreview(placeDesign(tool.blocks, a, p.current.build.facing).slice(0, 4096).flat(), key);
      }
    } else if (h.preview) {
      h.preview = '';
      e.setHologramPreview(null);
    }
  };

  /**
   * Every selectable body with its screen point: where the viewer draws its model (the world store's interpolated position)
   * when it has one, else its reported position eased toward. `look`: model (overlay marks only), token, or cut (a storey
   * above the level cut); `r`: its hit radius in pixels.
   */
  const bodies = (dt: number): Array<{ key: string; at: Pt | null; kind: 'pawn' | 'ai'; pos: V3; look: BodyLook; r: number }> => {
    const m = mats.current;
    const st = p.current.state;
    const e = engine();
    const ppb = m && e ? m.h / Math.max(1, e.rig.isoZoom) : 0;
    const out: Array<{ key: string; at: Pt | null; kind: 'pawn' | 'ai'; pos: V3; look: BodyLook; r: number }> = [];
    const k = Math.min(1, dt * 6);
    const add = (name: string, pos: readonly number[] | null | undefined, kind: 'pawn' | 'ai') => {
      if (!pos) return;
      const live = e?.runtime.store.bot(name);
      const prev = shown.current.get(name);
      const next: V3 =
        live && live.dim === 0
          ? [live.x, live.y, live.z]
          : prev && Math.hypot(prev[0] - pos[0]!, prev[2] - pos[2]!) < 12
            ? [prev[0] + (pos[0]! - prev[0]) * k, prev[1] + (pos[1]! - prev[1]) * k, prev[2] + (pos[2]! - prev[2]) * k]
            : [pos[0]!, pos[1]!, pos[2]!];
      shown.current.set(name, next);
      const look = bodyLook({
        detail: live ? (e?.runtime.entities.detailOf(live.name) ?? null) : null,
        pxPerBlock: ppb,
        feetY: next[1],
        cutY: p.current.cutY,
      });
      const at = m ? project(m, next[0], next[1] + anchorLift(look), next[2]) : null;
      let r = 18;
      if (look === 'model' && m && at) {
        const head = project(m, next[0], next[1] + 1.9, next[2]);
        r = Math.max(14, head ? Math.abs(head.y - at.y) + 6 : 0, ppb * 0.6);
      }
      out.push({ key: name, at, kind, pos: next, look, r });
    };
    for (const pw of st?.pawns ?? []) if (pw.online !== false) add(pw.name, pw.pos, 'pawn');
    for (const a of st?.ai ?? []) if (a.online !== false && (a.dim ?? 0) === 0) add(a.name, a.pos, 'ai');
    return out;
  };

  const entityList = (): EntityLite[] => {
    const e = engine();
    if (!e) return [];
    return e.runtime.store.entities(0).map((x) => ({ id: x.id, kind: x.kind, type: x.type, x: x.x, y: x.y, z: x.z }));
  };

  // ------------------------------------------------------------------ drawing

  /** Puts the placed labels on the DOM layer, reusing its elements (a handful at most; positions change every frame). */
  const syncLabels = (placed: ReadonlyArray<PlacedLabel<FailedCluster>>) => {
    const host = labelsEl.current;
    if (!host) return;
    while (host.children.length > placed.length) host.lastElementChild!.remove();
    placed.forEach((pl, i) => {
      let el = host.children[i] as HTMLButtonElement | undefined;
      if (!el) {
        el = document.createElement('button');
        el.type = 'button';
        el.className = LABEL_CLASS;
        el.dataset.testid = 'designation-label';
        const me = el;
        // A click selects the cluster's first designation (the inspector says why, in full, with Retry now / Cancel).
        el.addEventListener('pointerdown', (ev) => ev.stopPropagation());
        el.addEventListener('click', (ev) => {
          ev.stopPropagation();
          const ids = JSON.parse(me.dataset.ids ?? '[]') as Array<string | number>;
          if (ids.length) p.current.onSelect({ ...EMPTY_SEL, designation: { id: ids[0]!, cluster: ids } });
        });
        host.appendChild(el);
      }
      const cl = pl.item;
      if (el.textContent !== cl.text) el.textContent = cl.text;
      const tone = cl.tone ?? 'fail';
      if (el.dataset.tone !== tone) {
        el.dataset.tone = tone;
        el.className = tone === 'wait' ? WAIT_LABEL_CLASS : LABEL_CLASS;
      }
      const title = cl.ids.length > 1 ? `${cl.ids.length} designations: ${cl.reason}` : cl.reason;
      if (el.title !== title) el.title = title;
      const ids = JSON.stringify(cl.ids);
      if (el.dataset.ids !== ids) el.dataset.ids = ids;
      el.style.transform = `translate(${Math.round(pl.x)}px, ${Math.round(pl.y)}px)`;
    });
  };

  const draw = (c: HTMLCanvasElement, dt: number, now: number) => {
    const ctx = c.getContext('2d');
    const m = mats.current;
    if (!ctx || !m) return;
    const props = p.current;
    const st = props.state;
    ctx.clearRect(0, 0, c.width, c.height);
    const e = engine()!;
    const upp = unitsPerPx(e.rig.isoZoom, m.h);
    const cell = 1 / upp; // px per block
    const cutY = props.cutY;
    const quad = (x1: number, z1: number, x2: number, z2: number, y: number): Pt[] | null => {
      const a = project(m, x1, y, z1),
        b = project(m, x2, y, z1),
        cc = project(m, x2, y, z2),
        d = project(m, x1, y, z2);
      return a && b && cc && d ? [a, b, cc, d] : null;
    };
    const poly = (pts: Pt[]) => {
      ctx.beginPath();
      ctx.moveTo(pts[0]!.x, pts[0]!.y);
      for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i]!.x, pts[i]!.y);
      ctx.closePath();
    };
    const mark = (id: string, at: Pt, color: string, size = 14) => {
      const sp = markSprite(id, color, size);
      if (sp) {
        ctx.drawImage(sp, at.x - size / 2 - 3, at.y - size / 2 - 3, size + 6, size + 6);
        return;
      }
      const g = glyph(id);
      ctx.save();
      ctx.translate(at.x - size / 2, at.y - size / 2);
      ctx.fillStyle = 'rgba(12,14,18,0.72)';
      ctx.beginPath();
      ctx.arc(size / 2, size / 2, size / 2 + 2, 0, Math.PI * 2);
      ctx.fill();
      if (g) {
        ctx.scale(size / 16, size / 16);
        ctx.fillStyle = color;
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.4;
        ctx.fill(g, 'evenodd');
        if (id === 'hunt') ctx.stroke(g);
      }
      ctx.restore();
    };
    // Zones: tinted areas. Home zones (the houses) are the genre's home-area tint: a stronger fill, a solid edge, the name.
    let houseNo = 0;
    for (const z of st?.zones ?? []) {
      const [x1, y1, z1, x2, , z2] = z.area;
      const home = z.kind === 'home';
      if (home) houseNo++;
      const q = quad(Math.min(x1, x2), Math.min(z1, z2), Math.max(x1, x2) + 1, Math.max(z1, z2) + 1, y1 + 1.02);
      if (!q) continue;
      const col = zoneColor(z.kind);
      poly(q);
      ctx.fillStyle = home ? `${col}40` : `${col}2e`;
      ctx.fill();
      ctx.strokeStyle = home ? col : `${col}cc`;
      ctx.lineWidth = home ? 2 : 1.5;
      if (!home) ctx.setLineDash([6, 4]);
      ctx.stroke();
      ctx.setLineDash([]);
      if (home && cell > 3) {
        const name = z.name || z.label || `House ${houseNo}`;
        ctx.font = '600 11px system-ui, sans-serif';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        const tl = q.reduce((a, b) => (a.x + a.y <= b.x + b.y ? a : b));
        const w = ctx.measureText(name).width;
        ctx.fillStyle = 'rgba(10,12,16,0.7)';
        ctx.fillRect(tl.x + 3, tl.y + 3, w + 8, 15);
        ctx.fillStyle = '#cfe3ff';
        ctx.fillText(name, tl.x + 7, tl.y + 4.5);
      }
    }
    // The colony's home: a house mark.
    const homeAt = props.home;
    if (homeAt) {
      const at = project(m, homeAt[0] + 0.5, homeAt[1] + 1, homeAt[2] + 0.5);
      if (at) {
        mark('home', at, '#7fb8ff', 15);
        ctx.strokeStyle = '#7fb8ff';
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.arc(at.x, at.y, 12, 0, Math.PI * 2);
        ctx.stroke();
      }
    }
    // Beds: a bed mark, the owner's colour ring and initials.
    for (const bd of st?.beds ?? []) {
      if ((bd.dim ?? 0) !== 0 || (cutY !== null && bd.pos[1] >= cutY)) continue;
      const at = project(m, bd.pos[0] + 0.5, bd.pos[1] + 0.6, bd.pos[2] + 0.5);
      if (!at) continue;
      const owner = bd.pawn ? st?.pawns.find((x) => x.name === bd.pawn) : undefined;
      const col = bd.pawn ? pawnColor(bd.pawn) : '#c9ccd2';
      mark('bed', at, col, 13);
      if (bd.pawn) {
        const ini = (owner?.display ?? bd.pawn).slice(0, 2).toUpperCase();
        ctx.font = '700 9px system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        const w = ctx.measureText(ini).width + 6;
        ctx.fillStyle = col;
        ctx.fillRect(at.x + 7, at.y - 14, w, 11);
        ctx.fillStyle = '#0c1018';
        ctx.fillText(ini, at.x + 7 + w / 2, at.y - 8.5);
      }
      if (props.selection.bed && k3(props.selection.bed) === k3(bd.pos)) {
        ctx.strokeStyle = COLORS.select;
        ctx.lineWidth = 2;
        ctx.strokeRect(at.x - 11, at.y - 11, 22, 22);
      }
    }
    // Build ghosts: translucent blocks by state; anything above the level cut only as a faint outline (x-rayed). With build
    // holograms on (the viewer settings) the viewer draws them in the world instead (render/holograms.ts).
    const holoOn = getViewerQuality().holograms;
    const ghosts = holoOn ? [] : (st?.ghosts?.blocks ?? []);
    const angled = props.view === 'angled';
    const cube = (x: number, y: number, z: number, color: string, alpha: number, faint: boolean) => {
      if (!angled) {
        const q = quad(x, z, x + 1, z + 1, y + 1);
        if (!q) return;
        poly(q);
        if (faint) {
          ctx.strokeStyle = `${color}66`;
          ctx.lineWidth = 1;
          ctx.stroke();
        } else {
          ctx.globalAlpha = alpha;
          ctx.fillStyle = color;
          ctx.fill();
          ctx.globalAlpha = 1;
          ctx.strokeStyle = color;
          ctx.lineWidth = cell > 10 ? 1.2 : 0.6;
          ctx.stroke();
        }
        return;
      }
      // Angled: the top and the two faces toward the camera.
      const camPos = e.camera.position;
      const faces: Array<Array<[number, number, number]>> = [
        [
          [x, y + 1, z],
          [x + 1, y + 1, z],
          [x + 1, y + 1, z + 1],
          [x, y + 1, z + 1],
        ],
      ];
      if (camPos.x > x + 1)
        faces.push([
          [x + 1, y, z],
          [x + 1, y + 1, z],
          [x + 1, y + 1, z + 1],
          [x + 1, y, z + 1],
        ]);
      else if (camPos.x < x)
        faces.push([
          [x, y, z],
          [x, y + 1, z],
          [x, y + 1, z + 1],
          [x, y, z + 1],
        ]);
      if (camPos.z > z + 1)
        faces.push([
          [x, y, z + 1],
          [x + 1, y, z + 1],
          [x + 1, y + 1, z + 1],
          [x, y + 1, z + 1],
        ]);
      else if (camPos.z < z)
        faces.push([
          [x, y, z],
          [x + 1, y, z],
          [x + 1, y + 1, z],
          [x, y + 1, z],
        ]);
      for (const f of faces) {
        const pts = f.map(([a, b, cc]) => project(m, a, b, cc));
        if (pts.some((q) => !q)) continue;
        poly(pts as Pt[]);
        if (!faint) {
          ctx.globalAlpha = alpha;
          ctx.fillStyle = color;
          ctx.fill();
          ctx.globalAlpha = 1;
        }
        ctx.strokeStyle = faint ? `${color}66` : color;
        ctx.lineWidth = 0.8;
        ctx.stroke();
      }
    };
    const sorted = angled ? [...ghosts].sort((a, b) => a[1] - b[1]) : ghosts;
    let drawn = 0;
    for (const g of sorted) {
      if (drawn++ > 3000) break;
      const state = g[6];
      if (state === 'done') continue;
      const col = COLORS.ghost[state] ?? COLORS.ghost.todo!;
      cube(g[0], g[1], g[2], col, state === 'leased' ? 0.5 : 0.32, cutY !== null && g[1] >= cutY);
    }
    // Designations: a mark on every target (one per tree for chop). The marks and the failed ones' clusters are worked out
    // once per state; a frame only projects and stamps them (off-screen ones skipped).
    const model = designationModel(st?.designations ?? EMPTY_LIST);
    const failedOn: Array<{ id: string | number; cluster: Array<string | number>; x: number; y: number }> = [];
    const markSize = cell > 18 ? 14 : 11;
    const vw = c.clientWidth,
      vh = c.clientHeight;
    for (const mk of model.marks) {
      const b = mk.cell;
      const at = project(m, b[0] + 0.5, Math.min(b[1] + 1, cutY ?? 999), b[2] + 0.5);
      if (!at || at.x < -20 || at.y < -20 || at.x > vw + 20 || at.y > vh + 20) continue;
      mark(mk.d.kind, at, mk.col, markSize);
      // A designation that keeps failing (the director's status / reason): a red ring always; its reason as a short label
      // (one per cluster, see below) when zoomed in.
      if (mk.ring) {
        ctx.strokeStyle = mk.ring;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(at.x, at.y, 11, 0, Math.PI * 2);
        ctx.stroke();
        if (mk.failed) failedOn.push({ id: mk.d.id, cluster: model.clusterOf.get(String(mk.d.id)) ?? [mk.d.id], x: at.x, y: at.y });
      }
      if (mk.d.pawn) {
        ctx.strokeStyle = pawnColor(mk.d.pawn);
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(at.x, at.y, 10, 0, Math.PI * 2);
        ctx.stroke();
      }
    }
    // The failed designations' labels: one per cluster (same reason, adjacent), largest first, never on top of another; hidden
    // when zoomed out (the rings stay).
    const clusters = model.clusters;
    failedAt.current = failedOn;
    const view = { w: c.clientWidth, h: c.clientHeight };
    syncLabels(
      cell > LABEL_ZOOM
        ? placeLabels(
            clusters.map((cl) => {
              const a = project(m, cl.anchor[0] + 0.5, Math.min(cl.anchor[1] + 1, cutY ?? 999), cl.anchor[2] + 0.5);
              return { item: cl, at: a ? { x: a.x, y: a.y + 13 } : null, text: cl.text };
            }),
            view,
          )
        : [],
    );
    // Chests and workbenches: registered storage gets its mark and a priority pip; unregistered chests show faintly.
    const storages = new Map(
      (st?.storages ?? []).flatMap((s) => [
        [k3(s.chest), s] as const,
        ...(s.doubleWith ? [[k3(s.doubleWith), s] as const] : []),
      ]),
    );
    const selChests = new Set(props.selection.chests.map(k3));
    const seen = new Set<string>();
    for (const { pos, id } of chests.current.cells) {
      if (cutY !== null && pos[1] >= cutY) continue;
      const at = project(m, pos[0] + 0.5, pos[1] + 1, pos[2] + 0.5);
      if (!at) continue;
      if (id === 54 || id === 146) {
        const s = storages.get(k3(pos));
        if (s && seen.has(k3(s.chest))) continue;
        if (s) seen.add(k3(s.chest));
        const sel = selChests.has(k3(pos)) || (s && selChests.has(k3(s.chest)));
        ctx.globalAlpha = s ? 1 : 0.45;
        mark('chest', at, COLORS.chest, 13);
        ctx.globalAlpha = 1;
        if (s) {
          const pri = ['low', 'normal', 'preferred', 'important', 'critical'].indexOf(s.priority);
          for (let i = 0; i <= Math.max(0, pri); i++) {
            ctx.fillStyle = pri >= 3 ? '#ff9f43' : '#ffd166';
            ctx.fillRect(at.x + 9, at.y + 5 - i * 4, 3, 3);
          }
        }
        const known = cell > LABEL_ZOOM ? badges.current.get(k3(s ? s.chest : pos)) : undefined;
        if (cell > LABEL_ZOOM && (s || !known || k3(known.pos) === k3(pos))) {
          const badge = chestBadge(s, known, st?.categories); // one badge per container (a double chest's first half)
          if (badge) drawChestBadge(ctx, at, badge);
        }
        if (sel) {
          ctx.strokeStyle = COLORS.select;
          ctx.lineWidth = 2;
          ctx.strokeRect(at.x - 11, at.y - 11, 22, 22);
        }
      } else {
        const bench = (st?.workbenches ?? []).find((w) => k3(w.pos) === k3(pos));
        mark('bench', at, COLORS.bench, 13);
        const n = bench?.bills.length ?? 0;
        if (n) {
          ctx.fillStyle = '#9ec7ff';
          ctx.beginPath();
          ctx.arc(at.x + 10, at.y - 9, 6, 0, Math.PI * 2);
          ctx.fill();
          ctx.fillStyle = '#0c1220';
          ctx.font = '600 9px system-ui, sans-serif';
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(String(n), at.x + 10, at.y - 9);
        }
        if (props.selection.bench && k3(props.selection.bench) === k3(pos)) {
          ctx.strokeStyle = COLORS.select;
          ctx.lineWidth = 2;
          ctx.strokeRect(at.x - 11, at.y - 11, 22, 22);
        }
      }
    }
    // Bodies: order lines first, then tokens.
    const list = bodies(dt);
    const sel = new Set(props.selection.bodies);
    for (const b of list) {
      if (!sel.has(b.key) || !b.at) continue;
      const pw = st?.pawns.find((x) => x.name === b.key);
      const targets = [pw?.job?.target, ...(pw?.queue ?? []).map((o) => o.target)]
        .map((t) => targetPos(t))
        .filter((t): t is [number, number, number] => !!t);
      let from = b.at;
      ctx.strokeStyle = COLORS.order;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([5, 4]);
      for (const t of targets.slice(0, 6)) {
        const at = project(m, t[0] + 0.5, t[1] + 0.2, t[2] + 0.5);
        if (!at) continue;
        ctx.beginPath();
        ctx.moveTo(from.x, from.y);
        ctx.lineTo(at.x, at.y);
        ctx.stroke();
        ctx.strokeRect(at.x - 4, at.y - 4, 8, 8);
        from = at;
      }
      ctx.setLineDash([]);
    }
    const tokenR = Math.max(7, Math.min(13, cell * 0.55));
    const names = getViewerQuality().labels;
    ctx.font = '600 11px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (const b of list) {
      if (!b.at) continue;
      const pw = b.kind === 'pawn' ? st?.pawns.find((x) => x.name === b.key) : undefined;
      const ai = b.kind === 'ai' ? st?.ai?.find((x) => x.name === b.key) : undefined;
      const color = b.kind === 'ai' ? COLORS.ai : pawnColor(b.key);
      const { x, y } = b.at;
      // A body drawn as its model gets marks around it (no token over it); a storey above the cut shows a faint token.
      const model = b.look === 'model';
      const r = model ? Math.max(tokenR, b.r - 4) : tokenR;
      ctx.globalAlpha = b.look === 'cut' ? 0.35 : 1;
      if (sel.has(b.key)) {
        ctx.strokeStyle = COLORS.select;
        ctx.lineWidth = 2;
        const s = r + 5;
        ctx.beginPath();
        // corner brackets (the genre's selection mark, our own drawing)
        for (const [sx, sy] of [
          [-1, -1],
          [1, -1],
          [1, 1],
          [-1, 1],
        ] as const) {
          ctx.moveTo(x + sx * s, y + sy * (s - 5));
          ctx.lineTo(x + sx * s, y + sy * s);
          ctx.lineTo(x + sx * (s - 5), y + sy * s);
        }
        ctx.stroke();
      }
      if (model) {
        // The body's colour as a ring on the ground at its feet.
        const f = project(m, b.pos[0], b.pos[1] + 0.02, b.pos[2]),
          fx = project(m, b.pos[0] + 0.5, b.pos[1] + 0.02, b.pos[2]),
          fz = project(m, b.pos[0], b.pos[1] + 0.02, b.pos[2] + 0.5);
        if (f && fx && fz) {
          ctx.strokeStyle = color;
          ctx.lineWidth = 2.5;
          ctx.beginPath();
          ctx.ellipse(f.x, f.y, Math.max(4, Math.hypot(fx.x - f.x, fx.y - f.y)), Math.max(2, Math.hypot(fz.x - f.x, fz.y - f.y)), 0, 0, Math.PI * 2);
          ctx.stroke();
        }
      } else {
        ctx.fillStyle = 'rgba(10,12,16,0.78)';
        ctx.beginPath();
        ctx.arc(x, y, r, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = color;
        ctx.lineWidth = 3;
        ctx.stroke();
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.arc(x, y, r * 0.42, 0, Math.PI * 2);
        ctx.fill();
      }
      if (pw?.drafted) {
        const g = glyph('drafted');
        if (g) {
          ctx.save();
          ctx.translate(x + r - 4, y - r - 6);
          ctx.scale(0.75, 0.75);
          ctx.fillStyle = '#ff9f43';
          ctx.fill(g);
          ctx.restore();
        }
      }
      // job mark
      const job = pw?.job?.type;
      if (job && GLYPH_OF[job]) {
        const g = glyph(GLYPH_OF[job]);
        if (g) {
          ctx.save();
          ctx.translate(x - r - 8, y - r - 6);
          ctx.scale(0.7, 0.7);
          ctx.fillStyle = COLORS.designation[job] ?? '#ffffff';
          ctx.fill(g);
          ctx.restore();
        }
      }
      const danger = pw ? (pw.hp ?? 20) <= 8 || (pw.food ?? 20) <= 4 : false;
      // Name tags (the viewer settings): off leaves the names of the selected bodies and those in danger.
      if (names || sel.has(b.key) || danger) {
        const label = pw?.display ?? (ai ? (ai.kind === 'main' ? 'main' : ai.name.replace(/^maid_chan_/, '')) : b.key);
        const tw = ctx.measureText(label).width;
        ctx.fillStyle = 'rgba(10,12,16,0.7)';
        ctx.fillRect(x - tw / 2 - 4, y + r + 3, tw + 8, 15);
        ctx.fillStyle = danger ? COLORS.danger : b.kind === 'ai' ? '#d9ccff' : '#f4f6f8';
        ctx.fillText(label, x, y + r + 4);
      }
      ctx.globalAlpha = 1;
    }
    // The drag: a selection box, a formation line, or a tool's grid rectangle with its live targets.
    const d = drag.current;
    if (d && d.moved && d.button === 2) {
      void formation(d, false).then((f) => {
        if (drag.current === d) formationShown.current = f;
      });
      if (formationShown.current) drawFormation(ctx, m, formationShown.current);
    }
    const tool = props.tool;
    if (d && d.moved && d.button === 0) {
      if (tool.kind === 'select') {
        const rr = rectOf(d.start, d.now);
        ctx.fillStyle = 'rgba(245,197,66,0.10)';
        ctx.fillRect(rr.x, rr.y, rr.w, rr.h);
        ctx.strokeStyle = COLORS.select;
        ctx.lineWidth = 1.2;
        ctx.strokeRect(rr.x + 0.5, rr.y + 0.5, rr.w, rr.h);
      } else if (d.startCell && d.endCell) {
        drawToolDrag(ctx, m, d, quad, poly, mark, cube);
      }
    } else if (tool.kind === 'design' && hover.current?.cell && !d) {
      // A design follows the cursor (Q/E turns it) on the working level.
      // With build holograms on, the viewer draws it in the world (green where it fits, red where blocked; syncHolograms).
      const a = startCellOf(hover.current);
      if (a && !holoOn)
        for (const b of placeDesign(tool.blocks, a, props.build.facing).slice(0, 2500))
          cube(b[0], b[1], b[2], '#7fd3ff', 0.38, false);
      if (a) label(ctx, hover.current.at, `${tool.name}: ${tool.blocks.length} blocks · click to queue`, true);
    } else if (tool.kind === 'build' && hover.current?.cell && !d) {
      // The placement ghost under the cursor before a drag starts.
      const a = startCellOf(hover.current);
      if (a) {
        const pv = buildPreview(buildItem(tool.id, a, a, props.build));
        for (const b of pv.blocks.slice(0, 600)) cube(b[0], b[1], b[2], b[3] === 0 ? '#ff6b6b' : '#7fd3ff', 0.4, false);
      }
    } else if (tool.kind !== 'select' && hover.current?.cell && !d) {
      const hc = hover.current.cell;
      const q = quad(hc[0], hc[2], hc[0] + 1, hc[2] + 1, hc[1] + 1.02);
      if (q) {
        poly(q);
        ctx.strokeStyle = '#ffffffaa';
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    }
    void now;
  };

  /**
   * The formation for a right-drag: the drafted selected pawns on distinct standable cells spread along the line, each
   * paired with the slot on its side so paths do not cross. `final` waits for the standing heights it lacks; the preview
   * asks for them (one read in flight) and draws with what it has.
   */
  const formation = async (d: DragState, final: boolean): Promise<Formation | null> => {
    const props = p.current,
      st = props.state,
      e = engine();
    if (!st || !e || !d.startCell || !d.endCell) return null;
    const sel = new Set(props.selection.bodies);
    const pawns = st.pawns.filter((pw) => sel.has(pw.name) && pw.pos && pw.online !== false);
    const drafted = pawns.filter((pw) => pw.drafted);
    if (!drafted.length) return null;
    const level = props.level;
    const a: P2 = { x: d.startCell[0] + 0.5, z: d.startCell[2] + 0.5 },
      b: P2 = { x: d.endCell[0] + 0.5, z: d.endCell[2] + 0.5 };
    const raw = lineSlots(a, b, drafted.length);
    const sc = stand.current;
    if (sc.level !== level) Object.assign(sc, { level, cols: new Map() });
    const missing: Array<[number, number]> = [];
    const seen = new Set<string>();
    for (const s of raw)
      for (let dx = -SNAP_R; dx <= SNAP_R; dx++)
        for (let dz = -SNAP_R; dz <= SNAP_R; dz++) {
          const x = Math.floor(s.x) + dx,
            z = Math.floor(s.z) + dz,
            k = `${x},${z}`;
          if (!sc.cols.has(k) && !seen.has(k)) {
            seen.add(k);
            missing.push([x, z]);
          }
        }
    if (missing.length && (final || !sc.busy)) {
      const read = readStand(e, missing, level);
      if (final) await read;
    }
    const feet = (x: number, z: number) => sc.cols.get(`${x},${z}`);
    const cells = snapCells(raw, (x, z) => feet(x, z) !== null, SNAP_R);
    const pairs = assignAlong(
      drafted.map((pw) => ({ key: pw.name, x: pw.pos?.[0] ?? 0, z: pw.pos?.[2] ?? 0 })),
      cells.map(([x, z]) => ({ x: x + 0.5, z: z + 0.5 })),
      a,
      b,
    );
    const moves = pairs.map(([pawn, i]): FormationMove => {
      const [x, z] = cells[i]!;
      return { pawn, target: [x, feet(x, z) ?? level + 1, z] };
    });
    return { a, b, moves, ignored: pawns.length - drafted.length };
  };

  /** Reads the standing heights of columns (level - 4 .. level + 6) into the cache; columns not loaded count as the level. */
  const readStand = async (e: WorldViewerEngine, cols: Array<[number, number]>, level: number): Promise<void> => {
    const sc = stand.current;
    sc.busy = true;
    const y0 = level - 4,
      n = 11;
    const pts: number[] = [];
    for (const [x, z] of cols) for (let j = 0; j < n; j++) pts.push(x, y0 + j, z);
    try {
      const out = await e.runtime.host.blocksAt(0, pts);
      if (sc.level !== level) return;
      cols.forEach(([x, z], c) => {
        const ids = Array.from({ length: n }, (_, j) => out[(c * n + j) * 3]!);
        const f = standFeet(ids, y0, level);
        sc.cols.set(`${x},${z}`, f === undefined ? level + 1 : f);
      });
    } catch {
      /* the worker is gone: the formation keeps the level */
    } finally {
      sc.busy = false;
    }
  };

  /** The formation preview: the dashed line and a ghost token (the pawn's colour and initials) on every slot. */
  const drawFormation = (ctx: CanvasRenderingContext2D, m: ViewMats, f: Formation) => {
    const y = p.current.level + 1.02;
    const s1 = project(m, f.a.x, y, f.a.z),
      s2 = project(m, f.b.x, y, f.b.z);
    if (s1 && s2) {
      ctx.strokeStyle = COLORS.order;
      ctx.lineWidth = 2;
      ctx.setLineDash([7, 5]);
      ctx.beginPath();
      ctx.moveTo(s1.x, s1.y);
      ctx.lineTo(s2.x, s2.y);
      ctx.stroke();
      ctx.setLineDash([]);
    }
    ctx.font = '700 9px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const mv of f.moves) {
      const at = project(m, mv.target[0] + 0.5, mv.target[1] + 0.02, mv.target[2] + 0.5);
      if (!at) continue;
      const col = pawnColor(mv.pawn);
      ctx.fillStyle = 'rgba(10,12,16,0.55)';
      ctx.beginPath();
      ctx.arc(at.x, at.y, 9, 0, Math.PI * 2);
      ctx.fill();
      ctx.strokeStyle = col;
      ctx.lineWidth = 2;
      ctx.setLineDash([3, 2]);
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = col;
      const name = p.current.state?.pawns.find((x) => x.name === mv.pawn)?.display ?? mv.pawn;
      ctx.fillText(name.slice(0, 2).toUpperCase(), at.x, at.y + 0.5);
    }
    if (f.ignored && s2) label(ctx, s2, `${f.ignored} undrafted: not in the line`, false);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
  };

  const GLYPH_OF: Record<string, string> = {
    chop: 'chop',
    mine: 'mine',
    harvest: 'harvest',
    cut: 'cut',
    haul: 'haul',
    hunt: 'hunt',
    build: 'build',
    deconstruct: 'deconstruct',
    attack: 'hunt',
    craft: 'bench',
    cook: 'bench',
  };

  const drawToolDrag = (
    ctx: CanvasRenderingContext2D,
    m: ViewMats,
    d: DragState,
    quad: (x1: number, z1: number, x2: number, z2: number, y: number) => Pt[] | null,
    poly: (pts: Pt[]) => void,
    mark: (id: string, at: Pt, color: string, size?: number) => void,
    cube: (x: number, y: number, z: number, color: string, alpha: number, faint: boolean) => void,
  ) => {
    const props = p.current;
    const tool = props.tool;
    const a = d.startCell!,
      b = d.endCell!;
    const rect = cellRect(a, b);
    if (tool.kind === 'build') {
      const pv = buildPreview(buildItem(tool.id, a, b, { ...props.build, line: d.alt }));
      const col = pv.valid ? (pv.item.kind === 'remove' ? '#ff6b6b' : '#7fd3ff') : '#ff6b6b';
      for (const g of pv.blocks.slice(0, 1500)) cube(g[0], g[1], g[2], col, 0.42, false);
      label(
        ctx,
        d.now,
        `${pv.blocks.length} block${pv.blocks.length === 1 ? '' : 's'}${pv.item.kind !== 'remove' ? ` · ${pv.cost} ${typeof pv.item.material === 'string' ? pv.item.material.replace(/_/g, ' ') : 'blocks'}` : ''}${pv.reason ? ` · ${pv.reason}` : ''}`,
        pv.valid,
      );
      return;
    }
    const q = quad(rect[0], rect[1], rect[2] + 1, rect[3] + 1, props.level + 1.02);
    const col =
      tool.kind === 'designate'
        ? (COLORS.designation[tool.id] ?? '#ffffff')
        : tool.kind === 'zone'
          ? zoneColor(tool.id)
          : COLORS.chest;
    if (q) {
      poly(q);
      ctx.fillStyle = `${col}22`;
      ctx.fill();
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.5;
      ctx.stroke();
      // the grid
      const upp = unitsPerPx(engine()!.rig.isoZoom, m.h);
      if (1 / upp >= 9 && (rect[2] - rect[0]) * (rect[3] - rect[1]) < 2500) {
        ctx.strokeStyle = `${col}33`;
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let x = rect[0] + 1; x <= rect[2]; x++) {
          const s1 = project(m, x, props.level + 1.02, rect[1]),
            s2 = project(m, x, props.level + 1.02, rect[3] + 1);
          if (s1 && s2) {
            ctx.moveTo(s1.x, s1.y);
            ctx.lineTo(s2.x, s2.y);
          }
        }
        for (let z = rect[1] + 1; z <= rect[3]; z++) {
          const s1 = project(m, rect[0], props.level + 1.02, z),
            s2 = project(m, rect[2] + 1, props.level + 1.02, z);
          if (s1 && s2) {
            ctx.moveTo(s1.x, s1.y);
            ctx.lineTo(s2.x, s2.y);
          }
        }
        ctx.stroke();
      }
    }
    if (tool.kind === 'designate') {
      const res = scan.current.result;
      const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
      if (res?.height && res.cols) {
        // Exactly what will be designated: each column's top face outlined (fuller the deeper it goes), its bottom dashed.
        const deep = res.height.layers > 1;
        ctx.lineWidth = 1.2;
        for (const [x, z, top, bottom, n] of res.cols.slice(0, 2500)) {
          const qt = quad(x + 0.08, z + 0.08, x + 0.92, z + 0.92, top + 1.01);
          if (!qt) continue;
          poly(qt);
          ctx.fillStyle = `${col}${n > 1 ? (n > 3 ? '66' : '44') : '22'}`;
          ctx.fill();
          ctx.strokeStyle = res.height.tooMany ? '#ff6b6b' : col;
          ctx.stroke();
          if (deep && bottom < top && props.view === 'angled') {
            const qb = quad(x + 0.08, z + 0.08, x + 0.92, z + 0.92, bottom + 0.01);
            if (qb) {
              poly(qb);
              ctx.setLineDash([3, 3]);
              ctx.stroke();
              ctx.setLineDash([]);
            }
          }
        }
        const mode = props.heightMode;
        label(ctx, d.now, heightLabel(t.label, mode, res.height, props.layers), !res.height.tooMany && res.count > 0);
      } else if (res) {
        for (const c of res.cells.slice(0, 1200)) {
          const s = project(m, c[0] + 0.5, c[1] + 1, c[2] + 0.5);
          if (s) {
            ctx.fillStyle = `${col}bb`;
            ctx.fillRect(s.x - 3, s.y - 3, 6, 6);
          }
        }
        for (const en of res.entities) {
          const s = project(m, en.x, en.y + 0.3, en.z);
          if (s) mark(tool.id, s, col, 12);
        }
        if (tool.id === 'cancel') {
          for (const dd of props.state?.designations ?? []) {
            const bb = designationCells(dd)[0];
            if (bb && bb[0] >= rect[0] && bb[0] <= rect[2] && bb[2] >= rect[1] && bb[2] <= rect[3]) {
              const s = project(m, bb[0] + 0.5, bb[1] + 1, bb[2] + 0.5);
              if (s) {
                ctx.strokeStyle = '#ff6b6b';
                ctx.lineWidth = 2;
                ctx.beginPath();
                ctx.moveTo(s.x - 6, s.y - 6);
                ctx.lineTo(s.x + 6, s.y + 6);
                ctx.moveTo(s.x + 6, s.y - 6);
                ctx.lineTo(s.x - 6, s.y + 6);
                ctx.stroke();
              }
            }
          }
        }
        label(ctx, d.now, `${t.label}: ${res.count} ${res.count === 1 ? t.noun[0] : t.noun[1]}`, true);
      } else label(ctx, d.now, `${t.label}: …`, true);
    } else {
      label(ctx, d.now, `${rect[2] - rect[0] + 1} × ${rect[3] - rect[1] + 1}`, true);
    }
  };

  const label = (ctx: CanvasRenderingContext2D, at: Pt, text: string, ok: boolean) => {
    ctx.font = '600 12px system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(text).width + 12;
    ctx.fillStyle = 'rgba(10,12,16,0.85)';
    ctx.fillRect(at.x + 14, at.y + 10, w, 20);
    ctx.fillStyle = ok ? '#f4f6f8' : '#ff8a8a';
    ctx.fillText(text, at.x + 20, at.y + 20);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
  };

  // ------------------------------------------------------------------ input

  const local = (ev: { clientX: number; clientY: number }): Pt => {
    const r = canvas.current!.getBoundingClientRect();
    return { x: ev.clientX - r.left, y: ev.clientY - r.top };
  };

  /** The level's floor plane under a point (top-down placement and drags). */
  const groundCell = (pt: Pt): V3 | null => {
    const m = mats.current;
    if (!m) return null;
    return cellAt(m, pt.x, pt.y, p.current.level + 1);
  };

  const pick = async (pt: Pt): Promise<(PickedBlock & { face: number }) | null> => {
    const e = engine(),
      m = mats.current;
    if (!e || !m) return null;
    const r = rayAt(m, pt.x, pt.y);
    const hit = await e.runtime.host.pick(0, r.o, r.d, 400, e.pickCut()).catch(() => null);
    if (!hit) return null;
    return { ...hit, name: '', dim: 0 };
  };

  /** Where a build starts: the level's first layer in top-down, the face against the hovered block in the angled view. */
  const startCellOf = (h: { cell: V3 | null; face: V3 | null }): V3 | null => {
    if (p.current.view === 'angled' && h.face) return h.face;
    return h.cell ? [h.cell[0], p.current.level + 1, h.cell[2]] : null;
  };

  /** Is there something drafted pawns can attack under the point (a mob, an animal, a player, an AI bot; never a pawn)? */
  const attackableAt = (pt: Pt): boolean => {
    const b = bodyAt(pt);
    if (b) return b.kind === 'ai';
    const m = mats.current;
    const e = engine();
    if (!m || !e) return false;
    const pawns = p.current.state?.pawns ?? [];
    for (const x of e.runtime.store.entities(0)) {
      if (x.kind !== 'mob' && x.kind !== 'player') continue;
      if (x.kind === 'player' && pawns.some((pw) => pw.name === x.name)) continue;
      const s = project(m, x.x, x.y + 0.4, x.z);
      if (s && Math.hypot(s.x - pt.x, s.y - pt.y) <= 18) return true;
    }
    return false;
  };
  const attackAt = useRef(0);

  const updateHover = (pt: Pt) => {
    const now = performance.now();
    const cv = canvas.current;
    if (cv && p.current.attackMode && now - attackAt.current > 50) {
      attackAt.current = now;
      const on = attackableAt(pt);
      if ((cv.dataset.attack === '1') !== on) {
        cv.dataset.attack = on ? '1' : '';
        cv.style.cursor = on ? ATTACK_CURSOR : '';
      }
    } else if (cv && !p.current.attackMode && cv.dataset.attack === '1') {
      cv.dataset.attack = '';
      cv.style.cursor = '';
    }
    const cell = groundCell(pt);
    const h = hover.current ?? { at: pt, cell, face: null, pickAt: 0 };
    h.at = pt;
    h.cell = cell;
    hover.current = h;
    const tool = p.current.tool;
    if (p.current.view === 'angled' && (tool.kind === 'build' || tool.kind === 'design') && now - h.pickAt > 60) {
      h.pickAt = now;
      void pick(pt).then((hit) => {
        if (!hover.current) return;
        hover.current.face = hit ? faceCell(hit) : null;
        if (hit) hover.current.cell = [hit.x, hit.y, hit.z];
      });
    }
  };

  /** What a designate drag's preview depends on: the rectangle, the tool and its filter, height mode and level. */
  const scanKey = (rect: readonly number[], toolId: string): string => {
    const pr = p.current;
    return `${rect.join(',')}|${toolId}|${pr.mineFilter}|${pr.heightMode}|${pr.layers}|${pr.level}|${pr.cutY}`;
  };

  /** A Mine / Deconstruct drag's exact cells: each column's top and what its height mode takes under it. */
  const heightScan = async (
    e: WorldViewerEngine,
    tool: 'mine' | 'deconstruct',
    rect: readonly number[],
  ): Promise<HeightTargets> => {
    const pr = p.current;
    const q = heightQuery(pr.heightMode, pr.level, pr.layers, pr.cutY);
    const max = 4 * MAX_DRAG_CELLS;
    const raw = await e.runtime.host.stacks(
      0,
      [rect[0]!, q.min, rect[1]!],
      [rect[2]!, q.max, rect[3]!],
      [...NOT_GROUND_IDS],
      q.depth,
      q.above,
      max,
    );
    const t = heightTargets(tool, pr.heightMode, raw, pr.level, tool === 'mine' ? pr.mineFilter : 'all');
    return raw.length >= max * 6 ? { ...t, tooMany: true } : t;
  };

  /** Drops the drag in progress (Esc, the other button, leaving full screen): no preview, nothing on release. */
  const dropDrag = () => {
    drag.current = null;
    scan.current = { key: '', busy: false, result: null };
    formationShown.current = null;
    lockEsc(false);
    p.current.onCancelled?.();
  };

  /** Rescans a designate drag's targets (one scan in flight; the latest rectangle wins). */
  const rescan = () => {
    const d = drag.current,
      e = engine();
    const tool = p.current.tool;
    if (!d || !e || !d.startCell || !d.endCell || tool.kind !== 'designate') return;
    const rect = cellRect(d.startCell, d.endCell);
    const key = scanKey(rect, tool.id);
    if (scan.current.key === key || scan.current.busy) return;
    scan.current.key = key;
    const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
    const y = toolYRange(tool.id, p.current.level, p.current.build.height, p.current.cutY);
    const finish = (cells: V3[], count: number, ents: EntityLite[], height?: HeightTargets) => {
      if (drag.current !== d) return; // cancelled meanwhile
      scan.current.result = {
        rect,
        a: d.startCell!,
        b: d.endCell!,
        cells,
        entities: ents,
        count,
        ...(height ? { height, cols: columnSpans(height.cells) } : {}),
      };
    };
    if (t.entities) {
      const ents = entitiesIn(t, rect, entityList());
      finish([], ents.length, ents);
      return;
    }
    if (tool.id === 'cancel') {
      const n = (p.current.state?.designations ?? []).filter((dd) => {
        const bb = designationCells(dd)[0];
        return bb && bb[0] >= rect[0] && bb[0] <= rect[2] && bb[2] >= rect[1] && bb[2] <= rect[3];
      }).length;
      finish([], n, []);
      return;
    }
    scan.current.busy = true;
    const job = hasHeight(tool.id)
      ? heightScan(e, tool.id, rect).then((ht) => finish(ht.cells, ht.cells.length, [], ht))
      : e.runtime.host
          .scan(0, [rect[0], y[0], rect[1]], [rect[2], y[1], rect[3]], scanIds(tool.id), 6000)
          .then((s) => {
            const pv = previewTargets(tool.id, s, p.current.mineFilter);
            finish(pv.cells, pv.count, []);
          });
    void job
      .catch(() => undefined)
      .finally(() => {
        scan.current.busy = false;
        const dd = drag.current;
        if (dd) {
          const r2 = dd.startCell && dd.endCell ? scanKey(cellRect(dd.startCell, dd.endCell), tool.id) : '';
          if (r2 !== scan.current.key) rescan();
        }
      });
  };

  /** Another button went down during a drag: right during a left drag (left during a right drag) cancels it. */
  const chord = (button: number) => {
    if (gate.current.chord(button)) dropDrag();
  };

  const onPointerDown = (ev: React.PointerEvent<HTMLCanvasElement>) => {
    const pt = local(ev);
    if (gate.current.button !== null && (ev.buttons & ~buttonBit(ev.button)) !== 0) {
      // A second button while one is held (browsers that send it as a down, not a chorded move).
      chord(ev.button);
      return;
    }
    gate.current.start(ev.button);
    if (ev.button !== 1) lockEsc(true);
    canvas.current?.setPointerCapture(ev.pointerId);
    canvas.current?.focus({ preventScroll: true });
    updateHover(pt);
    if (ev.button === 2) {
      // The order menu on up; a right-drag with drafted pawns selected lines them up instead.
      const cell = groundCell(pt);
      drag.current = {
        start: pt,
        now: pt,
        button: 2,
        shift: ev.shiftKey,
        alt: false,
        startCell: cell,
        endCell: cell,
        moved: false,
      };
      stand.current = { level: p.current.level, cols: new Map(), busy: false };
      formationShown.current = null;
      return;
    }
    const startCell =
      (p.current.tool.kind === 'build' || p.current.tool.kind === 'design') && hover.current
        ? startCellOf(hover.current)
        : groundCell(pt);
    drag.current = {
      start: pt,
      now: pt,
      button: ev.button,
      shift: ev.shiftKey,
      alt: ev.altKey,
      startCell,
      endCell: startCell,
      moved: false,
    };
    scan.current = { key: '', busy: false, result: null };
  };

  const onPointerMove = (ev: React.PointerEvent<HTMLCanvasElement>) => {
    const pt = local(ev);
    // A chorded press: pointer events report a second button going down as a move with `button` set.
    if (ev.button >= 0 && (ev.buttons & buttonBit(ev.button)) !== 0 && gate.current.button !== null) chord(ev.button);
    const d = drag.current;
    if (d && d.button === 1) {
      // Middle-drag pans (Shift + middle-drag turns the angled view).
      const e = engine();
      if (e) {
        const dx = pt.x - d.now.x,
          dy = pt.y - d.now.y;
        if (ev.shiftKey && e.rig.mode === 'iso') e.rig.look(dx, dy);
        else e.rig.pan(dx, dy, canvas.current!.clientHeight);
      }
      d.now = pt;
      d.moved = true;
      return;
    }
    updateHover(pt);
    if (!d) return;
    if (d.button === 2) {
      d.now = pt;
      d.endCell = groundCell(pt) ?? d.endCell;
      if (!d.moved && isDrag(d.start, pt)) d.moved = true;
      return;
    }
    d.now = pt;
    d.alt = ev.altKey;
    if (!d.moved && isDrag(d.start, pt)) d.moved = true;
    const tool = p.current.tool;
    if (tool.kind === 'build') {
      const h = hover.current;
      const c = h ? startCellOf(h) : null;
      if (c && d.startCell)
        d.endCell =
          p.current.view === 'angled' &&
          (tool.id === 'wall' || tool.id === 'floor' || tool.id === 'roof' || tool.id === 'fence')
            ? [c[0], d.startCell[1], c[2]]
            : c;
    } else d.endCell = groundCell(pt) ?? d.endCell;
    if (d.moved && tool.kind === 'designate') rescan();
  };

  /** The body under a point: the nearest within its own hit radius (a model's covers its height). */
  const bodyAt = (pt: Pt) => {
    let best: ReturnType<typeof bodies>[number] | null = null,
      bd = Infinity;
    for (const b of bodies(0)) {
      if (!b.at) continue;
      const d = Math.hypot(b.at.x - pt.x, b.at.y - pt.y);
      if (d <= b.r && d < bd) {
        bd = d;
        best = b;
      }
    }
    return best;
  };

  const onPointerUp = async (ev: React.PointerEvent<HTMLCanvasElement>) => {
    const pt = local(ev);
    if (gate.current.release(ev.buttons) !== 'act') return; // cancelled: the release (and the cancelling click) do nothing
    lockEsc(false);
    const d = drag.current;
    drag.current = null;
    const props = p.current;
    if (ev.button === 2) {
      // Right click: drops the active tool (the genre); with something selected, the order menu for the thing under it.
      if (props.tool.kind !== 'select') {
        props.onDropTool();
        return;
      }
      if (d && d.button === 2 && d.moved && props.onFormation) {
        const f = await formation(d, true);
        if (f && f.moves.length) {
          props.onFormation(f.moves, ev.shiftKey, f.ignored);
          return;
        }
      }
      const target = await targetAt(pt);
      if (target) props.onContext(target, { x: ev.clientX, y: ev.clientY });
      return;
    }
    if (!d || d.button !== 0) return;
    const tool = props.tool;
    if (tool.kind === 'select') {
      if (d.moved) {
        const r: Rect = rectOf(d.start, pt);
        const inBox = bodies(0).filter(
          (b) => b.at && b.at.x >= r.x && b.at.x <= r.x + r.w && b.at.y >= r.y && b.at.y <= r.y + r.h,
        );
        if (inBox.length) {
          props.onSelect({
            bodies: d.shift
              ? [...new Set([...props.selection.bodies, ...inBox.map((b) => b.key)])]
              : inBox.map((b) => b.key),
            chests: [],
            bench: null,
          });
          return;
        }
        // No bodies: the chests in the box (edit their storage together).
        const m = mats.current;
        const boxChests = chests.current.cells
          .filter((c) => c.id === 54 || c.id === 146)
          .filter((c) => {
            const s = m ? project(m, c.pos[0] + 0.5, c.pos[1] + 1, c.pos[2] + 0.5) : null;
            return s && s.x >= r.x && s.x <= r.x + r.w && s.y >= r.y && s.y <= r.y + r.h;
          })
          .map((c) => c.pos);
        props.onSelect({ bodies: d.shift ? props.selection.bodies : [], chests: boxChests, bench: null });
        return;
      }
      // A bed mark under the cursor wins over a body standing farther from the click (pawns sleep on their beds).
      const m = mats.current;
      const bedHit = m
        ? (props.state?.beds ?? [])
            .map((b) => ({ b, s: project(m, b.pos[0] + 0.5, b.pos[1] + 0.6, b.pos[2] + 0.5) }))
            .map((x) => ({ ...x, d: x.s ? Math.hypot(x.s.x - pt.x, x.s.y - pt.y) : Infinity }))
            .filter((x) => x.d <= 10)
            .sort((a, b) => a.d - b.d)[0]
        : undefined;
      const hit = bodyAt(pt);
      if (bedHit && (!hit?.at || Math.hypot(hit.at.x - pt.x, hit.at.y - pt.y) > bedHit.d)) {
        props.onSelect({
          bodies: [],
          chests: [],
          bench: null,
          bed: [bedHit.b.pos[0], bedHit.b.pos[1], bedHit.b.pos[2]],
        });
        return;
      }
      // A failed designation's ring under the cursor (nearer than any body): its inspector (why, retry, cancel).
      const failHit = failedAt.current
        .map((f) => ({ f, d: Math.hypot(f.x - pt.x, f.y - pt.y) }))
        .filter((x) => x.d <= 12)
        .sort((a, b) => a.d - b.d)[0];
      if (failHit && (!hit?.at || Math.hypot(hit.at.x - pt.x, hit.at.y - pt.y) > failHit.d)) {
        props.onSelect({ ...EMPTY_SEL, designation: { id: failHit.f.id, cluster: failHit.f.cluster } });
        return;
      }
      if (hit) {
        const now = performance.now();
        if (lastClick.current && lastClick.current.name === hit.key && now - lastClick.current.t < 350) {
          const visible = bodies(0).filter(
            (b) =>
              b.kind === hit.kind &&
              b.at &&
              b.at.x >= 0 &&
              b.at.y >= 0 &&
              b.at.x <= canvas.current!.clientWidth &&
              b.at.y <= canvas.current!.clientHeight,
          );
          props.onSelectSame(
            hit.kind,
            visible.map((b) => b.key),
          );
          lastClick.current = null;
          return;
        }
        lastClick.current = { t: now, name: hit.key };
        const sel = props.selection;
        const has = sel.bodies.includes(hit.key);
        props.onSelect({
          bodies:
            ev.ctrlKey || ev.metaKey
              ? has
                ? sel.bodies.filter((n) => n !== hit.key)
                : [...sel.bodies, hit.key]
              : ev.shiftKey
                ? has
                  ? sel.bodies
                  : [...sel.bodies, hit.key]
                : [hit.key],
          chests: [],
          bench: null,
        });
        return;
      }
      // A chest or a workbench under the cursor.
      const block = await pick(pt);
      if (block && (block.id === 54 || block.id === 146)) {
        const pos: V3 = [block.x, block.y, block.z];
        const s = (props.state?.storages ?? []).find(
          (x) => k3(x.chest) === k3(pos) || (x.doubleWith && k3(x.doubleWith) === k3(pos)),
        );
        const chest: V3 = s ? s.chest : pos;
        const sel = props.selection;
        const has = sel.chests.some((c) => k3(c) === k3(chest));
        props.onSelect({
          bodies: [],
          chests: ev.shiftKey
            ? has
              ? sel.chests.filter((c) => k3(c) !== k3(chest))
              : [...sel.chests, chest]
            : [chest],
          bench: null,
        });
        return;
      }
      if (block && block.id === 26) {
        const bed = (props.state?.beds ?? []).find(
          (b) => Math.abs(b.pos[0] - block.x) + Math.abs(b.pos[2] - block.z) <= 1 && Math.abs(b.pos[1] - block.y) <= 1,
        );
        if (bed) {
          props.onSelect({ bodies: [], chests: [], bench: null, bed: [bed.pos[0], bed.pos[1], bed.pos[2]] });
          return;
        }
      }
      if (block && (block.id === 58 || block.id === 61 || block.id === 62)) {
        props.onSelect({ bodies: [], chests: [], bench: [block.x, block.y, block.z] });
        return;
      }
      if (!ev.shiftKey) props.onSelect({ bodies: [], chests: [], bench: null });
      return;
    }
    // A tool: a click is a 1×1 drag.
    if (!d.startCell) return;
    const end = d.endCell ?? d.startCell;
    if (tool.kind === 'designate') {
      const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
      const rect = cellRect(d.startCell, end);
      const y = toolYRange(tool.id, props.level, props.build.height, props.cutY);
      let res: DragResult | null = scan.current.result;
      if (!res || scan.current.key !== scanKey(rect, tool.id) || res.rect.join(',') !== rect.join(',')) {
        // A click, or the scan had not caught up: scan now.
        const e = engine();
        if (e && hasHeight(tool.id)) {
          const ht = await heightScan(e, tool.id, rect).catch(() => null);
          res = {
            rect,
            a: d.startCell,
            b: end,
            cells: ht?.cells ?? [],
            entities: [],
            count: ht?.cells.length ?? 0,
            ...(ht ? { height: ht } : {}),
          };
        } else if (t.entities) {
          const ents = entitiesIn(t, rect, entityList());
          res = { rect, a: d.startCell, b: end, cells: [], entities: ents, count: ents.length };
        } else if (e && tool.id !== 'cancel') {
          const s = await e.runtime.host
            .scan(0, [rect[0], y[0], rect[1]], [rect[2], y[1], rect[3]], scanIds(tool.id), 6000)
            .catch(() => []);
          const pv = previewTargets(tool.id, s, props.mineFilter);
          res = { rect, a: d.startCell, b: end, cells: pv.cells, entities: [], count: pv.count };
        } else res = { rect, a: d.startCell, b: end, cells: [], entities: [], count: 0 };
      }
      void toolArea;
      props.onTool({ ...res, moved: d.moved }, ev.shiftKey);
      return;
    }
    props.onTool(
      { rect: cellRect(d.startCell, end), a: d.startCell, b: end, cells: [], entities: [], count: 0, moved: d.moved },
      ev.shiftKey,
    );
  };

  /** What is under the cursor for the order menu: a ghost, a mob or a drop, a block, or bare ground. */
  const targetAt = async (pt: Pt): Promise<Target | null> => {
    const m = mats.current;
    const st = p.current.state;
    const e = engine();
    if (!m || !e) return null;
    const ents = e.runtime.store.entities(0);
    // An AI body (a hive bot) under the cursor: attacked by name (bots have no entity id in the viewer). Pawns are never targets.
    const body = bodyAt(pt);
    if (body?.kind === 'ai')
      return { kind: 'player', entity: null, name: body.key, bot: true, x: body.pos[0], y: body.pos[1], z: body.pos[2] };
    let best: Target | null = null;
    let bd = 18;
    for (const x of ents) {
      const s = project(m, x.x, x.y + 0.4, x.z);
      if (!s) continue;
      const dd = Math.hypot(s.x - pt.x, s.y - pt.y);
      if (dd > bd) continue;
      bd = dd;
      if (x.kind === 'item') best = { kind: 'item', entity: x.id, name: x.name ?? 'the drop', x: x.x, y: x.y, z: x.z };
      else if (x.kind === 'player' && x.name && !(st?.pawns ?? []).some((pw) => pw.name === x.name))
        best = {
          kind: 'player',
          entity: x.id,
          name: x.name,
          bot: (st?.ai ?? []).some((a) => a.name === x.name),
          x: x.x,
          y: x.y,
          z: x.z,
        };
      else if (x.kind === 'mob')
        best = {
          kind: 'mob',
          entity: x.id,
          name: x.name ?? 'the mob',
          animal: [90, 91, 92, 93, 94, 95, 96, 98, 100, 101, 102, 103].includes(x.type),
          x: x.x,
          y: x.y,
          z: x.z,
        };
    }
    if (best) return best;
    const cell = groundCell(pt);
    if (cell) {
      const g = (st?.ghosts?.blocks ?? []).find((b) => b[0] === cell[0] && b[2] === cell[2] && b[6] !== 'done');
      if (g) {
        const build = (st?.builds ?? []).find((b) => String(b.id) === String(g[5]));
        return { kind: 'ghost', build: g[5], label: build?.label ?? 'build', x: g[0], y: g[1], z: g[2] };
      }
    }
    const hit = await pick(pt);
    if (hit) return { kind: 'block', x: hit.x, y: hit.y, z: hit.z, id: hit.id, name: blockLabel(hit.id) };
    return cell ? { kind: 'ground', x: cell[0], y: p.current.level + 1, z: cell[2] } : null;
  };

  const onWheel = (ev: React.WheelEvent<HTMLCanvasElement>) => {
    const e = engine(),
      m = mats.current;
    if (!e || !m) return;
    const pt = local(ev);
    const r = rayAt(m, pt.x, pt.y);
    const at = rayPlaneY(r.o, r.d, e.rig.target.y) ?? [e.rig.target.x, 0, e.rig.target.z];
    const z = zoomAt({ x: e.rig.target.x, z: e.rig.target.z }, e.rig.isoZoom, Math.sign(ev.deltaY), {
      x: at[0],
      z: at[2],
    });
    e.rig.isoZoom = z.zoom;
    e.rig.target.x = z.x;
    e.rig.target.z = z.z;
  };

  // Leaving full screen mid-drag (the browser's Esc where Esc can't be locked) cancels the drag too.
  useEffect(() => {
    const off = () => {
      if (!document.fullscreenElement && gate.current.escape()) dropDrag();
    };
    document.addEventListener('fullscreenchange', off);
    return () => document.removeEventListener('fullscreenchange', off);
  }, []);

  /** The pointer was taken away (a touch scroll, the window lost it): the drag ends with nothing done. */
  const lost = () => {
    if (gate.current.button === null) return;
    gate.current = new DragGate();
    if (drag.current) dropDrag();
  };

  // The wheel must not scroll the page (a passive React listener cannot prevent it).
  useEffect(() => {
    const c = canvas.current;
    if (!c) return;
    const stop = (e: WheelEvent) => e.preventDefault();
    c.addEventListener('wheel', stop, { passive: false });
    return () => c.removeEventListener('wheel', stop);
  }, [ready]);

  // Tests and tools read the map through this (mock mode only).
  useEffect(() => {
    if (process.env.NEXT_PUBLIC_MOCK !== '1') return;
    (window as unknown as { __story?: unknown }).__story = {
      toPage,
      center: () => engine()?.rig.target.toArray(),
      zoom: () => engine()?.rig.isoZoom,
      setZoom: (z: number) => {
        const e = engine();
        if (e) e.rig.isoZoom = z;
      },
      focus: (x: number, z: number) => {
        const e = engine();
        if (e) e.rig.target.set(x + 0.5, p.current.level + 1, z + 0.5);
      },
      ready: () => !!engine() && !!mats.current,
      /** The live preview of the drag in progress (null = none): what its release would designate. */
      preview: () => {
        const r = scan.current.result;
        if (!drag.current || !r) return null;
        const t = DESIGNATE_TOOLS.find((x) => p.current.tool.kind === 'designate' && x.id === p.current.tool.id);
        return {
          count: r.count,
          cells: r.cells.length,
          label: r.height && t ? heightLabel(t.label, p.current.heightMode, r.height, p.current.layers) : null,
          top: r.height?.top ?? null,
          bottom: r.height?.bottom ?? null,
        };
      },
      /** Block ids at points [[x, y, z], …] (-1 where nothing is held). */
      blocks: async (pts: number[][]) => {
        const e = engine();
        if (!e) return [];
        const r = await e.runtime.host.blocksAt(0, pts.flat());
        return pts.map((_, i) => r[i * 3]!);
      },
      bodies: () =>
        [...shown.current].map(([name, pos]) => ({
          name,
          ...(toPage(pos[0], pos[1] + 0.2, pos[2]) ?? { x: NaN, y: NaN }),
        })),
      fps: () => engine()?.stats().fps,
      /** The viewer's mobs, players and drops with their page position (an attack's right-click). */
      entities: () =>
        (engine()?.runtime.store.entities(0) ?? []).map((x) => ({
          id: x.id,
          kind: x.kind,
          name: x.name,
          type: x.type,
          ...(toPage(x.x, x.y + 0.4, x.z) ?? { x: NaN, y: NaN }),
        })),
      /** The canvas cursor is the attack cursor. */
      attackCursor: () => canvas.current?.dataset.attack === '1',
      /** Each body: how it is shown (model, token, cut) and whether the viewer drew its model last frame. */
      models: () =>
        bodies(0).map((b) => ({ name: b.key, look: b.look, model: engine()?.runtime.entities.detailOf(b.key) ?? null })),
      /** The viewer's player models drawn last frame. */
      botModels: () => engine()?.runtime.entities.botModels ?? 0,
      holograms: () => engine()?.hologramStats() ?? null,
      hologramAt: (x: number, y: number, z: number) => engine()?.hologramAt(x, y, z) ?? false,
      ghosts: () => p.current.state?.ghosts?.blocks ?? [],
      mode: () => engine()?.rig.mode,
      level: () => p.current.level,
      beds: () => p.current.state?.beds ?? [],
      /** Failed designations: their cell, and their ring on screen when drawn this frame. */
      failed: () =>
        (p.current.state?.designations ?? [])
          .filter((d) => d.status === 'failed')
          .map((d) => {
            const at = failedAt.current.find((f) => String(f.id) === String(d.id));
            return { id: d.id, cell: designationCells(d)[0] ?? null, x: at?.x ?? null, y: at?.y ?? null };
          }),
    };
  });

  const layers = useMemo(
    () => ({
      bots: true,
      entities: true,
      claims: false,
      places: false,
      points: false,
      plans: false,
      dangers: false,
      notes: false,
      weather: false,
      cutaway: false,
      staleness: false,
      changes: true,
      aids: false,
    }),
    [],
  );

  // The viewer's element is kept while its inputs stay (React then skips its subtree): the page re-renders on every state
  // poll, and the viewer needs none of that (the overlay reads the state from a ref each frame).
  const cameraMode = props.view === 'top' ? 'top' : 'iso';
  const followName = mod && source ? '' : (props.state?.pawns[0]?.name ?? '');
  const viewerEl = useMemo(
    () =>
      mod && source ? (
        <mod.WorldViewer
          ref={viewer}
          source={source}
          dim={0}
          follow={null}
          layers={layers}
          cameraMode={cameraMode}
          xray="off"
          drawDistance={112}
          hideHint
          className="absolute inset-0"
        />
      ) : (
        <DotMap follow={followName} radius={40} className="absolute inset-0" label={false} />
      ),
    [mod, source, layers, cameraMode, followName],
  );

  if (!ready) return <div className="absolute inset-0 bg-canvas" aria-busy="true" />;
  return (
    <div ref={wrap} className="absolute inset-0 overflow-hidden bg-canvas" data-testid="story-map">
      {viewerEl}
      <canvas
        ref={canvas}
        tabIndex={0}
        aria-label="Colony map: drag to select, right-click for orders, wheel to zoom, WASD to pan"
        className="absolute inset-0 size-full cursor-crosshair outline-none"
        style={{ touchAction: 'none' }}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={(e) => void onPointerUp(e)}
        onPointerCancel={lost}
        onMouseDown={(e) => {
          // Mouse events still report every button press (pointer events chord them into moves): a fallback for the cancel.
          if (gate.current.button !== null && e.button !== gate.current.button) chord(e.button);
        }}
        onPointerLeave={() => (hover.current = null)}
        onWheel={onWheel}
        onContextMenu={(e) => e.preventDefault()}
        onAuxClick={(e) => e.preventDefault()}
      />
      <div
        ref={labelsEl}
        className="pointer-events-none absolute inset-0 overflow-hidden"
        data-testid="designation-labels"
      />
    </div>
  );
});

/** The attack cursor (crossed swords) while drafted pawns are selected and an attackable entity is under the pointer. */
const ATTACK_CURSOR =
  "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24' fill='none' stroke='%23e5484d' stroke-width='2.5' stroke-linecap='round'%3E%3Cpath d='M14.5 17.5 3 6V3h3l11.5 11.5M13 19l6-6M16 16l4 4M19 21l2-2M9.5 17.5 21 6V3h-3L6.5 14.5M11 19l-6-6M8 16l-4 4M5 21l-2-2'/%3E%3C/svg%3E\") 12 12, crosshair";

/** Labels show from this zoom (px per block) in; the rings show at any zoom. */
const LABEL_ZOOM = 14;
/** A failed designation's label: a calm pill in the theme's colours, readable in light and dark, 11px. */
const LABEL_CLASS =
  'pointer-events-auto absolute top-0 left-0 max-w-64 cursor-pointer truncate rounded-full border border-danger/50 bg-surface/90 px-2 ' +
  'py-px text-[11px] leading-[16px] font-semibold whitespace-nowrap text-fg shadow-sm hover:bg-surface';
/** A designation waiting for a tool ("Needs a stone pickaxe"): the same pill, neutral (it isn't failing). */
const WAIT_LABEL_CLASS = LABEL_CLASS.replace('border-danger/50', 'border-border-strong text-muted');
const EMPTY_SEL: Selection = { bodies: [], chests: [], bench: null };
const EMPTY_LIST: StoryColonyState['designations'] = [];

const BLOCK_LABELS: Record<number, string> = {
  1: 'stone',
  2: 'grass',
  3: 'dirt',
  4: 'cobblestone',
  5: 'planks',
  12: 'sand',
  13: 'gravel',
  14: 'gold ore',
  15: 'iron ore',
  16: 'coal ore',
  17: 'log',
  18: 'leaves',
  20: 'glass',
  56: 'diamond ore',
  58: 'crafting table',
  61: 'furnace',
  54: 'chest',
};
function blockLabel(id: number): string {
  return BLOCK_LABELS[id] ?? `block ${id}`;
}
