'use client';

/**
 * The 3D world pane: the viewer's `<WorldViewer>` (follow / fly / iso cameras, layers, x-ray, draw distance) plus the app-side
 * tools the viewer does not provide: a "go to" lookup (place, bot or x,y,z via /api/locate), a drops list, an F3-style HUD
 * (position, facing, the last picked block), and the container panel: a click on a chest, furnace, hopper… (in free cam, a click
 * or E with the crosshair on it) shows what the hive knows is inside. Renderer stats appear only with developer details on.
 */
import type { AimTarget, CameraMode, LayerToggles, PickedBlock, ViewerStats, WorldViewerHandle, XRayMode } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useAuth } from '@/lib/auth';
import { useEntities, useStatus } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { cn } from '@/lib/utils';
import { PortalContainer } from '@/components/ui/overlays';
import { isContainerBlock } from '@/features/live/inventory/containerLayout';
import { TakeControlButton } from '@/features/control/TakeControlButton';
import { concreteFields } from '@/features/settings/schema';
import { detectedProfile, setField, useRendererProbe, useViewerSettings } from '@/features/settings/store';
import { ContainerPanel, sameContainer, type ContainerTarget } from './ContainerPanel';
import { useWorldHost } from './integration';
import { ChatOverlay, Crosshair as AimCross, SelectionBar, TargetReadout } from './InWorld';
import { nextSelection, selectByNumber, sendDirective, type SendResult } from './directives';
import { goTo } from './worldGoto';
import { onWorldKey } from './worldKeys';
import { DropsPanel, FullscreenBar, PositionHud, StatsHud, WorldFallback, WorldToolbar } from './WorldPaneParts';

/** The Keyboard Lock API (fullscreen, secure contexts only): with Escape locked, Esc reaches the page instead of the browser. */
type KeyboardLock = { lock?: (keys: string[]) => Promise<void>; unlock?: () => void };
const keyboardOf = () => (navigator as Navigator & { keyboard?: KeyboardLock }).keyboard;

type Entities = ReturnType<typeof useEntities>['data'];
type Host = ReturnType<typeof useWorldHost>;

/** The viewer module and its source have loaded. */
const loaded = (h: Host): h is Host & { mod: NonNullable<Host['mod']>; source: NonNullable<Host['source']> } => h.ready && !!h.mod && !!h.source;

/** Following a bot starts in the follow camera; else free cam. */
const initialMode = (follow: string | undefined): CameraMode => (follow ? 'follow' : 'fly');

/** The followed bot (by name or short name) and its dimension (the overworld when none). */
function followed(data: Entities, follow: string | undefined) {
  const me = data?.bots.find((b) => b.name === follow || b.short === follow);
  return { me, dim: me?.dim ?? 0 };
}

/** In free cam: the crosshair and what it is on while the mouse is held, else (bar closed) how to look around. */
function FreeCamOverlay(p: { mode: CameraMode; locked: boolean; chatOpen: boolean; aim: AimTarget | null; bots: NonNullable<Entities>['bots'] | undefined }) {
  if (p.mode !== 'fly') return null;
  const aim = p.aim;
  if (p.locked)
    return (
      <>
        <AimCross />
        <TargetReadout target={aim} bot={aim?.kind === 'bot' ? p.bots?.find((b) => b.name === aim.name) : undefined} />
      </>
    );
  if (p.chatOpen) return null;
  return (
    <div className="pointer-events-none absolute top-3 left-1/2 -translate-x-1/2 rounded-md bg-black/55 px-2.5 py-1 text-center text-xs whitespace-nowrap text-white">
      Click to look around · WASD move · T chat · 1–9 pick a bot · Tab HUD · F full screen
    </div>
  );
}

/** The open container's panel, portalled into the stage (so it stays in fullscreen). */
function ContainerLayer({ container, stageEl, onClose }: { container: ContainerTarget | null; stageEl: HTMLDivElement | null; onClose: () => void }) {
  if (!container) return null;
  return (
    <PortalContainer.Provider value={stageEl}>
      <ContainerPanel
        key={`${container.dim}:${container.x},${container.y},${container.z}`}
        target={container}
        onClose={onClose}
        className="absolute bottom-3 left-1/2 z-10 max-h-[calc(100%-4.5rem)] -translate-x-1/2"
      />
    </PortalContainer.Provider>
  );
}

/**
 * `onBodyClick`: a bot clicked in the world (a plain click, or a free-cam click that selects it); the Live hive opens its
 * panel. `overlay`: drawn over the stage (inside it, so it stays in fullscreen).
 */
export function WorldPane({
  follow,
  returnTo,
  className,
  toolbarStart,
  onBodyClick,
  overlay,
}: {
  follow?: string;
  returnTo?: string;
  className?: string;
  toolbarStart?: ReactNode;
  onBodyClick?: (name: string) => void;
  overlay?: ReactNode;
}) {
  const host = useWorldHost();
  const bodyClick = useRef(onBodyClick);
  useEffect(() => {
    bodyClick.current = onBodyClick;
  }, [onBodyClick]);
  const { devDetails } = usePrefs();
  const viewer = useRef<WorldViewerHandle>(null);
  const [followTarget, setFollowTarget] = useState(follow);
  useEffect(() => setFollowTarget(follow), [follow]);
  const [mode, setMode] = useState<CameraMode>(initialMode(follow));
  const [xray, setXray] = useState<XRayMode>('auto');
  // The draw distance is the device's viewer setting (Settings → Viewer quality): this slider edits it for every view.
  const viewerSettings = useViewerSettings();
  const probe = useRendererProbe();
  const distance = concreteFields(viewerSettings, detectedProfile(probe)).drawDistance;
  const setDistance = (v: number) => setField('drawDistance', v, probe);
  const [layers, setLayers] = useState<Partial<LayerToggles>>({});
  const [hud, setHud] = useState(true);
  const [picked, setPicked] = useState<PickedBlock | null>(null);
  const [stats, setStats] = useState<ViewerStats | null>(null);
  const [gotoText, setGotoText] = useState('');
  const [gotoError, setGotoError] = useState<string | null>(null);
  const [drops, setDrops] = useState(false);
  const entities = useEntities();
  const status = useStatus();
  const { me, dim } = followed(entities.data, followTarget);
  const followedStatus = status.data?.bots.find((b) => b.name === me?.name);
  const dimRef = useRef(dim);
  useEffect(() => {
    dimRef.current = dim;
  }, [dim]);
  const { canChat, can } = useAuth();
  const takeover = mode === 'follow' && me && !me.main && (followedStatus?.kind ?? me.kind) === 'script'
    ? <TakeControlButton pawn={me.name} canControl={can('story')}
      online={me.online !== false && (followedStatus ? followedStatus.status === 'online' : me.online === true)} returnTo={returnTo} />
    : null;
  // The stage (the world and every overlay on it) is what goes fullscreen: the shared canvas moves with it, no new context.
  const stage = useRef<HTMLDivElement>(null);
  const [stageEl, setStageEl] = useState<HTMLDivElement | null>(null);
  const stageRef = useCallback((el: HTMLDivElement | null) => {
    stage.current = el;
    setStageEl(el);
  }, []);
  const [fullscreen, setFullscreen] = useState(false);
  const [locked, setLocked] = useState(false);
  const [escToPage, setEscToPage] = useState(false);

  const toggleFullscreen = useCallback(() => {
    const el = stage.current;
    if (!el) return;
    if (document.fullscreenElement === el) void document.exitFullscreen().catch(() => undefined);
    else void el.requestFullscreen?.({ navigationUI: 'hide' }).catch(() => undefined);
  }, []);

  useEffect(() => {
    const onFs = () => {
      const on = !!stage.current && document.fullscreenElement === stage.current;
      setFullscreen(on);
      setEscToPage(false);
      // Where the browser allows it (HTTPS), Esc comes to the page while fullscreen: the first Esc frees the mouse, the next
      // leaves fullscreen, like the game. Over plain HTTP the browser takes Esc and does both at once.
      if (on) void keyboardOf()?.lock?.(['Escape']).then(() => setEscToPage(true)).catch(() => undefined);
      else keyboardOf()?.unlock?.();
    };
    const onLock = () => setLocked(!!stage.current && !!document.pointerLockElement && stage.current.contains(document.pointerLockElement));
    document.addEventListener('fullscreenchange', onFs);
    document.addEventListener('pointerlockchange', onLock);
    return () => {
      document.removeEventListener('fullscreenchange', onFs);
      document.removeEventListener('pointerlockchange', onLock);
    };
  }, []);
  const [aim, setAim] = useState<AimTarget | null>(null);
  const aimRef = useRef<AimTarget | null>(null);
  useEffect(() => {
    aimRef.current = aim;
  }, [aim]);
  const [container, setContainer] = useState<ContainerTarget | null>(null);
  const closeContainer = useCallback(() => setContainer(null), []);
  const [selected, setSelected] = useState<string[]>([]);
  const [marker, setMarker] = useState<[number, number, number] | null>(null);
  const [chatOpen, setChatOpen] = useState(false);
  const [sent, setSent] = useState<SendResult[] | null>(null);
  const targets = selected.length ? selected : me ? [me.name] : [];

  // A picked block (a click in follow / iso, a click or E on the crosshair's block in free cam): a container opens its panel.
  const onBlockPick = useCallback((b: { x: number; y: number; z: number; id: number; dim: number }) => {
    if (isContainerBlock(b.id)) setContainer({ dim: b.dim, x: b.x, y: b.y, z: b.z });
  }, []);
  const onPick = useCallback(
    (b: PickedBlock) => {
      setPicked(b);
      onBlockPick(b);
    },
    [onBlockPick],
  );

  // Mock mode only (e2e): the chat bubbles this pane drew in its last frame, and its camera, for headless checks.
  useEffect(() => {
    if (process.env.NEXT_PUBLIC_MOCK !== '1') return;
    const w = window as unknown as {
      __mcxWorld?: {
        bubbles: () => unknown;
        camera: (m: CameraMode) => void;
        clickBot: (name: string) => void;
        clickBlock: (x: number, y: number, z: number, id: number) => void;
      };
    };
    w.__mcxWorld = {
      bubbles: () => viewer.current?.bubbles() ?? [],
      camera: (m) => viewer.current?.setCameraMode(m),
      clickBot: (name) => bodyClick.current?.(name), // what a click on that bot's model does
      clickBlock: (x, y, z, id) => onBlockPick({ x, y, z, id, dim: dimRef.current }), // what a click on that block does
    };
    return () => {
      delete w.__mcxWorld;
    };
  }, [onBlockPick]);

  // The bar needs the cursor: opening it releases the mouse, and while it is open nothing may lock it again (a click on the
  // world closes the bar instead, see below), so the chips and the close button stay clickable.
  const chatOpenRef = useRef(false);
  const openChat = useCallback(() => {
    chatOpenRef.current = true;
    if (document.pointerLockElement) document.exitPointerLock();
    setSent(null);
    setChatOpen(true);
  }, []);
  const closeChat = useCallback(() => {
    chatOpenRef.current = false;
    setChatOpen(false);
    viewer.current?.engine()?.surface.focus(); // back in the game: the next click locks the mouse again
  }, []);

  // While the bar is open, a press on the world (the canvas) closes it and takes the mouse back in free cam: one click,
  // which the viewer never sees (no pick, no selection). Capture phase, so it runs before the viewer's own listener.
  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const onDown = (e: PointerEvent) => {
      if (!chatOpenRef.current || !(e.target instanceof HTMLCanvasElement)) return;
      e.stopPropagation();
      e.preventDefault();
      closeChat();
      if (viewer.current?.getCameraMode() === 'fly' && e.button === 0) viewer.current.lock();
    };
    el.addEventListener('pointerdown', onDown, true);
    return () => el.removeEventListener('pointerdown', onDown, true);
  }, [closeChat, host.ready]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) =>
      onWorldKey(e, {
        stage: stage.current,
        chatOpen: () => chatOpenRef.current,
        toggleHud: () => setHud((h) => !h),
        openChat,
        closeChat,
        toggleFullscreen,
        toggleAimedContainer: () => {
          const t = aimRef.current;
          if (t?.kind === 'block' && isContainerBlock(t.id)) {
            const next = { dim: dimRef.current, x: t.x, y: t.y, z: t.z };
            setContainer((cur) => (sameContainer(cur, next) ? null : next));
          } else setContainer(null);
        },
        selectNumber: (n, add) => {
          const names = viewer.current?.botsInView().map((b) => b.name) ?? [];
          setSelected((cur) => selectByNumber(cur, names, n, add));
        },
      });
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [toggleFullscreen, openChat, closeChat]);

  const onAimClick = useCallback((t: AimTarget | null, mods: { shift: boolean; ctrl: boolean }) => {
    if (mods.ctrl) {
      setSelected([]);
      return;
    }
    if (t?.kind === 'bot') {
      setSelected((cur) => nextSelection(cur, t.name, mods));
      if (!mods.shift) bodyClick.current?.(t.name);
    } else if (t?.kind === 'block') {
      setMarker([t.x, t.y, t.z]);
      if (isContainerBlock(t.id)) setContainer({ dim: dimRef.current, x: t.x, y: t.y, z: t.z });
    }
  }, []);

  const nearbyDrops = useMemo(() => {
    if (!me) return [];
    return (entities.data?.entities ?? [])
      .filter((e) => e.kind === 'item' && e.dim === dim)
      .map((e) => ({ e, d: Math.hypot(e.x - me.x, e.y - me.y, e.z - me.z) }))
      .sort((a, b) => a.d - b.d)
      .slice(0, 60);
  }, [entities.data, me, dim]);

  const go = async (e: FormEvent) => {
    e.preventDefault();
    setGotoError(null);
    const q = gotoText.trim();
    if (!q) return;
    const err = await goTo(q, viewer, mode, setMode, setFollowTarget);
    if (err !== null) setGotoError(err);
  };
  const onMode = (m: CameraMode) => {
    setMode(m);
    viewer.current?.setCameraMode(m);
  };

  if (!loaded(host)) return <WorldFallback follow={followTarget} ready={host.ready} className={className} />;
  const { mod, source } = host;

  return (
    <div className={cn('flex size-full min-h-0 flex-col', className)}>
      <WorldToolbar
        toolbarStart={toolbarStart}
        takeover={takeover}
        mode={mode}
        onMode={onMode}
        layers={layers}
        setLayers={setLayers}
        xray={xray}
        setXray={setXray}
        distance={distance}
        setDistance={setDistance}
        gotoText={gotoText}
        setGotoText={setGotoText}
        gotoError={gotoError}
        onGo={(e) => void go(e)}
        drops={drops}
        toggleDrops={() => setDrops((d) => !d)}
        dropCount={nearbyDrops.length}
        hud={hud}
        toggleHud={() => setHud((h) => !h)}
        toggleFullscreen={toggleFullscreen}
      />
      <div ref={stageRef} className="relative min-h-0 flex-1 bg-canvas" data-testid="world-stage">
        <mod.WorldViewer
          ref={viewer}
          source={source}
          dim={dim}
          follow={followTarget ?? null}
          layers={layers}
          cameraMode={mode}
          xray={xray}
          onPick={onPick}
          onStats={devDetails ? setStats : undefined}
          onCameraModeChange={setMode}
          onSelectBot={(name) => {
            setFollowTarget(name);
            viewer.current?.followBot(name);
            bodyClick.current?.(name);
          }}
          onAim={setAim}
          onAimClick={onAimClick}
          marker={marker}
          highlighted={selected}
          hideHint
          className="absolute inset-0"
        />
        <FreeCamOverlay mode={mode} locked={locked} chatOpen={chatOpen} aim={aim} bots={entities.data?.bots} />
        <FullscreenBar shown={fullscreen} mode={mode} onMode={onMode} selected={selected} marker={marker} escToPage={escToPage} toggleFullscreen={toggleFullscreen} takeover={takeover} />
        {hud ? <SelectionBar selected={selected} marker={marker} onClear={() => setSelected([])} onClearMarker={() => setMarker(null)} /> : null}
        <ChatOverlay
          open={chatOpen}
          targets={targets}
          marker={marker}
          canChat={canChat}
          onSend={(text) => void sendDirective(targets, text).then(setSent)}
          onClose={closeChat}
          results={sent}
        />
        <PositionHud shown={hud} me={me} picked={picked} />
        <DropsPanel shown={drops} me={me} drops={nearbyDrops} onFocus={(e) => viewer.current?.focus(e.x, e.y, e.z)} />
        {overlay}
        <ContainerLayer container={container} stageEl={stageEl} onClose={closeContainer} />
        <StatsHud stats={devDetails ? stats : null} />
      </div>
    </div>
  );
}
