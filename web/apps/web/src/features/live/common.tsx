'use client';

import type { BotNow, BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { Code2, Cpu, Drumstick, Heart, Pause } from 'lucide-react';
import { useMemo } from 'react';
import { useHiveTree } from '@/lib/live/hooks';
import { useTicker } from '@/lib/live/ticker';
import { cn, toMs } from '@/lib/utils';
import { Badge, Dot, type Tone } from '@/components/ui/primitives';
import { Tooltip } from '@/components/ui/overlays';

export function flattenHive(node: HiveTreeNode | undefined): HiveTreeNode[] {
  if (!node) return [];
  return [node, ...(node.children ?? []).flatMap(flattenHive)];
}

/** name → hive node (role, owner, brain). */
export function useHiveIndex(): Map<string, HiveTreeNode> {
  const { data } = useHiveTree();
  return useMemo(() => new Map(flattenHive(data).map((n) => [n.name, n])), [data]);
}

export const KIND_LABEL: Record<HiveTreeNode['kind'], string> = {
  main: 'main hive',
  hive: 'hive',
  subhive: 'subhive',
  script: 'script-only',
};

export function statusTone(s: BotStatusView['status'] | undefined): Tone {
  return s === 'online' ? 'ok' : s === 'connecting' ? 'info' : s === 'dead' ? 'danger' : s === 'paused' ? 'warn' : 'neutral';
}

export function StatusDot({ status }: { status: BotStatusView['status'] | undefined }) {
  return (
    <Tooltip content={status ?? 'unknown'}>
      <span className="inline-flex" aria-label={`status ${status ?? 'unknown'}`}>
        <Dot tone={statusTone(status)} pulse={status === 'connecting'} />
      </span>
    </Tooltip>
  );
}

/** Hearts and hunger as two short bars (20 = full). */
export function Vitals({ hp, food, compact }: { hp: number; food: number; compact?: boolean }) {
  const low = hp <= 6;
  return (
    <div className={cn('grid grid-cols-2 gap-3', compact && 'gap-2')}>
      <div className="min-w-0">
        <div className="mb-1 flex items-center justify-between text-[11px]">
          <span className={cn('inline-flex items-center gap-1', low ? 'text-danger' : 'text-muted')}>
            <Heart className={cn('size-3', low && 'fill-current')} /> Health
          </span>
          <span className={cn('tabular font-medium', low ? 'text-danger' : 'text-fg')}>{hp}/20</span>
        </div>
        <div className="h-1.5 overflow-hidden rounded-full bg-surface-3" role="meter" aria-label="health" aria-valuenow={hp} aria-valuemin={0} aria-valuemax={20}>
          <div className={cn('h-full rounded-full transition-[width] duration-500', low ? 'bg-danger' : 'bg-ok')} style={{ width: `${(hp / 20) * 100}%` }} />
        </div>
      </div>
      <div className="min-w-0">
        <div className="mb-1 flex items-center justify-between text-[11px]">
          <span className={cn('inline-flex items-center gap-1', food <= 6 ? 'text-warn' : 'text-muted')}>
            <Drumstick className="size-3" /> Food
          </span>
          <span className="tabular font-medium text-fg">{food}/20</span>
        </div>
        <div className="h-1.5 overflow-hidden rounded-full bg-surface-3" role="meter" aria-label="food" aria-valuenow={food} aria-valuemin={0} aria-valuemax={20}>
          <div className={cn('h-full rounded-full transition-[width] duration-500', food <= 6 ? 'bg-warn' : 'bg-[#c9a43a]')} style={{ width: `${(food / 20) * 100}%` }} />
        </div>
      </div>
    </div>
  );
}

/** A running script as the bot lists and `now` carry it. */
export interface ScriptBrief {
  name: string;
  version?: number | null;
  status?: string | null;
  line?: number | null;
  paused?: boolean | null;
}

export function ScriptBadge({ script, onClick }: { script: ScriptBrief; onClick?: () => void }) {
  const content = (
    <>
      {script.paused ? <Pause className="size-3" /> : <Code2 className="size-3" />}
      <span className="max-w-32 truncate">{script.name}</span>
      {script.version ? <span className="text-subtle">v{script.version}</span> : null}
      {script.paused ? <span>paused</span> : script.line ? <span className="tabular text-subtle">L{script.line}</span> : null}
    </>
  );
  const tone = script.paused ? 'warn' : script.status === 'failed' ? 'danger' : 'info';
  return onClick ? (
    <button onClick={onClick} className="rounded-md focus-visible:outline-2" aria-label={`Script ${script.name}, open debugger`}>
      <Badge tone={tone}>{content}</Badge>
    </button>
  ) : (
    <Badge tone={tone}>{content}</Badge>
  );
}

/** "Thinking 3.2 s · turn · opus" — the elapsed timer ticks locally between `now` frames. */
export function InFlight({ inFlight, devDetails }: { inFlight: NonNullable<BotNow['inFlight']>; devDetails?: boolean }) {
  const now = useTicker(200);
  const since = toMs(inFlight.since) ?? now;
  const s = Math.max(0, (now - since) / 1000);
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-info" role="status">
      <Cpu className="size-3.5 animate-pulse-dot" />
      <span>
        Thinking <span className="tabular font-medium">{s < 60 ? `${s.toFixed(1)} s` : `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`}</span>
      </span>
      <span className="text-subtle">· {inFlight.purpose}</span>
      {devDetails ? <span className="text-subtle">· {inFlight.model}</span> : null}
    </span>
  );
}
