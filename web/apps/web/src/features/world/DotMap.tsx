'use client';

import type { WorldEntities } from '@mineclaw/contracts';
import { useEffect, useRef } from 'react';
import { useEntities } from '@/lib/live/hooks';
import { cn } from '@/lib/utils';

function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback;
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

/** Draws a top-down map of bots and entities around one bot. Pure canvas drawing (testable by calling with a context). */
export function drawDotMap(ctx: CanvasRenderingContext2D, w: number, h: number, data: WorldEntities | undefined, follow: string, radius: number) {
  const bg = cssVar('--canvas-bg', '#0a0c0f');
  const grid = cssVar('--border', '#252a33');
  const accent = cssVar('--accent', '#3ad19a');
  const muted = cssVar('--text-subtle', '#6f7884');
  const info = cssVar('--info', '#6aa4ff');
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, w, h);
  const me = data?.bots.find((b) => b.name === follow || b.short === follow);
  if (!me) {
    ctx.fillStyle = muted;
    ctx.font = '12px system-ui';
    ctx.textAlign = 'center';
    ctx.fillText('no position yet', w / 2, h / 2);
    return;
  }
  const scale = Math.min(w, h) / (radius * 2);
  const toX = (x: number) => w / 2 + (x - me.x) * scale;
  const toY = (z: number) => h / 2 + (z - me.z) * scale;
  // chunk grid
  ctx.strokeStyle = grid;
  ctx.lineWidth = 1;
  ctx.globalAlpha = 0.6;
  const x0 = Math.floor((me.x - radius) / 16) * 16;
  const z0 = Math.floor((me.z - radius) / 16) * 16;
  for (let x = x0; x <= me.x + radius; x += 16) {
    ctx.beginPath();
    ctx.moveTo(Math.round(toX(x)) + 0.5, 0);
    ctx.lineTo(Math.round(toX(x)) + 0.5, h);
    ctx.stroke();
  }
  for (let z = z0; z <= me.z + radius; z += 16) {
    ctx.beginPath();
    ctx.moveTo(0, Math.round(toY(z)) + 0.5);
    ctx.lineTo(w, Math.round(toY(z)) + 0.5);
    ctx.stroke();
  }
  ctx.globalAlpha = 1;
  for (const e of data?.entities ?? []) {
    if (e.dim !== me.dim) continue;
    const px = toX(e.x);
    const py = toY(e.z);
    if (px < -4 || py < -4 || px > w + 4 || py > h + 4) continue;
    ctx.fillStyle = e.kind === 'player' ? info : muted;
    ctx.beginPath();
    ctx.arc(px, py, e.kind === 'player' ? 3.5 : e.kind === 'item' ? 1.5 : 2.5, 0, Math.PI * 2);
    ctx.fill();
  }
  for (const b of data?.bots ?? []) {
    if (b.dim !== me.dim || b.name === me.name) continue;
    ctx.fillStyle = accent;
    ctx.globalAlpha = 0.6;
    ctx.beginPath();
    ctx.arc(toX(b.x), toY(b.z), 3, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
  }
  // the followed bot, with its heading (MC yaw 0 = south/+z)
  const yaw = ((me.yaw ?? 0) * Math.PI) / 180;
  ctx.save();
  ctx.translate(w / 2, h / 2);
  ctx.rotate(yaw);
  ctx.fillStyle = accent;
  ctx.beginPath();
  ctx.moveTo(0, 8);
  ctx.lineTo(5, -4);
  ctx.lineTo(0, -1);
  ctx.lineTo(-5, -4);
  ctx.closePath();
  ctx.fill();
  ctx.restore();
}

/** Lightweight stand-in for the 3D view while @mineclaw/world-viewer is absent: a live 2D dot map from entity positions. */
export function DotMap({ follow, radius = 24, className, label = true }: { follow: string; radius?: number; className?: string; label?: boolean }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const { data } = useEntities();
  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const draw = () => {
      const dpr = window.devicePixelRatio || 1;
      const w = canvas.clientWidth;
      const h = canvas.clientHeight;
      if (!w || !h) return;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      drawDotMap(ctx, w, h, data, follow, radius);
    };
    draw();
    const ro = new ResizeObserver(draw);
    ro.observe(canvas);
    return () => ro.disconnect();
  }, [data, follow, radius]);
  return (
    <div className={cn('overflow-hidden bg-canvas', className)}>
      <div className="relative size-full">
        <canvas ref={ref} className="absolute inset-0 size-full" role="img" aria-label={`Map around ${follow}`} />
        {label ? <span className="absolute right-1.5 bottom-1 text-[10px] text-subtle">map · {radius * 2} blocks</span> : null}
      </div>
    </div>
  );
}
