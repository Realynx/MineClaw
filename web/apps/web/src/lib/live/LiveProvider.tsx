'use client';

import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { LiveStatus } from '@mineclaw/contracts';
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { applyLiveEvent, liveKeys, resyncLive } from './dispatch';
import { connectionView, type ConnectionView } from './connection';
import { LiveStream, type StreamState } from './stream';

interface LiveContextValue {
  stream: StreamState;
  changedAt: number;
}

const LiveContext = createContext<LiveContextValue>({ stream: 'idle', changedAt: 0 });

/** Opens the tab's single SSE connection for the signed-in shell and feeds the query cache. */
export function LiveProvider({ children, enabled = true }: { children: ReactNode; enabled?: boolean }) {
  const qc = useQueryClient();
  const [stream, setStream] = useState<StreamState>('idle');
  const [changedAt, setChangedAt] = useState(() => Date.now());
  const ref = useRef<LiveStream | null>(null);

  useEffect(() => {
    if (!enabled) return;
    const live = new LiveStream({
      onMessage: (event, data) => applyLiveEvent(qc, event, data),
      onState: (s) => {
        setStream(s);
        setChangedAt(Date.now());
      },
      onResync: () => void resyncLive(qc),
    });
    ref.current = live;
    live.start();
    const onVisible = () => {
      // A long-hidden tab may have been throttled: catch up on return.
      if (document.visibilityState === 'visible' && live.current !== 'open') void resyncLive(qc);
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      live.stop();
      ref.current = null;
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
