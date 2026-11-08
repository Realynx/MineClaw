'use client';

import { QueryClientProvider } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { TooltipProvider } from '@/components/ui/overlays';
import { makeQueryClient } from '@/lib/api/query';
import { applyTheme, usePrefs } from '@/lib/prefs';
import { PwaProvider } from '@/features/pwa/PwaProvider';

const MOCK = process.env.NEXT_PUBLIC_MOCK === '1';

/** Starts the MSW worker before the first request in mock mode (NEXT_PUBLIC_MOCK=1); a no-op otherwise. */
function MockGate({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(!MOCK);
  useEffect(() => {
    if (!MOCK) return;
    let cancelled = false;
    void import('@/mocks/browser')
      .then((m) => m.startMocks())
      .then(() => {
        if (!cancelled) setReady(true);
      });
    return () => {
      cancelled = true;
    };
  }, []);
  return ready ? <>{children}</> : null;
}

function ThemeSync() {
  const { theme } = usePrefs();
  useEffect(() => {
    applyTheme(theme);
    if (theme !== 'system') return;
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const on = () => applyTheme('system');
    mq.addEventListener('change', on);
    return () => mq.removeEventListener('change', on);
  }, [theme]);
  return null;
}

export function Providers({ children }: { children: ReactNode }) {
  const [qc] = useState(makeQueryClient);
  return (
    <QueryClientProvider client={qc}>
      <TooltipProvider>
        <ThemeSync />
        <PwaProvider>
          <MockGate>{children}</MockGate>
        </PwaProvider>
      </TooltipProvider>
    </QueryClientProvider>
  );
}
