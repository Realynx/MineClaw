'use client';

import { useEffect, useRef, useState, type RefObject } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Volume2, VolumeX } from 'lucide-react';
import { Popover } from 'radix-ui';
import type { WorldEntities } from '@mineclaw/contracts';
import type { ControllerAudio, EntityEvent, WorldViewerEngine, WorldViewerHandle } from '@mineclaw/world-viewer';
import { loadWorldViewer } from '@/features/world/integration';
import { subscribeEntityFrames } from '@/features/world/entityFrames';
import { liveKeys } from '@/lib/live/dispatch';
import { CentreSurface, HEARING_DEPTH } from './audioSurface';

const KEY = 'mineclaw.controller.volume';

/** Top view: nothing more than HEARING_DEPTH blocks under the visible surface at the map centre is heard. Until that column
 * has loaded, the working level stands in for the surface (StoryMap pins the look-at point one block above it). The one
 * place that decides the floor. */
function hearingFloor(surface: CentreSurface, engine: WorldViewerEngine, dim: number): number {
  const t = engine.rig.target, cut = engine.pickCut();
  // A cut hides blocks only inside its radius, measured to the block centre as the viewer does.
  const dx = Math.floor(t.x) + 0.5 - (cut?.x ?? 0), dz = Math.floor(t.z) + 0.5 - (cut?.z ?? 0);
  const cutY = cut && dx * dx + dz * dz <= cut.r * cut.r ? cut.y : null;
  const top = surface.read(engine.runtime.host, dim, t.x, t.z, cutY);
  return (top ?? Math.floor(t.y) - 1) - HEARING_DEPTH;
}

export function StoryAudio({
  viewer,
  view,
  dim = 0,
}: {
  viewer: RefObject<WorldViewerHandle | null>;
  view: 'top' | 'angled' | 'fly';
  dim?: number;
}) {
  const qc = useQueryClient(),
    audio = useRef<ControllerAudio | null>(null),
    current = useRef({ view, dim });
  useEffect(() => {
    current.current = { view, dim };
  }, [view, dim]);
  const [volume, setVolume] = useState(0.6),
    [ready, setReady] = useState(false),
    [error, setError] = useState('');
  const volumeRef = useRef(0.6), lastVolume = useRef(0.6);
  useEffect(() => {
    let alive = true;
    try {
      const saved = localStorage.getItem(KEY), prior = localStorage.getItem(`${KEY}.last`);
      const value = saved === null ? 0.6 : Number(saved);
      if (Number.isFinite(value)) {
        volumeRef.current = Math.max(0, Math.min(1, value));
        setVolume(volumeRef.current);
      }
      lastVolume.current = volumeRef.current || (prior && Number(prior) > 0 ? Math.min(1, Number(prior)) : 0.6);
    } catch { /* storage unavailable */ }
    void loadWorldViewer().then((module) => {
      if (!alive || !module) return;
      const surface = new CentreSurface();
      const player = new module.ControllerAudio(() => {
        const engine = viewer.current?.engine();
        if (!engine?.surface.isConnected) return null;
        const camera = engine.camera,
          m = camera.matrixWorld.elements,
          topDown = current.current.view !== 'fly';
        const p = topDown ? engine.rig.target : camera.position;
        return {
          dim: current.current.dim,
          version: engine.runtime.version ?? '1.12.2',
          topDown,
          position: [p.x, p.y, p.z],
          forward: [-m[8], -m[9], -m[10]],
          up: [m[4], m[5], m[6]],
          // Only the top view has a hearing floor; the other views and a muted Controller ask the worker nothing.
          floorY: current.current.view === 'top' && volumeRef.current > 0 ? hearingFloor(surface, engine, current.current.dim) : null,
        };
      }, (enabled) => {
        if (!alive) return;
        setReady(enabled);
        if (enabled) setError('');
      });
      audio.current = player;
      player.setVolume(volumeRef.current);
    });
    const enable = () => {
      if (viewer.current?.engine()?.surface.isConnected)
        void audio.current?.enable().catch(() => { if (alive) setError('Click Enable sound to resume browser audio.'); });
    };
    document.addEventListener('pointerdown', enable);
    document.addEventListener('keydown', enable);
    const off = subscribeEntityFrames(() => {
      const frame = qc.getQueryData<WorldEntities>(liveKeys.entities);
      if (frame?.events) audio.current?.receive(frame.events as EntityEvent[]);
    });
    return () => {
      alive = false;
      off();
      document.removeEventListener('pointerdown', enable);
      document.removeEventListener('keydown', enable);
      audio.current?.dispose();
      audio.current = null;
    };
  }, [qc, viewer]);
  const change = (value: number) => {
    value = Math.max(0, Math.min(1, Math.round(value * 100) / 100));
    volumeRef.current = value;
    if (value > 0) lastVolume.current = value;
    setVolume(value);
    audio.current?.setVolume(value);
    try {
      localStorage.setItem(KEY, String(value));
      localStorage.setItem(`${KEY}.last`, String(lastVolume.current));
    } catch {
      /* optional persistence */
    }
    if (value > 0) void audio.current?.enable().catch(() => setError('Click Enable sound to resume browser audio.'));
  };
  return (
    <div
      className="absolute right-3 bottom-3 z-10 text-xs"
      data-controller-menu="sound"
    >
      <Popover.Root>
        <Popover.Trigger asChild>
          <button
            type="button"
            className="relative grid size-8 place-items-center rounded-lg border border-border bg-surface/90 text-muted shadow-sm hover:bg-surface hover:text-fg"
            aria-label="Sound settings"
            title={error || (volume ? `Sound ${Math.round(volume * 100)}%` : 'Sound muted')}
          >
            {volume ? <Volume2 className="size-4" aria-hidden /> : <VolumeX className="size-4" aria-hidden />}
            {error ? <span className="absolute top-1 right-1 size-1.5 rounded-full bg-warn" aria-hidden /> : null}
          </button>
        </Popover.Trigger>
        <Popover.Content
          side="top"
          align="end"
          sideOffset={8}
          className="z-50 rounded-lg border border-border bg-surface p-2 text-fg shadow-pop"
          aria-label="Sound settings"
          onEscapeKeyDown={(event) => event.stopPropagation()}
        >
          <div className="flex items-center gap-2">
            <button type="button" className="min-w-12 rounded px-1 py-1 hover:bg-surface-2" aria-label={volume ? 'Mute sound' : 'Unmute sound'} onClick={() => change(volume ? 0 : lastVolume.current)}>
              {volume ? 'Sound' : 'Muted'}
            </button>
            <button type="button" aria-label="Volume down" className="rounded px-1.5 py-1 hover:bg-surface-2" onClick={() => change(volume - 0.05)} disabled={volume === 0}>−</button>
            <input aria-label="Controller sound volume" className="w-24 accent-accent" type="range" min="0" max="1" step="0.05" value={volume} onChange={(e) => change(Number(e.target.value))} />
            <button type="button" aria-label="Volume up" className="rounded px-1.5 py-1 hover:bg-surface-2" onClick={() => change(volume + 0.05)} disabled={volume === 1}>+</button>
            <output className="w-8 text-right tabular-nums" aria-label="Sound volume">{Math.round(volume * 100)}%</output>
          </div>
          {!ready && volume > 0 && <button type="button" className="mt-1 rounded px-1 py-1 text-accent underline" onClick={() => { void audio.current?.enable().catch(() => setError('Audio is unavailable in this browser.')); }}>Enable sound</button>}
          {error && <p role="alert" className="mt-1 max-w-64">{error}</p>}
        </Popover.Content>
      </Popover.Root>
    </div>
  );
}
