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
    [open, setOpen] = useState(false),
    [error, setError] = useState('');
  useEffect(() => {
    let alive = true;
    void loadWorldViewer().then((module) => {
      if (!alive || !module) return;
      const player = new module.ControllerAudio(() => {
        const engine = viewer.current?.engine();
        if (!engine) return null;
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
      });
      audio.current = player;
      try {
        const saved = localStorage.getItem(KEY);
        const v = saved === null ? 0.6 : Number(saved);
        if (Number.isFinite(v)) {
          const clamped = Math.max(0, Math.min(1, v));
          setVolume(clamped);
          player.setVolume(clamped);
        }
      } catch {
        /* storage unavailable */
      }
    });
    const enable = () => {
      if (viewer.current?.engine()?.surface.isConnected)
        void audio.current?.enable().catch(() => setError('Click Sound to enable browser audio.'));
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
    setVolume(value);
    audio.current?.setVolume(value);
    try {
      localStorage.setItem(KEY, String(value));
    } catch {
      /* optional persistence */
    }
  };
  return (
    <div
      className="absolute right-3 bottom-3 z-10 rounded-lg border border-border bg-surface/95 p-2 text-xs shadow-sm"
      data-controller-menu="sound"
    >
      <button
        type="button"
        aria-expanded={open}
        onClick={() => {
          setOpen(!open);
          void audio.current?.enable().catch(() => setError('Audio is unavailable in this browser.'));
        }}
        className="px-1 py-0.5"
      >
        {volume ? 'Sound' : 'Muted'}
      </button>
      {open && (
        <div className="mt-2 max-w-56 space-y-2">
          <label className="flex items-center gap-2">
            Volume{' '}
            <input
              aria-label="Controller sound volume"
              type="range"
              min="0"
              max="1"
              step="0.05"
              value={volume}
              onChange={(e) => change(Number(e.target.value))}
            />
          </label>
          <button type="button" onClick={() => change(volume ? 0 : 0.6)}>
            {volume ? 'Mute' : 'Unmute'}
          </button>
          <p className="text-subtle">Uses sounds imported in Settings → Minecraft textures and sounds.</p>
          {error && <p role="alert">{error}</p>}
        </div>
      )}
    </div>
  );
}
