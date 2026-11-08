'use client';

/**
 * The Story Mode map: the shared world viewer in its `top` camera (or the angled `iso` view for 3D building) under one 2D
 * overlay canvas that takes every pointer event and draws the colony (zones, designation markers, build ghosts, chests and
 * workbenches, pawn tokens with names and job marks, AI bodies, selection and drag boxes, order targets, the placement
 * preview). The overlay redraws in one animation-frame loop that also pans (keys, screen edge) and keeps it ≥ 40 fps on
 * software WebGL: no blur, no DOM per pawn, a few hundred canvas primitives.
 */
import type { EntityView, PickedBlock, WorldViewerEngine, WorldViewerHandle } from '@mineclaw/world-viewer';
import { getViewerQuality } from '@mineclaw/world-viewer/settings';
import { designationCells, type StoryBuild, type StoryColonyState } from '@mineclaw/contracts';
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
  inRect,
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
import { anchorLift, bodyLook, ghostDigs, hologramCells, type BodyLook } from './bodyLook';
import { useContainerBadges } from './chestBadges';
import { placeLabels, type FailedCluster, type PlacedLabel } from './failedLabels';
import { designationModel, failedModel, k3, MapPainter, type FailedRing, type MapBody } from './mapDraw';
import {
  columnSpans,
  designationInRect,
  drawBuildHover,
  drawDesignHover,
  drawToolDrag,
  type ScanResult,
} from './mapDragDraw';
import type { WorldClock } from './clock';
import { type Selection } from './model';
import {
  buildItems,
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
  toolYRange,
  DESIGNATE_TOOLS,
  type BuildOpts,
  type BuildToolId,
  type DesignateId,
  type EntityLite,
  type Target,
  failedCells,
  stalledIn,
} from './tools';
import { COLORS, pawnColor } from './visuals';
import { buildTitle, isRepairBuild } from './zones/preserve';
import { drawZones, zoneLabel } from './zones/zoneDraw';
import { waterColumns } from './zones/zonePaint';
import { drawQuarries } from './zones/quarry';

export type MapTool =
  | { kind: 'select' }
  | { kind: 'designate'; id: DesignateId }
  | { kind: 'zone'; id: string; op: 'add' | 'remove'; into?: string | number }
  | { kind: 'build'; id: BuildToolId }
  | { kind: 'design'; id: number; name: string; blocks: DesignBlock[]; mine?: { min: number; max: number } }
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
  view: 'top' | 'angled' | 'fly';
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
  /** The page's world clock: the map's sky light and it share one anchor (the newer server sample wins, both ways). */
  clock?: WorldClock;
  /** A material hovered in the material selector: the placement preview keeps showing at the last map cell meanwhile. */
  previewHold?: boolean;
  onHover?(info: string | null): void;
  /** Drafted pawns are selected: an entity under the cursor (mob, animal, player, AI bot) shows the attack cursor. */
  attackMode?: boolean;
  /** A placement waiting for the owner's word (a water placement and where it would run): drawn as the placement hologram. */
  holoPreview?: { key: string; blocks: ReadonlyArray<readonly [number, number, number, number, number]> } | null;
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

/** The pointer over the map: its point, the level cell under it, the face against the hovered block (angled view). */
interface Hover {
  at: Pt;
  cell: V3 | null;
  face: V3 | null;
  pickAt: number;
}

/** What the viewer was last given for the holograms: the state's ghosts and builds (by identity) and the placement key. */
interface HoloSync {
  ghosts: unknown;
  builds: unknown;
  preview: string;
}

/** The frame loop's centre report: when it last ran, the column it reported, the page's callback it reported to. */
interface CentreReport {
  at: number;
  key: string;
  cb: StoryMapProps['onCentre'];
}

/** Pan keys and their direction (x, z). */
const PAN_KEYS = new Map<string, readonly [number, number]>([
  ['KeyW', [0, -1]],
  ['ArrowUp', [0, -1]],
  ['KeyS', [0, 1]],
  ['ArrowDown', [0, 1]],
  ['KeyA', [-1, 0]],
  ['ArrowLeft', [-1, 0]],
  ['KeyD', [1, 0]],
  ['ArrowRight', [1, 0]],
]);

/** The pan direction of the keys held, into `out` (no allocation per frame). */
function keyPan(keys: Iterable<string> | null | undefined, out: { x: number; z: number }): void {
  out.x = 0;
  out.z = 0;
  for (const code of keys ?? []) {
    const d = PAN_KEYS.get(code);
    if (!d) continue;
    out.x += d[0];
    out.z += d[1];
  }
}

/** Where a body is shown: the viewer's live model when it has one, else eased from where it was shown toward its reported
 * position (a jump of 12 blocks or more is taken at once). */
function easedPos(
  live: { dim: number; x: number; y: number; z: number } | null | undefined,
  prev: V3 | undefined,
  pos: readonly number[],
  k: number,
): V3 {
  if (live && live.dim === 0) return [live.x, live.y, live.z];
  const [x, y, z] = pos as [number, number, number];
  if (prev && Math.hypot(prev[0] - x, prev[2] - z) < 12)
    return [prev[0] + (x - prev[0]) * k, prev[1] + (y - prev[1]) * k, prev[2] + (z - prev[2]) * k];
  return [x, y, z];
}

/** A body's hit radius in px: a model's covers its height, a token's is fixed. */
function hitRadius(m: ViewMats | null, look: BodyLook, at: Pt | null, pos: V3, ppb: number): number {
  if (look !== 'model' || !m || !at) return 18;
  const head = project(m, pos[0], pos[1] + 1.9, pos[2]);
  return Math.max(14, head ? Math.abs(head.y - at.y) + 6 : 0, ppb * 0.6);
}

/** Is the body (if any) farther from the point than `d` px (or not on screen)? */
function fartherThan(hit: MapBody | null, pt: Pt, d: number): boolean {
  return !hit?.at || Math.hypot(hit.at.x - pt.x, hit.at.y - pt.y) > d;
}

/** The selection after a click on a body: Ctrl toggles it, Shift adds it, a plain click selects only it. */
function clickedBodies(sel: string[], key: string, mods: { ctrlKey: boolean; metaKey: boolean; shiftKey: boolean }): string[] {
  const has = sel.includes(key);
  if (mods.ctrlKey || mods.metaKey) return has ? sel.filter((n) => n !== key) : [...sel, key];
  if (mods.shiftKey) return has ? sel : [...sel, key];
  return [key];
}

/** Standing heights per column at one level (`x,z` -> feet y, null = nowhere to stand), and whether a read is in flight. */
interface StandCache {
  level: number;
  cols: Map<string, number | null>;
  busy: boolean;
}

/** The columns around each formation slot whose standing height the cache lacks (each once). */
function missingColumns(raw: readonly P2[], cols: ReadonlyMap<string, number | null>): Array<[number, number]> {
  const missing: Array<[number, number]> = [];
  const seen = new Set<string>();
  for (const s of raw)
    for (let dx = -SNAP_R; dx <= SNAP_R; dx++)
      for (let dz = -SNAP_R; dz <= SNAP_R; dz++) {
        const x = Math.floor(s.x) + dx,
          z = Math.floor(s.z) + dz,
          k = `${x},${z}`;
        if (!cols.has(k) && !seen.has(k)) {
          seen.add(k);
          missing.push([x, z]);
        }
      }
  return missing;
}

/** Builds that stay on the drag start's layer in the angled view. */
const FLAT_BUILDS: ReadonlySet<string> = new Set(['wall', 'floor', 'roof', 'fence']);

/** A designate scan's result handed to the drag's preview. */
type ScanFinish = (cells: V3[], count: number, ents: EntityLite[], height?: HeightTargets) => void;

/** Animal mob types (hunt, not fight). */
const ANIMAL_MOBS = [90, 91, 92, 93, 94, 95, 96, 98, 100, 101, 102, 103];

/** Something drafted pawns can attack: a mob or a player (never one of the colony's pawns). */
function isAttackable(x: EntityView, pawns: StoryColonyState['pawns']): boolean {
  if (x.kind !== 'mob' && x.kind !== 'player') return false;
  return !(x.kind === 'player' && pawns.some((pw) => pw.name === x.name));
}

/** Is an entity's mark (a little above its feet) within `px` of the point? */
function withinPx(m: ViewMats, x: EntityView, pt: Pt, px: number): boolean {
  const s = project(m, x.x, x.y + 0.4, x.z);
  return !!s && Math.hypot(s.x - pt.x, s.y - pt.y) <= px;
}

/** The order-menu target among the entities near the point (within 18 px; the nearest wins, a pawn's player shadows). */
function entityTargetAt(m: ViewMats, ents: readonly EntityView[], pt: Pt, st: StoryColonyState | undefined): Target | null {
  let best: Target | null = null;
  let bd = 18;
  for (const x of ents) {
    const s = project(m, x.x, x.y + 0.4, x.z);
    if (!s) continue;
    const dd = Math.hypot(s.x - pt.x, s.y - pt.y);
    if (dd > bd) continue;
    bd = dd;
    best = entityTarget(x, st) ?? best;
  }
  return best;
}

/** A drop, a player (not a pawn) or a mob as an order-menu target; null for anything else. */
function entityTarget(x: EntityView, st: StoryColonyState | undefined): Target | null {
  if (x.kind === 'item') return { kind: 'item', entity: x.id, name: x.name ?? 'the drop', x: x.x, y: x.y, z: x.z };
  if (x.kind === 'player') return playerTarget(x, st);
  if (x.kind !== 'mob') return null;
  return {
    kind: 'mob',
    entity: x.id,
    name: x.name ?? 'the mob',
    animal: ANIMAL_MOBS.includes(x.type),
    x: x.x,
    y: x.y,
    z: x.z,
  };
}

function playerTarget(x: EntityView, st: StoryColonyState | undefined): Target | null {
  if (!x.name || (st?.pawns ?? []).some((pw) => pw.name === x.name)) return null;
  return {
    kind: 'player',
    entity: x.id,
    name: x.name,
    bot: (st?.ai ?? []).some((a) => a.name === x.name),
    x: x.x,
    y: x.y,
    z: x.z,
  };
}

/** A queued (not done) build ghost in the cell's column, as an order-menu target. */
function ghostTargetAt(cell: V3, st: StoryColonyState | undefined): Target | null {
  const g = (st?.ghosts?.blocks ?? []).find((b) => b[0] === cell[0] && b[2] === cell[2] && b[6] !== 'done');
  if (!g) return null;
  const build = (st?.builds ?? []).find((b) => String(b.id) === String(g[5]));
  return { kind: 'ghost', build: g[5], x: g[0], y: g[1], z: g[2], ...ghostBuildOf(build) };
}

/** A ghost target's build fields: its title, and whether it is a preserved zone's repair (never cancelled by hand). */
function ghostBuildOf(build: StoryBuild | undefined): { label: string; repair?: boolean } {
  if (!build) return { label: 'build' };
  return isRepairBuild(build) ? { label: buildTitle(build), repair: true } : { label: build.label };
}

/** Per build kind (wall, door, chest…): its cells still queued and how many of them the viewer draws as holograms now. */
function hologramKinds(e: WorldViewerEngine | null, st: StoryColonyState | undefined) {
  const kinds = buildKinds(st);
  const out: Record<string, { queued: number; shown: number }> = {};
  for (const g of st?.ghosts?.blocks ?? []) {
    if (g[6] === 'done') continue;
    const k = (out[kinds.get(String(g[5])) ?? '?'] ??= { queued: 0, shown: 0 });
    k.queued++;
    if (hologramShown(e, g)) k.shown++;
  }
  return out;
}

/** Does the viewer draw this ghost cell as a hologram now (a dig outline for a dig)? */
function hologramShown(e: WorldViewerEngine | null, g: NonNullable<StoryColonyState['ghosts']>['blocks'][number]): boolean {
  return !!e?.hologramAt(g[0], g[1], g[2], ghostDigs(g) ? 'dig' : 'block');
}

const buildKinds = (st: StoryColonyState | undefined) =>
  new Map((st?.builds ?? []).map((b) => [String(b.id), b.kind] as const));

type AiBody = NonNullable<StoryColonyState['ai']>[number];
/** An AI body the map shows: online, in the overworld, with a position. */
const isShownAi = (a: AiBody): a is AiBody & { pos: NonNullable<AiBody['pos']> } =>
  a.online !== false && (a.dim ?? 0) === 0 && !!a.pos;

export const StoryMap =forwardRef<StoryMapHandle, StoryMapProps>(function StoryMap(props, ref) {
  const { mod, source, ready } = useWorldHost();
  const viewer = useRef<WorldViewerHandle | null>(null);
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const wrap = useRef<HTMLDivElement | null>(null);
  const p = useRef(props);
  p.current = props;
  const drag = useRef<DragState | null>(null);
  /** Esc / the other button cancel a drag; the release after a cancel does nothing (tools.ts `DragGate`). */
  const gate = useRef(new DragGate());
  const hover = useRef<Hover | null>(null);
  /** The last hover over the map (kept when the pointer leaves it: the material selector previews there). */
  const lastHover = useRef<Hover | null>(null);
  const shown = useRef(new Map<string, V3>());
  const scan = useRef<{ key: string; busy: boolean; result: ScanResult }>({
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
  const stand = useRef<StandCache>({
    level: 0,
    cols: new Map(),
    busy: false,
  });
  const formationShown = useRef<Formation | null>(null);
  /** The failed designations' labels (DOM over the canvas: crisp text in the theme's colours, hover titles, clickable). */
  const labelsEl = useRef<HTMLDivElement | null>(null);
  /** Where each failed designation's ring was drawn this frame, for clicks (id, the ids its cluster label stands for). */
  const failedAt = useRef<FailedRing[]>([]);
  const holo = useRef<HoloSync>({ ghosts: null, builds: null, preview: '' });
  /** The overlay's painter (mapDraw.ts): one for the map's life, re-aimed every frame. */
  const painter = useMemo(() => new MapPainter(), []);

  const engine = (): WorldViewerEngine | null => viewer.current?.engine() ?? null;

  const startFly = (e: WorldViewerEngine) => {
    // The top camera sits hundreds of blocks above its focus. Fly nearby while keeping the map framing for the return.
    const t = e.rig.target;
    e.rig.flyPos.set(t.x, t.y + 12, t.z + 16);
    e.rig.flyPitch = 35;
    e.rig.flyYaw = 180;
    e.surface.focus();
  };

  useEffect(() => {
    props.keys.current.clear();
    hover.current = null;
    lastHover.current = null;
    if (gate.current.escape()) dropDrag();
    if (props.view === 'fly') {
      const e = engine();
      if (e) startFly(e);
    } else canvas.current?.focus();
  }, [props.view]); // eslint-disable-line react-hooks/exhaustive-deps

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
      if (p.current.view === 'fly') startFly(e);
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

  /** Where the map is first placed: home; with no colony yet, the main bot or the player (null: neither showed up in time,
   * the map stays where it is); undefined = keep waiting (settling not asked yet, or still waiting for a body). */
  const firstSpot = (e: WorldViewerEngine, t0: number): V3 | null | undefined => {
    const home = p.current.home;
    if (home) return home;
    if (!p.current.settle) return undefined;
    const at = settleSpot(e, p.current.player);
    if (!at && performance.now() - t0 < SETTLE_WAIT_MS) return undefined;
    return at;
  };

  // First placement over the colony's home; with no colony yet, over the main bot (else the viewing user's player) so a new
  // colony settles where the hive is rather than at the world origin. The target height follows the level.
  useEffect(() => {
    let raf = 0;
    const t0 = performance.now();
    const tryPlace = () => {
      const e = engine();
      if (!e || placed.current) return;
      const at = firstSpot(e, t0);
      if (at === undefined) return;
      placed.current = true;
      e.rig.isoZoom = TOP_ZOOM;
      if (!at) return;
      const home = p.current.home;
      if (!home) p.current.onSettled?.(at[1]);
      e.rig.target.set(at[0] + 0.5, (home ? p.current.level : at[1]) + 1, at[2] + 0.5);
      if (p.current.view === 'fly') startFly(e);
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
    const centre: CentreReport = { at: 0, key: '', cb: undefined };
    const pan = { x: 0, z: 0 };
    const loop = (now: number) => {
      raf = requestAnimationFrame(loop);
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      const e = engine(),
        c = canvas.current;
      if (!e || !c) return;
      syncView(e);
      const w = c.clientWidth,
        h = c.clientHeight;
      if (c.width !== w || c.height !== h) {
        c.width = w;
        c.height = h;
      }
      if (p.current.view === 'fly') {
        c.getContext('2d')?.clearRect(0, 0, c.width, c.height);
        syncLabels([]);
        return;
      }
      panStep(e, dt, w, h, pan);
      const cam = e.camera;
      mats.current = {
        view: cam.matrixWorldInverse.elements,
        proj: cam.projectionMatrix.elements,
        world: cam.matrixWorld.elements,
        projInv: cam.projectionMatrixInverse.elements,
        w,
        h,
      };
      scanChests(e, now);
      reportCentre(e, now, centre);
      draw(c, dt);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  /** [frame] The viewer follows the page: a new engine's setup, the level cut, the level's height, the clock, the holograms. */
  const syncView = (e: WorldViewerEngine) => {
    // The level cut (x-ray above the working level) is a per-view uniform: no re-meshing when it moves. The camera looks
    // at the level's floor.
    if (lastEngine.current !== e) {
      lastEngine.current = e;
      appliedCut.current = undefined;
      holo.current = { ghosts: null, builds: null, preview: '' };
      // The pawns and AI bodies are drawn as their player models; the overlay draws their names (no second label).
      e.setBotLabels(false);
      if (p.current.view === 'fly') startFly(e); // free cam chosen before the viewer finished loading
    }
    syncLevelCut(e);
    if (placed.current && p.current.view !== 'fly') e.rig.target.y = p.current.level + 1;
    // Day and night: the viewer's index time and the colony clock are one anchor (the newer sample wins).
    p.current.clock?.syncViewer({
      anchor: () => e.runtime.worldTimeAnchor(0),
      set: (ticks, at, frozen) => e.runtime.setWorldTime(0, ticks, at, frozen),
    });
    syncHolograms(e);
  };

  /** [frame] Above the ground storey, the storeys below are dimmed for reference; the cut hides what is above it. */
  const syncLevelCut = (e: WorldViewerEngine) => {
    if (p.current.view === 'fly') {
      if (appliedCut.current !== 'fly') e.setLevelCut(null);
      appliedCut.current = 'fly';
      return;
    }
    const home = p.current.home;
    const dim = home && p.current.level > home[1] ? p.current.level + 1 : null;
    const cutKey = `${p.current.cutY}|${dim}`;
    if (appliedCut.current === cutKey) return;
    appliedCut.current = cutKey;
    e.setLevelCut(p.current.cutY !== null || dim !== null ? { y: p.current.cutY ?? 1e6, dimBelow: dim } : null);
  };

  /** [frame] Pan: keys, then the screen edge — the edge only in full screen (in the page the pointer crosses the map's edge on
   * its way to the nav and HUD, which panned the camera), only while the pointer is over the map and not dragging a box. */
  const panStep = (e: WorldViewerEngine, dt: number, w: number, h: number, pan: { x: number; z: number }) => {
    keyPan(p.current.keys.current, pan);
    const hv = hover.current;
    if (hv && !drag.current && document.hasFocus() && document.fullscreenElement) {
      const [ex, ey] = edgePan(hv.at, w, h, 8);
      pan.x += ex;
      pan.z += ey;
    }
    if (!pan.x && !pan.z) return;
    const speed = e.rig.isoZoom * 0.9 * dt;
    if (e.rig.mode === 'top') {
      e.rig.target.x += pan.x * speed;
      e.rig.target.z += pan.z * speed;
    } else e.rig.pan(-pan.x * speed * (h / e.rig.isoZoom), -pan.z * speed * (h / e.rig.isoZoom), h);
  };

  /** [frame] Chests and workbenches near the view (a worker scan every few seconds). */
  const scanChests = (e: WorldViewerEngine, now: number) => {
    if (now - chests.current.at <= 4000) return;
    chests.current.at = now;
    const t = e.rig.target,
      r = Math.min(64, e.rig.isoZoom);
    const lv = p.current.level;
    void e.runtime.host
      .scan(0, [t.x - r, lv - 12, t.z - r], [t.x + r, lv + 16, t.z + r], [54, 146, 58, 61, 62], 400)
      .then((s) => {
        const cells: Array<{ pos: V3; id: number }> = [];
        for (let i = 0; i + 5 < s.length + 1; i += 6) cells.push({ pos: [s[i]!, s[i + 1]!, s[i + 2]!], id: s[i + 3]! });
        chests.current.cells = cells;
      })
      .catch(() => undefined);
  };

  /** [frame] The centre column for the panels ("settles at", "move home here"), at most five times a second, once placed, and
   * only while a panel wants it (the page passes onCentre then: a re-render of the whole HUD per pan step otherwise). */
  const reportCentre = (e: WorldViewerEngine, now: number, centre: CentreReport) => {
    if (p.current.onCentre !== centre.cb) {
      centre.cb = p.current.onCentre;
      centre.key = '';
    }
    if (!placed.current || !centre.cb || now - centre.at <= 200) return;
    centre.at = now;
    const t = e.rig.target,
      cc: V3 = [Math.floor(t.x), p.current.level, Math.floor(t.z)];
    const key = cc.join(',');
    if (key === centre.key) return;
    centre.key = key;
    p.current.onCentre?.(cc);
  };

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
    syncPlacementHologram(e, h);
  };

  /** [holograms] The placement hologram: a water placement waiting for the owner's word, else the design or the structure /
   * furniture being placed (in the chosen material, under the cursor and while dragging: the material selector's hover shows
   * here at once), else none. */
  const syncPlacementHologram = (e: WorldViewerEngine, h: HoloSync) => {
    const tool = p.current.tool,
      d = drag.current,
      held = p.current.holoPreview;
    if (held && !d) {
      if ('w|' + held.key === h.preview) return;
      h.preview = 'w|' + held.key;
      e.setHologramPreview(held.blocks.flat(), h.preview);
      return;
    }
    const a = placementAnchor();
    if (a && tool.kind === 'design') showDesignHologram(e, h, tool, a);
    else if (a && tool.kind === 'build') showBuildHologram(e, h, tool.id, a, d);
    else if (h.preview) {
      h.preview = '';
      e.setHologramPreview(null);
    }
  };

  /** Where the design or the structure / furniture being placed is anchored (a build drag's start, else the cursor's cell). */
  const placementAnchor = (): V3 | null => {
    const tool = p.current.tool;
    if (tool.kind !== 'design' && !(tool.kind === 'build' && tool.id !== 'remove')) return null;
    const d = drag.current;
    if (tool.kind === 'build' && d?.startCell) return d.startCell;
    return hoverAnchor();
  };

  /** The cursor's placement cell while no drag is held (a material hovered in the selector keeps the last map cell). */
  const hoverAnchor = (): V3 | null => {
    const hv = hover.current ?? (p.current.previewHold ? lastHover.current : null);
    return hv?.cell && !drag.current ? startCellOf(hv) : null;
  };

  const showDesignHologram = (e: WorldViewerEngine, h: HoloSync, tool: Extract<MapTool, { kind: 'design' }>, a: V3) => {
    const key = `${tool.id}|${a.join(',')}|${p.current.build.facing}`;
    if (key === h.preview) return;
    h.preview = key;
    e.setHologramPreview(placeDesign(tool.blocks, a, p.current.build.facing, !!tool.mine).slice(0, 4096).flat(), key);
  };

  const showBuildHologram = (e: WorldViewerEngine, h: HoloSync, id: BuildToolId, ba: V3, d: DragState | null) => {
    const bb = d?.endCell ?? ba;
    const o = { ...p.current.build, line: d?.alt };
    const key = `b|${id}|${ba.join(',')}|${bb.join(',')}|${JSON.stringify(o)}`;
    if (key === h.preview) return;
    h.preview = key;
    // Every item it queues: a water tool's trench (dug first: dig outlines) and its water.
    const blocks = buildItems(id, ba, bb, o).flatMap((it) => buildPreview(it).blocks);
    e.setHologramPreview(blocks.slice(0, 4096).flat(), key);
  };

  /**
   * Every selectable body with its screen point: where the viewer draws its model (the world store's interpolated position)
   * when it has one, else its reported position eased toward. `look`: model (overlay marks only), token, or cut (a storey
   * above the level cut); `r`: its hit radius in pixels.
   */
  const bodies = (dt: number): MapBody[] => {
    const st = p.current.state;
    const ppb = pxPerBlock();
    const out: MapBody[] = [];
    const k = Math.min(1, dt * 6);
    for (const pw of st?.pawns ?? []) if (pw.online !== false && pw.pos) out.push(placeBody(pw.name, pw.pos, 'pawn', k, ppb));
    for (const a of st?.ai ?? []) if (isShownAi(a)) out.push(placeBody(a.name, a.pos, 'ai', k, ppb));
    return out;
  };

  const pxPerBlock = (): number => {
    const m = mats.current,
      e = engine();
    return m && e ? m.h / Math.max(1, e.rig.isoZoom) : 0;
  };

  /** One body where it is shown now (moving `shown` toward its reported position by `k`). */
  const placeBody = (name: string, pos: readonly number[], kind: MapBody['kind'], k: number, ppb: number): MapBody => {
    const e = engine(),
      m = mats.current;
    const live = e?.runtime.store.bot(name);
    const next = easedPos(live, shown.current.get(name), pos, k);
    shown.current.set(name, next);
    const look = bodyLook({
      detail: live ? (e?.runtime.entities.detailOf(live.name) ?? null) : null,
      pxPerBlock: ppb,
      feetY: next[1],
      cutY: p.current.cutY,
    });
    const at = m ? project(m, next[0], next[1] + anchorLift(look), next[2]) : null;
    return { key: name, at, kind, pos: next, look, r: hitRadius(m, look, at, next, ppb) };
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

  /** One frame of the overlay, bottom to top (the layers live in mapDraw.ts and mapDragDraw.ts). */
  const draw = (c: HTMLCanvasElement, dt: number) => {
    const ctx = c.getContext('2d');
    const m = mats.current;
    if (!ctx || !m) return;
    const props = p.current;
    ctx.clearRect(0, 0, c.width, c.height);
    const e = engine()!;
    const pt = painter;
    const cell = 1 / unitsPerPx(e.rig.isoZoom, m.h); // px per block
    pt.begin(ctx, m, cell, props.cutY, props.view === 'angled', e.camera.position, c.clientWidth, c.clientHeight);
    drawGround(pt, props);
    const holoOn = getViewerQuality().holograms;
    drawBuildLayer(pt, props.state, holoOn);
    drawDesignationLayer(pt, props.state);
    pt.drawChests(chests.current.cells, props.state, props.selection, badges.current, LABEL_ZOOM);
    drawBodyLayer(pt, props, dt);
    drawPointer(pt, props, holoOn);
  };

  /** Zones by their cells (zones/zoneDraw.ts): work zones tinted with a dashed edge (growing zones say what they sow), the
   * overlay layers apart (houses: the home-area tint, a solid edge, the name; keep-out: red stripes); the quarries' stairs
   * (zones/quarry.tsx); the colony's home; the beds. */
  const drawGround = (pt: MapPainter, props: StoryMapProps) => {
    const st = props.state;
    const zones = st?.zones ?? [];
    drawZones(pt.ctx, zones, pt.at, pt.cell, (z, houseNo) => zoneLabel(z, houseNo, st?.plants));
    drawQuarries(pt.ctx, zones, pt.at, pt.cell);
    pt.drawHome(props.home);
    pt.drawBeds(st?.beds ?? [], st?.pawns, props.selection.bed);
  };

  /** Build ghosts: with build holograms on (the viewer settings) the viewer draws them in the world instead
   * (render/holograms.ts). Failed build cells (docs/web/story-mode.md "Retrying failed build cells"): drawn even when the
   * viewer shows the holograms; hovering one names its reason (the canvas title), the Retry tool (O) takes it again. */
  const drawBuildLayer = (pt: MapPainter, st: StoryColonyState | undefined, holoOn: boolean) => {
    const blocks = st?.ghosts?.blocks ?? EMPTY_GHOSTS;
    if (!holoOn) pt.drawGhosts(blocks);
    pt.drawFailedBuildCells(blocks);
  };

  /** Bodies: order lines first, then tokens. */
  const drawBodyLayer = (pt: MapPainter, props: StoryMapProps, dt: number) => {
    const list = bodies(dt);
    const sel = new Set(props.selection.bodies);
    pt.drawOrderLines(list, sel, props.state?.pawns);
    pt.drawBodies(list, sel, props.state);
  };

  /** Designations: a mark on every target (one per tree for chop), worked out once per state; a frame only projects and stamps
   * them. The failed ones' labels: one per cluster (same reason, adjacent), largest first, never on top of another; hidden
   * when zoomed out (the rings stay). */
  const drawDesignationLayer = (pt: MapPainter, st: StoryColonyState | undefined) => {
    const model = designationModel(st?.designations ?? EMPTY_LIST);
    failedAt.current = pt.drawDesignations(model);
    syncLabels(
      pt.cell > LABEL_ZOOM
        ? placeLabels(
            model.clusters.map((cl) => {
              const a = pt.at(cl.anchor[0] + 0.5, Math.min(cl.anchor[1] + 1, pt.cutY ?? 999), cl.anchor[2] + 0.5);
              return { item: cl, at: a ? { x: a.x, y: a.y + 13 } : null, text: cl.text };
            }),
            { w: pt.vw, h: pt.vh },
          )
        : [],
    );
  };

  /** The drag (a selection box, a formation line, or a tool's grid rectangle with its live targets), else the placement under
   * the cursor. */
  const drawPointer = (pt: MapPainter, props: StoryMapProps, holoOn: boolean) => {
    const d = drag.current;
    if (d && d.moved && d.button === 2) drawFormationDrag(pt, d);
    if (d) {
      if (d.moved && d.button === 0) drawLeftDrag(pt, props, d);
      return;
    }
    const hv = hover.current;
    if (hv?.cell) drawHover(pt, props, hv, holoOn);
  };

  const drawFormationDrag = (pt: MapPainter, d: DragState) => {
    void formation(d, false).then((f) => {
      if (drag.current === d) formationShown.current = f;
    });
    if (formationShown.current) drawFormation(pt, formationShown.current);
  };

  const drawLeftDrag = (pt: MapPainter, props: StoryMapProps, d: DragState) => {
    if (props.tool.kind === 'select') {
      const ctx = pt.ctx;
      const rr = rectOf(d.start, d.now);
      ctx.fillStyle = 'rgba(245,197,66,0.10)';
      ctx.fillRect(rr.x, rr.y, rr.w, rr.h);
      ctx.strokeStyle = COLORS.select;
      ctx.lineWidth = 1.2;
      ctx.strokeRect(rr.x + 0.5, rr.y + 0.5, rr.w, rr.h);
    } else if (d.startCell && d.endCell) drawToolDrag(pt, props, d, scan.current.result, water.current);
  };

  /** Before a drag: a design follows the cursor (Q/E turns it) on the working level, a build tool shows its placement ghost
   * (with build holograms on, the viewer draws both: syncHolograms), another tool outlines the cell. */
  const drawHover = (pt: MapPainter, props: StoryMapProps, hv: Hover, holoOn: boolean) => {
    const tool = props.tool;
    if (tool.kind === 'design') {
      const a = startCellOf(hv);
      if (a) drawDesignHover(pt, tool, a, hv.at, props, holoOn);
    } else if (tool.kind === 'build') {
      const a = startCellOf(hv);
      if (a && (!holoOn || tool.id === 'remove')) drawBuildHover(pt, tool.id, a, props.build);
    } else if (tool.kind !== 'select' && hv.cell) pt.drawCellOutline(hv.cell);
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
    const read = requestStand(e, sc, raw, level, final);
    if (final && read) await read;
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

  /** Asks for the standing heights the slots' columns lack (the cache is per level): the read, or null when none is started
   * (nothing missing, or a preview while one is in flight). */
  const requestStand = (
    e: WorldViewerEngine,
    sc: StandCache,
    raw: readonly P2[],
    level: number,
    final: boolean,
  ): Promise<void> | null => {
    if (sc.level !== level) Object.assign(sc, { level, cols: new Map() });
    const missing = missingColumns(raw, sc.cols);
    return missing.length && (final || !sc.busy) ? readStand(e, missing, level) : null;
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
  const drawFormation = (pt: MapPainter, f: Formation) => {
    const ctx = pt.ctx,
      m = pt.m;
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
    if (f.ignored && s2) pt.label(s2, `${f.ignored} undrafted: not in the line`, false);
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
    return e.runtime.store.entities(0).some((x) => isAttackable(x, pawns) && withinPx(m, x, pt, 18));
  };
  const attackAt = useRef(0);

  const updateHover = (pt: Pt) => {
    const now = performance.now();
    const cv = canvas.current;
    if (cv) syncAttackCursor(cv, pt, now);
    const cell = groundCell(pt);
    if (cv) syncCellTitle(cv, cell);
    const h = hover.current ?? { at: pt, cell, face: null, pickAt: 0 };
    h.at = pt;
    h.cell = cell;
    hover.current = h;
    lastHover.current = h;
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

  /** Drafted pawns selected: the attack cursor over something they can attack (checked at most every 50 ms). */
  const syncAttackCursor = (cv: HTMLCanvasElement, pt: Pt, now: number) => {
    const attack = p.current.attackMode;
    if (attack && now - attackAt.current > 50) {
      attackAt.current = now;
      const on = attackableAt(pt);
      if ((cv.dataset.attack === '1') !== on) {
        cv.dataset.attack = on ? '1' : '';
        cv.style.cursor = on ? ATTACK_CURSOR : '';
      }
    } else if (!attack && cv.dataset.attack === '1') {
      cv.dataset.attack = '';
      cv.style.cursor = '';
    }
  };

  /** A failed build cell under the cursor: its reason as the map's tooltip. */
  const syncCellTitle = (cv: HTMLCanvasElement, cell: V3 | null) => {
    const blocks = p.current.state?.ghosts?.blocks ?? EMPTY_GHOSTS;
    const f = cell ? failedModel(blocks).byColumn.get(`${cell[0]},${cell[2]}`) : undefined;
    const title = f ? `Build ${f.build}: ${f.cell.join(',')} failed: ${f.reason} (Retry tool: O)` : '';
    if (cv.title !== title) cv.title = title;
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

  /** A fishing-zone drag: the still water under the rectangle (one scan in flight; the latest rectangle wins). */
  const water = useRef<{ key: string; cells: Set<number> | null; busy: boolean }>({ key: '', cells: null, busy: false });
  const rescanWater = () => {
    const d = drag.current,
      e = engine();
    if (!d || !e || !d.startCell || !d.endCell || water.current.busy) return;
    const rect = cellRect(d.startCell, d.endCell);
    const lv = p.current.level;
    const key = `${rect.join(',')}@${lv}`;
    if (water.current.key === key) return;
    water.current.busy = true;
    void e.runtime.host
      .scan(0, [rect[0], lv - 4, rect[1]], [rect[2], lv + 3, rect[3]], [8, 9], 20000)
      .then((s) => {
        if (drag.current === d) water.current = { key, cells: waterColumns(s), busy: true };
      })
      .catch(() => undefined)
      .finally(() => {
        water.current.busy = false;
        const dd = drag.current;
        if (dd?.startCell && dd.endCell && `${cellRect(dd.startCell, dd.endCell).join(',')}@${p.current.level}` !== water.current.key)
          rescanWater();
      });
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
    if (quickPreview(tool.id, rect, finish)) return;
    scan.current.busy = true;
    void scanTargets(e, tool.id, rect, finish)
      .catch(() => undefined)
      .finally(() => {
        scan.current.busy = false;
        rescanIfMoved(tool.id);
      });
  };

  /** Previews that need no block scan: entities (drops, animals), and Cancel / Retry (counted from the colony). */
  const quickPreview = (id: DesignateId, rect: [number, number, number, number], finish: ScanFinish): boolean => {
    const t = DESIGNATE_TOOLS.find((x) => x.id === id)!;
    if (t.entities) {
      const ents = entitiesIn(t, rect, entityList());
      finish([], ents.length, ents);
      return true;
    }
    const n = colonyCount(id, rect);
    if (n === null) return false;
    finish([], n, []);
    return true;
  };

  /** Cancel: the designations in the box; Retry: the failed or resting designations and failed build cells in it (what the
   * release retries: its count, never a scan). Null for other tools. */
  const colonyCount = (id: DesignateId, rect: [number, number, number, number]): number | null => {
    const st = p.current.state;
    const des = st?.designations ?? [];
    if (id === 'cancel') return des.filter((dd) => designationInRect(dd, rect)).length;
    if (id === 'retry') return stalledIn(des, rect).length + failedCells(st?.ghosts?.blocks ?? EMPTY_GHOSTS, rect).length;
    return null;
  };

  /** A worker scan of the box: a height-mode column scan (Mine, Deconstruct), else the tool's blocks at the level. */
  const scanTargets = (
    e: WorldViewerEngine,
    id: DesignateId,
    rect: [number, number, number, number],
    finish: ScanFinish,
  ): Promise<void> => {
    if (hasHeight(id)) return heightScan(e, id, rect).then((ht) => finish(ht.cells, ht.cells.length, [], ht));
    const y = toolYRange(id, p.current.level, p.current.build.height, p.current.cutY);
    return e.runtime.host
      .scan(0, [rect[0], y[0], rect[1]], [rect[2], y[1], rect[3]], scanIds(id), 6000)
      .then((s) => {
        const pv = previewTargets(id, s, p.current.mineFilter);
        finish(pv.cells, pv.count, []);
      });
  };

  /** After a scan: the drag moved on meanwhile, so scan its new rectangle. */
  const rescanIfMoved = (id: DesignateId) => {
    const dd = drag.current;
    if (!dd) return;
    const r2 = dd.startCell && dd.endCell ? scanKey(cellRect(dd.startCell, dd.endCell), id) : '';
    if (r2 !== scan.current.key) rescan();
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
    chordMove(ev);
    const d = drag.current;
    if (d && d.button === 1) {
      middleDrag(ev, d, pt);
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
    toolDragMove(ev, d, pt);
  };

  /** A chorded press: pointer events report a second button going down as a move with `button` set. */
  const chordMove = (ev: React.PointerEvent) => {
    if (ev.button >= 0 && (ev.buttons & buttonBit(ev.button)) !== 0 && gate.current.button !== null) chord(ev.button);
  };

  /** Middle-drag pans (Shift + middle-drag turns the angled view). */
  const middleDrag = (ev: React.PointerEvent, d: DragState, pt: Pt) => {
    const e = engine();
    if (e) {
      const dx = pt.x - d.now.x,
        dy = pt.y - d.now.y;
      if (ev.shiftKey && e.rig.mode === 'iso') e.rig.look(dx, dy);
      else e.rig.pan(dx, dy, canvas.current!.clientHeight);
    }
    d.now = pt;
    d.moved = true;
  };

  /** A tool's left drag follows the pointer (a build by the cursor's placement cell); a designate or fishing-zone drag
   * rescans its preview. */
  const toolDragMove = (ev: React.PointerEvent, d: DragState, pt: Pt) => {
    d.now = pt;
    d.alt = ev.altKey;
    d.shift = ev.shiftKey; // a zone paint: Shift extends the zone the drag started in
    if (!d.moved && isDrag(d.start, pt)) d.moved = true;
    const tool = p.current.tool;
    d.endCell = tool.kind === 'build' ? buildDragEnd(tool.id, d) : (groundCell(pt) ?? d.endCell);
    if (d.moved) rescanDrag(tool);
  };

  /** A build drag's end: the cursor's placement cell; walls, floors, roofs and fences stay on the start's layer when angled. */
  const buildDragEnd = (id: BuildToolId, d: DragState): V3 | null => {
    const h = hover.current;
    const c = h ? startCellOf(h) : null;
    if (!c || !d.startCell) return d.endCell;
    return p.current.view === 'angled' && FLAT_BUILDS.has(id) ? [c[0], d.startCell[1], c[2]] : c;
  };

  const rescanDrag = (tool: MapTool) => {
    if (tool.kind === 'designate') rescan();
    else if (tool.kind === 'zone' && tool.id === 'fish' && tool.op === 'add') rescanWater();
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
    if (ev.button === 2) await rightUp(ev, pt, d, props);
    else if (!d || d.button !== 0) return;
    else if (props.tool.kind === 'select') await selectUp(ev, pt, d, props);
    else await toolUp(ev, d, props);
  };

  /** Right click: drops the active tool (the genre); with something selected, the order menu for the thing under it; a
   * right-drag with drafted pawns selected lines them up. */
  const rightUp = async (ev: React.PointerEvent, pt: Pt, d: DragState | null, props: StoryMapProps) => {
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
  };

  /** The select tool: a box selects the bodies in it (else the chests), a click what is under it. */
  const selectUp = async (ev: React.PointerEvent, pt: Pt, d: DragState, props: StoryMapProps) => {
    if (d.moved) boxSelect(pt, d, props);
    else await clickSelect(ev, pt, props);
  };

  const boxSelect = (pt: Pt, d: DragState, props: StoryMapProps) => {
    const r: Rect = rectOf(d.start, pt);
    const inBox = bodies(0).filter((b) => b.at && inRect(r, b.at));
    if (inBox.length) {
      props.onSelect({
        bodies: d.shift ? [...new Set([...props.selection.bodies, ...inBox.map((b) => b.key)])] : inBox.map((b) => b.key),
        chests: [],
        bench: null,
      });
      return;
    }
    // No bodies: the chests in the box (edit their storage together).
    props.onSelect({ bodies: d.shift ? props.selection.bodies : [], chests: chestsIn(r), bench: null });
  };

  /** The chests whose marks are inside a screen rectangle. */
  const chestsIn = (r: Rect): V3[] => {
    const m = mats.current;
    return chests.current.cells
      .filter((c) => c.id === 54 || c.id === 146)
      .filter((c) => {
        const s = m ? project(m, c.pos[0] + 0.5, c.pos[1] + 1, c.pos[2] + 0.5) : null;
        return s && inRect(r, s);
      })
      .map((c) => c.pos);
  };

  /** A click: a bed mark or a failed designation's ring nearer than any body, a body, else the block (a chest, a bed, a bench). */
  const clickSelect = async (ev: React.PointerEvent, pt: Pt, props: StoryMapProps) => {
    // A bed mark under the cursor wins over a body standing farther from the click (pawns sleep on their beds).
    const bedHit = bedMarkAt(pt, props);
    const hit = bodyAt(pt);
    if (bedHit && fartherThan(hit, pt, bedHit.d)) {
      props.onSelect({ bodies: [], chests: [], bench: null, bed: [bedHit.b.pos[0], bedHit.b.pos[1], bedHit.b.pos[2]] });
      return;
    }
    // A failed designation's ring under the cursor (nearer than any body): its inspector (why, retry, cancel).
    const failHit = failedRingAt(pt);
    if (failHit && fartherThan(hit, pt, failHit.d)) {
      props.onSelect({ ...EMPTY_SEL, designation: { id: failHit.f.id, cluster: failHit.f.cluster } });
      return;
    }
    if (hit) clickBody(ev, hit, props);
    else await clickBlock(ev, pt, props);
  };

  /** The nearest bed mark within 10 px. */
  const bedMarkAt = (pt: Pt, props: StoryMapProps) => {
    const m = mats.current;
    if (!m) return undefined;
    return (props.state?.beds ?? [])
      .map((b) => ({ b, s: project(m, b.pos[0] + 0.5, b.pos[1] + 0.6, b.pos[2] + 0.5) }))
      .map((x) => ({ ...x, d: x.s ? Math.hypot(x.s.x - pt.x, x.s.y - pt.y) : Infinity }))
      .filter((x) => x.d <= 10)
      .sort((a, b) => a.d - b.d)[0];
  };

  /** The nearest failed designation's ring within 12 px. */
  const failedRingAt = (pt: Pt) =>
    failedAt.current
      .map((f) => ({ f, d: Math.hypot(f.x - pt.x, f.y - pt.y) }))
      .filter((x) => x.d <= 12)
      .sort((a, b) => a.d - b.d)[0];

  /** A click on a body: plain = only it, Shift adds, Ctrl toggles; a double click selects every visible body of its kind. */
  const clickBody = (ev: React.PointerEvent, hit: MapBody, props: StoryMapProps) => {
    const now = performance.now();
    if (lastClick.current && lastClick.current.name === hit.key && now - lastClick.current.t < 350) {
      const c = canvas.current!;
      const view: Rect = { x: 0, y: 0, w: c.clientWidth, h: c.clientHeight };
      const visible = bodies(0).filter((b) => b.kind === hit.kind && b.at && inRect(view, b.at));
      props.onSelectSame(
        hit.kind,
        visible.map((b) => b.key),
      );
      lastClick.current = null;
      return;
    }
    lastClick.current = { t: now, name: hit.key };
    props.onSelect({ bodies: clickedBodies(props.selection.bodies, hit.key, ev), chests: [], bench: null });
  };

  /** A chest or a workbench (or a bed's block) under the cursor; else nothing (Shift keeps the selection). */
  const clickBlock = async (ev: React.PointerEvent, pt: Pt, props: StoryMapProps) => {
    const block = await pick(pt);
    if (block && selectBlock(ev, block, props)) return;
    if (!ev.shiftKey) props.onSelect({ bodies: [], chests: [], bench: null });
  };

  /** Selects the chest, bed or workbench a block is; false when it is none of them. */
  const selectBlock = (ev: React.PointerEvent, block: PickedBlock, props: StoryMapProps): boolean => {
    if (block.id === 54 || block.id === 146) {
      selectChest(ev, [block.x, block.y, block.z], props);
      return true;
    }
    const bed = block.id === 26 ? bedOfBlock(block, props) : undefined;
    if (bed) {
      props.onSelect({ bodies: [], chests: [], bench: null, bed: [bed.pos[0], bed.pos[1], bed.pos[2]] });
      return true;
    }
    if (block.id !== 58 && block.id !== 61 && block.id !== 62) return false;
    props.onSelect({ bodies: [], chests: [], bench: [block.x, block.y, block.z] });
    return true;
  };

  /** A chest block selects its storage's chest (a double chest's first half); Shift toggles it in the selection. */
  const selectChest = (ev: React.PointerEvent, pos: V3, props: StoryMapProps) => {
    const s = (props.state?.storages ?? []).find(
      (x) => k3(x.chest) === k3(pos) || (x.doubleWith && k3(x.doubleWith) === k3(pos)),
    );
    const chest: V3 = s ? s.chest : pos;
    const sel = props.selection;
    const has = sel.chests.some((c) => k3(c) === k3(chest));
    props.onSelect({
      bodies: [],
      chests: ev.shiftKey ? (has ? sel.chests.filter((c) => k3(c) !== k3(chest)) : [...sel.chests, chest]) : [chest],
      bench: null,
    });
  };

  /** The bed a bed block belongs to (its foot or head, a block apart). */
  const bedOfBlock = (block: PickedBlock, props: StoryMapProps) =>
    (props.state?.beds ?? []).find(
      (b) => Math.abs(b.pos[0] - block.x) + Math.abs(b.pos[2] - block.z) <= 1 && Math.abs(b.pos[1] - block.y) <= 1,
    );

  /** A tool: a click is a 1×1 drag; a designate tool sends what its drag previewed (or scans now). */
  const toolUp = async (ev: React.PointerEvent, d: DragState, props: StoryMapProps) => {
    if (!d.startCell) return;
    const end = d.endCell ?? d.startCell;
    const tool = props.tool;
    if (tool.kind === 'designate') {
      const res = await designateResult(tool.id, d.startCell, end);
      props.onTool({ ...res, moved: d.moved }, ev.shiftKey);
      return;
    }
    props.onTool(
      { rect: cellRect(d.startCell, end), a: d.startCell, b: end, cells: [], entities: [], count: 0, moved: d.moved },
      ev.shiftKey,
    );
  };

  /** A designate drag's targets: the live preview when it matches the rectangle, else scanned now (a click, or the scan had not
   * caught up). */
  const designateResult = async (id: DesignateId, a: V3, end: V3): Promise<DragResult> => {
    const rect = cellRect(a, end);
    const res = scan.current.result;
    if (res && scan.current.key === scanKey(rect, id) && res.rect.join(',') === rect.join(',')) return res;
    const t = DESIGNATE_TOOLS.find((x) => x.id === id)!;
    const e = engine();
    if (e && hasHeight(id)) return heightResult(e, id, rect, a, end);
    if (t.entities) {
      const ents = entitiesIn(t, rect, entityList());
      return { rect, a, b: end, cells: [], entities: ents, count: ents.length };
    }
    if (e && id !== 'cancel') {
      const pr = p.current;
      const y = toolYRange(id, pr.level, pr.build.height, pr.cutY);
      const s = await e.runtime.host
        .scan(0, [rect[0], y[0], rect[1]], [rect[2], y[1], rect[3]], scanIds(id), 6000)
        .catch(() => []);
      const pv = previewTargets(id, s, pr.mineFilter);
      return { rect, a, b: end, cells: pv.cells, entities: [], count: pv.count };
    }
    return { rect, a, b: end, cells: [], entities: [], count: 0 };
  };

  const heightResult = async (
    e: WorldViewerEngine,
    id: 'mine' | 'deconstruct',
    rect: [number, number, number, number],
    a: V3,
    end: V3,
  ): Promise<DragResult> => {
    const ht = await heightScan(e, id, rect).catch(() => null);
    return {
      rect,
      a,
      b: end,
      cells: ht?.cells ?? [],
      entities: [],
      count: ht?.cells.length ?? 0,
      ...(ht ? { height: ht } : {}),
    };
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
    const best = entityTargetAt(m, ents, pt, st);
    if (best) return best;
    const cell = groundCell(pt);
    const ghost = cell ? ghostTargetAt(cell, st) : null;
    if (ghost) return ghost;
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
      /** The view's world time and sky light subtraction (0 noon … 11 midnight) of its last frame. */
      sky: () => engine()?.sky() ?? null,
      /** Each body: how it is shown (model, token, cut) and whether the viewer drew its model last frame. */
      models: () =>
        bodies(0).map((b) => ({ name: b.key, look: b.look, model: engine()?.runtime.entities.detailOf(b.key) ?? null })),
      /** The viewer's player models drawn last frame. */
      botModels: () => engine()?.runtime.entities.botModels ?? 0,
      holograms: () => engine()?.hologramStats() ?? null,
      hologramAt: (x: number, y: number, z: number, kind?: 'block' | 'dig') => engine()?.hologramAt(x, y, z, kind) ?? false,
      /** Per build kind (wall, door, chest…): its cells still queued and how many of them draw as holograms now. */
      hologramKinds: () => hologramKinds(engine(), p.current.state),
      /** The viewer's non-air blocks in a box, flat [x, y, z, id, meta, below-id, …] (e2e: find solid ground). */
      scan: (min: V3, max: V3) => engine()?.runtime.host.scan(0, min, max, [], 4096) ?? Promise.resolve([]),
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
  const cameraMode = props.view === 'top' ? 'top' : props.view === 'fly' ? 'fly' : 'iso';
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
          hideHint={cameraMode !== 'fly'}
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
        tabIndex={props.view === 'fly' ? -1 : 0}
        aria-label="Colony map: drag to select, right-click for orders, wheel to zoom, WASD to pan"
        className="absolute inset-0 size-full cursor-crosshair outline-none"
        style={{ touchAction: 'none', pointerEvents: props.view === 'fly' ? 'none' : undefined }}
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
const EMPTY_GHOSTS: NonNullable<StoryColonyState['ghosts']>['blocks'] = [];

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
