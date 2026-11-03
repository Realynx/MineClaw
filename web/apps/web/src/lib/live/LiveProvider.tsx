'use client';

import { notifyManager, useQuery, useQueryClient } from '@tanstack/react-query';
import type { LiveStatus } from '@mineclaw/contracts';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { publishReconnect } from '@/features/world/blocks';
import { applyLiveEvent, liveKeys, resyncLive } from './dispatch';
import { connectionView, type ConnectionView } from './connection';
import { LiveStream, type LiveEventName, type StreamState } from './stream';

interface LiveContextValue {
  stream: StreamState;
  changedAt: number;
}

const LiveContext = createContext<LiveContextValue>({ stream: 'idle', changedAt: 0 });

/**
 * Coalesces stream frames and applies them once per animation frame (inside one query-cache batch), so a burst of SSE
 * events causes one render pass, not one per frame. While the tab is hidden (no rAF) it flushes on a short timer.
 */
export function createFrameBatcher(apply: (batch: Array<[LiveEventName, unknown]>) => void) {
  let queue: Array<[LiveEventName, unknown]> = [];
  let scheduled = false;
  const flush = () => {
    scheduled = false;
    const batch = queue;
    queue = [];
    if (batch.length) apply(batch);
  };
  return {
    push(event: LiveEventName, data: unknown) {
      queue.push([event, data]);
      if (scheduled) return;
      scheduled = true;
      if (typeof document !== 'undefined' && document.visibilityState === 'visible' && typeof requestAnimationFrame === 'function') {
        requestAnimationFrame(flush);
      } else {
        setTimeout(flush, 100);
      }
    },
    flush,
  };
}

/** Opens the tab's single SSE connection for the signed-in shell and feeds the query cache. */
export function LiveProvider({ children, enabled = true }: { children: ReactNode; enabled?: boolean }) {
  const qc = useQueryClient();
  const [stream, setStream] = useState<StreamState>('idle');
  const [changedAt, setChangedAt] = useState(() => Date.now());

  useEffect(() => {
    if (!enabled) return;
    const batcher = createFrameBatcher((batch) =>
      notifyManager.batch(() => {
        for (const [event, data] of batch) applyLiveEvent(qc, event, data);
      }),
    );
    const live = new LiveStream({
      onMessage: (event, data) => batcher.push(event, data),
      onState: (s) => {
        setStream(s);
        setChangedAt(Date.now());
      },
      onResync: () => {
        batcher.flush();
        publishReconnect();
        void resyncLive(qc);
      },
    });
    live.start();
    const onVisible = () => {
      // A long-hidden tab may have been throttled: catch up on return.
      if (document.visibilityState === 'visible' && live.current !== 'open') void resyncLive(qc);
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      live.stop();
    };
  }, [qc, enabled]);

  const value = useMemo(() => ({ stream, changedAt }), [stream, changedAt]);
  return <LiveContext.Provider value={value}>{children}</LiveContext.Provider>;
}

/** The header indicator's state (re-evaluates every second so the grace period can expire). */
export function useConnection(): ConnectionView & { status: LiveStatus | undefined } {
  const { stream, changedAt } = useContext(LiveContext);
  const { data: status } = useQuery<LiveStatus>({
    queryKey: liveKeys.connection,
    queryFn: () => Promise.resolve(undefined as unknown as LiveStatus),
    enabled: false,
    staleTime: Infinity,
  });
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  return { ...connectionView(stream, status, now - changedAt), status };
}
