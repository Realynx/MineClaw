'use client';

/**
 * Quick viewer settings from a viewer's own toolbar (Live world pane, Story Mode's controls): the preset, how far mobs and
 * players are drawn, the fps overlay and the measured frame rate, with a link to the full panel (/settings).
 */
import { Gauge } from 'lucide-react';
import Link from 'next/link';
import { useContext } from 'react';
import { Popover } from 'radix-ui';
import { Button } from '@/components/ui/primitives';
import { PortalContainer, Segmented, Switch } from '@/components/ui/overlays';
import { cn } from '@/lib/utils';
import { PRESET_LABELS, concreteFields, type Preset } from './schema';
import { detectedProfile, setField, setPerfOverlay, setPreset, useRendererProbe, useViewerSettings, useViewerStatus } from './store';

const QUICK: Preset[] = ['auto', 'low', 'medium', 'high'];

export function ViewerQualityButton({ compact, className }: { compact?: boolean; className?: string }) {
  const container = useContext(PortalContainer) ?? undefined; // inside a full-screen view (Story Mode), else document.body
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        {compact ? (
          <button
            type="button"
            className={cn('grid size-6 place-items-center rounded hover:bg-surface-2', className)}
            title="Viewer quality"
            aria-label="Viewer quality"
          >
            <Gauge className="size-3.5" />
          </button>
        ) : (
          <Button size="sm" variant="ghost" className={className} aria-label="Viewer quality">
            <Gauge className="size-3.5" /> Quality
          </Button>
        )}
      </Popover.Trigger>
      <Popover.Portal container={container}>
        <Popover.Content
          align="start"
          sideOffset={6}
          className="z-50 w-72 space-y-3 rounded-xl border border-border bg-surface p-4 text-fg shadow-pop"
          aria-label="Viewer quality"
        >
          <QuickBody />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function QuickBody() {
  const s = useViewerSettings();
  const probe = useRendererProbe();
  const status = useViewerStatus();
  const f = concreteFields(s, detectedProfile(probe));
  return (
    <>
      <div className="flex items-center justify-between">
        <span className="text-sm font-semibold">Viewer quality</span>
        <span className="tabular text-xs text-muted" aria-live="polite">
          {status ? `${Math.round(status.fps)} fps${status.software ? ' · software' : ''}` : ''}
        </span>
      </div>
      <div className="space-y-1">
        <Segmented<Preset>
          label="Quality preset"
          size="sm"
          value={s.preset}
          onChange={(p) => setPreset(p, probe)}
          options={(s.preset === 'custom' ? [...QUICK, 'custom' as const] : QUICK).map((p) => ({ value: p, label: PRESET_LABELS[p].label }))}
        />
      </div>
      <div className="space-y-1">
        <div className="text-xs text-muted">Mobs and players</div>
        <Segmented<'ground' | 'near'>
          label="Entity distance"
          size="sm"
          value={f.entityDistance === 'ground' ? 'ground' : 'near'}
          onChange={(m) => setField('entityDistance', m === 'ground' ? 'ground' : 48, probe)}
          options={[
            { value: 'ground', label: 'Same as ground' },
            { value: 'near', label: 'Near only' },
          ]}
        />
      </div>
      <Switch checked={s.perfOverlay} onCheckedChange={setPerfOverlay} label={<span className="text-sm">Frame rate overlay</span>} />
      <div className="border-t border-border pt-3">
        <Link href="/settings" className="text-sm font-medium text-accent hover:underline">
          All viewer settings →
        </Link>
      </div>
    </>
  );
}
