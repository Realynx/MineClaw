import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** The signed-out frame (sign-in, first-run setup): grid backdrop, the MineClaw mark, a title and a card. */
export function AuthFrame({
  title,
  subtitle,
  children,
  wide,
}: {
  title: ReactNode;
  subtitle: ReactNode;
  children: ReactNode;
  wide?: boolean;
}) {
  return (
    <div className="relative grid min-h-dvh place-items-center overflow-hidden bg-bg px-4 py-10">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 opacity-[0.5] dark:opacity-30"
        style={{
          backgroundImage: 'linear-gradient(var(--border) 1px, transparent 1px), linear-gradient(90deg, var(--border) 1px, transparent 1px)',
          backgroundSize: '32px 32px',
          maskImage: 'radial-gradient(ellipse at center, black 20%, transparent 70%)',
        }}
      />
      <div className={cn('relative w-full', wide ? 'max-w-lg' : 'max-w-sm')}>
        <div className="mb-6 flex flex-col items-center text-center">
          <span className="mb-3 grid size-11 place-items-center rounded-xl bg-accent text-accent-fg shadow-card">
            <svg viewBox="0 0 24 24" className="size-6" aria-hidden>
              <path fill="currentColor" d="M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4z" />
              <path fill="currentColor" d="M13 13h7v7h-7z" opacity=".55" />
            </svg>
          </span>
          <h1 className="text-xl font-semibold tracking-tight text-fg">{title}</h1>
          <p className="mt-1 text-sm text-muted">{subtitle}</p>
        </div>
        <div className="rounded-xl border border-border bg-surface p-6 shadow-pop">{children}</div>
      </div>
    </div>
  );
}
