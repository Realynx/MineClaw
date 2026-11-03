'use client';

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { hasRole, type Me, type Role, type RouteAccess } from '@mineclaw/contracts';
import { usePathname, useRouter } from 'next/navigation';
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, ApiError, onUnauthorized, setCsrfToken } from './api/client';
import { qk } from './api/query';

interface AuthValue {
  me: Me;
  can: (access: RouteAccess) => boolean;
  /** Has a linked player and the `chat` role: may talk to bots. */
  canChat: boolean;
  isAdmin: boolean;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export const meKey = qk('auth.me');

export function useAuth(): AuthValue {
  const v = useContext(AuthContext);
  if (!v) throw new Error('useAuth outside AuthGate');
  return v;
}

/** Provides a fixed signed-in user (tests and stories). */
export function StaticAuthProvider({ me, children }: { me: Me; children: ReactNode }) {
  const value = useMemo<AuthValue>(
    () => ({
      me,
      can: (a) => hasRole(me.roles, a),
      canChat: hasRole(me.roles, 'chat') && !!me.player,
      isAdmin: hasRole(me.roles, 'admin'),
      signOut: () => Promise.resolve(),
    }),
    [me],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Same as useAuth but tolerant of being outside the gate (e.g. the login page). */
export function useMaybeAuth(): AuthValue | null {
  return useContext(AuthContext);
}

export function loginHref(pathname: string, reason?: 'expired'): string {
  const next = pathname && pathname !== '/login' ? `?next=${encodeURIComponent(pathname)}` : '';
  const sep = next ? '&' : '?';
  return `/login${next}${reason ? `${sep}reason=${reason}` : ''}`;
}

/**
 * Gates the app shell on a session. No session → the login page. A 401 later (idle or absolute timeout) shows the
 * session-expired dialog instead of silently failing every widget.
 */
export function AuthGate({
  children,
  fallback,
  expired,
}: {
  children: ReactNode;
  fallback: (error: ApiError | null, retry: () => void) => ReactNode;
  expired: (href: string) => ReactNode;
}) {
  const router = useRouter();
  const pathname = usePathname();
  const qc = useQueryClient();
  const [isExpired, setExpired] = useState(false);

  const me = useQuery<Me, ApiError>({
    queryKey: meKey,
    queryFn: ({ signal }) => api('auth.me', { signal }),
    staleTime: 5 * 60_000,
    retry: (n, e) => !(e instanceof ApiError && e.status === 401) && n < 2,
  });

  useEffect(() => {
    if (me.error?.status === 401 && !me.data) router.replace(loginHref(pathname));
  }, [me.error, me.data, router, pathname]);

  useEffect(
    () =>
      onUnauthorized((route) => {
        if (route === 'auth.login' || route === 'auth.csrf') return;
        if (qc.getQueryData(meKey)) setExpired(true);
      }),
    [qc],
  );

  const signOut = useCallback(async () => {
    try {
      await api('auth.logout', { body: {} });
    } catch {
      /* the session may already be gone */
    }
    setCsrfToken(null);
    qc.clear();
    router.replace('/login');
  }, [qc, router]);

  const value = useMemo<AuthValue | null>(() => {
    if (!me.data) return null;
    const roles: Role[] = me.data.roles;
    return {
      me: me.data,
      can: (a) => hasRole(roles, a),
      canChat: hasRole(roles, 'chat') && !!me.data.player,
      isAdmin: hasRole(roles, 'admin'),
      signOut,
    };
  }, [me.data, signOut]);

  if (!value) return <>{fallback(me.error && me.error.status !== 401 ? me.error : null, () => void me.refetch())}</>;
  return (
    <AuthContext.Provider value={value}>
      {children}
      {isExpired ? expired(loginHref(pathname, 'expired')) : null}
    </AuthContext.Provider>
  );
}
