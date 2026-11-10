'use client';

import { useCallback, useSyncExternalStore } from 'react';
import type { SimControllerApi, SimSnapshot } from './simController.types';

/** The controller's snapshot, re-rendering when it changes. The API is called through its object, never unbound. */
export function useSimSnapshot(api: SimControllerApi): SimSnapshot {
  const subscribe = useCallback((fn: () => void) => api.subscribe(fn), [api]);
  const snapshot = useCallback(() => api.snapshot(), [api]);
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}
