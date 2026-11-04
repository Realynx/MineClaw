import { forwardRef, useEffect, useImperativeHandle, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react';
import { WorldViewerEngine, type EngineOptions } from '../render/engine';
import type { CameraMode, Dim, LayerToggles, PickedBlock, ViewerStats, WorldSource, XRayMode } from '../types';
import type { BotView } from '../world/entities';
import type { AimTarget } from '../render/aim';
import type { BubbleKind, ShownBubble } from '../render/bubbles';
import { useWorldRuntime } from './WorldThumbnail';
import { getViewerQuality, subscribeViewerQuality } from '../render/viewerQuality';

export interface WorldViewerProps {
  source: WorldSource;
  /** 0 overworld, -1 nether. */
  dim?: Dim;
  /** Bot name (or short key) the follow/iso cameras track and the x-ray keeps in sight. */
  follow?: string | null;
  layers?: Partial<LayerToggles>;
  /** Initial camera mode (change it later with the handle or this prop). */
  cameraMode?: CameraMode;
  xray?: XRayMode;
  /** Blocks, 32..256: the most this view draws (the viewer settings' draw distance applies below it). Absent = the setting. */
  drawDistance?: number;
  onSelectBot?: (name: string) => void;
  onPick?: (block: PickedBlock) => void;
  onStats?: (s: ViewerStats) => void;
  onCameraModeChange?: (m: CameraMode) => void;
  onError?: (route: string, e: unknown) => void;
  /** Free cam, mouse locked: what the crosshair points at (null = nothing, or the mouse was released). */
  onAim?: (t: AimTarget | null) => void;
  /** A left click with the mouse locked, on the crosshair's target (replaces the plain pick while locked). */
  onAimClick?: (t: AimTarget | null, mods: { shift: boolean; ctrl: boolean }) => void;
  /** A target block marker drawn in the world. */
  marker?: [number, number, number] | null;
  /** Bots outlined in the world (a selection). */
  highlighted?: string[];
  /** Hide the built-in "click to look around" hint (the app draws its own HUD). */
  hideHint?: boolean;
  /** Runtime tuning when the viewer has no WorldViewerProvider above (poll periods, LRU size, worker factory, pixel ratio).
   * Read once at mount. Inside a provider the shared runtime's options apply. */
  options?: Omit<EngineOptions, 'source' | 'runtime' | 'dim' | 'follow' | 'layers' | 'cameraMode' | 'xray' | 'drawDistance' | 'onSelectBot' | 'onPick' | 'onStats' | 'onNotes' | 'onCameraModeChange' | 'onError'>;
  className?: string;
  style?: CSSProperties;
}

export interface WorldViewerHandle {
  /** Centre the camera on a block (stops following until `follow` changes or `followBot` is called). */
  focus(x: number, y: number, z: number, view?: { distance?: number; pitch?: number }): void;
  setCameraMode(mode: CameraMode): void;
  getCameraMode(): CameraMode;
  followBot(name: string | null): void;
  bots(): BotView[];
  stats(): ViewerStats | null;
  /** The crosshair's current target (free cam, mouse locked). */
  aim(): AimTarget | null;
  /** Bots of the viewed dimension, nearest the camera first. */
  botsInView(): BotView[];
  /** Locks the mouse to the view (free cam look); needs a user gesture. */
  lock(): void;
  /** Whether the mouse is locked to this view. */
  locked(): boolean;
  /** A chat bubble over the bot or player called `name`, in every view of the runtime (thumbnails too). */
  say(name: string, text: string, opts?: { kind?: BubbleKind }): boolean;
  /** The chat bubbles this view drew in its last frame: speaker, anchor in CSS pixels of the view, lines. */
  bubbles(): readonly ShownBubble[];
  /** The underlying engine (advanced use). */
  engine(): WorldViewerEngine | null;
}

/**
 * The three.js world viewer. Mount it in a sized container; it fills it. One WebGL context, one mesh worker and the polling
 * loops live exactly as long as the component (everything is released on unmount).
 */
export const WorldViewer = forwardRef<WorldViewerHandle, WorldViewerProps>(function WorldViewer(props, ref) {
  const host = useRef<HTMLDivElement | null>(null);
  const engine = useRef<WorldViewerEngine | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [mode, setMode] = useState<CameraMode>(props.cameraMode ?? 'follow');
  const [locked, setLocked] = useState(false);
  useEffect(() => {
    const onLock = () => setLocked(!!document.pointerLockElement && !!host.current?.contains(document.pointerLockElement));
    document.addEventListener('pointerlockchange', onLock);
    return () => document.removeEventListener('pointerlockchange', onLock);
  }, []);
  const cbs = useRef(props);
  cbs.current = props;
  // Inside a WorldViewerProvider: draw from the shared runtime (wait while it mounts); otherwise own a private one.
  const shared = useWorldRuntime();

  useEffect(() => {
    if (!host.current || shared === null) return;
    const e = new WorldViewerEngine(host.current, {
      ...props.options,
      runtime: shared ?? undefined,
      source: props.source,
      dim: props.dim,
      follow: props.follow,
      layers: props.layers,
      cameraMode: props.cameraMode,
      xray: props.xray,
      drawDistance: props.drawDistance,
      onSelectBot: (n) => cbs.current.onSelectBot?.(n),
      onPick: (b) => cbs.current.onPick?.(b),
      onStats: (s) => cbs.current.onStats?.(s),
      onNotes: (n) => setNotes((old) => (old.join('\n') === n.join('\n') ? old : n)),
      onCameraModeChange: (m) => {
        setMode(m);
        cbs.current.onCameraModeChange?.(m);
      },
      onError: (r, err) => cbs.current.onError?.(r, err),
      onAim: (t) => cbs.current.onAim?.(t),
      onAimClick: (t, m) => (cbs.current.onAimClick ? cbs.current.onAimClick(t, m) : undefined),
    });
    if (!props.onAimClick) e.setCallbacks({ onAimClick: undefined });
    engine.current = e;
    return () => {
      engine.current = null;
      e.dispose();
    };
    // The engine is created once per mount (and runtime); prop changes are pushed below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shared]);

  useEffect(() => engine.current?.setSource(props.source), [props.source]);
  useEffect(() => engine.current?.setDim(props.dim ?? 0), [props.dim]);
  useEffect(() => engine.current?.setFollow(props.follow ?? null), [props.follow]);
  const layersKey = JSON.stringify(props.layers ?? {});
  useEffect(() => engine.current?.setLayers(props.layers ?? {}), [layersKey]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (props.cameraMode) setMode(props.cameraMode);
    if (props.cameraMode && engine.current && engine.current.getCameraMode() !== props.cameraMode) engine.current.setCameraMode(props.cameraMode);
  }, [props.cameraMode]);
  useEffect(() => engine.current?.setXRay(props.xray ?? 'auto'), [props.xray]);
  useEffect(() => engine.current?.setDrawDistance(props.drawDistance ?? null), [props.drawDistance]);
  const markerKey = props.marker ? props.marker.join(',') : '';
  useEffect(() => engine.current?.setMarker(props.marker ?? null), [markerKey]); // eslint-disable-line react-hooks/exhaustive-deps
  const highlightKey = (props.highlighted ?? []).join(',');
  useEffect(() => engine.current?.setHighlighted(props.highlighted ?? []), [highlightKey]); // eslint-disable-line react-hooks/exhaustive-deps

  useImperativeHandle(
    ref,
    () => ({
      focus: (x, y, z, view) => engine.current?.focus(x, y, z, view),
      setCameraMode: (m) => engine.current?.setCameraMode(m),
      getCameraMode: () => engine.current?.getCameraMode() ?? 'follow',
      followBot: (n) => engine.current?.setFollow(n),
      bots: () => engine.current?.bots() ?? [],
      stats: () => engine.current?.stats() ?? null,
      aim: () => engine.current?.aim() ?? null,
      botsInView: () => engine.current?.botsInView() ?? [],
      lock: () => {
        const s = engine.current?.surface;
        if (!s) return;
        s.focus();
        void Promise.resolve(s.requestPointerLock?.()).catch(() => undefined);
      },
      locked: () => engine.current?.locked() ?? false,
      say: (name, text, opts) => engine.current?.say(name, text, opts) ?? false,
      bubbles: () => engine.current?.bubbles() ?? [],
      engine: () => engine.current,
    }),
    [],
  );

  return (
    <div className={props.className} style={{ position: 'relative', overflow: 'hidden', width: '100%', height: '100%', ...props.style }}>
      <div ref={host} style={{ position: 'absolute', inset: 0 }} />
      <FlyHint mode={mode} locked={locked} hidden={props.hideHint} />
      <PerfOverlay engine={engine} />
      <WorldNotes notes={notes} layers={props.layers} />
    </div>
  );
});

/** Free cam with the mouse not yet locked: how to look around and move. */
function FlyHint({ mode, locked, hidden }: { mode: CameraMode; locked: boolean; hidden?: boolean }) {
  if (mode !== 'fly' || locked || hidden) return null;
  return (
    <div
      style={{ position: 'absolute', left: '50%', top: 12, transform: 'translateX(-50%)', padding: '4px 10px', font: '12px system-ui, sans-serif', color: '#fff', background: 'rgba(0,0,0,0.55)', borderRadius: 6, pointerEvents: 'none', whiteSpace: 'nowrap' }}
    >
      Click to look around · WASD move · Space/E up · Q down · Shift fast · Esc releases the mouse
    </div>
  );
}

/** The overlays' notes (the `notes` layer, on by default), bottom left. */
function WorldNotes({ notes, layers }: { notes: string[]; layers?: Partial<LayerToggles> }) {
  if (!(layers?.notes ?? true) || notes.length === 0) return null;
  return (
    <ul
      style={{ position: 'absolute', left: 8, bottom: 8, margin: 0, padding: '4px 8px', listStyle: 'none', font: '12px system-ui, sans-serif', color: '#fff', background: 'rgba(0,0,0,0.5)', borderRadius: 6, pointerEvents: 'none' }}
      aria-label="world notes"
    >
      {notes.map((n, i) => (
        <li key={i}>{n}</li>
      ))}
    </ul>
  );
}

/** The viewer settings' fps readout (`perfOverlay`): this view's frame rate, the timed frame and the controller's levers. */
function PerfOverlay({ engine }: { engine: { current: WorldViewerEngine | null } }) {
  const on = useSyncExternalStore(subscribeViewerQuality, () => getViewerQuality().perfOverlay, () => false);
  const [text, setText] = useState('');
  useEffect(() => {
    if (!on) return;
    const tick = () => {
      const e = engine.current;
      const s = e?.stats();
      if (!e || !s) return;
      const m = s.memory;
      setText(
        `${s.fps.toFixed(0)} fps · ${m.frameMs.toFixed(1)} ms · ${Math.round(s.resolutionScale * 100)}% res · ${Math.round(m.drawFactor * 100)}% dist · ${e.runtime.entities.instances} ent · ${m.quality}`,
      );
    };
    tick();
    const id = setInterval(tick, 500);
    return () => clearInterval(id);
  }, [on, engine]);
  if (!on || !text) return null;
  return (
    <div
      data-testid="viewer-perf"
      style={{ position: 'absolute', right: 8, top: 8, padding: '3px 8px', font: '11px ui-monospace, monospace', color: '#fff', background: 'rgba(0,0,0,0.55)', borderRadius: 6, pointerEvents: 'none', whiteSpace: 'nowrap', zIndex: 5 }}
    >
      {text}
    </div>
  );
}
