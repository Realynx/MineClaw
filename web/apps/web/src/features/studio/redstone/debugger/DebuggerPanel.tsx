'use client';

/**
 * The debugger's three tabs in the redstone panel: Simulate (the run controls), Probes (the watch list, the timeline and
 * the breakpoints) and Inspector (the picked cell). It follows the controller's snapshot once and hands it down.
 */
import type { Pos } from '@mineclaw/redstone';
import type { ReactNode } from 'react';
import type { SimControllerApi } from '../simController.types';
import { useSimSnapshot } from '../useSimSnapshot';
import { Breakpoints } from './Breakpoints';
import { Inspector } from './Inspector';
import { SimControls } from './SimControls';
import { Timeline } from './Timeline';
import { WatchList } from './WatchList';

export interface DebuggerPanelProps {
  api: SimControllerApi;
  /** The debugger tab that is open: the run controls, the probes with their timeline, or the cell inspector. */
  tab: 'simulate' | 'probes' | 'inspector';
  /** The cell under the pointer now, read when an action needs it. */
  aimed: () => Pos | null;
  /** The cell the inspector shows. */
  selected: Pos | null;
  /** Asks the page to bring a cell into view (a breakpoint hit, a probe). */
  onJump: (at: Pos) => void;
}

export function DebuggerPanel({ api, tab, selected, onJump }: DebuggerPanelProps): ReactNode {
  const snap = useSimSnapshot(api);
  if (tab === 'simulate') return <SimControls api={api} snap={snap} onJump={onJump} />;
  if (tab === 'inspector') return <Inspector api={api} snap={snap} selected={selected} onJump={onJump} />;
  return (
    <div className="divide-y divide-border">
      <WatchList api={api} snap={snap} selected={selected} onJump={onJump} />
      <Timeline api={api} snap={snap} />
      <Breakpoints api={api} snap={snap} selected={selected} onJump={onJump} />
    </div>
  );
}
