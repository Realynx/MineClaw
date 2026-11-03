'use client';

import { ChevronsLeft, ChevronsRight, Menu, X } from 'lucide-react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useState, type ReactNode } from 'react';
import { useAuth } from '@/lib/auth';
import { setPrefs, usePrefs } from '@/lib/prefs';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/primitives';
import { Tooltip } from '@/components/ui/overlays';
import { CommandPalette } from './CommandPalette';
import { Header } from './Header';
import { NAV } from './nav';

function Logo({ collapsed }: { collapsed: boolean }) {
  return (
    <Link href="/live" className="flex h-14 items-center gap-2.5 px-4 text-fg" aria-label="MineClaw home">
      <span className="grid size-7 shrink-0 place-items-center rounded-lg bg-accent text-accent-fg shadow-card">
        <svg viewBox="0 0 24 24" className="size-4" aria-hidden>
          <path fill="currentColor" d="M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4z" opacity=".95" />
          <path fill="currentColor" d="M13 13h7v7h-7z" opacity=".55" />
        </svg>
      </span>
      {!collapsed ? (
        <span className="text-[15px] font-semibold tracking-tight">
          MineClaw <span className="font-normal text-muted">Explorer</span>
        </span>
      ) : null}
    </Link>
  );
}

function SidebarNav({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const pathname = usePathname();
  const { can } = useAuth();
  return (
    <nav aria-label="Main" className="flex-1 overflow-y-auto px-2 pb-4">
      {NAV.map((group) => {
        const items = group.items.filter((i) => !i.access || can(i.access));
        if (items.length === 0) return null;
        return (
          <div key={group.label} className="mt-4 first:mt-1">
            {!collapsed ? (
              <div className="px-2.5 pb-1.5 text-[11px] font-semibold tracking-wider text-subtle uppercase">{group.label}</div>
            ) : (
              <div className="mx-3 mb-2 border-t border-border" />
            )}
            <ul className="space-y-0.5">
              {items.map((item) => {
                const active = pathname === item.href || pathname.startsWith(`${item.href}/`);
                const Icon = item.icon;
                const link = (
                  <Link
                    href={item.href}
                    onClick={onNavigate}
                    aria-current={active ? 'page' : undefined}
                    className={cn(
                      'group flex h-9 items-center gap-2.5 rounded-lg px-2.5 text-[13px] font-medium transition-colors',
                      active ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
                      collapsed && 'justify-center px-0',
                    )}
                  >
                    <Icon className={cn('size-4 shrink-0', active ? 'text-accent' : 'text-subtle group-hover:text-muted')} />
                    {!collapsed ? <span className="truncate">{item.label}</span> : <span className="sr-only">{item.label}</span>}
                  </Link>
                );
                return (
                  <li key={item.href}>
                    {collapsed ? (
                      <Tooltip content={item.label} side="right">
                        {link}
                      </Tooltip>
                    ) : (
                      link
                    )}
                  </li>
                );
              })}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}

export function Shell({ children }: { children: ReactNode }) {
  const { sidebarCollapsed } = usePrefs();
  const [mobileOpen, setMobileOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const pathname = usePathname();

  useEffect(() => setMobileOpen(false), [pathname]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <div className="flex h-dvh overflow-hidden bg-bg">
      <a
        href="#main"
        className="sr-only z-50 rounded-md bg-surface px-3 py-2 focus:not-sr-only focus:absolute focus:top-2 focus:left-2"
      >
        Skip to content
      </a>
      {/* Desktop / tablet sidebar */}
      <aside
        className={cn(
          'hidden shrink-0 flex-col border-r border-border bg-surface transition-[width] duration-200 md:flex',
          sidebarCollapsed ? 'w-16' : 'w-60',
        )}
      >
        <Logo collapsed={sidebarCollapsed} />
        <SidebarNav collapsed={sidebarCollapsed} />
        <div className={cn('border-t border-border p-2', sidebarCollapsed && 'flex justify-center')}>
          <Button
            variant="ghost"
            size={sidebarCollapsed ? 'icon-sm' : 'sm'}
            className={cn(!sidebarCollapsed && 'w-full justify-start')}
            onClick={() => setPrefs({ sidebarCollapsed: !sidebarCollapsed })}
            aria-label={sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            {sidebarCollapsed ? <ChevronsRight className="size-4" /> : <ChevronsLeft className="size-4" />}
            {!sidebarCollapsed ? 'Collapse' : null}
          </Button>
        </div>
      </aside>

      {/* Phone sidebar */}
      {mobileOpen ? (
        <div className="fixed inset-0 z-40 md:hidden" role="dialog" aria-modal="true" aria-label="Navigation">
          <div className="absolute inset-0 bg-black/40" onClick={() => setMobileOpen(false)} />
          <aside className="absolute inset-y-0 left-0 flex w-64 flex-col border-r border-border bg-surface shadow-pop">
            <div className="flex items-center justify-between pr-2">
              <Logo collapsed={false} />
              <Button variant="ghost" size="icon-sm" onClick={() => setMobileOpen(false)} aria-label="Close navigation">
                <X className="size-4" />
              </Button>
            </div>
            <SidebarNav collapsed={false} onNavigate={() => setMobileOpen(false)} />
          </aside>
        </div>
      ) : null}

      <div className="flex min-w-0 flex-1 flex-col">
        <Header
          onOpenPalette={() => setPaletteOpen(true)}
          menuButton={
            <Button variant="ghost" size="icon" className="md:hidden" onClick={() => setMobileOpen(true)} aria-label="Open navigation">
              <Menu className="size-5" />
            </Button>
          }
        />
        <main id="main" className="min-h-0 flex-1 overflow-auto">
          {children}
        </main>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
    </div>
  );
}

/** Standard page padding + max width. Full-bleed pages (graphs, bot view) skip it. */
export function PageBody({ children, className, wide }: { children: ReactNode; className?: string; wide?: boolean }) {
  return <div className={cn('mx-auto w-full px-4 py-6 sm:px-6', wide ? 'max-w-[110rem]' : 'max-w-7xl', className)}>{children}</div>;
}
