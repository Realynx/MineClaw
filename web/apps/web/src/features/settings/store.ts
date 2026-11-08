'use client';

/**
 * The viewer settings of this device: read once from localStorage (every access guarded: private windows and blocked site data
 * have no storage, and then the defaults simply apply for the session), pushed to the world viewer's quality store (every
 * mounted viewer applies it on its next frame), and kept in step across tabs through the `storage` event.
 */
import { getViewerQuality, PROFILES, probeRendererOnce, setViewerQuality, viewerStatus, type RendererProbe } from '@mineclaw/world-viewer/settings';
import { useEffect, useState, useSyncExternalStore } from 'react';
import { initPreviewSettings } from '@/features/live/previewSettings';
import {
  DEFAULT_SETTINGS,
  effectiveQuality,
  parseSettings,
  SETTINGS_KEY,
  withField,
  withPreset,
  type FieldKey,
  type Preset,
  type QualityFields,
  type ViewerSettings,
} from './schema';

let state: ViewerSettings = DEFAULT_SETTINGS;
let loaded = false;
const listeners = new Set<() => void>();

function readStored(): ViewerSettings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? parseSettings(JSON.parse(raw)) : DEFAULT_SETTINGS;
  } catch {
    return DEFAULT_SETTINGS; // storage unavailable, or not JSON
  }
}

function push(): void {
  setViewerQuality(effectiveQuality(state));
}

/** Reads storage once (no side effects: it runs as a render snapshot; `initViewerSettings` pushes to the viewer). */
function load(): ViewerSettings {
  if (loaded) return state;
  loaded = true;
  state = readStored();
  return state;
}

function emit(): void {
  for (const l of listeners) l();
}

/** Replaces the settings: validated, saved (when storage allows), applied to every viewer, announced. */
export function saveViewerSettings(next: ViewerSettings): void {
  load();
  state = parseSettings(next);
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(state));
  } catch {
    /* storage unavailable: the change still applies for this session */
  }
  push();
  emit();
}

export function getViewerSettings(): ViewerSettings {
  return load();
}

export function resetViewerSettings(): void {
  saveViewerSettings(DEFAULT_SETTINGS);
}

/** The active quality profile's values for Auto's "profile's own" fields (the detected renderer's). */
export function detectedProfile(probe: Pick<RendererProbe, 'software'> | null) {
  return PROFILES[probe?.software === false ? 'gpu' : 'software'];
}

export function setPreset(p: Preset, probe: Pick<RendererProbe, 'software'> | null): void {
  saveViewerSettings(withPreset(load(), p, detectedProfile(probe)));
}

export function setField<K extends FieldKey>(key: K, value: QualityFields[K], probe: Pick<RendererProbe, 'software'> | null): void {
  saveViewerSettings(withField(load(), key, value, detectedProfile(probe)));
}

export function setPerfOverlay(on: boolean): void {
  saveViewerSettings({ ...load(), perfOverlay: on });
}

/** For tests: forget what was loaded (the next read goes to storage again). */
export function _resetForTests(): void {
  loaded = false;
  state = DEFAULT_SETTINGS;
}

export function useViewerSettings(): ViewerSettings {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    load,
    () => DEFAULT_SETTINGS,
  );
}

let started = false;

/**
 * Once per page (the shell): applies the stored settings before any viewer draws, follows changes made in other tabs and, in
 * mock mode, exposes `window.__mcxViewer` (the applied quality and the live viewer status) for headless checks.
 */
export function initViewerSettings(): void {
  initPreviewSettings();
  load();
  push();
  if (started || typeof window === 'undefined') return;
  started = true;
  window.addEventListener('storage', (e) => {
    if (e.key !== SETTINGS_KEY) return;
    try {
      state = e.newValue ? parseSettings(JSON.parse(e.newValue)) : DEFAULT_SETTINGS;
    } catch {
      state = DEFAULT_SETTINGS;
    }
    push();
    emit();
  });
  if (process.env.NEXT_PUBLIC_MOCK === '1')
    (window as unknown as { __mcxViewer?: unknown }).__mcxViewer = { quality: getViewerQuality, status: () => viewerStatus(), settings: () => state };
}

/** The renderer probe (software WebGL or a GPU), run once per page after mount; null until then. */
export function useRendererProbe(): RendererProbe | null {
  const [p, setP] = useState<RendererProbe | null>(null);
  useEffect(() => {
    setP(probeRendererOnce());
  }, []);
  return p;
}

/** The latest viewer status (measured fps, levers, entity reach) polled twice a second; null while no viewer draws. */
export function useViewerStatus(): ReturnType<typeof viewerStatus> {
  const [s, setS] = useState<ReturnType<typeof viewerStatus>>(null);
  useEffect(() => {
    const tick = () => setS(viewerStatus());
    tick();
    const id = setInterval(tick, 500);
    return () => clearInterval(id);
  }, []);
  return s;
}
