'use client';

import { X } from 'lucide-react';
import { Dialog as D, Switch as S, Tabs as T, ToggleGroup, Tooltip as TT } from 'radix-ui';
import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { Button } from './primitives';

// ---------- Dialog ----------

export function Dialog({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
  size = 'md',
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl';
}) {
  const width = { sm: 'max-w-sm', md: 'max-w-lg', lg: 'max-w-2xl', xl: 'max-w-4xl' }[size];
  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className="fixed inset-0 z-50 bg-black/45 backdrop-blur-[2px] data-[state=open]:animate-in" />
        <D.Content
          className={cn(
            'fixed top-1/2 left-1/2 z-50 flex max-h-[min(90vh,52rem)] w-[calc(100vw-2rem)] -translate-x-1/2 -translate-y-1/2 flex-col',
            'rounded-xl border border-border bg-surface shadow-pop focus:outline-none',
            width,
          )}
        >
          <div className="flex items-start gap-3 border-b border-border px-5 py-4">
            <div className="min-w-0 flex-1">
              <D.Title className="text-base font-semibold text-fg">{title}</D.Title>
              {description ? <D.Description className="mt-0.5 text-sm text-muted">{description}</D.Description> : null}
            </div>
            <D.Close asChild>
              <Button variant="ghost" size="icon-sm" aria-label="Close">
                <X className="size-4" />
              </Button>
            </D.Close>
          </div>
          <div className="min-h-0 flex-1 overflow-auto px-5 py-4">{children}</div>
          {footer ? (
            <div className="flex flex-wrap items-center justify-end gap-2 border-t border-border px-5 py-3">{footer}</div>
          ) : null}
        </D.Content>
      </D.Portal>
    </D.Root>
  );
}

// ---------- Drawer (side sheet; history and detail panels live here, one click away) ----------

export function Drawer({
  open,
  onOpenChange,
  title,
  subtitle,
  actions,
  children,
  width = 'md',
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  width?: 'md' | 'lg' | 'xl';
}) {
  const w = { md: 'sm:w-[30rem]', lg: 'sm:w-[42rem]', xl: 'sm:w-[min(64rem,92vw)]' }[width];
  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className="fixed inset-0 z-40 bg-black/30" />
        <D.Content
          className={cn(
            'fixed inset-y-0 right-0 z-40 flex w-full flex-col border-l border-border bg-surface shadow-pop focus:outline-none',
            w,
          )}
          aria-describedby={undefined}
        >
          <div className="flex items-center gap-3 border-b border-border px-5 py-3.5">
            <div className="min-w-0 flex-1">
              <D.Title className="truncate text-base font-semibold text-fg">{title}</D.Title>
              {subtitle ? <p className="truncate text-xs text-muted">{subtitle}</p> : null}
            </div>
            {actions}
            <D.Close asChild>
              <Button variant="ghost" size="icon-sm" aria-label="Close panel">
                <X className="size-4" />
              </Button>
            </D.Close>
          </div>
          <div className="min-h-0 flex-1 overflow-auto">{children}</div>
        </D.Content>
      </D.Portal>
    </D.Root>
  );
}

// ---------- Tabs ----------

export function Tabs({
  value,
  onValueChange,
  tabs,
  children,
  className,
  listClassName,
}: {
  value: string;
  onValueChange: (v: string) => void;
  tabs: Array<{ value: string; label: ReactNode; count?: number; hidden?: boolean }>;
  children: ReactNode;
  className?: string;
  listClassName?: string;
}) {
  return (
    <T.Root value={value} onValueChange={onValueChange} className={cn('flex min-h-0 flex-col', className)}>
      <T.List
        className={cn('flex shrink-0 items-center gap-1 overflow-x-auto border-b border-border px-1', listClassName)}
        aria-label="Sections"
      >
        {tabs
          .filter((t) => !t.hidden)
          .map((t) => (
            <T.Trigger
              key={t.value}
              value={t.value}
              className={cn(
                'relative -mb-px inline-flex h-10 items-center gap-1.5 border-b-2 border-transparent px-3 text-[13px] font-medium whitespace-nowrap',
                'text-muted transition-colors hover:text-fg data-[state=active]:border-accent data-[state=active]:text-fg',
              )}
            >
              {t.label}
              {t.count !== undefined ? (
                <span className="tabular rounded bg-surface-2 px-1.5 text-[11px] text-muted">{t.count}</span>
              ) : null}
            </T.Trigger>
          ))}
      </T.List>
      {children}
    </T.Root>
  );
}

export const TabPanel = ({ value, children, className }: { value: string; children: ReactNode; className?: string }) => (
  <T.Content value={value} className={cn('min-h-0 flex-1 focus-visible:outline-none', className)}>
    {children}
  </T.Content>
);

// ---------- Switch ----------

export function Switch({
  checked,
  onCheckedChange,
  label,
  id,
  disabled,
}: {
  checked: boolean;
  onCheckedChange: (v: boolean) => void;
  label?: ReactNode;
  id?: string;
  disabled?: boolean;
}) {
  return (
    <label className={cn('inline-flex cursor-pointer items-center gap-2 text-sm text-fg', disabled && 'opacity-50')}>
      <S.Root
        id={id}
        checked={checked}
        onCheckedChange={onCheckedChange}
        disabled={disabled}
        className="relative h-5 w-9 shrink-0 rounded-full bg-surface-3 transition-colors data-[state=checked]:bg-accent"
      >
        <S.Thumb className="block size-4 translate-x-0.5 rounded-full bg-white shadow transition-transform data-[state=checked]:translate-x-[18px]" />
      </S.Root>
      {label}
    </label>
  );
}

// ---------- Segmented control ----------

export function Segmented<V extends string>({
  value,
  onChange,
  options,
  label,
  size = 'md',
}: {
  value: V;
  onChange: (v: V) => void;
  options: Array<{ value: V; label: ReactNode; title?: string }>;
  label: string;
  size?: 'sm' | 'md';
}) {
  return (
    <ToggleGroup.Root
      type="single"
      value={value}
      onValueChange={(v) => v && onChange(v as V)}
      aria-label={label}
      className="inline-flex shrink-0 items-center rounded-lg border border-border bg-surface-2 p-0.5"
    >
      {options.map((o) => (
        <ToggleGroup.Item
          key={o.value}
          value={o.value}
          title={o.title}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-md font-medium text-muted transition-colors hover:text-fg',
            'data-[state=on]:bg-surface data-[state=on]:text-fg data-[state=on]:shadow-card',
            size === 'sm' ? 'h-6 px-2 text-xs' : 'h-7 px-2.5 text-[13px]',
          )}
        >
          {o.label}
        </ToggleGroup.Item>
      ))}
    </ToggleGroup.Root>
  );
}

// ---------- Tooltip ----------

export function TooltipProvider({ children }: { children: ReactNode }) {
  return (
    <TT.Provider delayDuration={300} skipDelayDuration={100}>
      {children}
    </TT.Provider>
  );
}

export function Tooltip({ content, children, side = 'top' }: { content: ReactNode; children: ReactNode; side?: 'top' | 'bottom' | 'left' | 'right' }) {
  if (!content) return <>{children}</>;
  return (
    <TT.Root>
      <TT.Trigger asChild>{children}</TT.Trigger>
      <TT.Portal>
        <TT.Content
          side={side}
          sideOffset={6}
          className="z-[60] max-w-xs rounded-md border border-border bg-surface px-2.5 py-1.5 text-xs text-fg shadow-pop"
        >
          {content}
        </TT.Content>
      </TT.Portal>
    </TT.Root>
  );
}
