import { createContext, useContext, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { WorldRuntime, type RuntimeOptions } from '../render/runtime';
import { registerThumbnail, type ThumbnailHandle, type ThumbnailViewOptions } from '../render/thumbnails';
import type { WorldSource } from '../types';

/** undefined = no provider above; null = the provider is mounting. */
const RuntimeContext = createContext<WorldRuntime | null | undefined>(undefined);

export interface WorldViewerProviderProps {
  source: WorldSource;
  /** Read once at mount (poll periods, worker factory, render slots per frame, thumbnail size caps…). */
  options?: RuntimeOptions;
  children?: ReactNode;
}

/**
 * Owns the ONE WebGL context, mesh worker, world store and section meshes that every `<WorldThumbnail>` and `<WorldViewer>`
 * below it draws from. Put it around the page (e.g. the hive grid plus the bot view): each extra view costs a camera and a
 * viewport, never another copy of the world or another context.
 */
export function WorldViewerProvider({ source, options, children }: WorldViewerProviderProps) {
  const [rt, setRt] = useState<WorldRuntime | null>(null);
  useEffect(() => {
    const r = new WorldRuntime(source, options).retain();
    setRt(r);
    return () => {
      setRt(null);
      r.release();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => rt?.setSource(source), [rt, source]);
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
export function WorldThumbnail({ follow, className, style, onClick, title, angle, distance, pitch, fps, drawDistance, xray }: WorldThumbnailProps) {
  const rt = useWorldRuntime();
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const handle = useRef<ThumbnailHandle | null>(null);
  useEffect(() => {
    if (!rt || !canvas.current) return;
    const h = registerThumbnail(rt, canvas.current, follow, { angle, distance, pitch, fps, drawDistance, xray });
    handle.current = h;
    return () => {
      handle.current = null;
      h.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rt]);
  useEffect(() => handle.current?.setFollow(follow), [follow]);
  useEffect(() => handle.current?.setOptions({ angle, distance, pitch, fps, drawDistance, xray }), [angle, distance, pitch, fps, drawDistance, xray]);
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
