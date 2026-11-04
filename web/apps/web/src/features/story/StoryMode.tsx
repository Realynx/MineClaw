'use client';

/**
 * The Story Mode screen: the colony map full-bleed with the game HUD over it. It owns the selection, the active tool and its
 * options, the working level and x-ray, the view (top-down by default, angled for 3D building), the open bottom tab, the
 * keyboard map, and turns every map gesture into director requests for the colony's pawns and directives for selected AI
 * bodies (one toast says what went where).
 */
import {
  materialKey,
  normArea,
  STORY_FURNITURE,
  tickClock,
  type StoryAlert,
  type StoryColonyState,
  type StoryDirectiveSpec,
  type StoryOnlineBlocked,
  type StoryZone,
} from '@mineclaw/contracts';
import { Maximize2, Minimize2 } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { PortalContainer } from '@/components/ui/overlays';
import { ChatOverlay } from '@/features/world/InWorld';
import { isTyping, sendDirective } from '@/features/world/directives';
import { loadWorldViewer, WorldHost } from '@/features/world/integration';
import { api, ApiError, errorText } from '@/lib/api/client';
import { useAuth } from '@/lib/auth';
import { newRequestId } from '@/lib/utils';
import { cn } from '@/lib/utils';
import {
  clickBody,
  dispatch,
  EMPTY_SELECTION,
  orderAi,
  orderPawns,
  splitBodies,
  type DirectorCall,
  type Selection,
} from './model';
import {
  barBodies,
  ColonistBar,
  ContextMenu,
  ControlCluster,
  HelpOverlay,
  OptionsBar,
  panel,
  TimeSlider,
  Toast,
} from './StoryHud';
import {
  AiInspector,
  BedInspector,
  BillsInspector,
  DesignationInspector,
  GroupInspector,
  PawnInspector,
  StorageInspector,
  UndesignatedBench,
} from './StoryInspector';
import { useFullscreen } from './useFullscreen';
import { GrowingZonesPanel, growPlantNow, type GrowBillChange } from './GrowingZones';
import { FishingZonesPanel } from './zones/FishingZones';
import { zoneBody, zoneInto, zoneToast } from './zones/zonePaint';
import type { Condition } from './notices';
import { StoryNotices, type JumpTarget } from './StoryNotices';
import { StoryMap, type DragResult, type FormationMove, type MapTool, type StoryMapHandle } from './StoryMap';
import {
  ArchitectPanel,
  ColonyPanel,
  DirectivesPanel,
  NewColony,
  QueuePanel,
  SchedulePanel,
  TabBar,
  WorkPanel,
  type TabId,
} from './StoryPanels';
import {
  buildItem,
  cellsArea,
  clampHeight,
  clampLayers,
  contextMenu,
  DEFAULT_HEIGHT,
  countLabel,
  designBlocks,
  designationBody,
  designationDirective,
  designCells,
  designItems,
  mineCheck,
  mineItems,
  DESIGNATE_TOOLS,
  hasHeight,
  heightBody,
  HEIGHT_MODES,
  MAX_DRAG_CELLS,
  placeDesign,
  keyAction,
  releaseIsNoop,
  levelCutY,
  nextLevel,
  orderBody,
  PAN_KEYS,
  preview,
  rotateFacing,
  toolArea,
  toolYRange,
  ZONE_TOOLS,
  type BuildOpts,
  type HeightMode,
  type MenuItem,
  type Target,
} from './tools';
import type { StorageClip } from './storage';
import { catchUp, colonyKey, useArchitect, useColonies, useColony, useStoryWrite } from './useStory';
import { setItemIconSource } from './visuals';
import { useQueryClient } from '@tanstack/react-query';

type V3 = [number, number, number];
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

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

export function StoryMode() {
  const { me, can, canChat } = useAuth();
  const canWrite = can('story');
  const router = useRouter();
  const params = useSearchParams();
  const qc = useQueryClient();
  const colonies = useColonies();
  const list = colonies.data?.colonies ?? [];
  const id = params.get('colony') ?? list[0]?.id ?? null;
  const colony = useColony(id);
  const state = colony.data;
  const write = useStoryWrite(id);

  const map = useRef<StoryMapHandle | null>(null);
  const keys = useRef(new Set<string>());
  const [selection, setSelection] = useState<Selection>(EMPTY_SELECTION);
  const [tool, setTool] = useState<MapTool>({ kind: 'select' });
  const [build, setBuild] = useState<BuildOpts>({
    height: 3,
    roof: 'flat',
    overhang: false,
    facing: 's',
    material: 'oak_planks',
  });
  const [mineFilter, setMineFilter] = useState<'all' | 'ores' | 'stone' | 'soil'>('all');
  /** How deep Mine and Deconstruct drags go (each tool keeps its own mode), and the Layers count. */
  const [heightModes, setHeightModes] = useState<Record<'mine' | 'deconstruct', HeightMode>>({ ...DEFAULT_HEIGHT });
  const [layers, setLayers] = useState(2);
  const heightTool = tool.kind === 'designate' && hasHeight(tool.id) ? tool.id : null;
  const heightMode: HeightMode = heightTool ? heightModes[heightTool] : 'surface';
  // Before a colony exists, the floor under the main bot the map settled over.
  const [settledY, setSettledY] = useState<number | null>(null);
  const ground = state?.home?.[1] ?? settledY ?? 64;
  /** The map centre's column (x, level, z): where a new colony settles, where "move home here" moves it. */
  const [centre, setCentre] = useState<V3 | null>(null);
  /** A home migration waiting for its confirm (the new home column). */
  const [migrate, setMigrate] = useState<V3 | null>(null);
  const noColony = !state && !colony.isLoading && !colonies.isLoading;
  const [level, setLevel] = useState<number | null>(null);
  const lv = level ?? ground;
  // X-ray above the working level: automatic while building or on an upper storey (trees and hills stay whole otherwise).
  const [xray, setXray] = useState<'auto' | 'on' | 'off'>('auto');
  const [view, setView] = useState<'top' | 'angled'>('top');
  const [tab, setTab] = useState<TabId | null>(null);
  const architect = useArchitect(id, state?.rev, tab === 'architect');
  const [menu, setMenu] = useState<{
    at: { x: number; y: number };
    title: string;
    items: MenuItem[];
    target: Target;
  } | null>(null);
  const [toast, setToast] = useState<{ text: string; tone: 'ok' | 'warn' } | null>(null);
  const [help, setHelp] = useState(false);
  const [chat, setChat] = useState(false);
  const [clip, setClip] = useState<StorageClip | null>(null);
  const picking = useRef<((c: V3 | null) => void) | null>(null);
  const lastDraft = useRef<{ key: string; drafted: boolean; at: number } | null>(null);
  const [pickHint, setPickHint] = useState(false);
  const [order, setOrder] = useBarOrder(me.username);
  /** The last "Bring online" refusals (per pawn; dropped once that pawn is in game). */
  const [blocked, setBlocked] = useState<StoryOnlineBlocked[]>([]);
  // Full screen: the whole Story Mode (map + HUD, so every panel, menu, dialog and toast stays visible), Shift+F or the button.
  const { attach: fsAttach, el: fsEl, on: fullscreen, toggle: toggleFullscreen } = useFullscreen<HTMLDivElement>();
  const xrayOn = xray === 'on' || (xray === 'auto' && (tool.kind === 'build' || lv > ground));
  const cutY = levelCutY(lv, build.height, xrayOn);

  // Item icons from the committed textures, once the viewer module is here.
  useEffect(() => {
    void loadWorldViewer().then((m) => m && setItemIconSource(m.itemIconResolver()));
  }, []);

  const say = useCallback((text: string, tone: 'ok' | 'warn' = 'ok') => {
    setToast({ text, tone });
  }, []);
  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 3500);
    return () => clearTimeout(t);
  }, [toast]);

  const pawns = useMemo(() => orderPawns(state?.pawns ?? [], order), [state?.pawns, order]);
  const offlineNames = useMemo(
    () => (state?.pawns ?? []).filter((p) => p.online === false || p.state === 'offline').map((p) => p.name),
    [state?.pawns],
  );
  const stillBlocked = blocked.filter((b) => offlineNames.includes(b.pawn));
  const displayOf = (account: string) => state?.pawns.find((p) => p.name === account)?.display ?? account;

  /** "Bring online": these pawns (null = every offline one) are whitelisted again and started, the cap permitting. */
  const bringOnline = async (names: string[] | null, overCap = false): Promise<StoryOnlineBlocked[]> => {
    if (!canWrite) {
      say('Needs the story role', 'warn');
      return [];
    }
    try {
      const out = await write('story.online', { pawns: names, ...(overCap ? { overCap: true } : {}) });
      setBlocked((cur) => [...cur.filter((b) => !(names ?? offlineNames).includes(b.pawn)), ...out.blocked]);
      const n = out.started.length;
      if (out.blocked.length)
        say(
          `${n ? `${n} coming online; ` : ''}${out.blocked.length} can't: ${out.blocked[0]!.reason}`,
          n ? 'ok' : 'warn',
        );
      else
        say(
          n
            ? `${n} pawn${n === 1 ? '' : 's'} coming online${overCap ? ' (on the regulars’ seats)' : ''}`
            : 'Nobody is offline',
        );
      return out.blocked;
    } catch (e) {
      say(errorText(e), 'warn');
      return [];
    }
  };
  const ai = useMemo(() => orderAi(state?.ai ?? []), [state?.ai]);
  const bodies = useMemo(() => barBodies(pawns, ai), [pawns, ai]);
  const split = splitBodies(state, selection.bodies);

  const stock = useMemo(() => {
    const m = new Map<string, number>();
    for (const s of state?.storages ?? [])
      for (const [i, mm, n] of s.contents) m.set(`${i}:${mm}`, (m.get(`${i}:${mm}`) ?? 0) + n);
    return m;
  }, [state?.storages]);

  /** Runs one action: the director call for the colony and/or a directive for the AI bodies. */
  const run = useCallback(
    async (
      director: DirectorCall | null,
      directive: StoryDirectiveSpec | null,
      verb: string,
      after?: (out: unknown) => string | null,
    ) => {
      if (!canWrite) {
        say('Your account can watch Story Mode but not give orders (needs the story role)', 'warn');
        return;
      }
      const plan = dispatch(split, director, directive, newRequestId(), verb);
      try {
        const [out] = await Promise.all([
          plan.director ? write(plan.director.route, plan.director.body as never) : Promise.resolve(null),
          plan.directive ? api('story.directives', { body: plan.directive }) : Promise.resolve(null),
        ]);
        const head = after?.(out);
        say([head, plan.summary].filter(Boolean).join(' · '));
      } catch (e) {
        say(errorText(e), 'warn');
      }
    },
    [canWrite, split, write, say],
  );

  const focusBody = useCallback(
    (name: string) => {
      const b = bodies.find((x) => x.name === name);
      if (b?.pos) map.current?.focus(Math.floor(b.pos[0]!), Math.floor(b.pos[2]!), Math.floor(b.pos[1]!));
    },
    [bodies],
  );

  // ------------------------------------------------------------------ map gestures

  const onSelect = (sel: Selection) => {
    if (picking.current && sel.chests.length) {
      const r = picking.current;
      picking.current = null;
      setPickHint(false);
      r(sel.chests[0]!);
      return;
    }
    setSelection(sel);
    setMenu(null);
  };

  const onTool = async (r: DragResult, shift: boolean) => {
    // A click (under the drag threshold) paints no zone, and a box with nothing in it designates nothing: no request, the tool stays.
    const kind =
      tool.kind === 'designate' || tool.kind === 'zone' || tool.kind === 'storage' ? tool.kind : ('other' as const);
    if (releaseIsNoop(kind, r.moved ?? true, r.count)) {
      if (tool.kind === 'designate' && r.moved) {
        const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
        say(`${t.label}: nothing ${hasHeight(tool.id) ? 'at that height ' : ''}in the box`, 'warn');
      }
      return;
    }
    if (tool.kind === 'designate' && r.height?.tooMany) {
      say(`Too many blocks (max ${MAX_DRAG_CELLS}): drag a smaller box or a shallower mode`, 'warn');
      return;
    }
    if (!shift && tool.kind !== 'zone') setTool({ kind: 'select' }); // zone tools stay in hand (Shift extends a zone)
    if (tool.kind === 'designate' && hasHeight(tool.id) && r.height) {
      // Exactly the cells the preview showed (its height mode), as blocks: the director never expands them deeper.
      const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
      const area = cellsArea(r.height.cells, toolArea(r.rect, [lv, lv]));
      const body = heightBody(tool.id, r.height.cells, mineFilter);
      await run(
        { route: 'story.designations', body },
        designationDirective(tool.id, area, r.height.cells, mineFilter),
        'designated',
        (out) => countLabel(t, (out as { count?: number } | null)?.count ?? r.count),
      );
      return;
    }
    if (tool.kind === 'designate') {
      const t = DESIGNATE_TOOLS.find((x) => x.id === tool.id)!;
      const area = toolArea(r.rect, toolYRange(tool.id, lv, build.height, cutY));
      const body = designationBody(tool.id, area, mineFilter);
      if (t.entities && r.entities.length) Object.assign(body, { entities: r.entities.map((e) => e.id) });
      await run(
        { route: 'story.designations', body },
        designationDirective(tool.id, area, r.cells, mineFilter),
        tool.id === 'cancel' ? 'cancelled' : 'designated',
        (out) => {
          const n = (out as { count?: number } | null)?.count ?? r.count;
          return tool.id === 'cancel' ? `Cancelled ${n}` : countLabel(t, n);
        },
      );
      return;
    }
    if (tool.kind === 'home') {
      setMigrate([r.a[0], lv, r.a[2]]);
      setTab('colony');
      return;
    }
    if (tool.kind === 'zone') {
      // RimWorld painting (zones/zonePaint.ts): erase sends the kind (or none: any); Shift extends the zone the drag started in.
      if (!canWrite) return say('Needs the story role', 'warn');
      const into = zoneInto(tool, state?.zones ?? [], r.a, shift);
      const label =
        into !== undefined ? 'Zone extended' : `${ZONE_TOOLS.find((z) => z.id === tool.id)?.label ?? 'Zone'} painted`;
      await write('story.zones', zoneBody(tool, r.rect, lv, growPlantNow(), into)).then(
        (out) =>
          say(
            zoneToast(label, tool, out) + (out.warnings?.length ? ` (${out.warnings.join('; ')})` : ''),
            out.warnings?.length ? 'warn' : 'ok',
          ),
        (e) => say(errorText(e), 'warn'),
      );
      return;
    }
    if (tool.kind === 'storage') {
      const area = normArea([r.rect[0], lv - 4, r.rect[1], r.rect[2], lv + 8, r.rect[3]]);
      if (!canWrite) return say('Needs the story role', 'warn');
      await write('story.storage', { op: 'assign_area', area }).then(
        (out) => {
          const n = out.count ?? out.chests?.length ?? 0;
          say(`${n} chest${n === 1 ? '' : 's'} registered as storage`);
        },
        (e) => say(errorText(e), 'warn'),
      );
      return;
    }
    if (tool.kind === 'design') {
      const placed = placeDesign(tool.blocks, r.a, build.facing, !!tool.mine);
      const items = tool.mine ? mineItems(placed, tool.name) : designItems(placed, tool.name);
      await run(
        { route: 'story.builds', body: { op: 'add', items } },
        { kind: 'build', build: items },
        'queued',
        () => `Queued ${tool.name}: ${placed.length} blocks`,
      );
      return;
    }
    if (tool.kind === 'build') {
      const item = buildItem(tool.id, r.a, r.b, build);
      const pv = preview(item, stock.get(materialKey(item.material).join(':')));
      if (!pv.valid) return say(pv.reason ?? 'cannot place that', 'warn');
      await run(
        { route: 'story.builds', body: { op: 'add', items: [item] } },
        { kind: 'build', build: [item] },
        'queued',
        () => `Queued: ${pv.blocks.length} block${pv.blocks.length === 1 ? '' : 's'}`,
      );
    }
  };

  const onContext = (target: Target, at: { x: number; y: number }) => {
    const items = contextMenu(target, split.pawns.length > 0, split.ai.length > 0);
    const title =
      target.kind === 'block'
        ? target.name
        : target.kind === 'ghost'
          ? 'Blueprint'
          : target.kind === 'mob' || target.kind === 'item'
            ? target.name
            : 'Here';
    setMenu({ at, title, items, target });
  };

  const onMenuPick = async (item: MenuItem, queue: boolean) => {
    const target = menu?.target;
    setMenu(null);
    if (item.action === 'cancel-build' && target?.kind === 'ghost') {
      await write('story.builds', { op: 'remove', ids: [target.build] }).then(
        () => say('Blueprint cancelled'),
        (e) => say(errorText(e), 'warn'),
      );
      return;
    }
    if (item.action === 'first-build' && target?.kind === 'ghost') {
      await write('story.builds', { op: 'reorder', ids: [target.build] }).then(
        () => say('Moved to the front of the queue'),
        (e) => say(errorText(e), 'warn'),
      );
      return;
    }
    if ((item.action === 'storage' || item.action === 'bills') && target?.kind === 'block') {
      const pos: V3 = [target.x, target.y, target.z];
      setSelection(
        item.action === 'storage' ? { bodies: [], chests: [pos], bench: null } : { bodies: [], chests: [], bench: pos },
      );
      return;
    }
    const body = orderBody(split.pawns, item, queue);
    await run(
      body ? { route: 'story.orders', body } : null,
      item.directive ?? null,
      queue ? 'queued' : 'ordered',
      () => item.label,
    );
  };

  /** A right-drag line: each drafted pawn moves to its own cell on it (drafted pawns then hold there). */
  const onFormation = async (moves: FormationMove[], queue: boolean, ignored: number) => {
    if (!canWrite) return say('Needs the story role', 'warn');
    const res = await Promise.allSettled(
      moves.map((mv) =>
        write('story.orders', {
          pawns: [mv.pawn],
          order: { kind: 'move', target: { x: mv.target[0], y: mv.target[1], z: mv.target[2] } },
          ...(queue ? { queue: true } : {}),
        }),
      ),
    );
    const bad = res.filter((r) => r.status === 'rejected');
    const note = ignored ? ` · ${ignored} undrafted pawn${ignored === 1 ? '' : 's'} not in it (draft with R)` : '';
    if (bad.length)
      say(
        `${bad.length} of ${moves.length} formation orders failed: ${errorText((bad[0] as PromiseRejectedResult).reason)}`,
        'warn',
      );
    else
      say(
        `${queue ? 'Queued a formation' : 'Formation'}: ${moves.length} pawn${moves.length === 1 ? '' : 's'} along the line${note}`,
      );
  };

  const draft = useCallback(
    async (names: string[], drafted: boolean) => {
      if (!names.length) return;
      if (!canWrite) return say('Needs the story role', 'warn');
      await write('story.draft', { pawns: names, drafted }).then(
        () => say(`${drafted ? 'Drafted' : 'Undrafted'} ${names.length} pawn${names.length === 1 ? '' : 's'}`),
        (e) => say(errorText(e), 'warn'),
      );
    },
    [canWrite, write, say],
  );

  /** The world's time of day (through the bot's op account); the colony clock catches up after. */
  const setTime = useCallback(
    async (tick: number) => {
      if (!canWrite) return say('Changing the time needs the story role', 'warn');
      try {
        const out = await api('story.time', { body: { time: tick } });
        say(`Time set to ${tickClock(out.time)}${out.by ? ` via ${out.by}` : ''}`);
        if (id) void catchUp(qc, id).catch(() => undefined);
      } catch (e) {
        const status = e instanceof ApiError ? e.status : 0;
        say(
          status === 409
            ? 'No op account — op the main bot to change time'
            : status === 429
              ? 'One time change a second: try again'
              : errorText(e),
          'warn',
        );
      }
    },
    [canWrite, say, id, qc],
  );

  const togglePause = useCallback(() => {
    if (!state || !canWrite) return;
    void write('story.pause', { paused: !state.paused }).then(() =>
      say(state.paused ? 'Colony resumed' : 'Colony paused'),
    );
  }, [state, canWrite, write, say]);

  const pickChest = useCallback(
    () =>
      new Promise<V3 | null>((resolve) => {
        picking.current = resolve;
        setPickHint(true);
      }),
    [],
  );

  // ------------------------------------------------------------------ keys

  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      if (isTyping(e.target) || document.querySelector('[role="dialog"][data-state="open"]')) return;
      if (PAN_KEYS[e.code]) {
        keys.current.add(e.code);
        e.preventDefault();
        return;
      }
      const a = keyAction(e, tool.kind === 'build', heightTool !== null);
      if (!a) return;
      e.preventDefault();
      if (a.startsWith('height:') && heightTool) {
        const m = a.slice(7) as HeightMode;
        setHeightModes((cur) => ({ ...cur, [heightTool]: m }));
        say(`${HEIGHT_MODES.find((h) => h.id === m)!.label}${m === 'layers' ? `: ${layers}` : ''}`);
        return;
      }
      if (a === 'layersUp' || a === 'layersDown') {
        setLayers((n) => clampLayers(n + (a === 'layersUp' ? 1 : -1)));
        if (heightTool) setHeightModes((cur) => ({ ...cur, [heightTool]: 'layers' }));
        return;
      }
      if (a === 'draft') {
        // R toggles; a second press before the state caught up flips the last request rather than re-reading old state.
        const key = [...split.pawns].sort().join(',');
        const last = lastDraft.current;
        const anyUndrafted =
          last && last.key === key && Date.now() - last.at < 4000
            ? !last.drafted
            : split.pawns.some((n) => !state?.pawns.find((p) => p.name === n)?.drafted);
        lastDraft.current = { key, drafted: anyUndrafted, at: Date.now() };
        void draft(split.pawns, anyUndrafted);
      } else if (a === 'pause') togglePause();
      else if (a === 'escape') {
        // A drag in progress is cancelled first (nothing happens on release); the key goes no further.
        if (map.current?.cancelDrag()) {
          e.stopPropagation();
          return;
        }
        if (picking.current) {
          picking.current(null);
          picking.current = null;
          setPickHint(false);
        } else if (menu) setMenu(null);
        else if (help) setHelp(false);
        else if (chat) setChat(false);
        else if (tool.kind !== 'select') setTool({ kind: 'select' });
        else if (tab) setTab(null);
        else setSelection(EMPTY_SELECTION);
      } else if (a === 'chat') setChat(true);
      else if (a === 'view') setView((v) => (v === 'top' ? 'angled' : 'top'));
      else if (a === 'xray') setXray(xrayOn ? 'off' : 'on');
      else if (a === 'levelUp' || a === 'levelDown')
        setLevel(nextLevel(lv, build.height, a === 'levelUp' ? 1 : -1, e.shiftKey));
      else if (a === 'rotateCcw' || a === 'rotateCw')
        setBuild((b) => ({ ...b, facing: rotateFacing(b.facing, a === 'rotateCw' ? 1 : -1) }));
      else if (a === 'heightUp' || a === 'heightDown')
        setBuild((b) => ({ ...b, height: clampHeight(b.height + (a === 'heightUp' ? 1 : -1)) }));
      else if (a === 'help') setHelp((h) => !h);
      else if (a === 'fullscreen') toggleFullscreen();
      else if (a === 'focus') {
        if (selection.bodies[0]) focusBody(selection.bodies[0]);
        else if (state?.home) map.current?.focus(state.home[0], state.home[2], state.home[1]);
      } else if (a.startsWith('tool:')) {
        const t = a.slice(5) as never;
        setTool((cur) =>
          cur.kind === 'designate' && cur.id === t ? { kind: 'select' } : { kind: 'designate', id: t },
        );
      } else if (a.startsWith('tab:')) {
        const t = a.slice(4) as TabId;
        setTab((cur) => (cur === t ? null : t));
      }
    };
    const up = (e: KeyboardEvent) => keys.current.delete(e.code);
    const blur = () => keys.current.clear();
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', blur);
    };
  }, [
    tool,
    heightTool,
    say,
    layers,
    split,
    state,
    menu,
    help,
    chat,
    tab,
    lv,
    build.height,
    selection,
    draft,
    togglePause,
    focusBody,
    xrayOn,
    toggleFullscreen,
  ]);

  // ------------------------------------------------------------------ alerts

  const onAlert = (a: StoryAlert) => {
    if (a.pawn) {
      setSelection({ ...EMPTY_SELECTION, bodies: [a.pawn] });
      focusBody(a.pawn);
    }
    if (a.fix?.tab === 'work') setTab('work');
    if (a.fix?.tab === 'storage') {
      const s = state?.storages?.[0];
      if (s) setSelection({ bodies: [], chests: [s.chest], bench: null });
      else setTool({ kind: 'storage' });
    }
    if (a.fix?.workbench) setSelection({ bodies: [], chests: [], bench: a.fix.workbench });
  };
  /** A message or log entry: its alert's fix, its pawn, or the place it names. */
  const onJump = (t: JumpTarget) => {
    if (t.alert) onAlert(t.alert);
    else if (t.pawn) {
      setSelection({ ...EMPTY_SELECTION, bodies: [t.pawn] });
      focusBody(t.pawn);
    }
    if (!t.pawn && t.pos) map.current?.focus(t.pos[0], t.pos[2], t.pos[1]);
  };
  /** An ongoing condition: select the pawns it is about, or open what fixes it. */
  const onCondition = (c: Condition) => {
    if (c.pawns.length) {
      setSelection({ ...EMPTY_SELECTION, bodies: c.pawns });
      focusBody(c.pawns[0]!);
    }
    if (c.tab === 'storage') {
      const s = state?.storages?.[0];
      if (s) setSelection({ bodies: [], chests: [s.chest], bench: null });
      else setTool({ kind: 'storage' });
    }
  };

  // ------------------------------------------------------------------ the inspector

  const inspector = (() => {
    const close = () => setSelection(EMPTY_SELECTION);
    if (selection.designation) {
      const sel = selection.designation;
      const d = (state?.designations ?? []).find((x) => String(x.id) === String(sel.id));
      if (!d) return null;
      const live = new Set((state?.designations ?? []).map((x) => String(x.id)));
      const cluster = sel.cluster.filter((x) => live.has(String(x)));
      const designations = (op: 'retry' | 'cancel', ids: Array<string | number>) =>
        void write('story.designations', { op, ids }).then(
          () => {
            say(
              op === 'retry'
                ? `Trying ${ids.length === 1 ? 'it' : `${ids.length}`} again now`
                : `${ids.length} designation${ids.length === 1 ? '' : 's'} cancelled`,
            );
            if (op === 'cancel' && ids.some((x) => String(x) === String(d.id))) close();
          },
          (e) => say(errorText(e), 'warn'),
        );
      return (
        <DesignationInspector
          designation={d}
          cluster={cluster.length ? cluster : [d.id]}
          canWrite={canWrite}
          onRetry={(ids) => designations('retry', ids)}
          onCancel={(ids) => designations('cancel', ids)}
          onClose={close}
        />
      );
    }
    if (selection.bench) {
      const bench = (state?.workbenches ?? []).find((w) => k3(w.pos) === k3(selection.bench!));
      return bench ? (
        <BillsInspector
          bench={bench}
          state={state}
          canWrite={canWrite}
          write={write}
          onPickChest={pickChest}
          onClose={close}
        />
      ) : (
        <UndesignatedBench pos={selection.bench} canWrite={canWrite} write={write} onClose={close} />
      );
    }
    if (selection.bed) {
      const bed = (state?.beds ?? []).find((b) => k3(b.pos) === k3(selection.bed!));
      return bed ? <BedInspector bed={bed} state={state} canWrite={canWrite} write={write} onClose={close} /> : null;
    }
    if (selection.chests.length)
      return (
        <StorageInspector
          chests={selection.chests}
          state={state}
          canWrite={canWrite}
          write={write}
          clip={clip}
          onClip={(c) => (setClip(c), say('Storage settings copied'))}
          onClose={close}
        />
      );
    if (selection.bodies.length === 1) {
      const p = state?.pawns.find((x) => x.name === selection.bodies[0]);
      const bed = p ? (state?.beds ?? []).find((b) => b.pawn === p.name) : undefined;
      if (p)
        return (
          <PawnInspector
            pawn={p}
            bed={bed}
            onShowBed={bed ? () => map.current?.focus(bed.pos[0], bed.pos[2], bed.pos[1]) : undefined}
            canWrite={canWrite}
            onDraft={(d) => void draft([p.name], d)}
            onClearQueue={() => void write('story.orders', { pawns: [p.name], order: { kind: 'stop' } })}
            onClose={close}
            blocked={stillBlocked.filter((b) => b.pawn === p.name)}
            onBringOnline={state?.active === false ? undefined : (overCap) => void bringOnline([p.name], overCap)}
          />
        );
      const a = state?.ai?.find((x) => x.name === selection.bodies[0]);
      if (a) return <AiInspector body={a} state={state} onChat={() => setChat(true)} onClose={close} />;
    }
    if (selection.bodies.length > 1)
      return (
        <GroupInspector
          names={selection.bodies}
          state={state}
          canWrite={canWrite}
          onDraft={(d) => void draft(split.pawns, d)}
          onClose={close}
        />
      );
    return null;
  })();

  // `/story?design=N` (from the design pages): pick the design up as a placement ghost on the map.
  const designParam = params.get('design');
  const designDone = useRef<string | null>(null);
  useEffect(() => {
    if (!designParam || !state || designDone.current === designParam) return;
    designDone.current = designParam;
    const id = Number(designParam);
    if (!Number.isInteger(id) || id < 1) return;
    void (async () => {
      const name = await api('designs.get', { params: { id } }).then(
        (d) => d.design.name,
        () => `design #${id}`,
      );
      let blocks = designBlocks(await api('blueprints.get', { params: { id } }).catch(() => null));
      if (!blocks.length) blocks = designBlocks(await api('buildplans.get', { params: { id } }).catch(() => null));
      // The Design Studio opens a design's newest revision as cells: the reliable source when the account may design.
      // (with its dug cells: a basement goes under the clicked level). Converted to the server's version: a design drawn
      // for the other one keeps what maps (substitutions said) and is refused when a block of it doesn't exist here.
      let converted = '';
      if (!blocks.length && can('design')) {
        const server = await api('designer.models')
          .then((m) => m.version)
          .catch(() => null);
        const s = await api('designer.create', {
          body: { designId: id, ...(server === 'b1.8.1' || server === '1.12.2' ? { convertTo: server } : {}) },
        }).catch(() => null);
        if (s?.compat && !s.compat.compatible)
          return say(
            `${name} is drawn for ${s.compat.from}; ${s.compat.to} has no ${s.compat.missing.map((m) => `${m.name} (${m.count})`).join(', ')}: convert it in the Design Studio first`,
            'warn',
          );
        if (s?.compat?.substituted.length)
          converted = ` (converted from ${s.compat.from}: ${s.compat.substituted.map((m) => `${m.count} ${m.name}`).join(', ')})`;
        blocks = designCells(s);
        // A mine design keeps its absolute y: it is refused when it would dig into the bedrock floor.
        if (s?.kind === 'mine' && blocks.length) {
          const m = mineCheck(blocks);
          if (m.refusal) return say(`${name}: ${m.refusal}`, 'warn');
          setTool({ kind: 'design', id, name, blocks, mine: { min: m.min, max: m.max } });
          return say(`${name}: a mine at y ${m.min}..${m.max}, absolute: move to a spot and click to queue it (Q/E turns)${converted}`);
        }
      }
      if (!blocks.length) return say(`${name} has no block list the colony can build from`, 'warn');
      setTool({ kind: 'design', id, name, blocks });
      say(`${name}: move to a spot and click to queue it (Q/E turns)${converted}`);
    })();
  }, [designParam, state, say, can]);

  /** A column as a home: the surface there (the working level when its chunk is not loaded). */
  const homeAt = async (x: number, z: number): Promise<{ x: number; y: number; z: number }> => {
    const y = (await map.current?.surface(x, z).catch(() => null)) ?? lv;
    return { x, y, z };
  };

  /** The map centre as a home. */
  const centreHome = async (): Promise<{ x: number; y: number; z: number } | null> => {
    const c = map.current?.center();
    return c ? homeAt(c[0], c[2]) : null;
  };

  const create = async (name: string, n: number) => {
    if (!canWrite) return say('Needs the story role', 'warn');
    const home = await centreHome();
    try {
      const out = await api('story.create', { body: { name, pawns: n, ...(home ? { home } : {}) } });
      await qc.invalidateQueries({ queryKey: ['story.list'] });
      router.replace(`/story?colony=${encodeURIComponent(out.id)}`);
      setTab(null);
      say(`${name} founded with ${n} pawn${n === 1 ? '' : 's'}`);
    } catch (e) {
      say(errorText(e), 'warn');
    }
  };

  /** Migrates home to a column (after the confirm): the bot brings far pawns over. */
  const moveHome = async (at: V3) => {
    setMigrate(null);
    if (!canWrite) return say('Needs the story role', 'warn');
    const home = await homeAt(at[0], at[2]);
    try {
      const out = await write('story.home', { home });
      setLevel(null);
      say(`Home moved to ${out.home[0]}, ${out.home[2]}: pawns far from it are brought here`);
    } catch (e) {
      say(errorText(e), 'warn');
    }
  };

  /** A body from the top bar: focus it when it is off the map's picture (a double-click always does). */
  const showBody = (name: string) => {
    const b = bodies.find((x) => x.name === name);
    if (b?.pos && !map.current?.onScreen(b.pos[0]!, b.pos[1]!, b.pos[2]!)) focusBody(name);
  };

  /** A zone's bills (planting or fishing), on the zones route. */
  const onZoneBill = (z: StoryZone, body: GrowBillChange) =>
    void write('story.zones', { ...body, id: z.id }).then(
      (out) =>
        say(out.warnings?.length ? out.warnings.join('; ') : 'Bills updated', out.warnings?.length ? 'warn' : 'ok'),
      (e) => say(errorText(e), 'warn'),
    );
  const focusZone = (z: StoryZone) =>
    map.current?.focus(Math.floor((z.area[0] + z.area[3]) / 2), Math.floor((z.area[2] + z.area[5]) / 2), z.area[1]);
  /** Fishing zones (zones/FishingZones.tsx), under the growing zones: water, fishers, catches, bills. */
  const fishingZones = <FishingZonesPanel state={state} canWrite={canWrite} onBill={onZoneBill} onShow={focusZone} />;

  /** Growing zones (GrowingZones.tsx), beside the Colony tab: plant, seeds, status, seek seeds. */
  const growingZones = (
    <GrowingZonesPanel
      state={state}
      canWrite={canWrite}
      onShow={(z) =>
        map.current?.focus(Math.floor((z.area[0] + z.area[3]) / 2), Math.floor((z.area[2] + z.area[5]) / 2), z.area[1])
      }
      onUpdate={(z, change) =>
        void write('story.zones', { op: 'update', id: z.id, ...change }).then(
          (out) =>
            say(
              out.warnings?.length ? out.warnings.join('; ') : 'Growing zone updated',
              out.warnings?.length ? 'warn' : 'ok',
            ),
          (e) => say(errorText(e), 'warn'),
        )
      }
      onBill={onZoneBill}
      onExtend={(z) => {
        setTool({ kind: 'zone', id: z.kind, op: 'add', into: z.id });
        setTab('architect');
      }}
      onSeek={(z) =>
        void write('story.zones', { op: 'seek_seeds', id: z.id }).then(
          (out) =>
            say(out.kind === 'bill' ? 'A bill for seeds was added' : `${out.count ?? 0} tall grass designated to cut`),
          (e) => say(errorText(e), 'warn'),
        )
      }
    />
  );

  const panelEl = (() => {
    switch (tab) {
      case 'architect':
        return (
          <ArchitectPanel
            tool={tool}
            onTool={setTool}
            plants={state?.plants}
            zones={state?.zones}
            palette={architect.data}
            onPick={(e) => {
              // A placeable: its default build tool, with it as the material (the build's auto bills craft what's missing).
              const furniture = (STORY_FURNITURE as readonly string[]).includes(e.kind);
              setTool({ kind: 'build', id: furniture ? `furniture:${e.kind}` : e.kind } as MapTool);
              if (!furniture) setBuild((b) => ({ ...b, material: [e.material[0], e.material[1]] }));
              say(`${e.label}: drag on the map to place`);
            }}
          />
        );
      case 'work':
        return (
          <WorkPanel
            pawns={pawns}
            ai={ai}
            canWrite={canWrite}
            onSet={(names, work) =>
              void write('story.priorities', { pawns: names, work }).catch((e) => say(errorText(e), 'warn'))
            }
          />
        );
      case 'schedule':
        return (
          <SchedulePanel
            pawns={pawns}
            ai={ai}
            hour={state?.hour}
            canWrite={canWrite}
            onPaint={(reqs) =>
              reqs.forEach((r) => void write('story.schedule', r).catch((e) => say(errorText(e), 'warn')))
            }
          />
        );
      case 'queue':
        return (
          <QueuePanel
            builds={state?.builds ?? []}
            canWrite={canWrite}
            onOp={(op, ids) => void write('story.builds', { op, ids }).catch((e) => say(errorText(e), 'warn'))}
          />
        );
      case 'directives':
        return <DirectivesPanel state={state} />;
      case 'colony':
        return (
          <ColonyPanel
            state={state}
            colonies={list}
            current={id}
            canWrite={canWrite}
            onSwitch={(c) => router.replace(`/story?colony=${encodeURIComponent(c)}`)}
            onCreate={(name, n) => void create(name, n)}
            centre={centre}
            migrate={migrate}
            onMoveHome={() => setMigrate(map.current?.center() ?? centre)}
            onConfirmMove={() => migrate && void moveHome(migrate)}
            onCancelMove={() => setMigrate(null)}
            onPickHome={() => {
              setMigrate(null);
              setTool({ kind: 'home' });
            }}
            onDrawHouse={() => setTool({ kind: 'zone', id: 'home', op: 'add' })}
            onShowZone={(z) =>
              map.current?.focus(
                Math.floor((z.area[0] + z.area[3]) / 2),
                Math.floor((z.area[2] + z.area[5]) / 2),
                z.area[1],
              )
            }
            onRenameZone={(z, name) =>
              void write('story.zones', { op: 'rename', id: z.id, name }).then(
                () => say(name ? `Renamed to ${name}` : 'Name cleared'),
                (e) => say(errorText(e), 'warn'),
              )
            }
            onDeleteZone={(z) =>
              void write('story.zones', { op: 'remove', id: z.id }).then(
                () => say(`${z.name || z.label || 'House'} removed`),
                (e) => say(errorText(e), 'warn'),
              )
            }
            onPause={togglePause}
            onOptions={(o) => void write('story.options', o).catch((e) => say(errorText(e), 'warn'))}
            onRelease={() => void write('story.release', {}).then(() => say('Pawns released; the colony is saved'))}
            onResume={() =>
              void write('story.resume', {}).then(
                () => say('The pawns are coming back'),
                (e) => say(errorText(e), 'warn'),
              )
            }
            blocked={stillBlocked}
            onBringAll={(overCap) => void bringOnline(null, overCap)}
            displayOf={displayOf}
          />
        );
      default:
        return null;
    }
  })();

  const queued = (state?.builds ?? []).filter((b) => b.status !== 'done').length;
  const directivesLive = (state?.directives ?? []).filter(
    (d) => d.status === 'sent' || d.status === 'acknowledged',
  ).length;

  return (
    <div
      ref={fsAttach}
      className={cn(
        'relative h-full min-h-[32rem] overflow-hidden bg-canvas select-none',
        fullscreen && 'h-screen w-screen',
      )}
      data-testid="story-mode"
      data-fullscreen={fullscreen || undefined}
    >
      {/* Dialogs and tooltips mount inside this element while it is full screen (else they would be invisible). */}
      <PortalContainer.Provider value={fullscreen ? fsEl : null}>
        <WorldHost>
          <StoryMap
            ref={map}
            state={state}
            selection={selection}
            tool={tool}
            build={build}
            mineFilter={mineFilter}
            heightMode={heightMode}
            layers={layers}
            level={lv}
            cutY={cutY}
            view={view}
            keys={keys}
            home={(state?.home as V3 | undefined) ?? null}
            settle={noColony}
            player={me.player ?? null}
            onCentre={noColony || tab === 'colony' ? setCentre : undefined}
            onSettled={setSettledY}
            onSelect={onSelect}
            onSelectSame={(kind, visible) =>
              setSelection({
                ...EMPTY_SELECTION,
                bodies: visible.filter((n) =>
                  kind === 'pawn' ? state?.pawns.some((p) => p.name === n) : state?.ai?.some((x) => x.name === n),
                ),
              })
            }
            onTool={(r, shift) => void onTool(r, shift)}
            onContext={onContext}
            onFormation={(moves, queue, ignored) => void onFormation(moves, queue, ignored)}
            onDropTool={() => setTool({ kind: 'select' })}
            onCancelled={() => say('Cancelled')}
          />
        </WorldHost>

        <div className="pointer-events-none absolute inset-0">
          {/* top: controls (left), the colonist bar (centre), alerts (right) */}
          <div className="absolute right-2 bottom-2">
            {state ? (
              <ControlCluster
                name={state.name ?? id ?? ''}
                hour={state.hour}
                paused={!!state.paused}
                canWrite={canWrite}
                onPause={togglePause}
                view={view}
                onView={() => setView((v) => (v === 'top' ? 'angled' : 'top'))}
                level={lv}
                ground={ground}
                height={build.height}
                xray={xrayOn}
                onLevel={(dir) => setLevel(nextLevel(lv, build.height, dir, false))}
                onXray={() => setXray(xrayOn ? 'off' : 'on')}
                onHelp={() => setHelp((h) => !h)}
              >
                <TimeSlider
                  tod={state.timeOfDay}
                  control={state.timeControl ?? colonies.data?.timeControl}
                  canWrite={canWrite}
                  onSet={setTime}
                />
              </ControlCluster>
            ) : null}
          </div>
          <div className="absolute top-2 right-2">
            <button
              type="button"
              onClick={toggleFullscreen}
              aria-label={fullscreen ? 'Exit full screen' : 'Full screen'}
              aria-pressed={fullscreen}
              title={fullscreen ? 'Exit full screen (Esc or Shift+F)' : 'Full screen (Shift+F)'}
              className={cn(panel, 'pointer-events-auto grid size-8 place-items-center hover:bg-surface-2')}
              data-testid="story-fullscreen"
            >
              {fullscreen ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
            </button>
          </div>
          <div className="absolute inset-x-12 top-2 flex flex-col items-center gap-1.5">
            {state ? (
              <ColonistBar
                bodies={bodies}
                selected={selection.bodies}
                order={order}
                onOrder={setOrder}
                onClick={(name, mods) => {
                  setSelection((s) => clickBody(s, name, mods));
                  if (!mods.shift && !mods.ctrl) showBody(name);
                }}
                onBox={(names, add) =>
                  setSelection((s) => ({
                    ...EMPTY_SELECTION,
                    bodies: add ? [...new Set([...s.bodies, ...names])] : names,
                  }))
                }
                onJump={(name) => {
                  setSelection({ ...EMPTY_SELECTION, bodies: [name] });
                  focusBody(name);
                }}
                onBringAll={
                  canWrite && state.active !== false
                    ? // Refused by the cap: the Colony tab shows why, with the explicit "Use regulars' seats".
                      () => void bringOnline(null).then((b) => b.length && setTab('colony'))
                    : null
                }
                pending={state.seats?.pending ?? 0}
                onAdd={
                  canWrite && state.active !== false
                    ? (n) =>
                        void write('story.pawns', { op: 'add', count: n }).then(
                          (out) =>
                            say(
                              out.blocked.length
                                ? `${n - out.pending.length} joining now; ${out.pending.length} wait: ${out.blocked[0]!.reason}`
                                : `${n} pawn${n === 1 ? '' : 's'} joining`,
                              out.blocked.length ? 'warn' : 'ok',
                            ),
                          (e) => say(errorText(e), 'warn'),
                        )
                    : null
                }
                onRemove={
                  canWrite && state.active !== false
                    ? (name) =>
                        void write('story.pawns', { op: 'remove', pawn: name }).then(
                          () => {
                            say(`${displayOf(name)} is leaving: their items go to storage`);
                            setSelection(EMPTY_SELECTION);
                          },
                          (e) => say(errorText(e), 'warn'),
                        )
                    : null
                }
              />
            ) : null}
            <OptionsBar
              tool={tool}
              build={build}
              onBuild={(b) => setBuild((x) => ({ ...x, ...b }))}
              mineFilter={mineFilter}
              onMineFilter={setMineFilter}
              heightMode={heightMode}
              onHeightMode={(m) => heightTool && setHeightModes((cur) => ({ ...cur, [heightTool]: m }))}
              layers={layers}
              onLayers={(n) => setLayers(clampLayers(n))}
              level={lv}
              stock={stock}
              onClose={() => setTool({ kind: 'select' })}
            />
            {pickHint ? (
              <div className={cn(panel, 'pointer-events-auto px-3 py-1.5 text-xs font-medium')}>
                Click a chest on the map to store the bill&apos;s products there (Esc cancels)
              </div>
            ) : null}
          </div>
          <div className="absolute top-[5.75rem] right-2">
            <StoryNotices state={state} colony={id} toast={toast} onJump={onJump} onCondition={onCondition} />
          </div>

          {/* middle overlays */}
          {help ? (
            <div className="absolute top-24 left-1/2 -translate-x-1/2">
              <HelpOverlay onClose={() => setHelp(false)} />
            </div>
          ) : null}
          {noColony ? (
            <div className="absolute inset-0 grid place-items-center">
              <div className={cn(panel, 'pointer-events-auto w-96 space-y-2 p-4')}>
                <h2 className="text-base font-semibold">Story Mode</h2>
                <p className="text-xs text-muted">
                  Run a colony of pawns: script-only bodies that take work by your priorities and schedule. Pan to where
                  they should settle, then found it.
                </p>
                <NewColony canWrite={canWrite} at={centre} onCreate={(name, n) => void create(name, n)} />
              </div>
            </div>
          ) : null}

          {/* bottom: inspector + the open tab's window, the tab bar, the toast, the chat */}
          <div className="absolute bottom-9 left-2 flex items-end gap-2">
            {inspector}
            {panelEl}
            {tab === 'colony' ? (
              <>
                {growingZones}
                {fishingZones}
              </>
            ) : null}
          </div>
          <div className="absolute bottom-12 left-1/2 -translate-x-1/2">
            <Toast text={toast?.text ?? null} tone={toast?.tone} />
          </div>
          <div className="absolute bottom-0 left-2">
            <TabBar open={tab} onOpen={setTab} badge={{ queue: queued, directives: directivesLive }} />
          </div>
          {state ? (
            <div className="pointer-events-auto">
              <ChatOverlay
                open={chat}
                targets={
                  split.ai.length ? split.ai : selection.bodies.filter((n) => state?.ai?.some((a) => a.name === n))
                }
                marker={null}
                canChat={canChat}
                results={null}
                onClose={() => setChat(false)}
                onSend={(text) =>
                  void sendDirective(split.ai, text).then((res) => {
                    const bad = res.filter((r) => !r.ok);
                    say(
                      bad.length ? `Not sent to ${bad.map((b) => b.bot).join(', ')}` : `Said to ${split.ai.join(', ')}`,
                      bad.length ? 'warn' : 'ok',
                    );
                  })
                }
              />
            </div>
          ) : null}
        </div>
        {menu ? (
          <ContextMenu
            at={menu.at}
            title={menu.title}
            items={menu.items}
            onPick={(it, q) => void onMenuPick(it, q)}
            onClose={() => setMenu(null)}
          />
        ) : null}
      </PortalContainer.Provider>
    </div>
  );
}

export type { StoryColonyState };
export { colonyKey };
