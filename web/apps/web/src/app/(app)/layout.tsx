'use client';

import type { ReactNode } from 'react';
import { SessionExpired } from '@/components/shell/SessionExpired';
import { Shell } from '@/components/shell/Shell';
import { ErrorState, Skeleton } from '@/components/ui/primitives';
import { AuthGate } from '@/lib/auth';
import { LiveProvider } from '@/lib/live/LiveProvider';

function Booting() {
  return (
    <div className="flex h-dvh" aria-busy="true" aria-label="Loading">
      <div className="hidden w-60 border-r border-border bg-surface p-4 md:block">
        <Skeleton className="mb-6 h-7 w-36" />
        {Array.from({ length: 9 }, (_, i) => (
          <Skeleton key={i} className="mb-2 h-8 w-full" />
        ))}
      </div>
      <div className="flex-1">
        <div className="h-14 border-b border-border bg-surface" />
        <div className="grid grid-cols-1 gap-4 p-6 sm:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-56" />
          ))}
        </div>
      </div>
    </div>
  );
}

export default function AppLayout({ children }: { children: ReactNode }) {
  return (
    <AuthGate
      fallback={(error, retry) =>
        error ? (
          <div className="grid h-dvh place-items-center">
            <ErrorState error={error} onRetry={retry} title="The explorer server is not answering" />
          </div>
        ) : (
          <Booting />
        )
      }
      expired={(href) => <SessionExpired href={href} />}
    >
      <LiveProvider>
        <Shell>{children}</Shell>
      </LiveProvider>
    </AuthGate>
  );
}
