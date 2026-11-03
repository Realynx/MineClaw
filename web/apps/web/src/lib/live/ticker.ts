'use client';

import { useEffect, useState } from 'react';

/** Re-renders every `ms` and returns the current time (for elapsed timers). Pauses while the tab is hidden. */
export function useTicker(ms = 1000, enabled = true): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    const t = setInterval(() => {
      if (document.visibilityState === 'visible') setNow(Date.now());
    }, ms);
    return () => clearInterval(t);
  }, [ms, enabled]);
  return now;
}
