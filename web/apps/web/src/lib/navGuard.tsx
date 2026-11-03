'use client';

/**
 * Leaving a page with unsaved work (the Design Studio first; any page can use it). The Next app router has no route blocking,
 * so the guard intercepts at the app level:
 * - in-app links (the sidebar, headers, any `<a href>` to another page of the app): a capture-phase click listener on the
 *   document stops the click before Next's `<Link>` sees it and asks first;
 * - programmatic navigation (the command palette…): `guardedNavigate(go)` asks first;
 * - browser back / forward: while a page is dirty a duplicate history entry of the current page sits on top, so "back" lands on
 *   the same page (Next restores it as it was) and the guard asks; leaving goes back past both;
 * - tab close, reload, external links: `beforeunload` (the browser's own prompt).
 * The question is the app's own dialog: "Leave without saving?" with Save and leave / Leave / Stay.
 */
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';

export interface NavGuard {
  /** Whether leaving now would lose work. */
  dirty: () => boolean;
  /** Saves the work; resolves true when it is safe to leave. Absent = no "Save and leave". */
  save?: () => Promise<boolean>;
  /** What would be lost ("the design's unsaved changes"). */
  what?: string;
}

// ---------------------------------------------------------------------------------------------------------- the store

const guards = new Set<NavGuard>();
const listeners = new Set<() => void>();
let pending: { go: () => void; guard: NavGuard } | null = null;
/** Set while a navigation the user agreed to runs (nothing asks again meanwhile). */
let bypass = false;
let bypassTimer: ReturnType<typeof setTimeout> | null = null;

const emit = () => {
  for (const l of listeners) l();
};

/** The first registered guard that is dirty now, or null (navigation is free). */
export function blockingGuard(): NavGuard | null {
  for (const g of guards) if (g.dirty()) return g;
  return null;
}

/** Registers a guard; returns its removal. */
export function addGuard(g: NavGuard): () => void {
  guards.add(g);
  emit();
  return () => {
    guards.delete(g);
    if (pending?.guard === g) pending = null;
    emit();
  };
}

/** Tells the host a guard's dirty state may have changed (history sentinel, beforeunload). */
export function guardsChanged(): void {
  emit();
}

/** Runs `go` now when nothing is dirty (true), else holds it for the dialog's answer (false). */
export function guardedNavigate(go: () => void): boolean {
  const g = blockingGuard();
  if (!g) {
    go();
    return true;
  }
  pending = { go, guard: g };
  emit();
  return false;
}

/** The navigation waiting for an answer (the host's dialog), or null. */
export function pendingNavigation(): { guard: NavGuard } | null {
  return pending;
}

/** The dialog's answer: leave (run the held navigation, with or without a save first) or stay. */
export async function resolveNavigation(answer: 'leave' | 'save' | 'stay'): Promise<boolean> {
  const p = pending;
  if (!p) return false;
  if (answer === 'stay') {
    pending = null;
    emit();
    return false;
  }
  if (answer === 'save') {
    const ok = p.guard.save ? await p.guard.save().catch(() => false) : false;
    if (!ok) return false; // the dialog stays: the save failed
  }
  pending = null;
  // Leaving: nothing asks again (or takes the history entry off after "Save and leave") while the navigation runs and the
  // page unmounts.
  bypass = true;
  if (bypassTimer) clearTimeout(bypassTimer);
  bypassTimer = setTimeout(() => (bypass = false), 1500);
  emit();
  p.go();
  return true;
}

function subscribe(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

// ---------------------------------------------------------------------------------------------------------- link clicks

/**
 * The in-app page a click is headed for, when the guard should ask about it: a plain left click (no modifier, not a new tab or
 * a download) on a same-origin link to another path. Null = let the click through (hash links, the same page with other
 * query parameters count as staying).
 */
export function guardedHref(
  ev: {
    button: number;
    metaKey: boolean;
    ctrlKey: boolean;
    shiftKey: boolean;
    altKey: boolean;
    defaultPrevented: boolean;
  },
  a: { href: string; target: string; hasAttribute(n: string): boolean } | null,
  here: { origin: string; pathname: string },
): string | null {
  if (!a || ev.defaultPrevented || ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return null;
  if ((a.target && a.target !== '_self') || a.hasAttribute('download')) return null;
  let url: URL;
  try {
    url = new URL(a.href, here.origin);
  } catch {
    return null;
  }
  if (url.origin !== here.origin || url.pathname === here.pathname) return null;
  return url.pathname + url.search + url.hash;
}

// ---------------------------------------------------------------------------------------------------------- hooks

/** Guards the calling page while `dirty` holds (the latest `save` is used). */
export function useNavigationGuard(dirty: boolean, opts: { save?: () => Promise<boolean>; what?: string } = {}): void {
  const state = useRef({ dirty, opts });
  useEffect(() => {
    state.current = { dirty, opts };
  });
  useEffect(
    () =>
      addGuard({
        dirty: () => state.current.dirty,
        get save() {
          return state.current.opts.save;
        },
        get what() {
          return state.current.opts.what;
        },
      }),
    [],
  );
  useEffect(() => guardsChanged(), [dirty]);
}

/**
 * Installed once by the shell: intercepts link clicks and back/forward while a page is dirty, warns on unload, and asks with
 * the app's dialog.
 */
export function NavigationGuardHost() {
  const router = useRouter();
  const held = useSyncExternalStore(subscribe, pendingNavigation, () => null);
  const dirty = useSyncExternalStore(
    subscribe,
    () => !!blockingGuard(),
    () => false,
  );
  const [saving, setSaving] = useState(false);
  const sentinel = useRef(false);
  const ignorePop = useRef(false);

  // Links: capture phase, before Next's <Link> handler (React listens at the root, below the document).
  useEffect(() => {
    const onClick = (ev: MouseEvent) => {
      if (bypass || !blockingGuard()) return;
      const a = (ev.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
      const href = guardedHref(ev, a, window.location);
      if (!href) return;
      ev.preventDefault();
      ev.stopPropagation();
      guardedNavigate(() => router.push(href));
    };
    document.addEventListener('click', onClick, true);
    return () => document.removeEventListener('click', onClick, true);
  }, [router]);

  // Unload: the browser's own prompt.
  useEffect(() => {
    if (!dirty) return;
    const onUnload = (e: BeforeUnloadEvent) => {
      if (bypass || !blockingGuard()) return;
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', onUnload);
    return () => window.removeEventListener('beforeunload', onUnload);
  }, [dirty]);

  // Back / forward: a duplicate entry of this page on top while dirty; "back" lands on the original (same page) and asks.
  useEffect(() => {
    if (dirty && !sentinel.current) {
      window.history.pushState(window.history.state, '', window.location.href);
      sentinel.current = true;
    } else if (!dirty && sentinel.current) {
      sentinel.current = false;
      // Saved (the page is still here): take the duplicate entry off again. Left (the page is gone): it stays behind.
      if (guards.size > 0 && !bypass) {
        ignorePop.current = true;
        window.history.back();
      }
    }
  }, [dirty]);

  useEffect(() => {
    const onPop = () => {
      if (ignorePop.current) {
        ignorePop.current = false;
        return;
      }
      if (!sentinel.current || bypass) return;
      if (!blockingGuard()) {
        sentinel.current = false;
        return;
      }
      // Back from the sentinel: we are on this page again. Put the sentinel back and ask.
      window.history.pushState(window.history.state, '', window.location.href);
      guardedNavigate(() => {
        sentinel.current = false;
        window.history.go(-2);
      });
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const answer = useCallback(async (a: 'leave' | 'save' | 'stay') => {
    if (a === 'save') setSaving(true);
    try {
      await resolveNavigation(a);
    } finally {
      setSaving(false);
    }
  }, []);

  const what = held?.guard.what ?? 'your unsaved changes';
  return (
    <Dialog
      open={!!held}
      onOpenChange={(o) => {
        if (!o && !saving) void answer('stay');
      }}
      title="Leave without saving?"
      description={`Leaving now loses ${what}.`}
      size="sm"
      footer={
        <>
          <Button variant="ghost" onClick={() => void answer('stay')} disabled={saving}>
            Stay
          </Button>
          <Button variant="secondary" onClick={() => void answer('leave')} disabled={saving}>
            Leave
          </Button>
          {held?.guard.save ? (
            <Button variant="primary" onClick={() => void answer('save')} loading={saving}>
              Save and leave
            </Button>
          ) : null}
        </>
      }
    />
  );
}
