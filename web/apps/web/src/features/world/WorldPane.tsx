'use client';

/**
 * The 3D world pane: the viewer's `<WorldViewer>` (follow / fly / iso cameras, layers, x-ray, draw distance) plus the app-side
 * tools the viewer does not provide: a "go to" lookup (place, bot or x,y,z via /api/locate), a drops list, and an F3-style HUD
 * (position, facing, the last picked block). Renderer stats appear only with developer details on.
 */
import type { AimTarget, CameraMode, LayerToggles, PickedBlock, ViewerStats, WorldViewerHandle, XRayMode } from '@mineclaw/world-viewer';
import { Crosshair, Gem, Layers, LocateFixed, Maximize, Minimize, Monitor } from 'lucide-react';
import { Popover } from 'radix-ui';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api, errorText } from '@/lib/api/client';
import { useAuth } from '@/lib/auth';
import { useEntities } from '@/lib/live/hooks';
import { usePrefs } from '@/lib/prefs';
import { bytes, cn, dimName, num } from '@/lib/utils';
import { Button, Checkbox, Input, Select } from '@/components/ui/primitives';
import { Segmented, Tooltip } from '@/components/ui/overlays';
import { ViewerQualityButton } from '@/features/settings/QuickSettings';
import { concreteFields } from '@/features/settings/schema';
import { detectedProfile, setField, useRendererProbe, useViewerSettings } from '@/features/settings/store';
import { DotMap } from './DotMap';
import { useWorldHost } from './integration';
import { ChatOverlay, Crosshair as AimCross, SelectionBar, TargetReadout } from './InWorld';
import { closesOpenBar, isTyping, nextSelection, selectByNumber, sendDirective, type SendResult } from './directives';

/** The Keyboard Lock API (fullscreen, secure contexts only): with Escape locked, Esc reaches the page instead of the browser. */
type KeyboardLock = { lock?: (keys: string[]) => Promise<void>; unlock?: () => void };
const keyboardOf = () => (navigator as Navigator & { keyboard?: KeyboardLock }).keyboard;

const LAYER_LABELS: Array<[keyof LayerToggles, string]> = [
  ['bots', 'Bots'],
  ['entities', 'Mobs, players, drops'],
  ['claims', 'Claims'],
  ['places', 'Places & structures'],
  ['points', 'Chests, gens, farms, portals'],
  ['plans', 'Build plans (ghosts)'],
  ['dangers', 'Dangers'],
  ['notes', 'Notes'],
  ['bubbles', 'Chat bubbles'],
  ['weather', 'Weather'],
  ['cutaway', 'Roof cut-away'],
  ['staleness', 'Tint unseen areas'],
  ['changes', 'Highlight changed blocks'],
];

function facing(yaw: number | null | undefined): string {
  if (yaw === null || yaw === undefined) return '—';
  // Minecraft yaw: 0 = south (+z), 90 = west, 180 = north, 270 = east
  const dirs = ['south', 'south-west', 'west', 'north-west', 'north', 'north-east', 'east', 'south-east'];
  return dirs[Math.round((((yaw % 360) + 360) % 360) / 45) % 8]!;
}

function parseCoords(q: string): [number, number, number] | null {
  const m = /^\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*$/.exec(q);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

/**
 * `onBodyClick`: a bot clicked in the world (a plain click, or a free-cam click that selects it); the Live hive opens its
 * panel. `overlay`: drawn over the stage (inside it, so it stays in fullscreen).
 */
export function WorldPane({
  follow,
  className,
  toolbarStart,
  onBodyClick,
  overlay,
}: {
  follow?: string;
  className?: string;
  toolbarStart?: ReactNode;
  onBodyClick?: (name: string) => void;
  overlay?: ReactNode;
}) {
  const { mod, source, ready } = useWorldHost();
  const bodyClick = useRef(onBodyClick);
  useEffect(() => {
    bodyClick.current = onBodyClick;
  }, [onBodyClick]);
  const { devDetails } = usePrefs();
  const viewer = useRef<WorldViewerHandle>(null);
  const [mode, setMode] = useState<CameraMode>(follow ? 'follow' : 'fly');
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
  const me = entities.data?.bots.find((b) => b.name === follow || b.short === follow);
  const dim = me?.dim ?? 0;
  const { canChat } = useAuth();
  // The stage (the world and every overlay on it) is what goes fullscreen: the shared canvas moves with it, no new context.
  const stage = useRef<HTMLDivElement>(null);
  const [fullscreen, setFullscreen] = useState(false);
  const [locked, setLocked] = useState(false);
  const [aim, setAim] = useState<AimTarget | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [marker, setMarker] = useState<[number, number, number] | null>(null);
  const [chatOpen, setChatOpen] = useState(false);
  const [sent, setSent] = useState<SendResult[] | null>(null);
  const [escToPage, setEscToPage] = useState(false);
  const targets = selected.length ? selected : me ? [me.name] : [];

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

  // Mock mode only (e2e): the chat bubbles this pane drew in its last frame, and its camera, for headless checks.
  useEffect(() => {
    if (process.env.NEXT_PUBLIC_MOCK !== '1') return;
    const w = window as unknown as { __mcxWorld?: { bubbles: () => unknown; camera: (m: CameraMode) => void; clickBot: (name: string) => void } };
    w.__mcxWorld = {
      bubbles: () => viewer.current?.bubbles() ?? [],
      camera: (m) => viewer.current?.setCameraMode(m),
      clickBot: (name) => bodyClick.current?.(name), // what a click on that bot's model does
    };
    return () => {
      delete w.__mcxWorld;
    };
  }, []);

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
  }, [closeChat, ready]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'F3') {
        e.preventDefault();
        setHud((h) => !h);
        return;
      }
      if (isTyping(e.target)) return;
      const el = stage.current;
      const inWorld = !!el && (el.contains(document.activeElement) || document.fullscreenElement === el || (!!document.pointerLockElement && el.contains(document.pointerLockElement)));
      if (!inWorld || e.altKey || e.ctrlKey || e.metaKey) return;
      if ((e.key === 'Enter' || e.key === ' ') && (e.target as HTMLElement | null)?.closest?.('button, a')) return; // a focused control
      if (chatOpenRef.current) {
        // The bar is open but its input doesn't have the keys (disabled, or a button has focus): T / Enter / Esc close it.
        if (closesOpenBar(e)) {
          e.preventDefault();
          closeChat();
        }
        return;
      }
      if (e.code === 'KeyF' && !e.shiftKey) {
        e.preventDefault();
        toggleFullscreen();
      } else if (e.code === 'KeyT' || e.key === 'Enter' || e.key === '/') {
        e.preventDefault();
        openChat();
      } else if (e.key === 'Tab') {
        e.preventDefault();
        setHud((h) => !h);
      } else if (/^Digit[1-9]$/.test(e.code)) {
        const names = viewer.current?.botsInView().map((b) => b.name) ?? [];
        setSelected((cur) => selectByNumber(cur, names, Number(e.code.slice(5)), e.shiftKey));
      } else if (e.key === 'Escape') {
        if (document.pointerLockElement) document.exitPointerLock();
        else if (document.fullscreenElement === el) void document.exitFullscreen().catch(() => undefined);
      }
    };
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
    } else if (t?.kind === 'block') setMarker([t.x, t.y, t.z]);
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
    const c = parseCoords(q);
    if (c) {
      viewer.current?.focus(c[0], c[1], c[2]);
      return;
    }
    const bot = viewer.current?.bots().find((b) => b.name.toLowerCase() === q.toLowerCase() || b.dto.short === q);
    if (bot && mode === 'fly') {
      // Free cam stays free: fly to the bot with the crosshair on its body.
      viewer.current?.focus(bot.x - 0.5, bot.y + 0.6, bot.z - 0.5, { distance: 4, pitch: 8 });
      return;
    }
    if (bot) {
      viewer.current?.followBot(bot.name);
      setMode('follow');
      return;
    }
    try {
      const r = await api('locate', { query: { q } });
      viewer.current?.focus(r.x, r.y, r.z);
    } catch (err) {
      setGotoError(errorText(err).replace('Not found.', `No place called “${q}”.`));
    }
  };

  if (!ready || !mod || !source) {
    return (
      <div className={cn('relative size-full', className)}>
        {follow ? <DotMap follow={follow} radius={40} className="absolute inset-0" /> : <div className="absolute inset-0 bg-canvas" />}
        {ready ? (
          <div className="absolute top-3 left-3 max-w-xs rounded-lg border border-border bg-surface px-3 py-2 text-xs text-muted shadow-card">
            The 3D viewer could not load. Showing a top-down map of nearby bots and entities.
          </div>
        ) : null}
      </div>
    );
  }

  return (
    <div className={cn('flex size-full min-h-0 flex-col', className)}>
      <div className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-3 py-2">
        {toolbarStart}
        <Segmented<CameraMode>
          label="Camera"
          size="sm"
          value={mode}
          onChange={(m) => {
            setMode(m);
            viewer.current?.setCameraMode(m);
          }}
          options={[
            { value: 'follow', label: 'Follow', title: 'Orbit the followed bot' },
            { value: 'fly', label: 'Fly', title: 'WASD/QE + mouse; Shift fast; wheel sets speed' },
            { value: 'iso', label: 'Iso', title: 'Isometric dolly; Q/E rotate' },
          ]}
        />
        <Popover.Root>
          <Popover.Trigger asChild>
            <Button size="sm" variant="ghost">
              <Layers className="size-3.5" /> Layers
            </Button>
          </Popover.Trigger>
          <Popover.Portal>
            <Popover.Content align="start" sideOffset={6} className="z-50 w-64 space-y-2 rounded-xl border border-border bg-surface p-4 shadow-pop">
              {LAYER_LABELS.map(([k, label]) => (
                <div key={k}>
                  <Checkbox checked={layers[k] ?? true} onChange={(v) => setLayers((l) => ({ ...l, [k]: v }))} label={label} />
                </div>
              ))}
              <div className="border-t border-border pt-3">
                <label className="mb-1 block text-xs text-muted" htmlFor="xray">
                  X-ray toward bots
                </label>
                <Select id="xray" value={xray} onChange={(e) => setXray(e.target.value as XRayMode)} className="h-8 w-full">
                  <option value="auto">Auto (when hidden)</option>
                  <option value="on">On</option>
                  <option value="off">Off</option>
                </Select>
              </div>
              <div>
                <label className="mb-1 flex justify-between text-xs text-muted" htmlFor="dist">
                  Draw distance (all views) <span className="tabular text-fg">{distance} blocks</span>
                </label>
                <input id="dist" type="range" min={32} max={256} step={16} value={distance} onChange={(e) => setDistance(Number(e.target.value))} className="w-full accent-[var(--accent)]" />
              </div>
            </Popover.Content>
          </Popover.Portal>
        </Popover.Root>
        <ViewerQualityButton />
        <form onSubmit={(e) => void go(e)} className="flex items-center gap-1">
          <Input
            value={gotoText}
            onChange={(e) => setGotoText(e.target.value)}
            placeholder="Go to place, bot or x,y,z"
            aria-label="Go to a place, a bot or coordinates"
            className="h-8 w-56"
          />
          <Button type="submit" size="icon-sm" variant="ghost" aria-label="Go">
            <LocateFixed className="size-3.5" />
          </Button>
        </form>
        {gotoError ? <span className="text-xs text-danger">{gotoError}</span> : null}
        <span className="flex-1" />
        <Tooltip content="Drops near the bot">
          <Button size="sm" variant={drops ? 'subtle' : 'ghost'} aria-pressed={drops} onClick={() => setDrops((d) => !d)}>
            <Gem className="size-3.5" /> Drops{nearbyDrops.length ? <span className="tabular text-subtle">{nearbyDrops.length}</span> : null}
          </Button>
        </Tooltip>
        <Tooltip content="Position HUD (F3)">
          <Button size="sm" variant={hud ? 'subtle' : 'ghost'} aria-pressed={hud} onClick={() => setHud((h) => !h)}>
            <Crosshair className="size-3.5" /> HUD
          </Button>
        </Tooltip>
        <Tooltip content="Full screen (F)">
          <Button size="icon-sm" variant="ghost" aria-label="Full screen" onClick={toggleFullscreen}>
            <Maximize className="size-3.5" />
          </Button>
        </Tooltip>
      </div>
      <div ref={stage} className="relative min-h-0 flex-1 bg-canvas" data-testid="world-stage">
        <mod.WorldViewer
          ref={viewer}
          source={source}
          dim={dim}
          follow={follow ?? null}
          layers={layers}
          cameraMode={mode}
          xray={xray}
          onPick={setPicked}
          onStats={devDetails ? setStats : undefined}
          onCameraModeChange={setMode}
          onSelectBot={(name) => {
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
        {locked && mode === 'fly' ? (
          <>
            <AimCross />
            <TargetReadout target={aim} bot={aim?.kind === 'bot' ? entities.data?.bots.find((b) => b.name === aim.name) : undefined} />
          </>
        ) : null}
        {mode === 'fly' && !locked && !chatOpen ? (
          <div className="pointer-events-none absolute top-3 left-1/2 -translate-x-1/2 rounded-md bg-black/55 px-2.5 py-1 text-center text-xs whitespace-nowrap text-white">
            Click to look around · WASD move · T chat · 1–9 pick a bot · Tab HUD · F full screen
          </div>
        ) : null}
        {fullscreen ? (
          <div className="absolute top-3 right-3 z-10 flex gap-1.5" style={{ marginTop: selected.length || marker ? 32 : 0 }}>
            <Segmented<CameraMode>
              label="Camera"
              size="sm"
              value={mode}
              onChange={(m) => {
                setMode(m);
                viewer.current?.setCameraMode(m);
              }}
              options={[
                { value: 'follow', label: 'Follow' },
                { value: 'fly', label: 'Free cam' },
              ]}
            />
            <Button size="sm" variant="subtle" onClick={toggleFullscreen} aria-label="Exit full screen">
              <Minimize className="size-3.5" /> Exit {escToPage ? '(Esc twice)' : '(Esc)'}
            </Button>
          </div>
        ) : null}
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
        {hud ? (
          <div className="pointer-events-none absolute top-3 left-3 space-y-0.5 rounded-md bg-black/55 px-2.5 py-2 font-mono text-[11px] leading-snug text-white">
            {me ? (
              <>
                <div>
                  {me.name} · {dimName(me.dim)}
                </div>
                <div>
                  XYZ {me.x.toFixed(1)} / {me.y.toFixed(1)} / {me.z.toFixed(1)}
                </div>
                <div>
                  Facing {facing(me.yaw)} ({Math.round(me.yaw ?? 0)}°)
                </div>
              </>
            ) : (
              <div>no followed bot</div>
            )}
            <div className="pt-1 text-white/80">
              {picked
                ? `Block ${picked.name}${picked.meta ? `:${picked.meta}` : ''} at ${picked.x},${picked.y},${picked.z} · ${picked.live ? 'live' : 'remembered'}${picked.by ? ` · seen by ${picked.by}` : ''}`
                : 'Click a block to inspect it'}
            </div>
          </div>
        ) : null}
        {drops ? (
          <div className="absolute top-3 right-3 bottom-3 flex w-64 flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-pop">
            <div className="border-b border-border px-3 py-2 text-xs font-medium text-fg">Drops near {me?.name ?? 'the bot'}</div>
            <ul className="min-h-0 flex-1 overflow-auto py-1 text-xs">
              {nearbyDrops.length === 0 ? <li className="px-3 py-4 text-center text-muted">No drops nearby.</li> : null}
              {nearbyDrops.map(({ e, d }) => (
                <li key={`${e.dim}:${e.id ?? `${e.x},${e.z}`}`}>
                  <button onClick={() => viewer.current?.focus(e.x, e.y, e.z)} className="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-surface-2">
                    <span className="min-w-0 flex-1 truncate text-fg">
                      {e.name ?? 'item'} ×{e.count ?? 1}
                    </span>
                    <span className="tabular text-subtle">{d.toFixed(0)} m</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        {overlay}
        {devDetails && stats ? (
          <div className="pointer-events-none absolute bottom-3 left-3 rounded-md bg-black/55 px-2.5 py-2 font-mono text-[11px] leading-snug text-white">
            <div className="flex items-center gap-1.5">
              <Monitor className="size-3" /> {stats.fps.toFixed(0)} fps · {stats.frameMs.toFixed(1)} ms · {Math.round(stats.resolutionScale * 100)}% res
            </div>
            <div>
              {num(stats.drawCalls)} draws · {num(stats.triangles)} tris · {stats.textured ? 'textured' : 'flat'} · {stats.version}
            </div>
            <div>
              {num(stats.memory.columns)} columns · {num(stats.memory.sections)} sections ({num(stats.memory.sectionsQueued)} queued) · {stats.memory.views} views
            </div>
            <div>
              GPU {bytes(stats.memory.gpuBytes)} · worker {bytes(stats.memory.workerBytes)} · received {bytes(stats.memory.bytesReceived)}
            </div>
            <div className="text-white/70">{stats.gpu}</div>
          </div>
        ) : null}
      </div>
    </div>
  );
}
