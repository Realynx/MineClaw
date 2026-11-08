'use client';

/**
 * Settings → Viewer quality: one panel for how every world view of the explorer renders on this device (Live, the bot cards,
 * Controller, the Design Studio). Changes apply at once to every mounted viewer; a small live preview shows the effect and
 * measures the frame rate. Docs: docs/web/architecture.md, "Viewer settings".
 */
import type { CameraMode } from '@mineclaw/world-viewer';
import { Cpu, MonitorSmartphone, RotateCcw } from 'lucide-react';
import { useState } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Button, PageHeader } from '@/components/ui/primitives';
import { Segmented, Switch } from '@/components/ui/overlays';
import { WorldHost, useWorldHost } from '@/features/world/integration';
import { DotMap } from '@/features/world/DotMap';
import { useEntities } from '@/lib/live/hooks';
import { fmtBlocks, fmtScale, Group, SettingRow, Slider, SwitchRow } from './controls';
import { FPS_CAPS, LIMITS, PRESET_LABELS, PRESETS, concreteFields, type Preset } from './schema';
import {
  detectedProfile,
  resetViewerSettings,
  setField,
  setPerfOverlay,
  setPreset,
  useRendererProbe,
  useViewerSettings,
  useViewerStatus,
} from './store';
import { recommendedPreset } from '@mineclaw/world-viewer/settings';
import { GameAssets } from './GameAssets';

export function SettingsPage() {
  return (
    <PageBody className="max-w-5xl">
      <PageHeader title="Settings" subtitle="Saved on this device only: graphics depend on the machine." />
      <GameAssets />
      <ViewerQualitySection />
    </PageBody>
  );
}

function ViewerQualitySection() {
  const s = useViewerSettings();
  const probe = useRendererProbe();
  const status = useViewerStatus();
  const f = concreteFields(s, detectedProfile(probe));
  const set: SetField = (k, v) => setField(k, v, probe);
  const software = status?.software ?? probe?.software ?? null;
  const target = software ? 40 : 60;

  return (
    <section aria-labelledby="viewer-quality" className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1">
          <h2 id="viewer-quality" className="text-base font-semibold text-fg">
            Viewer quality
          </h2>
          <p className="text-sm text-muted">Applies at once to every world view: Live, the bot cards, Controller and the Design Studio.</p>
        </div>
        <Button size="sm" variant="ghost" onClick={resetViewerSettings}>
          <RotateCcw className="size-3.5" /> Reset to defaults
        </Button>
      </div>

      <div className="grid gap-4 lg:grid-cols-[1fr_20rem]">
        <Preview />
        <div className="space-y-3 rounded-xl border border-border bg-surface p-4 shadow-card" data-testid="viewer-status">
          <Measured status={status} />
          <RendererInfo software={software} target={target} renderer={status?.renderer ?? probe?.renderer} />
          <Recommended software={software} preset={s.preset} onUse={(p) => setPreset(p, probe)} />
        </div>
      </div>

      <div className="rounded-xl border border-border bg-surface p-4 shadow-card">
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-sm font-medium text-fg" id="preset-label">
            Preset
          </span>
          <Segmented<Preset>
            label="Quality preset"
            value={s.preset}
            onChange={(p) => setPreset(p, probe)}
            options={PRESETS.map((p) => ({ value: p, label: PRESET_LABELS[p].label }))}
          />
        </div>
        <p className="mt-2 text-xs text-subtle">{PRESET_LABELS[s.preset].hint} Changing any setting below switches to Custom.</p>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <DistanceGroup f={f} set={set} />
        <ResolutionGroup f={f} set={set} target={target} />
        <EffectsGroup f={f} set={set} />
        <OverlaysGroup f={f} set={set} perfOverlay={s.perfOverlay} />
      </div>

      <p className="text-xs text-subtle">
        Some views keep their own limits: the bot-card thumbnails stay small and cheap (40 blocks, a few frames a second), and the
        Design Studio draws its plot up to 96 blocks.
      </p>
    </section>
  );
}

type Fields = ReturnType<typeof concreteFields>;
type SetField = <K extends keyof Fields>(k: K, v: Fields[K]) => void;
type ViewerStatus = ReturnType<typeof useViewerStatus>;

/** The frame rate measured in the open world views. */
function Measured({ status }: { status: ViewerStatus }) {
  return (
    <div>
      <div className="text-[11px] font-semibold tracking-wider text-subtle uppercase">Measured</div>
      <div className="tabular text-2xl font-semibold text-fg" aria-live="polite">
        {status ? `${Math.round(status.fps)} fps` : '—'}
      </div>
      <p className="text-xs text-subtle">
        {status
          ? `${status.frameMs.toFixed(1)} ms a frame · ${fmtScale(status.scale)} resolution · ${Math.round(status.drawFactor * 100)}% distance`
          : 'Open a world view (or the preview) to measure.'}
      </p>
    </div>
  );
}

function RendererBadge({ software }: { software: boolean | null }) {
  if (software === null) return <Badge>checking…</Badge>;
  if (software)
    return (
      <Badge tone="warn">
        <Cpu className="size-3" /> Software WebGL
      </Badge>
    );
  return (
    <Badge tone="ok">
      <MonitorSmartphone className="size-3" /> GPU
    </Badge>
  );
}

function targetText(software: boolean | null, target: number): string {
  if (software === null) return '';
  return software ? `Hardware acceleration is off: the target is ${target} fps.` : `Target ${target} fps.`;
}

/** Software WebGL or a GPU, the renderer's name, and the frame rate it aims for. */
function RendererInfo({ software, target, renderer }: { software: boolean | null; target: number; renderer: string | undefined }) {
  return (
    <div>
      <div className="text-[11px] font-semibold tracking-wider text-subtle uppercase">Renderer</div>
      <div className="mt-1 flex items-center gap-2">
        <RendererBadge software={software} />
      </div>
      <p className="mt-1 truncate text-xs text-subtle" title={renderer}>
        {renderer ?? ''}
      </p>
      <p className="mt-1 text-xs text-muted">{targetText(software, target)}</p>
    </div>
  );
}

/** The preset recommended for this renderer (once it is known), and a way to use it. */
function Recommended({ software, preset, onUse }: { software: boolean | null; preset: Preset; onUse: (p: Preset) => void }) {
  const recommended = software === null ? null : recommendedPreset({ software });
  if (!recommended) return null;
  return (
    <div className="flex items-center justify-between gap-2 border-t border-border pt-3">
      <span className="text-xs text-muted">
        Recommended: <span className="font-medium text-fg">{PRESET_LABELS[recommended].label}</span>
      </span>
      {preset !== recommended ? (
        <Button size="sm" variant="secondary" onClick={() => onUse(recommended)}>
          Use it
        </Button>
      ) : (
        <Badge tone="accent">in use</Badge>
      )}
    </div>
  );
}

function DistanceGroup({ f, set }: { f: Fields; set: SetField }) {
  return (
    <Group title="Distance">
      <Slider
        label="Draw distance"
        hint="How far the world is drawn in the follow and free cameras (Live, the Design Studio)."
        value={f.drawDistance}
        {...LIMITS.drawDistance}
        format={fmtBlocks}
        onChange={(v) => set('drawDistance', v)}
      />
      <Slider
        label="Map ground cover"
        hint="Top-down and angled maps (Controller) draw the ground they show up to this far from the centre."
        value={f.coverCap}
        {...LIMITS.coverCap}
        format={fmtBlocks}
        onChange={(v) => set('coverCap', v)}
      />
      <SettingRow
        label="Entity distance"
        hint="Mobs and players: as far as the ground (they stay on a zoomed-out map; far ones are cheap markers), or only near the centre."
        value={f.entityDistance === 'ground' ? 'same as ground' : fmtBlocks(f.entityDistance)}
      >
        <div className="flex flex-wrap items-center gap-3">
          <Segmented<'ground' | 'limit'>
            label="Entity distance"
            size="sm"
            value={f.entityDistance === 'ground' ? 'ground' : 'limit'}
            onChange={(m) => set('entityDistance', m === 'ground' ? 'ground' : 48)}
            options={[
              { value: 'ground', label: 'Same as ground' },
              { value: 'limit', label: 'Limit' },
            ]}
          />
          {f.entityDistance !== 'ground' ? (
            <input
              type="range"
              aria-label="Entity distance in blocks"
              {...LIMITS.entityDistance}
              value={f.entityDistance}
              onChange={(e) => set('entityDistance', Number(e.target.value))}
              className="min-w-32 flex-1 accent-[var(--accent)]"
            />
          ) : null}
        </div>
      </SettingRow>
      <SwitchRow label="Item drops and XP orbs" hint="Dropped items and experience orbs on the ground." checked={f.drops} onChange={(v) => set('drops', v)} />
    </Group>
  );
}

function ResolutionGroup({ f, set, target }: { f: Fields; set: SetField; target: number }) {
  return (
    <Group title="Resolution and frame rate">
      <Slider
        label="Render scale"
        hint="Pixels drawn per screen pixel: lower is faster and softer; never above the screen's own density."
        value={f.renderScale}
        {...LIMITS.renderScale}
        format={fmtScale}
        onChange={(v) => set('renderScale', v)}
      />
      <SettingRow label="Frame rate cap" hint="Fewer frames leave the machine (and its fans) more room; the world still moves smoothly at 30.">
        <Segmented<string>
          label="Frame rate cap"
          size="sm"
          value={f.fpsCap === null ? 'off' : String(f.fpsCap)}
          onChange={(v) => set('fpsCap', v === 'off' ? null : Number(v))}
          options={[...FPS_CAPS.map((c) => ({ value: String(c), label: `${c}` })), { value: 'off', label: 'Unlimited' }]}
        />
      </SettingRow>
      <SwitchRow
        label="Adaptive quality"
        hint={`Lower resolution, then distance, when frames run slow, to hold ${target} fps; given back when there is headroom.`}
        checked={f.adaptive}
        onChange={(v) => set('adaptive', v)}
      />
    </Group>
  );
}

function EffectsGroup({ f, set }: { f: Fields; set: SetField }) {
  return (
    <Group title="Effects">
      <SwitchRow label="Animated textures" hint="Water, lava, fire and portals move." checked={f.animatedTextures} onChange={(v) => set('animatedTextures', v)} />
      <SwitchRow label="Clouds" hint="The cloud layer in the sky." checked={f.clouds} onChange={(v) => set('clouds', v)} />
      <SwitchRow label="Weather" hint="Rain, snow and the darker rainy sky." checked={f.weather} onChange={(v) => set('weather', v)} />
      <SwitchRow label="Ambient occlusion" hint="Soft shading where blocks meet." checked={f.ambientOcclusion} onChange={(v) => set('ambientOcclusion', v)} />
      <SwitchRow label="Fog" hint="Fades the world out at the edge of the draw distance." checked={f.fog} onChange={(v) => set('fog', v)} />
      <Slider
        label="Detailed models"
        hint="Full animated mob and player models within this (apparent) distance; simple boxes beyond."
        value={f.entityModelDistance}
        {...LIMITS.entityModelDistance}
        format={(v) => (v === 0 ? 'boxes only' : fmtBlocks(v))}
        onChange={(v) => set('entityModelDistance', v)}
      />
    </Group>
  );
}

function OverlaysGroup({ f, set, perfOverlay }: { f: Fields; set: SetField; perfOverlay: boolean }) {
  return (
    <Group title="Overlays">
      <SwitchRow label="Name tags" hint="Names over bots, players, mobs and drops." checked={f.labels} onChange={(v) => set('labels', v)} />
      <SwitchRow label="Chat bubbles" hint="What players and bots say, over their heads." checked={f.bubbles} onChange={(v) => set('bubbles', v)} />
      <SwitchRow label="X-ray silhouettes" hint="Bots and players hidden behind blocks show as outlines." checked={f.xray} onChange={(v) => set('xray', v)} />
      <SwitchRow label="Build holograms" hint="Controller's queued builds as see-through blocks in the world; off: flat outlines on the map." checked={f.holograms} onChange={(v) => set('holograms', v)} />
      <SwitchRow label="Frame rate overlay" hint="A small fps and frame-time readout in the corner of every world view." checked={perfOverlay} onChange={setPerfOverlay} />
    </Group>
  );
}

/** A live look at the world with the current settings (the shared hive runtime: no second WebGL context). */
function Preview() {
  const [on, setOn] = useState(true);
  const [mode, setMode] = useState<CameraMode>('follow');
  return (
    <div className="flex min-h-72 flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-card">
      <div className="flex items-center gap-2 border-b border-border px-3 py-2">
        <span className="text-sm font-medium text-fg">Preview</span>
        <Segmented<CameraMode>
          label="Preview camera"
          size="sm"
          value={mode}
          onChange={setMode}
          options={[
            { value: 'follow', label: 'Follow' },
            { value: 'top', label: 'Map' },
          ]}
        />
        <span className="flex-1" />
        <Switch checked={on} onCheckedChange={setOn} label={<span className="text-xs text-muted">Live</span>} />
      </div>
      <div className="relative min-h-0 flex-1 bg-canvas">
        {on ? (
          <WorldHost>
            <PreviewViewer mode={mode} />
          </WorldHost>
        ) : (
          <div className="absolute inset-0 grid place-items-center text-xs text-muted">Preview paused</div>
        )}
      </div>
    </div>
  );
}

function PreviewViewer({ mode }: { mode: CameraMode }) {
  const { mod, source, ready } = useWorldHost();
  const entities = useEntities();
  const first = entities.data?.bots[0];
  const bot = first?.name ?? null;
  if (!ready || !mod || !source) return bot ? <DotMap follow={bot} radius={30} className="absolute inset-0" label={false} /> : null;
  return (
    <mod.WorldViewer
      source={source}
      dim={first?.dim ?? 0}
      follow={bot}
      cameraMode={mode}
      hideHint
      className="absolute inset-0"
    />
  );
}
