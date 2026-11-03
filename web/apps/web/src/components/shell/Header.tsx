'use client';

import { Bug, KeyRound, LogOut, Monitor, Moon, Search, Sun, UserRound } from 'lucide-react';
import { usePathname } from 'next/navigation';
import { DropdownMenu as DM, Popover } from 'radix-ui';
import { useState, type ReactNode } from 'react';
import { useAuth } from '@/lib/auth';
import { useConnection } from '@/lib/live/LiveProvider';
import { setPrefs, usePrefs, type ThemePref } from '@/lib/prefs';
import { ago, cn } from '@/lib/utils';
import { Badge, Button, Kbd } from '@/components/ui/primitives';
import { Tooltip } from '@/components/ui/overlays';
import { NAV_ITEMS } from './nav';
import { TotpDialog } from './TotpDialog';

const levelStyle = {
  live: { dot: 'bg-ok', ring: 'bg-ok/30', text: 'text-fg' },
  connecting: { dot: 'bg-info', ring: 'bg-info/30', text: 'text-muted' },
  reconnecting: { dot: 'bg-warn', ring: 'bg-warn/30', text: 'text-warn' },
  degraded: { dot: 'bg-warn', ring: 'bg-warn/30', text: 'text-warn' },
  offline: { dot: 'bg-danger', ring: 'bg-danger/30', text: 'text-danger' },
} as const;

/** The ONE connection indicator (explorer-spec §17.7): stream state + Nest's `status` event. */
export function ConnectionIndicator() {
  const c = useConnection();
  const s = levelStyle[c.level];
  const pulsing = c.level === 'connecting' || c.level === 'reconnecting';
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <button
          className="inline-flex h-8 items-center gap-2 rounded-lg border border-border bg-surface px-2.5 text-xs font-medium hover:bg-surface-2"
          aria-label={`Connection: ${c.label}. ${c.detail}`}
        >
          <span className="relative flex size-2.5 items-center justify-center">
            {c.level === 'live' ? <span className={cn('absolute inline-flex size-full animate-ping rounded-full opacity-60', s.ring)} style={{ animationDuration: '2.4s' }} /> : null}
            <span className={cn('relative inline-flex size-2 rounded-full', s.dot, pulsing && 'animate-pulse-dot')} />
          </span>
          <span className={cn('hidden sm:inline', s.text)}>{c.label}</span>
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content
          align="end"
          sideOffset={8}
          className="z-50 w-72 rounded-xl border border-border bg-surface p-4 text-sm shadow-pop"
        >
          <div className="mb-1 flex items-center gap-2 font-medium text-fg">
            <span className={cn('inline-flex size-2 rounded-full', s.dot)} /> {c.label}
          </div>
          <p className="text-muted">{c.detail}</p>
          {c.status ? (
            <dl className="mt-3 grid grid-cols-2 gap-y-1 text-xs">
              <dt className="text-subtle">Bridge</dt>
              <dd className="text-right text-fg capitalize">{c.status.upstream}</dd>
              <dt className="text-subtle">Bot</dt>
              <dd className="text-right text-fg capitalize">{c.status.bot}</dd>
              {c.status.latencyMs !== null && c.status.latencyMs !== undefined ? (
                <>
                  <dt className="text-subtle">Latency</dt>
                  <dd className="tabular text-right text-fg">{Math.round(c.status.latencyMs)} ms</dd>
                </>
              ) : null}
              <dt className="text-subtle">Checked</dt>
              <dd className="text-right text-fg">{ago(c.status.at)}</dd>
            </dl>
          ) : null}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

const themeIcon: Record<ThemePref, ReactNode> = {
  system: <Monitor className="size-4" />,
  light: <Sun className="size-4" />,
  dark: <Moon className="size-4" />,
};

const menuItem =
  'flex h-8 cursor-pointer items-center gap-2 rounded-md px-2 text-[13px] text-fg outline-none data-[highlighted]:bg-surface-2';

function ThemeMenu() {
  const { theme } = usePrefs();
  return (
    <DM.Root>
      <Tooltip content="Theme">
        <DM.Trigger asChild>
          <Button variant="ghost" size="icon" aria-label={`Theme: ${theme}`}>
            {themeIcon[theme]}
          </Button>
        </DM.Trigger>
      </Tooltip>
      <DM.Portal>
        <DM.Content align="end" sideOffset={6} className="z-50 min-w-36 rounded-lg border border-border bg-surface p-1 shadow-pop">
          <DM.RadioGroup value={theme} onValueChange={(v) => setPrefs({ theme: v as ThemePref })}>
            {(['system', 'light', 'dark'] as const).map((t) => (
              <DM.RadioItem key={t} value={t} className={menuItem}>
                {themeIcon[t]}
                <span className="flex-1 capitalize">{t}</span>
                <DM.ItemIndicator className="size-1.5 rounded-full bg-accent" />
              </DM.RadioItem>
            ))}
          </DM.RadioGroup>
        </DM.Content>
      </DM.Portal>
    </DM.Root>
  );
}

function UserMenu() {
  const { me, signOut } = useAuth();
  const [totp, setTotp] = useState(false);
  return (
    <>
      <DM.Root>
        <DM.Trigger asChild>
          <button
            className="inline-flex h-8 items-center gap-2 rounded-lg pr-2 pl-1 text-[13px] font-medium text-fg hover:bg-surface-2"
            aria-label={`Account: ${me.username}`}
          >
            <span className="grid size-6 place-items-center rounded-full bg-surface-3 text-xs font-semibold uppercase">
              {me.username.slice(0, 1)}
            </span>
            <span className="hidden lg:inline">{me.username}</span>
          </button>
        </DM.Trigger>
        <DM.Portal>
          <DM.Content align="end" sideOffset={6} className="z-50 w-64 rounded-lg border border-border bg-surface p-1 shadow-pop">
            <div className="px-2 py-2">
              <div className="flex items-center gap-2 text-sm font-medium text-fg">
                <UserRound className="size-4 text-subtle" /> {me.username}
              </div>
              <div className="mt-1 text-xs text-muted">
                {me.player ? <>Plays as <span className="font-medium text-fg">{me.player}</span></> : 'No linked player (cannot chat)'}
              </div>
              <div className="mt-2 flex flex-wrap gap-1">
                {me.roles.map((r) => (
                  <Badge key={r} tone={r === 'admin' ? 'accent' : 'neutral'}>
                    {r}
                  </Badge>
                ))}
              </div>
            </div>
            <DM.Separator className="my-1 h-px bg-border" />
            <DM.Item className={menuItem} onSelect={() => setTotp(true)}>
              <KeyRound className="size-4 text-subtle" /> Two-factor {me.totpEnrolled ? '(on)' : 'setup'}
            </DM.Item>
            <DM.Item className={menuItem} onSelect={() => void signOut()}>
              <LogOut className="size-4 text-subtle" /> Sign out
            </DM.Item>
            <div className="px-2 pt-1 pb-1.5 text-[11px] text-subtle">Session ends {ago(me.idleExpiresAt)} if idle</div>
          </DM.Content>
        </DM.Portal>
      </DM.Root>
      <TotpDialog open={totp} onOpenChange={setTotp} />
    </>
  );
}

export function Header({ onOpenPalette, menuButton }: { onOpenPalette: () => void; menuButton: ReactNode }) {
  const pathname = usePathname();
  const { devDetails } = usePrefs();
  const page = NAV_ITEMS.find((i) => pathname === i.href || pathname.startsWith(`${i.href}/`));
  return (
    <header className="flex h-14 shrink-0 items-center gap-2 border-b border-border bg-surface px-3 sm:px-4">
      {menuButton}
      <div className="min-w-0 text-[13px] font-medium text-muted max-md:hidden lg:w-44">{page?.label}</div>
      <button
        onClick={onOpenPalette}
        className="mx-auto flex h-9 w-full max-w-md min-w-0 items-center gap-2 rounded-lg border border-border bg-surface-2 px-3 text-left text-[13px] text-subtle transition-colors hover:border-border-strong hover:text-muted"
        aria-label="Search and jump (Ctrl+K)"
      >
        <Search className="size-4 shrink-0" />
        <span className="flex-1 truncate">Jump to a bot, script, memory or page…</span>
        <span className="hidden items-center gap-0.5 sm:flex">
          <Kbd>Ctrl</Kbd>
          <Kbd>K</Kbd>
        </span>
      </button>
      <div className="flex shrink-0 items-center gap-1">
        <ConnectionIndicator />
        <Tooltip content={devDetails ? 'Hide developer details' : 'Show developer details (tokens, costs, timings)'}>
          <Button
            variant="ghost"
            size="icon"
            aria-pressed={devDetails}
            aria-label="Developer details"
            onClick={() => setPrefs({ devDetails: !devDetails })}
            className={cn(devDetails && 'bg-accent-soft text-accent hover:text-accent')}
          >
            <Bug className="size-4" />
          </Button>
        </Tooltip>
        <ThemeMenu />
        <UserMenu />
      </div>
    </header>
  );
}
