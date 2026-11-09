'use client';

import { useEffect, useRef, useState, type RefObject } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { WorldEntities } from '@mineclaw/contracts';
import type { ControllerAudio, EntityEvent, WorldViewerHandle } from '@mineclaw/world-viewer';
import { loadWorldViewer } from '@/features/world/integration';
import { subscribeEntityFrames } from '@/features/world/entityFrames';
import { liveKeys } from '@/lib/live/dispatch';

const KEY = 'mineclaw.controller.volume';
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
      className="absolute right-3 bottom-3 z-10 rounded-lg border border-border bg-surface/95 p-2 text-xs shadow-sm"
      data-controller-menu="sound"
      title="Uses sounds imported in Settings → Minecraft textures and sounds."
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
    </div>
  );
}
