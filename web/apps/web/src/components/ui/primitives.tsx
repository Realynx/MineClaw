'use client';

import { AlertTriangle, Inbox, Loader2, RefreshCw } from 'lucide-react';
import {
  forwardRef,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react';
import { errorText } from '@/lib/api/client';
import { cn } from '@/lib/utils';

// ---------- Button ----------

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'subtle';
type ButtonSize = 'sm' | 'md' | 'icon' | 'icon-sm';

const variantClass: Record<ButtonVariant, string> = {
  primary: 'bg-accent text-accent-fg hover:bg-accent-strong shadow-card',
  secondary: 'bg-surface border border-border text-fg hover:bg-surface-2 hover:border-border-strong shadow-card',
  ghost: 'text-muted hover:text-fg hover:bg-surface-2',
  subtle: 'bg-surface-2 text-fg hover:bg-surface-3',
  danger: 'bg-danger text-white hover:opacity-90 shadow-card',
};
const sizeClass: Record<ButtonSize, string> = {
  sm: 'h-7 px-2.5 text-xs gap-1.5 rounded-md',
  md: 'h-9 px-3.5 text-sm gap-2 rounded-lg',
  icon: 'h-9 w-9 rounded-lg justify-center',
  'icon-sm': 'h-7 w-7 rounded-md justify-center',
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', loading, className, children, disabled, type = 'button', ...rest },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || loading}
      className={cn(
        'inline-flex shrink-0 items-center font-medium whitespace-nowrap transition-colors select-none',
        'disabled:pointer-events-none disabled:opacity-50',
        variantClass[variant],
        sizeClass[size],
        className,
      )}
      {...rest}
    >
      {loading ? <Loader2 className="size-4 animate-spin" aria-hidden /> : null}
      {children}
    </button>
  );
});

// ---------- Card ----------

export function Card({ className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('rounded-card border border-border bg-surface shadow-card', className)} {...rest} />;
}

export function CardHeader({
  title,
  subtitle,
  actions,
  icon,
  className,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  icon?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex min-h-12 items-center gap-3 border-b border-border px-4 py-2.5', className)}>
      {icon ? <span className="text-subtle">{icon}</span> : null}
      <div className="min-w-0 flex-1">
        <h2 className="truncate text-[13px] font-semibold text-fg">{title}</h2>
        {subtitle ? <p className="truncate text-xs text-muted">{subtitle}</p> : null}
      </div>
      {actions ? <div className="flex shrink-0 items-center gap-1.5">{actions}</div> : null}
    </div>
  );
}

// ---------- Badge ----------

export type Tone = 'neutral' | 'accent' | 'ok' | 'warn' | 'danger' | 'info';
const toneClass: Record<Tone, string> = {
  neutral: 'bg-surface-2 text-muted border-border',
  accent: 'bg-accent-soft text-accent border-transparent',
  ok: 'bg-ok-soft text-ok border-transparent',
  warn: 'bg-warn-soft text-warn border-transparent',
  danger: 'bg-danger-soft text-danger border-transparent',
  info: 'bg-info-soft text-info border-transparent',
};

export function Badge({
  tone = 'neutral',
  className,
  children,
  ...rest
}: HTMLAttributes<HTMLSpanElement> & { tone?: Tone }) {
  return (
    <span
      className={cn(
        'inline-flex h-5 shrink-0 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium whitespace-nowrap',
        toneClass[tone],
        className,
      )}
      {...rest}
    >
      {children}
    </span>
  );
}

export function Dot({ tone = 'neutral', pulse }: { tone?: Tone; pulse?: boolean }) {
  const color: Record<Tone, string> = {
    neutral: 'bg-subtle',
    accent: 'bg-accent',
    ok: 'bg-ok',
    warn: 'bg-warn',
    danger: 'bg-danger',
    info: 'bg-info',
  };
  return <span aria-hidden className={cn('inline-block size-2 rounded-full', color[tone], pulse && 'animate-pulse-dot')} />;
}

// ---------- Skeleton / states ----------

export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden className={cn('skeleton rounded-md', className)} />;
}

export function SkeletonLines({ lines = 3, className }: { lines?: number; className?: string }) {
  return (
    <div className={cn('space-y-2', className)} aria-busy="true" aria-label="Loading">
      {Array.from({ length: lines }, (_, i) => (
        <Skeleton key={i} className={cn('h-3.5', i === lines - 1 ? 'w-2/3' : 'w-full')} />
      ))}
    </div>
  );
}

export function EmptyState({
  title,
  hint,
  icon,
  action,
  className,
}: {
  title: ReactNode;
  hint?: ReactNode;
  icon?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-2 px-6 py-10 text-center', className)}>
      <div className="mb-1 rounded-full bg-surface-2 p-3 text-subtle">{icon ?? <Inbox className="size-5" />}</div>
      <p className="text-sm font-medium text-fg">{title}</p>
      {hint ? <p className="max-w-sm text-xs text-muted">{hint}</p> : null}
      {action ? <div className="mt-2">{action}</div> : null}
    </div>
  );
}

export function ErrorState({
  error,
  onRetry,
  title = 'Could not load this',
  className,
}: {
  error: unknown;
  onRetry?: () => void;
  title?: string;
  className?: string;
}) {
  return (
    <div role="alert" className={cn('flex flex-col items-center justify-center gap-2 px-6 py-10 text-center', className)}>
      <div className="mb-1 rounded-full bg-danger-soft p-3 text-danger">
        <AlertTriangle className="size-5" />
      </div>
      <p className="text-sm font-medium text-fg">{title}</p>
      <p className="max-w-sm text-xs text-muted">{errorText(error)}</p>
      {onRetry ? (
        <Button size="sm" onClick={onRetry} className="mt-2">
          <RefreshCw className="size-3.5" /> Try again
        </Button>
      ) : null}
    </div>
  );
}

export function InlineError({ error }: { error: unknown }) {
  if (!error) return null;
  return (
    <p role="alert" className="flex items-start gap-1.5 text-xs text-danger">
      <AlertTriangle className="mt-px size-3.5 shrink-0" /> {errorText(error)}
    </p>
  );
}

export function Spinner({ className }: { className?: string }) {
  return <Loader2 aria-label="Loading" className={cn('size-4 animate-spin text-subtle', className)} />;
}

// ---------- form controls ----------

const fieldBase =
  'rounded-lg border border-border bg-surface px-3 text-sm text-fg placeholder:text-subtle transition-colors ' +
  'hover:border-border-strong focus:border-accent focus:outline-none focus:ring-2 focus:ring-[var(--ring)] ' +
  'disabled:opacity-60';

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(function Input(
  { className, ...rest },
  ref,
) {
  return <input ref={ref} className={cn(fieldBase, 'h-9 w-full', className)} {...rest} />;
});

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(function Textarea(
  { className, ...rest },
  ref,
) {
  return <textarea ref={ref} className={cn(fieldBase, 'min-h-20 w-full py-2 leading-relaxed', className)} {...rest} />;
});

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(function Select(
  { className, children, ...rest },
  ref,
) {
  return (
    <select
      ref={ref}
      className={cn(
        fieldBase,
        'h-9 cursor-pointer appearance-none bg-[length:16px] bg-[right_0.5rem_center] bg-no-repeat pr-8',
        "bg-[url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23888' stroke-width='2'%3E%3Cpath d='m6 9 6 6 6-6'/%3E%3C/svg%3E\")]",
        className,
      )}
      {...rest}
    >
      {children}
    </select>
  );
});

export function Field({
  label,
  hint,
  htmlFor,
  children,
  className,
}: {
  label: ReactNode;
  hint?: ReactNode;
  htmlFor?: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('space-y-1.5', className)}>
      <label htmlFor={htmlFor} className="block text-xs font-medium text-muted">
        {label}
      </label>
      {children}
      {hint ? <p className="text-[11px] text-subtle">{hint}</p> : null}
    </div>
  );
}

export function Checkbox({
  label,
  checked,
  onChange,
  disabled,
}: {
  label: ReactNode;
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <label className={cn('inline-flex cursor-pointer items-center gap-2 text-sm text-fg', disabled && 'opacity-50')}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className="size-4 rounded border-border accent-[var(--accent)]"
      />
      {label}
    </label>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="inline-flex h-5 min-w-5 items-center justify-center rounded border border-border bg-surface-2 px-1 font-mono text-[10px] text-muted">
      {children}
    </kbd>
  );
}

// ---------- meters ----------

export function Meter({
  value,
  max = 1,
  tone = 'accent',
  className,
  label,
}: {
  value: number;
  max?: number;
  tone?: Tone;
  className?: string;
  label?: string;
}) {
  const p = max > 0 && Number.isFinite(value) ? Math.max(0, Math.min(1, value / max)) : 0;
  const color: Record<Tone, string> = {
    neutral: 'bg-subtle',
    accent: 'bg-accent',
    ok: 'bg-ok',
    warn: 'bg-warn',
    danger: 'bg-danger',
    info: 'bg-info',
  };
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuenow={Number.isFinite(value) ? value : 0}
      aria-valuemin={0}
      aria-valuemax={max}
      className={cn('h-1.5 w-full overflow-hidden rounded-full bg-surface-3', className)}
    >
      <div className={cn('h-full rounded-full transition-[width] duration-500', color[tone])} style={{ width: `${p * 100}%` }} />
    </div>
  );
}

export function Stat({ label, value, hint }: { label: ReactNode; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-[11px] font-medium tracking-wide text-subtle uppercase">{label}</div>
      <div className="tabular truncate text-sm font-medium text-fg">{value}</div>
      {hint ? <div className="truncate text-xs text-muted">{hint}</div> : null}
    </div>
  );
}

export function PageHeader({
  title,
  subtitle,
  actions,
  children,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <div className="mb-5 flex flex-wrap items-end gap-x-4 gap-y-3">
      <div className="min-w-0 flex-1">
        <h1 className="text-xl font-semibold tracking-tight text-fg">{title}</h1>
        {subtitle ? <p className="mt-0.5 text-sm text-muted">{subtitle}</p> : null}
      </div>
      {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
      {children}
    </div>
  );
}

/** Two-column key/value list. */
export function KeyValues({ rows, className }: { rows: Array<[ReactNode, ReactNode]>; className?: string }) {
  return (
    <dl className={cn('grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-1.5 text-sm', className)}>
      {rows.map(([k, v], i) => (
        <div key={i} className="contents">
          <dt className="text-muted">{k}</dt>
          <dd className="min-w-0 break-words text-fg">{v ?? <span className="text-subtle">—</span>}</dd>
        </div>
      ))}
    </dl>
  );
}
