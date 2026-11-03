'use client';

/**
 * The world in the browser (@mineclaw/world-viewer), behind one lazy door: `loadWorldViewer()` dynamic-imports
 * ./viewer-module.ts (three.js + the mesh worker). Until it arrives, and if it ever fails to load, views show the 2D dot map
 * placeholder, so pages never break or shift layout.
 *
 * `<WorldHost>` wraps an area in the viewer's `WorldViewerProvider`: ONE WebGL context, one world store and one set of section
 * meshes for every thumbnail and pane inside it.
 */
import { useQueryClient } from '@tanstack/react-query';
import type { WorldSource } from '@mineclaw/world-viewer';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { DotMap } from './DotMap';
import type * as ViewerModule from './viewer-module';
import { createWorldSource } from './WorldSource';

export type WorldViewerModule = typeof ViewerModule;

const MOCK = process.env.NEXT_PUBLIC_MOCK === '1';

let loading: Promise<WorldViewerModule | null> | null = null;

/** The only place that loads the viewer. Resolves to null when it cannot be loaded (the placeholder stays). */
export function loadWorldViewer(): Promise<WorldViewerModule | null> {
  loading ??= import('./viewer-module').catch((e: unknown) => {
    console.warn('[world] viewer failed to load; using the 2D placeholder', e);
    loading = null;
    return null;
  });
  return loading;
}

interface WorldCtx {
  mod: WorldViewerModule | null;
  source: WorldSource | null;
  ready: boolean;
}

const Ctx = createContext<WorldCtx | null>(null);

export function useWorldHost(): WorldCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error('world views need <WorldHost>');
  return c;
}

export function WorldHost({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [state, setState] = useState<{ mod: WorldViewerModule | null; source: WorldSource | null; ready: boolean }>({
    mod: null,
    source: null,
    ready: false,
  });
  useEffect(() => {
    let live = true;
    void (async () => {
      const mod = await loadWorldViewer();
      let source: WorldSource | null = null;
      if (mod) source = MOCK ? await mod.loadMockSource() : createWorldSource(qc);
      if (live) setState({ mod, source, ready: true });
    })();
    return () => {
      live = false;
    };
  }, [qc]);
  const value = useMemo(() => state, [state]);
  const inner = <Ctx.Provider value={value}>{children}</Ctx.Provider>;
  const { mod, source } = state;
  return mod && source ? <mod.WorldViewerProvider source={source}>{inner}</mod.WorldViewerProvider> : inner;
}

/** A bot card's live 3D thumbnail (shared renderer, a few fps, paused offscreen), or the dot map until it loads. */
export function BotThumbnail({ bot, className }: { bot: string; className?: string }) {
  const { mod, source } = useWorldHost();
  if (mod && source) return <mod.WorldThumbnail follow={bot} fps={3} className={className} title={`${bot} in the world`} />;
  return <DotMap follow={bot} radius={20} className={className} label={false} />;
}
