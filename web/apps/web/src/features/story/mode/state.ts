/**
 * Story Mode's screen state, one hook per concern: the colony data and the account, the toast, the tool in hand and its
 * options, the working level / x-ray / view, the HUD's overlays, the colony's bodies, and the material selector.
 * `useStoryModeState` puts them together into the one object the actions, the key map and the HUD read (`StoryModeState`).
 */
import type { StoryColonyState, StoryColonySummary, StoryOnlineBlocked } from '@mineclaw/contracts';
import { useQueryClient } from '@tanstack/react-query';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCallback, useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { ApiError } from '@/lib/api/client';
import { useAuth } from '@/lib/auth';
import type { AttackWho } from '../assign/gear';
import { WorldClock } from '../clock';
import {
  defaultMaterial,
  loadMaterialMemory,
  materialsFor,
  materialToolOf,
  matKey,
  recentFor,
  rememberMaterial,
  saveMaterialMemory,
  searchMaterials,
  type MaterialMemory,
  type MaterialOption,
  type MaterialToolId,
} from '../materials';
import { EMPTY_SELECTION, orderAi, orderPawns, splitBodies, type Selection } from '../model';
import type { StorageClip } from '../storage';
import { barBodies } from '../StoryHud';
import type { MapTool, StoryMapHandle } from '../StoryMap';
import type { TabId } from '../StoryPanels';
import {
  DEFAULT_HEIGHT,
  hasHeight,
  levelCutY,
  type BuildOpts,
  type HeightMode,
  type MenuItem,
  type Target,
} from '../tools';
import { useFullscreen } from '../useFullscreen';
import { useArchitect, useColonies, useColony, useStoryWrite } from '../useStory';
import { clearView, loadView, saveView, type StoryView } from '../viewMemory';
import type { PendingWater } from '../WaterTools';

export type V3 = [number, number, number];
export type Tone = 'ok' | 'warn';
export type Say = (text: string, tone?: Tone) => void;
export type MineFilter = 'all' | 'ores' | 'stone' | 'soil';
type Setter<T> = Dispatch<SetStateAction<T>>;

/** The right-click menu: where, its title, its items and what was clicked. */
export interface MenuState {
  at: { x: number; y: number };
  title: string;
  items: MenuItem[];
  target: Target;
}

/** The colonist bar's order, remembered per account. */
function useBarOrder(user: string): [string[], (n: string[]) => void] {
  const key = `mcx-story-bar:${user}`;
  const [order, setOrder] = useState<string[]>(() => {
    try {
      const raw = localStorage.getItem(key);
      return raw ? (JSON.parse(raw) as string[]) : [];
    } catch {
      return [];
    }
  });
  const save = useCallback(
    (n: string[]) => {
      setOrder(n);
      try {
        localStorage.setItem(key, JSON.stringify(n));
      } catch {
        /* storage unavailable */
      }
    },
    [key],
  );
  return [order, save];
}

/** The page's one world clock (../clock.ts): anchored to the server's newest time sample, the colony state's or the map's. */
function useWorldClock(state: StoryColonyState | undefined): WorldClock {
  const [clock] = useState(() => new WorldClock());
  const timeOfDay = state?.timeOfDay;
  const timeFrozen = state?.timeFrozen;
  useEffect(() => {
    clock.colony(timeOfDay, timeFrozen);
  }, [clock, timeOfDay, timeFrozen]);
  return clock;
}

/** What the page knows about the colony it opens, from the list request and the state request. */
export interface ColonyLoad {
  /** `?colony=` names the colony (it is asked for whatever the list says). */
  explicit: boolean;
  listLoaded: boolean;
  listCount: number;
  listFailed: boolean;
  hasState: boolean;
  stateFailed: boolean;
  /** The state request answered 404: there is no such colony. */
  stateMissing: boolean;
}

/**
 * No colony, or a colony that cannot be reached? `noColony` (the found-a-colony card; the map settles over the main
 * bot) only when the server said so: the list came back empty, or it came back and the opened colony answered 404.
 * `loadError` when a request failed (the page says so and keeps asking). Neither = still loading. A failed request is
 * never "no colony": the map would settle somewhere else and stay there when the state arrives after all.
 */
export function colonyLoadState(s: ColonyLoad): { noColony: boolean; loadError: boolean } {
  if (s.hasState) return { noColony: false, loadError: false };
  const noColony = s.listLoaded && (s.stateMissing || (!s.explicit && s.listCount === 0));
  const loadError = !noColony && ((s.listFailed && !s.listLoaded) || (s.stateFailed && !s.stateMissing));
  return { noColony, loadError };
}

/**
 * The colony that opens with no `?colony=` in the address: the one this browser opened last (asked for before the list
 * is in, so the two requests run side by side; kept while the list has it), else the first active one, else the first.
 */
export function defaultColony(
  list: ReadonlyArray<StoryColonySummary> | undefined,
  remembered: string | null,
): string | null {
  if (!list) return remembered;
  if (remembered && list.some((c) => c.id === remembered)) return remembered;
  return (list.find((c) => c.active !== false) ?? list[0])?.id ?? null;
}

const isMissing = (e: unknown) => e instanceof ApiError && e.status === 404;

/**
 * The view this browser last had of a colony (../viewMemory.ts). `opening` is read once per visit: the map opens on it.
 * `remembered`, its colony, follows what is saved, so an address without `?colony=` stays on the colony last shown.
 */
function useViewMemory(user: string) {
  const [opening, setOpening] = useState(() => loadView(user));
  const [remembered, setRemembered] = useState(() => opening?.colony ?? null);
  const remember = useCallback(
    (view: StoryView) => {
      saveView(user, view);
      setRemembered(view.colony);
    },
    [user],
  );
  const forget = useCallback(() => {
    clearView(user);
    setOpening(null);
    setRemembered(null);
  }, [user]);
  return { opening, remembered, remember, forget };
}

/** The account, the colonies, the open colony (`?colony=`, else `defaultColony`), how its load stands, its writer. */
function useStoryData() {
  const { me, can, canChat } = useAuth();
  const router = useRouter();
  const params = useSearchParams();
  const qc = useQueryClient();
  const colonies = useColonies();
  const list = colonies.data?.colonies ?? [];
  const explicit = params.get('colony');
  const { opening, remembered, remember, forget } = useViewMemory(me.username);
  const id = explicit ?? defaultColony(colonies.data?.colonies, remembered);
  const colony = useColony(id);
  const state = colony.data;
  const write = useStoryWrite(id);
  const clock = useWorldClock(state);
  const hasState = !!state;
  const stateMissing = isMissing(colony.error);
  const { noColony, loadError } = colonyLoadState({
    explicit: explicit !== null,
    listLoaded: colonies.data !== undefined,
    listCount: list.length,
    // Counted, not `isError`: each refetch of a list that never loaded goes back to "pending": the card would blink.
    listFailed: colonies.errorUpdateCount > 0,
    hasState,
    stateFailed: colony.isError,
    stateMissing,
  });
  // The remembered colony answered 404: it is forgotten, so the list's choice opens instead.
  const gone = stateMissing && id !== null && id === remembered;
  useEffect(() => {
    if (gone) forget();
  }, [gone, forget]);
  /** The map's view, remembered for the colony it shows (never for one whose state is not here). */
  const rememberView = useCallback(
    (view: { x: number; z: number; zoom: number }) => {
      if (id && hasState) remember({ colony: id, ...view });
    },
    [remember, id, hasState],
  );
  return {
    me,
    can,
    canChat,
    canWrite: can('story'),
    router,
    params,
    qc,
    colonies,
    list,
    id,
    state,
    write,
    clock,
    noColony,
    loadError,
    initialView: opening,
    rememberView,
  };
}

/** The toast: `say` shows a line for 3.5 s. */
function useToast() {
  const [toast, setToast] = useState<{ text: string; tone: Tone } | null>(null);
  const say = useCallback<Say>((text, tone = 'ok') => {
    setToast({ text, tone });
  }, []);
  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 3500);
    return () => clearTimeout(t);
  }, [toast]);
  return { toast, say };
}

/** The tool in hand and its options: the structure options, Mine's filter, Mine / Deconstruct's height mode and Layers. */
function useToolState() {
  const [tool, setTool] = useState<MapTool>({ kind: 'select' });
  const [build, setBuild] = useState<BuildOpts>({
    height: 3,
    roof: 'flat',
    overhang: false,
    facing: 's',
    material: 'oak_planks',
  });
  const [mineFilter, setMineFilter] = useState<MineFilter>('all');
  /** How deep Mine and Deconstruct drags go (each tool keeps its own mode), and the Layers count. */
  const [heightModes, setHeightModes] = useState<Record<'mine' | 'deconstruct', HeightMode>>({ ...DEFAULT_HEIGHT });
  const [layers, setLayers] = useState(2);
  const heightTool = tool.kind === 'designate' && hasHeight(tool.id) ? tool.id : null;
  const heightMode: HeightMode = heightTool ? heightModes[heightTool] : 'surface';
  return {
    tool,
    setTool,
    build,
    setBuild,
    mineFilter,
    setMineFilter,
    setHeightModes,
    layers,
    setLayers,
    heightTool,
    heightMode,
  };
}

/** The working level, x-ray and the view. */
function useLevel(state: StoryColonyState | undefined, tool: MapTool, buildHeight: number) {
  // Before a colony exists, the floor under the main bot the map settled over.
  const [settledY, setSettledY] = useState<number | null>(null);
  const ground = state?.home?.[1] ?? settledY ?? 64;
  const [level, setLevel] = useState<number | null>(null);
  const lv = level ?? ground;
  // X-ray above the working level: automatic while building or on an upper storey (trees and hills stay whole otherwise).
  const [xray, setXray] = useState<'auto' | 'on' | 'off'>('auto');
  const [view, setView] = useState<'top' | 'angled' | 'fly'>('top');
  const xrayOn = xray === 'on' || (xray === 'auto' && (tool.kind === 'build' || lv > ground));
  const cutY = levelCutY(lv, buildHeight, xrayOn);
  return { setSettledY, ground, setLevel, lv, setXray, xrayOn, cutY, view, setView };
}

/** The HUD's windows, dialogs and pending choices. */
function useOverlays() {
  const map = useRef<StoryMapHandle | null>(null);
  const keys = useRef(new Set<string>());
  const [selection, setSelection] = useState<Selection>(EMPTY_SELECTION);
  /** The map centre's column (x, level, z): where a new colony settles, where "move home here" moves it. */
  const [centre, setCentre] = useState<V3 | null>(null);
  /** A home migration waiting for its confirm (the new home column). */
  const [migrate, setMigrate] = useState<V3 | null>(null);
  const [tab, setTab] = useState<TabId | null>(null);
  const [menu, setMenu] = useState<MenuState | null>(null);
  /** An attack on a player or bot waiting for the owner's confirm (once per target per session). */
  const [confirmAttack, setConfirmAttack] = useState<{ who: AttackWho; pawns: string[]; queue: boolean } | null>(null);
  /** "Pick up…" from the right-click menu: the item chooser for these pawns. */
  const [pickupFor, setPickupFor] = useState<string[] | null>(null);
  const [help, setHelp] = useState(false);
  const [chat, setChat] = useState(false);
  const [clip, setClip] = useState<StorageClip | null>(null);
  const picking = useRef<((c: V3 | null) => void) | null>(null);
  const lastDraft = useRef<{ key: string; drafted: boolean; at: number } | null>(null);
  const [pickHint, setPickHint] = useState(false);
  /** The last "Bring online" refusals (per pawn; dropped once that pawn is in game). */
  const [blocked, setBlocked] = useState<StoryOnlineBlocked[]>([]);
  // Water (docs/web/story-mode.md "Water"): a placement that would flood beyond what was drawn, or that the irrigation planner
  // pre-filled, waits for the owner's Queue.
  const [pendingWater, setPendingWater] = useState<PendingWater | null>(null);
  return {
    map,
    keys,
    selection,
    setSelection,
    centre,
    setCentre,
    migrate,
    setMigrate,
    tab,
    setTab,
    menu,
    setMenu,
    confirmAttack,
    setConfirmAttack,
    pickupFor,
    setPickupFor,
    help,
    setHelp,
    chat,
    setChat,
    clip,
    setClip,
    picking,
    lastDraft,
    pickHint,
    setPickHint,
    blocked,
    setBlocked,
    pendingWater,
    setPendingWater,
  };
}

/** The colony's bodies: pawns in bar order, AI bodies, the bar, the selection split, who is offline, storage stock. */
function useBodies(state: StoryColonyState | undefined, order: string[], selected: readonly string[]) {
  const statePawns = state?.pawns;
  const stateAi = state?.ai;
  const storages = state?.storages;
  const pawns = useMemo(() => orderPawns(statePawns ?? [], order), [statePawns, order]);
  const offlineNames = useMemo(
    () => (statePawns ?? []).filter((p) => p.online === false || p.state === 'offline').map((p) => p.name),
    [statePawns],
  );
  const ai = useMemo(() => orderAi(stateAi ?? []), [stateAi]);
  const bodies = useMemo(() => barBodies(pawns, ai), [pawns, ai]);
  const stock = useMemo(() => {
    const m = new Map<string, number>();
    for (const s of storages ?? [])
      for (const [i, mm, n] of s.contents) m.set(`${i}:${mm}`, (m.get(`${i}:${mm}`) ?? 0) + n);
    return m;
  }, [storages]);
  return { pawns, offlineNames, ai, bodies, split: splitBodies(state, selected), stock };
}

/** A new tool starts on its last material (else the first one it can use); new options keep a choice that is still valid. */
function useToolMaterialDefault(
  matTool: MaterialToolId | null,
  matOptions: MaterialOption[] | null,
  matCurrent: string,
  last: MaterialMemory['last'],
  setBuild: Setter<BuildOpts>,
  setMatHover: Setter<MaterialOption | null>,
) {
  const matToolSeen = useRef<string | null>(null);
  useEffect(() => {
    setMatHover(null);
    if (!matTool || !matOptions) {
      matToolSeen.current = matTool;
      return;
    }
    const switched = matToolSeen.current !== matTool;
    matToolSeen.current = matTool;
    const cur = matOptions.find((o) => o.key === matCurrent && o.unlocked);
    if (!switched && cur) return;
    const pick = defaultMaterial(matOptions, last[matTool] ?? (cur ? cur.key : null));
    if (pick && pick.key !== matCurrent) setBuild((b) => ({ ...b, material: pick.material }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [matTool, matOptions]);
}

/** The material selector: a structure tool's materials (the palette is fetched while one is in hand, too). */
function useMaterialSelector(
  tool: MapTool,
  open: { id: string | null; state: StoryColonyState | undefined; tab: TabId | null },
  stock: Map<string, number>,
  build: BuildOpts,
  setBuild: Setter<BuildOpts>,
) {
  const matTool = materialToolOf(tool);
  const { id, state, tab } = open;
  const architect = useArchitect(id, state?.rev, tab === 'architect' || matTool !== null, state?.architectKey);
  const [matMemory, setMatMemory] = useState<MaterialMemory>(() => ({ last: {}, recent: [] }));
  useEffect(() => setMatMemory(loadMaterialMemory()), []);
  const [matQuery, setMatQuery] = useState('');
  /** A material hovered in the selector: the map previews it until the pointer leaves. */
  const [matHover, setMatHover] = useState<MaterialOption | null>(null);
  const entries = architect.data?.entries;
  const matOptions = useMemo(() => (matTool ? materialsFor(matTool, entries, stock) : null), [matTool, entries, stock]);
  const matCurrent = matKey(build.material);
  /** Chooses `o` for the tool in hand (remembered per tool, and as the most recent). */
  const chooseMaterial = useCallback(
    (o: MaterialOption, forTool = matTool) => {
      if (!o.unlocked) return;
      setBuild((b) => ({ ...b, material: o.material }));
      if (forTool)
        setMatMemory((m) => {
          const next = rememberMaterial(m, forTool, o.key);
          saveMaterialMemory(next);
          return next;
        });
    },
    [matTool, setBuild],
  );
  useToolMaterialDefault(matTool, matOptions, matCurrent, matMemory.last, setBuild, setMatHover);
  const matShown = useMemo(() => (matOptions ? searchMaterials(matOptions, matQuery) : null), [matOptions, matQuery]);
  const recent = matMemory.recent;
  const matRecent = useMemo(
    () =>
      matOptions
        ? recentFor(
            matOptions.filter((o) => o.unlocked),
            recent,
          )
        : [],
    [matOptions, recent],
  );
  return {
    matTool,
    architect,
    matQuery,
    setMatQuery,
    matHover,
    setMatHover,
    matCurrent,
    chooseMaterial,
    matShown,
    matRecent,
  };
}

/** Everything Story Mode's screen holds, as one object (see the hooks above). */
export function useStoryModeState() {
  const data = useStoryData();
  const toast = useToast();
  const tools = useToolState();
  const ui = useOverlays();
  const level = useLevel(data.state, tools.tool, tools.build.height);
  const [order, setOrder] = useBarOrder(data.me.username);
  const bodies = useBodies(data.state, order, ui.selection.bodies);
  const materials = useMaterialSelector(
    tools.tool,
    { id: data.id, state: data.state, tab: level.view === 'fly' ? 'architect' : ui.tab },
    bodies.stock,
    tools.build,
    tools.setBuild,
  );
  // Full screen: the whole Story Mode (map + HUD, so every panel, menu, dialog and toast stays visible), Shift+F or the button.
  const fullscreen = useFullscreen<HTMLDivElement>();
  return { ...data, ...toast, ...tools, ...ui, ...level, order, setOrder, ...bodies, ...materials, fullscreen };
}

export type StoryModeState = ReturnType<typeof useStoryModeState>;
