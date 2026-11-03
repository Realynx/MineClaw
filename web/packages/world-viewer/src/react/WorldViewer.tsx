import { forwardRef, useEffect, useImperativeHandle, useRef, useState, type CSSProperties } from 'react';
import { WorldViewerEngine, type EngineOptions } from '../render/engine';
import type { CameraMode, Dim, LayerToggles, PickedBlock, ViewerStats, WorldSource, XRayMode } from '../types';
import type { BotView } from '../world/entities';
import { useWorldRuntime } from './WorldThumbnail';

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
  /** Blocks, 32..256 (default 128). */
  drawDistance?: number;
  onSelectBot?: (name: string) => void;
  onPick?: (block: PickedBlock) => void;
  onStats?: (s: ViewerStats) => void;
  onCameraModeChange?: (m: CameraMode) => void;
  onError?: (route: string, e: unknown) => void;
  /** Runtime tuning when the viewer has no WorldViewerProvider above (poll periods, LRU size, worker factory, pixel ratio).
   * Read once at mount. Inside a provider the shared runtime's options apply. */
  options?: Omit<EngineOptions, 'source' | 'runtime' | 'dim' | 'follow' | 'layers' | 'cameraMode' | 'xray' | 'drawDistance' | 'onSelectBot' | 'onPick' | 'onStats' | 'onNotes' | 'onCameraModeChange' | 'onError'>;
  className?: string;
  style?: CSSProperties;
}

export interface WorldViewerHandle {
  /** Centre the camera on a block (stops following until `follow` changes or `followBot` is called). */
  focus(x: number, y: number, z: number): void;
  setCameraMode(mode: CameraMode): void;
  getCameraMode(): CameraMode;
  followBot(name: string | null): void;
  bots(): BotView[];
  stats(): ViewerStats | null;
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
      onCameraModeChange: (m) => cbs.current.onCameraModeChange?.(m),
      onError: (r, err) => cbs.current.onError?.(r, err),
    });
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
    if (props.cameraMode && engine.current && engine.current.getCameraMode() !== props.cameraMode) engine.current.setCameraMode(props.cameraMode);
  }, [props.cameraMode]);
  useEffect(() => engine.current?.setXRay(props.xray ?? 'auto'), [props.xray]);
  useEffect(() => engine.current?.setDrawDistance(props.drawDistance ?? 128), [props.drawDistance]);

  useImperativeHandle(
    ref,
    () => ({
      focus: (x, y, z) => engine.current?.focus(x, y, z),
      setCameraMode: (m) => engine.current?.setCameraMode(m),
      getCameraMode: () => engine.current?.getCameraMode() ?? 'follow',
      followBot: (n) => engine.current?.setFollow(n),
      bots: () => engine.current?.bots() ?? [],
      stats: () => engine.current?.stats() ?? null,
      engine: () => engine.current,
    }),
    [],
  );

  const showNotes = (props.layers?.notes ?? true) && notes.length > 0;
  return (
    <div className={props.className} style={{ position: 'relative', overflow: 'hidden', width: '100%', height: '100%', ...props.style }}>
      <div ref={host} style={{ position: 'absolute', inset: 0 }} />
      {showNotes && (
        <ul
          style={{ position: 'absolute', left: 8, bottom: 8, margin: 0, padding: '4px 8px', listStyle: 'none', font: '12px system-ui, sans-serif', color: '#fff', background: 'rgba(0,0,0,0.5)', borderRadius: 6, pointerEvents: 'none' }}
          aria-label="world notes"
        >
          {notes.map((n, i) => (
            <li key={i}>{n}</li>
          ))}
        </ul>
      )}
    </div>
  );
});
