'use client';

/**
 * The world pane's toolbar and overlays (WorldPane.tsx owns the state and the viewer): the camera / layers / quality / go-to /
 * drops / HUD / full screen toolbar, the fallback map, the fullscreen bar, the F3-style position HUD, the drops list and the
 * renderer stats.
 */
import type { WorldEntities } from '@mineclaw/contracts';
import type { CameraMode, LayerToggles, PickedBlock, ViewerStats, XRayMode } from '@mineclaw/world-viewer';
import { Crosshair, Gem, Layers, LocateFixed, Maximize, Minimize, Monitor } from 'lucide-react';
import { Popover } from 'radix-ui';
import type { FormEvent, ReactNode } from 'react';
import { Button, Checkbox, Input, Select } from '@/components/ui/primitives';
import { Segmented, Tooltip } from '@/components/ui/overlays';
import { ViewerQualityButton } from '@/features/settings/QuickSettings';
import { bytes, cn, dimName, num } from '@/lib/utils';
import { DotMap } from './DotMap';

type Bot = WorldEntities['bots'][number];
type Entity = WorldEntities['entities'][number];
export interface NearbyDrop {
  e: Entity;
  d: number;
}

const LAYER_LABELS: Array<[keyof LayerToggles, string]> = [
  ['bots', 'Bots'],
  ['entities', 'Mobs, players, drops'],
  ['claims', 'Claims'],
  ['places', 'Places & structures'],
  ['points', 'Chests, gens, farms, portals'],
  ['plans', 'Build plans (ghosts)'],
  ['dangers', 'Dangers'],
  ['notes', 'Notes'],
  ['bubbles', 'Chat bubbles'],
  ['weather', 'Weather'],
  ['cutaway', 'Roof cut-away'],
  ['staleness', 'Tint unseen areas'],
  ['changes', 'Highlight changed blocks'],
];

function facing(yaw: number | null | undefined): string {
  if (yaw === null || yaw === undefined) return '—';
  // Minecraft yaw: 0 = south (+z), 90 = west, 180 = north, 270 = east
  const dirs = ['south', 'south-west', 'west', 'north-west', 'north', 'north-east', 'east', 'south-east'];
  return dirs[Math.round((((yaw % 360) + 360) % 360) / 45) % 8]!;
}

/** Before the viewer loads (or when it cannot): the top-down dot map of the followed bot, or the empty canvas. */
export function WorldFallback({ follow, ready, className }: { follow?: string; ready: boolean; className?: string }) {
  return (
    <div className={cn('relative size-full', className)}>
      {follow ? <DotMap follow={follow} radius={40} className="absolute inset-0" /> : <div className="absolute inset-0 bg-canvas" />}
      {ready ? (
        <div className="absolute top-3 left-3 max-w-xs rounded-lg border border-border bg-surface px-3 py-2 text-xs text-muted shadow-card">
          The 3D viewer could not load. Showing a top-down map of nearby bots and entities.
        </div>
      ) : null}
    </div>
  );
}

/** The layer toggles, the x-ray mode and the draw distance. */
function LayersPopover(p: {
  layers: Partial<LayerToggles>;
  setLayers: (f: (l: Partial<LayerToggles>) => Partial<LayerToggles>) => void;
  xray: XRayMode;
  setXray: (m: XRayMode) => void;
  distance: number;
  setDistance: (v: number) => void;
}) {
  return (
    <Popover.Root>
      <Popover.Trigger asChild>
        <Button size="sm" variant="ghost">
          <Layers className="size-3.5" /> Layers
        </Button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} className="z-50 w-64 space-y-2 rounded-xl border border-border bg-surface p-4 shadow-pop">
          {LAYER_LABELS.map(([k, label]) => (
            <div key={k}>
              <Checkbox checked={p.layers[k] ?? true} onChange={(v) => p.setLayers((l) => ({ ...l, [k]: v }))} label={label} />
            </div>
          ))}
          <div className="border-t border-border pt-3">
            <label className="mb-1 block text-xs text-muted" htmlFor="xray">
              X-ray toward bots
            </label>
            <Select id="xray" value={p.xray} onChange={(e) => p.setXray(e.target.value as XRayMode)} className="h-8 w-full">
              <option value="auto">Auto (when hidden)</option>
              <option value="on">On</option>
              <option value="off">Off</option>
            </Select>
          </div>
          <div>
            <label className="mb-1 flex justify-between text-xs text-muted" htmlFor="dist">
              Draw distance (all views) <span className="tabular text-fg">{p.distance} blocks</span>
            </label>
            <input id="dist" type="range" min={32} max={256} step={16} value={p.distance} onChange={(e) => p.setDistance(Number(e.target.value))} className="w-full accent-[var(--accent)]" />
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/** The pane's toolbar: camera, layers, viewer quality, go to, drops, HUD, full screen. */
export function WorldToolbar(
  p: React.ComponentProps<typeof LayersPopover> & {
    toolbarStart?: ReactNode;
    takeover?: ReactNode;
    mode: CameraMode;
    onMode: (m: CameraMode) => void;
    gotoText: string;
    setGotoText: (t: string) => void;
    gotoError: string | null;
    onGo: (e: FormEvent) => void;
    drops: boolean;
    toggleDrops: () => void;
    dropCount: number;
    hud: boolean;
    toggleHud: () => void;
    toggleFullscreen: () => void;
  },
) {
  return (
    <div className="flex flex-wrap items-center gap-2 border-b border-border bg-surface px-3 py-2">
      {p.toolbarStart}
      <Segmented<CameraMode>
        label="Camera"
        size="sm"
        value={p.mode}
        onChange={p.onMode}
        options={[
          { value: 'follow', label: 'Follow', title: 'Orbit the followed bot' },
          { value: 'fly', label: 'Fly', title: 'WASD/QE + mouse; Shift fast; wheel sets speed' },
          { value: 'iso', label: 'Iso', title: 'Isometric dolly; Q/E rotate' },
        ]}
      />
      {p.takeover}
      <LayersPopover layers={p.layers} setLayers={p.setLayers} xray={p.xray} setXray={p.setXray} distance={p.distance} setDistance={p.setDistance} />
      <ViewerQualityButton />
      <form onSubmit={p.onGo} className="flex items-center gap-1">
        <Input
          value={p.gotoText}
          onChange={(e) => p.setGotoText(e.target.value)}
          placeholder="Go to place, bot or x,y,z"
          aria-label="Go to a place, a bot or coordinates"
          className="h-8 w-56"
        />
        <Button type="submit" size="icon-sm" variant="ghost" aria-label="Go">
          <LocateFixed className="size-3.5" />
        </Button>
      </form>
      {p.gotoError ? <span className="text-xs text-danger">{p.gotoError}</span> : null}
      <span className="flex-1" />
      <Tooltip content="Drops near the bot">
        <Button size="sm" variant={p.drops ? 'subtle' : 'ghost'} aria-pressed={p.drops} onClick={p.toggleDrops}>
          <Gem className="size-3.5" /> Drops{p.dropCount ? <span className="tabular text-subtle">{p.dropCount}</span> : null}
        </Button>
      </Tooltip>
      <Tooltip content="Position HUD (F3)">
        <Button size="sm" variant={p.hud ? 'subtle' : 'ghost'} aria-pressed={p.hud} onClick={p.toggleHud}>
          <Crosshair className="size-3.5" /> HUD
        </Button>
      </Tooltip>
      <Tooltip content="Full screen (F)">
        <Button size="icon-sm" variant="ghost" aria-label="Full screen" onClick={p.toggleFullscreen}>
          <Maximize className="size-3.5" />
        </Button>
      </Tooltip>
    </div>
  );
}

/** In full screen: the camera switch and the way out (lowered under the selection bar when that shows). */
export function FullscreenBar(p: {
  shown: boolean;
  mode: CameraMode;
  onMode: (m: CameraMode) => void;
  selected: readonly string[];
  marker: unknown;
  escToPage: boolean;
  toggleFullscreen: () => void;
  takeover?: ReactNode;
}) {
  if (!p.shown) return null;
  return (
    <div className="absolute top-3 right-3 z-10 flex gap-1.5" style={{ marginTop: p.selected.length || p.marker ? 32 : 0 }}>
      <Segmented<CameraMode>
        label="Camera"
        size="sm"
        value={p.mode}
        onChange={p.onMode}
        options={[
          { value: 'follow', label: 'Follow' },
          { value: 'fly', label: 'Free cam' },
        ]}
      />
      {p.takeover}
      <Button size="sm" variant="subtle" onClick={p.toggleFullscreen} aria-label="Exit full screen">
        <Minimize className="size-3.5" /> Exit {p.escToPage ? '(Esc twice)' : '(Esc)'}
      </Button>
    </div>
  );
}

function pickedLine(picked: PickedBlock | null): string {
  if (!picked) return 'Click a block to inspect it';
  const meta = picked.meta ? `:${picked.meta}` : '';
  const by = picked.by ? ` · seen by ${picked.by}` : '';
  return `Block ${picked.name}${meta} at ${picked.x},${picked.y},${picked.z} · ${picked.live ? 'live' : 'remembered'}${by}`;
}

/** The F3-style HUD: the followed bot's name, dimension, position and facing, and the last picked block. */
export function PositionHud({ shown, me, picked }: { shown: boolean; me: Bot | undefined; picked: PickedBlock | null }) {
  if (!shown) return null;
  return (
    <div className="pointer-events-none absolute top-3 left-3 space-y-0.5 rounded-md bg-black/55 px-2.5 py-2 font-mono text-[11px] leading-snug text-white">
      {me ? (
        <>
          <div>
            {me.name} · {dimName(me.dim)}
          </div>
          <div>
            XYZ {me.x.toFixed(1)} / {me.y.toFixed(1)} / {me.z.toFixed(1)}
          </div>
          <div>
            Facing {facing(me.yaw)} ({Math.round(me.yaw ?? 0)}°)
          </div>
        </>
      ) : (
        <div>no followed bot</div>
      )}
      <div className="pt-1 text-white/80">{pickedLine(picked)}</div>
    </div>
  );
}

/** The drops near the followed bot, nearest first; a click flies the camera to one. */
export function DropsPanel({ shown, me, drops, onFocus }: { shown: boolean; me: Bot | undefined; drops: readonly NearbyDrop[]; onFocus: (e: Entity) => void }) {
  if (!shown) return null;
  return (
    <div className="absolute top-3 right-3 bottom-3 flex w-64 flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-pop">
      <div className="border-b border-border px-3 py-2 text-xs font-medium text-fg">Drops near {me?.name ?? 'the bot'}</div>
      <ul className="min-h-0 flex-1 overflow-auto py-1 text-xs">
        {drops.length === 0 ? <li className="px-3 py-4 text-center text-muted">No drops nearby.</li> : null}
        {drops.map(({ e, d }) => (
          <li key={`${e.dim}:${e.id ?? `${e.x},${e.z}`}`}>
            <button onClick={() => onFocus(e)} className="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-surface-2">
              <span className="min-w-0 flex-1 truncate text-fg">
                {e.name ?? 'item'} ×{e.count ?? 1}
              </span>
              <span className="tabular text-subtle">{d.toFixed(0)} m</span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The renderer's stats (developer details; nothing before the first, or with them off). */
export function StatsHud({ stats }: { stats: ViewerStats | null }) {
  if (!stats) return null;
  return (
    <div className="pointer-events-none absolute bottom-3 left-3 rounded-md bg-black/55 px-2.5 py-2 font-mono text-[11px] leading-snug text-white">
      <div className="flex items-center gap-1.5">
        <Monitor className="size-3" /> {stats.fps.toFixed(0)} fps · {stats.frameMs.toFixed(1)} ms · {Math.round(stats.resolutionScale * 100)}% res
      </div>
      <div>
        {num(stats.drawCalls)} draws · {num(stats.triangles)} tris · {stats.textured ? 'textured' : 'flat'} · {stats.version}
      </div>
      <div>
        {num(stats.memory.columns)} columns · {num(stats.memory.sections)} sections ({num(stats.memory.sectionsQueued)} queued) · {stats.memory.views} views
      </div>
      <div>
        GPU {bytes(stats.memory.gpuBytes)} · worker {bytes(stats.memory.workerBytes)} · received {bytes(stats.memory.bytesReceived)}
      </div>
      <div className="text-white/70">{stats.gpu}</div>
    </div>
  );
}
