'use client';

import { useEffect, useState, type RefObject } from 'react';
import { Crosshair } from '@/features/world/InWorld';

/** Only this Story map's locked free camera owns the center crosshair. */
export function StoryFlyCrosshair({ flying, host }: { flying: boolean; host: RefObject<HTMLDivElement | null> }) {
  const [locked, setLocked] = useState(false);
  useEffect(() => {
    const changed = () =>
      setLocked(!!document.pointerLockElement && !!host.current?.contains(document.pointerLockElement));
    changed();
    document.addEventListener('pointerlockchange', changed);
    return () => document.removeEventListener('pointerlockchange', changed);
  }, [host]);
  return flying && locked ? <Crosshair /> : null;
}
