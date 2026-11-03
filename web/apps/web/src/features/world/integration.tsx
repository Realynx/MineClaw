'use client';

/**
 * Wraps the optional @mineclaw/world-viewer behind one lazy door (./viewer-module.ts). When the package is absent every view
 * falls back to a lightweight placeholder (the 2D dot map), so pages never break or shift layout.
 */
import { useQueryClient } from '@tanstack/react-query';
import { Box } from 'lucide-react';
import { createContext, useContext, useEffect, useMemo, useState, type ComponentType, type ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { DotMap } from './DotMap';
import { createWorldSource, type WorldSource } from './WorldSource';

export interface WorldViewerApi {
  WorldViewerProvider: ComponentType<{ source: WorldSource; children?: ReactNode }>;
  WorldThumbnail: ComponentType<{ source: WorldSource; follow: string; className?: string }>;
  WorldViewer: ComponentType<{ source: WorldSource; follow?: string; dim?: number; camera?: 'follow' | 'free'; className?: string }>;
}

let loading: Promise<WorldViewerApi | null> | null = null;

/** The only place that imports the viewer module. Resolves to null when the package is not wired in. */
export function loadWorldViewer(): Promise<WorldViewerApi | null> {
  loading ??= import('./viewer-module')
    .then((m: Record<string, unknown>) => {
      if (!m.available) return null;
      const Provider = (m.WorldViewerProvider ?? m.Provider) as WorldViewerApi['WorldViewerProvider'] | undefined;
      const Thumb = (m.WorldThumbnail ?? m.Thumbnail) as WorldViewerApi['WorldThumbnail'] | undefined;
      const Viewer = (m.WorldViewer ?? m.WorldView ?? m.default) as WorldViewerApi['WorldViewer'] | undefined;
      if (!Provider || !Thumb || !Viewer) {
        console.warn('[world] @mineclaw/world-viewer is missing expected exports; using the 2D placeholder');
        return null;
      }
      return { WorldViewerProvider: Provider, WorldThumbnail: Thumb, WorldViewer: Viewer };
    })
    .catch((e: unknown) => {
      console.warn('[world] viewer failed to load; using the 2D placeholder', e);
      return null;
    });
  return loading;
}

interface WorldCtx {
  viewer: WorldViewerApi | null;
  ready: boolean;
  source: WorldSource;
}

const Ctx = createContext<WorldCtx | null>(null);

/** Hosts the shared renderer (one WebGL context for every thumbnail and pane under it). */
export function WorldHost({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const source = useMemo(() => createWorldSource(qc), [qc]);
  const [viewer, setViewer] = useState<WorldViewerApi | null>(null);
  const [ready, setReady] = useState(false);
  useEffect(() => {
    let live = true;
    void loadWorldViewer().then((v) => {
      if (!live) return;
      setViewer(v);
      setReady(true);
    });
    return () => {
      live = false;
    };
  }, []);
  const value = useMemo(() => ({ viewer, ready, source }), [viewer, ready, source]);
  const inner = <Ctx.Provider value={value}>{children}</Ctx.Provider>;
  return viewer ? <viewer.WorldViewerProvider source={source}>{inner}</viewer.WorldViewerProvider> : inner;
}

function useWorld(): WorldCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error('world views need <WorldHost>');
  return c;
}

/** A bot card's live thumbnail: the shared-renderer 3D thumbnail, or the dot map. */
export function BotThumbnail({ bot, className }: { bot: string; className?: string }) {
  const { viewer, source } = useWorld();
  if (viewer) return <viewer.WorldThumbnail source={source} follow={bot} className={className} />;
  return <DotMap follow={bot} radius={20} className={className} label={false} />;
}

/** The world tab / split pane: follow camera on a bot (or free camera). */
export function WorldPane({ follow, dim, className }: { follow?: string; dim?: number; className?: string }) {
  const { viewer, ready, source } = useWorld();
  if (viewer) return <viewer.WorldViewer source={source} follow={follow} dim={dim} camera={follow ? 'follow' : 'free'} className={cn('size-full', className)} />;
  return (
    <div className={cn('relative size-full', className)}>
      {follow ? <DotMap follow={follow} radius={40} className="absolute inset-0" /> : <div className="absolute inset-0 bg-canvas" />}
      {ready ? (
        <div className="absolute top-3 left-3 flex max-w-xs items-start gap-2 rounded-lg border border-border bg-surface/90 px-3 py-2 text-xs text-muted shadow-card backdrop-blur">
          <Box className="mt-0.5 size-3.5 shrink-0 text-subtle" />
          <span>The 3D world viewer is not installed in this build. Showing a live top-down map of nearby bots and entities.</span>
        </div>
      ) : null}
    </div>
  );
}
