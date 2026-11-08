import { createContext, useContext, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { WorldRuntime, type RuntimeOptions } from '../render/runtime';
import { registerThumbnail, type ThumbnailHandle, type ThumbnailViewOptions } from '../render/thumbnails';
import type { WorldSource } from '../types';

/** undefined = no provider above; null = the provider is mounting. */
const RuntimeContext = createContext<WorldRuntime | null | undefined>(undefined);

export interface WorldViewerProviderProps {
  source: WorldSource;
  /** Read once when the runtime is created (poll periods, worker factory, render slots per frame, quality…). */
  options?: RuntimeOptions;
  /**
   * Keep the runtime alive across unmounts under this key (default "default"; `false` = dispose with the provider). Every
   * provider with the same key attaches to the same module-level runtime, so navigating between pages (hive grid → bot
   * view) keeps the world store, the section meshes and the WebGL context. A new `source` object for the same key is
   * swapped in without dropping the world. Call `disposeWorldRuntimes()` on logout.
   */
  persist?: string | false;
  children?: ReactNode;
}

const persistent = new Map<string, WorldRuntime>();

/** Disposes every persistent runtime (logout, or a hard reset); the next provider starts a fresh one. */
export function disposeWorldRuntimes(): void {
  for (const rt of persistent.values()) rt.release();
  persistent.clear();
}

function acquire(key: string | false, source: WorldSource, options: RuntimeOptions | undefined): WorldRuntime {
  if (key === false) return new WorldRuntime(source, options).retain();
  let rt = persistent.get(key);
  if (!rt || rt.isDisposed) {
    rt = new WorldRuntime(source, options).retain(); // the map's own reference
    persistent.set(key, rt);
  } else rt.replaceSource(source);
  return rt.retain();
}

/**
 * Owns the ONE WebGL context, mesh worker, world store and section meshes that every `<WorldThumbnail>` and `<WorldViewer>`
 * below it draws from. Put it around the page (e.g. the hive grid plus the bot view): each extra view costs a camera and a
 * viewport, never another copy of the world or another context.
 */
export function WorldViewerProvider({ source, options, persist = 'default', children }: WorldViewerProviderProps) {
  const [rt, setRt] = useState<WorldRuntime | null>(null);
  useEffect(() => {
    const r = acquire(persist, source, options);
    setRt(r);
    return () => {
      setRt(null);
      r.release(); // a persistent runtime keeps the map's reference and only pauses its network loops without views
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [persist]);
  useEffect(() => rt?.replaceSource(source), [rt, source]);
  return <RuntimeContext.Provider value={rt}>{children}</RuntimeContext.Provider>;
}

/** The shared runtime: undefined outside a provider, null while it mounts. */
export function useWorldRuntime(): WorldRuntime | null | undefined {
  return useContext(RuntimeContext);
}

export interface WorldThumbnailProps extends ThumbnailViewOptions {
  /** Bot name or short key ("main", "67"). */
  follow: string;
  className?: string;
  style?: CSSProperties;
  onClick?: () => void;
  title?: string;
}

/** A small live third-person view of one bot, rendered by the surrounding `WorldViewerProvider` at a few frames a second. */
export function WorldThumbnail({ follow, className, style, onClick, title, angle, distance, pitch, fps, drawDistance, xray, bubbles }: WorldThumbnailProps) {
  const rt = useWorldRuntime();
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const handle = useRef<ThumbnailHandle | null>(null);
  useLayoutEffect(() => {
    if (!rt || !canvas.current) return;
    const h = registerThumbnail(rt, canvas.current, follow, { angle, distance, pitch, fps, drawDistance, xray, bubbles });
    handle.current = h;
    return () => {
      handle.current = null;
      h.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rt]);
  useEffect(() => handle.current?.setFollow(follow), [follow]);
  useEffect(() => handle.current?.setOptions({ angle, distance, pitch, fps, drawDistance, xray, bubbles }), [angle, distance, pitch, fps, drawDistance, xray, bubbles]);
  return (
    <canvas
      ref={canvas}
      className={className}
      title={title ?? follow}
      onClick={onClick}
      style={{ display: 'block', width: '100%', height: '100%', background: '#1b2232', cursor: onClick ? 'pointer' : undefined, ...style }}
    />
  );
}
