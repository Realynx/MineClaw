'use client';

import { useCallback, useEffect, useState } from 'react';

/**
 * Full screen for one element (the Live world pane's pattern: the element and everything over it go full screen, so the map
 * and the whole HUD stay; Esc is the browser's way out and the state follows `fullscreenchange`). `attach` is the element's callback ref.
 */
export function useFullscreen<T extends HTMLElement = HTMLDivElement>() {
  const [el, setEl] = useState<T | null>(null);
  const [on, setOn] = useState(false);
  useEffect(() => {
    if (typeof document === 'undefined') return;
    const sync = () => setOn(!!el && document.fullscreenElement === el);
    sync();
    document.addEventListener('fullscreenchange', sync);
    return () => document.removeEventListener('fullscreenchange', sync);
  }, [el]);
  const toggle = useCallback(() => {
    if (!el) return;
    if (document.fullscreenElement === el) void document.exitFullscreen?.()?.catch(() => undefined);
    else void el.requestFullscreen?.({ navigationUI: 'hide' })?.catch(() => undefined);
  }, [el]);
  return { attach: setEl, el, on, toggle };
}
