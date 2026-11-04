'use client';

import { Command, defaultFilter } from 'cmdk';
import { Bot, Boxes, Bug, Code2, FileText, Moon, Search, Settings, Sun } from 'lucide-react';
import { useRouter } from 'next/navigation';
import { Dialog as D } from 'radix-ui';
import { useDeferredValue, useEffect, useState, type ReactNode } from 'react';
import { guardedNavigate } from '@/lib/navGuard';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { useStatus } from '@/lib/live/hooks';
import { resolvedTheme, setPrefs, usePrefs } from '@/lib/prefs';
import { Kbd } from '@/components/ui/primitives';
import { NAV_ITEMS } from './nav';

const itemClass =
  'flex h-10 cursor-pointer items-center gap-3 rounded-lg px-3 text-[13px] text-fg aria-selected:bg-surface-2 data-[disabled=true]:opacity-50';
const groupClass =
  '[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:pt-3 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:tracking-wider [&_[cmdk-group-heading]]:text-subtle [&_[cmdk-group-heading]]:uppercase';

function Item({
  onSelect,
  icon,
  children,
  hint,
  value,
}: {
  onSelect: () => void;
  icon: ReactNode;
  children: ReactNode;
  hint?: ReactNode;
  value: string;
}) {
  return (
    <Command.Item value={value} onSelect={onSelect} className={itemClass}>
      <span className="text-subtle">{icon}</span>
      <span className="min-w-0 flex-1 truncate">{children}</span>
      {hint ? <span className="truncate text-xs text-subtle">{hint}</span> : null}
    </Command.Item>
  );
}

/** Named things (bots, pages, scripts, designs) outrank the free-text memory search for the same words. */
function rank(value: string, search: string, keywords?: string[]): number {
  const s = defaultFilter(value, search, keywords);
  return value.startsWith('search memories') || value.startsWith('memory ') ? s * 0.5 : s;
}

/** Ctrl+K: jump to bots, scripts, memories, designs and pages. Lists load only while the palette is open. */
export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const router = useRouter();
  const { can } = useAuth();
  const [q, setQ] = useState('');
  const dq = useDeferredValue(q.trim());
  const status = useStatus();
  const scripts = useApi('scripts.list', { query: { source: 'live' } }, { enabled: open });
  const designs = useApi('designs.list', { query: { limit: 200 } }, { enabled: open });
  const memories = useApi('search', { query: { q: dq, k: 6 } }, { enabled: open && dq.length >= 3, staleTime: 60_000 });

  useEffect(() => {
    if (!open) setQ('');
  }, [open]);

  const go = (href: string) => {
    onOpenChange(false);
    guardedNavigate(() => router.push(href));
  };

  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className="fixed inset-0 z-50 bg-black/40" />
        <D.Content
          aria-describedby={undefined}
          className="fixed top-[12vh] left-1/2 z-50 w-[calc(100vw-2rem)] max-w-xl -translate-x-1/2 overflow-hidden rounded-xl border border-border bg-surface shadow-pop"
        >
          <D.Title className="sr-only">Command palette</D.Title>
          <Command label="Command palette" shouldFilter loop filter={rank}>
            <div className="flex items-center gap-2 border-b border-border px-4">
              <Search className="size-4 text-subtle" />
              <Command.Input
                value={q}
                onValueChange={setQ}
                placeholder="Search bots, scripts, designs, memories, pages…"
                className="h-12 flex-1 bg-transparent text-sm text-fg outline-none placeholder:text-subtle"
              />
              <Kbd>Esc</Kbd>
            </div>
            <Command.List className="max-h-[60vh] overflow-y-auto p-2">
              <Command.Empty className="px-3 py-8 text-center text-sm text-muted">Nothing matches “{q}”.</Command.Empty>

              {dq.length >= 3 ? <MemoryGroup dq={dq} hits={memories.data?.hits} go={go} /> : null}

              <Command.Group heading="Bots" className={groupClass}>
                {(status.data?.bots ?? []).map((b) => (
                  <Item
                    key={b.name}
                    value={`bot ${b.name} ${b.task ?? ''}`}
                    icon={<Bot className="size-4" />}
                    hint={b.status}
                    onSelect={() => go(`/live/${encodeURIComponent(b.name)}`)}
                  >
                    {b.name}
                  </Item>
                ))}
              </Command.Group>

              <Command.Group heading="Pages" className={groupClass}>
                {NAV_ITEMS.filter((i) => !i.access || can(i.access)).map((i) => {
                  const Icon = i.icon;
                  return (
                    <Item
                      key={i.href}
                      value={`page ${i.label} ${i.description} ${(i.keywords ?? []).join(' ')}`}
                      icon={<Icon className="size-4" />}
                      hint={i.description}
                      onSelect={() => go(i.href)}
                    >
                      {i.label}
                    </Item>
                  );
                })}
                <Item
                  value="page settings viewer quality graphics fps frame rate performance draw distance entities"
                  icon={<Settings className="size-4" />}
                  hint="Viewer quality on this device"
                  onSelect={() => go('/settings')}
                >
                  Settings
                </Item>
              </Command.Group>

              <ScriptsGroup scripts={scripts.data?.scripts} go={go} />
              <DesignsGroup designs={designs.data?.items} go={go} />

              <ActionsGroup close={() => onOpenChange(false)} />
            </Command.List>
          </Command>
        </D.Content>
      </D.Portal>
    </D.Root>
  );
}

type Go = (href: string) => void;
type Hit = NonNullable<ReturnType<typeof useApi<'search'>>['data']>['hits'][number];

/** The memory search, and the top hits (a hit with a RAPTOR leaf opens it in the cloud). */
function MemoryGroup({ dq, hits, go }: { dq: string; hits: readonly Hit[] | undefined; go: Go }) {
  const searchHref = `/search?q=${encodeURIComponent(dq)}`;
  return (
    <Command.Group heading="Memories" className={groupClass}>
      <Item value={`search memories ${dq}`} icon={<Search className="size-4" />} onSelect={() => go(searchHref)} hint="recall search">
        Search memories for “{dq}”
      </Item>
      {(hits ?? []).map((h) => (
        <Item
          key={h.docId}
          value={`memory ${dq} ${h.title} ${h.ref}`}
          icon={<FileText className="size-4" />}
          hint={h.kind}
          onSelect={() => go(h.raptorLeaf !== null && h.raptorLeaf !== undefined ? `/memory-activity?layout=cloud&node=${h.raptorLeaf}` : searchHref)}
        >
          {h.title}
        </Item>
      ))}
    </Command.Group>
  );
}

type Script = NonNullable<ReturnType<typeof useApi<'scripts.list'>>['data']>['scripts'][number];
type Design = NonNullable<ReturnType<typeof useApi<'designs.list'>>['data']>['items'][number];

function ScriptsGroup({ scripts, go }: { scripts: readonly Script[] | undefined; go: Go }) {
  if (!scripts?.length) return null;
  return (
    <Command.Group heading="Scripts" className={groupClass}>
      {scripts.map((s) => (
        <Item
          key={s.name}
          value={`script ${s.name} ${s.purpose ?? ''}`}
          icon={<Code2 className="size-4" />}
          hint={`v${s.version} · ${s.lang}`}
          onSelect={() => go(`/scripts?name=${encodeURIComponent(s.name)}`)}
        >
          {s.name}
        </Item>
      ))}
    </Command.Group>
  );
}

function DesignsGroup({ designs, go }: { designs: readonly Design[] | undefined; go: Go }) {
  if (!designs?.length) return null;
  return (
    <Command.Group heading="Designs" className={groupClass}>
      {designs.map((d) => (
        <Item
          key={d.id}
          value={`design ${d.id} ${d.name} ${d.requester ?? ''}`}
          icon={<Boxes className="size-4" />}
          hint={`#${d.id} · r${d.rev}`}
          onSelect={() => go(`/designs?id=${d.id}`)}
        >
          {d.name}
        </Item>
      ))}
    </Command.Group>
  );
}

/** Switch the theme, show or hide developer details (each closes the palette). */
function ActionsGroup({ close }: { close: () => void }) {
  const prefs = usePrefs();
  const dark = resolvedTheme(prefs.theme) === 'dark';
  return (
    <Command.Group heading="Actions" className={groupClass}>
      <Item
        value="toggle theme dark light"
        icon={dark ? <Sun className="size-4" /> : <Moon className="size-4" />}
        onSelect={() => {
          setPrefs({ theme: dark ? 'light' : 'dark' });
          close();
        }}
      >
        Switch to {dark ? 'light' : 'dark'} theme
      </Item>
      <Item
        value="developer details toggle dev stats"
        icon={<Bug className="size-4" />}
        onSelect={() => {
          setPrefs({ devDetails: !prefs.devDetails });
          close();
        }}
      >
        {prefs.devDetails ? 'Hide' : 'Show'} developer details
      </Item>
    </Command.Group>
  );
}
