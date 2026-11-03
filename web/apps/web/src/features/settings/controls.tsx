'use client';

/** Small building blocks of the viewer settings panel: a labelled row with a one-line hint, a slider, a switch row. */
import { useId, type ReactNode } from 'react';
import { Switch } from '@/components/ui/overlays';
import { cn } from '@/lib/utils';

export function SettingRow({
  label,
  hint,
  value,
  htmlFor,
  children,
  className,
}: {
  label: ReactNode;
  hint: ReactNode;
  value?: ReactNode;
  htmlFor?: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('space-y-1.5 py-3', className)}>
      <div className="flex items-baseline justify-between gap-3">
        <label htmlFor={htmlFor} className="text-sm font-medium text-fg">
          {label}
        </label>
        {value !== undefined ? <span className="tabular text-xs text-muted">{value}</span> : null}
      </div>
      {children}
      <p className="text-xs text-subtle">{hint}</p>
    </div>
  );
}

export function Slider({
  label,
  hint,
  value,
  min,
  max,
  step,
  format,
  onChange,
  disabled,
}: {
  label: string;
  hint: ReactNode;
  value: number;
  min: number;
  max: number;
  step: number;
  format: (v: number) => string;
  onChange: (v: number) => void;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <SettingRow label={label} hint={hint} value={format(value)} htmlFor={id}>
      <input
        id={id}
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        aria-valuetext={format(value)}
        onChange={(e) => onChange(Number(e.target.value))}
        className="w-full accent-[var(--accent)] disabled:opacity-50"
      />
    </SettingRow>
  );
}

export function SwitchRow({ label, hint, checked, onChange }: { label: string; hint: ReactNode; checked: boolean; onChange: (v: boolean) => void }) {
  const id = useId();
  return (
    <div className="flex items-start justify-between gap-4 py-3">
      <div className="min-w-0">
        <label htmlFor={id} className="text-sm font-medium text-fg">
          {label}
        </label>
        <p className="mt-0.5 text-xs text-subtle">{hint}</p>
      </div>
      <Switch id={id} checked={checked} onCheckedChange={onChange} />
    </div>
  );
}

export function Group({ title, children, className }: { title: string; children: ReactNode; className?: string }) {
  return (
    <section className={cn('rounded-xl border border-border bg-surface px-4 py-1 shadow-card', className)} aria-label={title}>
      <h3 className="pt-3 text-[11px] font-semibold tracking-wider text-subtle uppercase">{title}</h3>
      <div className="divide-y divide-border">{children}</div>
    </section>
  );
}

export const fmtBlocks = (v: number) => `${v} blocks`;
export const fmtScale = (v: number) => `${Math.round(v * 100)}%`;
