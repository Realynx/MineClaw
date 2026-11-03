'use client';

/**
 * Per-viewer UI preferences (theme, developer details, chat toggles), persisted in localStorage. Every read and write is
 * guarded: storage may be unavailable (private windows, blocked site data) and the app must work without it.
 */
import { useSyncExternalStore } from 'react';

export type ThemePref = 'system' | 'light' | 'dark';

export interface Prefs {
  theme: ThemePref;
  devDetails: boolean;
  showPrivateSpeech: boolean;
  sidebarCollapsed: boolean;
}

const KEY = 'mcx-prefs';
export const THEME_KEY = 'mcx-theme';

const defaults: Prefs = { theme: 'system', devDetails: false, showPrivateSpeech: false, sidebarCollapsed: false };

let state: Prefs = defaults;
let loaded = false;
const listeners = new Set<() => void>();

function load(): Prefs {
  if (loaded) return state;
  loaded = true;
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) state = { ...defaults, ...(JSON.parse(raw) as Partial<Prefs>) };
    const theme = localStorage.getItem(THEME_KEY);
    if (theme === 'light' || theme === 'dark' || theme === 'system') state = { ...state, theme };
  } catch {
    /* storage unavailable */
  }
  return state;
}

export function setPrefs(patch: Partial<Prefs>): void {
  state = { ...load(), ...patch };
  try {
    localStorage.setItem(KEY, JSON.stringify(state));
    localStorage.setItem(THEME_KEY, state.theme);
  } catch {
    /* storage unavailable */
  }
  if (patch.theme) applyTheme(patch.theme);
  for (const l of listeners) l();
}

export function usePrefs(): Prefs {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    load,
    () => defaults,
  );
}

export function resolvedTheme(pref: ThemePref): 'light' | 'dark' {
  if (pref !== 'system') return pref;
  return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function applyTheme(pref: ThemePref): void {
  const dark = resolvedTheme(pref) === 'dark';
  document.documentElement.classList.toggle('dark', dark);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

/** Inline script for <head>: sets the theme class before first paint (no flash, no layout shift). */
export const themeBootScript = `(function(){try{var p=localStorage.getItem('${THEME_KEY}')||'system';var d=p==='dark'||(p==='system'&&matchMedia('(prefers-color-scheme: dark)').matches);var e=document.documentElement;e.classList.toggle('dark',d);e.dataset.theme=d?'dark':'light';}catch(_){document.documentElement.classList.add('dark');}})();`;
