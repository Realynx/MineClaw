'use client';

/**
 * The Story Mode screen: the colony map full-bleed with the game HUD over it. It owns the selection, the active tool and its
 * options, the working level and x-ray, the view (top-down by default, angled for 3D building), the open bottom tab, the
 * keyboard map, and turns every map gesture into director requests for the colony's pawns and directives for selected AI
 * bodies (one toast says what went where).
 *
 * The pieces live in ./mode: the screen state (state.ts) and its memoised commands (commands.ts), the actions (actions.ts,
 * release.ts for a tool's drag, menu.ts for the right-click, water.ts), the key map (keys.ts), `?design=N` (designParam.ts),
 * and the picture (StoryModeView.tsx, SelectionInspector.tsx, tabPanel.tsx).
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { useEffect } from 'react';
import { PortalContainer } from '@/components/ui/overlays';
import { loadWorldViewer, WorldHost } from '@/features/world/integration';
import { cn } from '@/lib/utils';
import { useStory } from './mode/commands';
import { useDesignParam } from './mode/designParam';
import { useStoryKeys } from './mode/keys';
import { StoryDialogs, StoryHudLayer, StoryMapSlot } from './mode/StoryModeView';
import { colonyKey } from './useStory';
import { setItemIconSource } from './visuals';

/** Item icons from the browser's local game pack, once the viewer module is here. */
function useItemIcons(version: '1.12.2' | 'b1.8.1') {
  useEffect(() => {
    let alive = true, generation = 0;
    const load = () => {
      const current = ++generation;
      void loadWorldViewer().then(async (m) => { if (m) { const source = await m.itemIconResolver(version); if (alive && current === generation) setItemIconSource(source); } }).catch(() => undefined);
    };
    load();
    window.addEventListener('mineclaw-assets-changed', load);
    return () => { alive = false; window.removeEventListener('mineclaw-assets-changed', load); };
  }, [version]);
}

export function StoryMode() {
  const c = useStory();
  useItemIcons(c.state?.version === '1.12.2' ? '1.12.2' : 'b1.8.1');
  useStoryKeys(c);
  useDesignParam(c);
  const { attach, el, on } = c.fullscreen;
  return (
    <div
      ref={attach}
      className={cn('relative h-full min-h-[32rem] overflow-hidden bg-canvas select-none', on && 'h-screen w-screen')}
      data-testid="story-mode"
      data-fullscreen={on || undefined}
    >
      {/* Dialogs and tooltips mount inside this element while it is full screen (else they would be invisible). */}
      <PortalContainer.Provider value={on ? el : null}>
        <WorldHost>
          <StoryMapSlot c={c} />
        </WorldHost>
        <StoryHudLayer c={c} />
        <StoryDialogs c={c} />
      </PortalContainer.Provider>
    </div>
  );
}

export type { StoryColonyState };
export { colonyKey };
