'use client';

/**
 * The world in the browser (@mineclaw/world-viewer), behind one lazy door: `loadWorldViewer()` dynamic-imports
 * ./viewer-module.ts (three.js + the mesh worker). Until it arrives, and if it ever fails to load, views show the 2D dot map
 * placeholder, so pages never break or shift layout.
 *
 * `<WorldHost>` wraps an area in the viewer's `WorldViewerProvider`: ONE WebGL context, one world store and one set of section
 * meshes for every thumbnail and pane inside it. The provider attaches to the package's persistent runtime ("hive"), so
 * moving between pages (hive grid → bot view) keeps the world, the meshes and the context; `disposeWorld()` (sign-out)
 * drops it.
 */
import { useQueryClient } from '@tanstack/react-query';
import type { WorldSource } from '@mineclaw/world-viewer';
import type { Me } from '@mineclaw/contracts';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { qk } from '@/lib/api/query';
import { api } from '@/lib/api/client';
import { DotMap } from './DotMap';
import type * as ViewerModule from './viewer-module';
import { createWorldSource } from './WorldSource';

export type WorldViewerModule = typeof ViewerModule;

const MOCK = process.env.NEXT_PUBLIC_MOCK === '1';

let loading: Promise<WorldViewerModule | null> | null = null;

/** The only place that loads the viewer. Resolves to null when it cannot be loaded (the placeholder stays). */
export function loadWorldViewer(): Promise<WorldViewerModule | null> {
  loading ??= import('./viewer-module').then((mod) => {
    // Mock mode too: its in-page gateway serves the Skin Browser's pawn skins (mocks/skins.ts).
    mod.configureHumanSkins((query) => api('world.humanSkins', { query }));
    return mod;
  }).catch((e: unknown) => {
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
      // Chat bubbles show whispers only when the signed-in user's player sent them (read from the auth query at the time of
      // the line; lib/auth imports this module, so it is not imported back).
      const getPlayer = () => qc.getQueryData<Me>(qk('auth.me'))?.player ?? null;
      if (mod) source = MOCK && process.env.NEXT_PUBLIC_MOCK_REAL_WORLD !== '1' ? await mod.loadMockSource(getPlayer) : createWorldSource(qc, { player: getPlayer });
      if (live) setState({ mod, source, ready: true });
    })();
    return () => {
      live = false;
    };
  }, [qc]);
  const value = useMemo(() => state, [state]);
  const inner = <Ctx.Provider value={value}>{children}</Ctx.Provider>;
  const { mod, source } = state;
  return mod && source ? (
    <mod.WorldViewerProvider source={source} persist="hive">
      {inner}
    </mod.WorldViewerProvider>
  ) : (
    inner
  );
}

/** Drops the persistent world runtime (WebGL context, worker, store); call on sign-out. No-op if the viewer never loaded. */
export function disposeWorld(): void {
  void loading?.then((mod) => mod?.disposeWorldRuntimes());
}

/** A bot card's live 3D thumbnail (shared renderer, a few fps, paused offscreen), or the dot map until it loads. */
export function BotThumbnail({ bot, className, fps }: { bot: string; className?: string; fps?: number }) {
  const { mod, source } = useWorldHost();
  if (mod && source) return <mod.WorldThumbnail follow={bot} fps={fps} className={className} title={`${bot} in the world`} />;
  return <DotMap follow={bot} radius={20} className={className} label={false} />;
}
