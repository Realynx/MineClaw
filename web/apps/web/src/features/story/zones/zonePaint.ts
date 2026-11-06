/**
 * The Zones tool's requests and live preview (docs/web/story-mode.md "Zones"): what a paint or an erase sends, which zone a
 * Shift-drag extends, and the resulting cells computed with the same rules as the bot (`paintZones` / `eraseZones` in contracts).
 */
import {
  cellKey,
  describeZonePaint,
  eraseZones,
  isOverlayZone,
  paintZones,
  rectCells,
  waterColumns,
  zoneCellSet,
  type StoryAck,
  type StoryArea,
  type StoryZone,
  type StoryZoneRequest,
  type ZonePaintOut,
  type ZonePaintResult,
} from '@mineclaw/contracts';

/** The Zones tool: add paints `id` (a kind); remove erases zones of `id` (a kind, or `any`). `into` extends one zone. */
export interface ZoneTool {
  kind: 'zone';
  id: string;
  op: 'add' | 'remove';
  into?: string | number;
}

export const ERASE_ANY = 'any';

/** The still-water columns of a block scan (a fishing zone's preview). */
export { waterColumns };

/** How many cells (columns) a zone has. */
export const zoneCellCount = (z: StoryZone): number => z.cells ?? zoneCellSet(z).size;

/** [x1, z1, x2, z2] of a drag. */
export type ZoneRect = readonly [number, number, number, number];

/**
 * The zone a paint extends: the tool's own target (Extend from a zone list), else on Shift the zone of the tool's kind the drag
 * started in (growing zones of any plant: the plant comes from that zone). Nothing for erase.
 */
export function zoneInto(
  tool: ZoneTool,
  zones: readonly StoryZone[],
  start: readonly number[] | null | undefined,
  shift: boolean,
): string | number | undefined {
  if (tool.op !== 'add') return undefined;
  if (tool.into !== undefined) return tool.into;
  if (!shift || !start) return undefined;
  const k = cellKey(start[0]!, start[2]!);
  return zones.find((z) => z.kind === tool.id && zoneCellSet(z).has(k))?.id;
}

/** The request a released drag sends (a Preserve zone: the `range` it keeps under and over the level). */
export function zoneBody(
  tool: ZoneTool,
  rect: ZoneRect,
  level: number,
  plant: string | null,
  into: string | number | undefined,
  mode?: 'quarry' | 'ores',
  range?: { below: number; above: number },
): StoryZoneRequest {
  const area: StoryArea = [
    Math.min(rect[0], rect[2]),
    level,
    Math.min(rect[1], rect[3]),
    Math.max(rect[0], rect[2]),
    level,
    Math.max(rect[1], rect[3]),
  ];
  if (tool.op === 'remove')
    return tool.id === ERASE_ANY ? { op: 'remove', area } : { op: 'remove', kind: tool.id as never, area };
  if (into !== undefined) return { op: 'add', area, into };
  return {
    op: 'add',
    kind: tool.id as never,
    area,
    ...(tool.id === 'farm' && plant ? { plant } : {}),
    ...(tool.id === 'mine' && mode ? { mode } : {}),
    ...(tool.id === 'preserve' && range ? { below: range.below, above: range.above } : {}),
  };
}

let seq = 0;
/** What the paint or erase would make of the colony's zones (null when the director would refuse it). */
export function zonePreview(
  zones: readonly StoryZone[],
  tool: ZoneTool,
  rect: ZoneRect,
  level: number,
  plant: string | null,
  into: string | number | undefined,
  water?: ReadonlySet<number> | null,
): ZonePaintOut<StoryZone> | null {
  const newId = () => `~preview${++seq}`;
  const r = [
    Math.min(rect[0], rect[2]),
    Math.min(rect[1], rect[3]),
    Math.max(rect[0], rect[2]),
    Math.max(rect[1], rect[3]),
  ] as const;
  try {
    if (tool.op === 'remove') return eraseZones(zones, tool.id === ERASE_ANY ? null : tool.id, r, newId);
    return paintZones(
      zones,
      {
        kind: tool.id,
        plant: tool.id === 'farm' ? plant : null,
        rect: r,
        y: [level, level],
        into,
        // A fishing zone keeps only still water: the scanned water cells of the rectangle.
        cells:
          tool.id === 'fish' && water
            ? new Set([...rectCells(r[0], r[1], r[2], r[3])].filter((k) => water.has(k)))
            : null,
      },
      (kind, p) => ({ id: newId(), kind, area: [r[0], level, r[1], r[2], level, r[3]], plant: p }),
      newId,
    );
  } catch {
    return null;
  }
}

/** The preview's line under the cursor: the cells and what happens to the zones. */
export function previewLabel(tool: ZoneTool, out: ZonePaintOut<StoryZone> | null, cells: number): string {
  if (!out) return tool.id === 'fish' && tool.op === 'add' ? 'no still water here' : 'too large';
  if (tool.op === 'remove') {
    const n = out.removedCells.size;
    return n ? `erase ${n} cell${n === 1 ? '' : 's'} · ${describeZonePaint(out.result)}` : 'nothing to erase here';
  }
  const overlay = isOverlayZone(tool.id)
    ? ' · overlay: work zones stay'
    : tool.id === 'fish'
      ? ' · still water only'
      : '';
  return `${cells} cell${cells === 1 ? '' : 's'} · ${describeZonePaint(out.result, out.removedCells.size)}${overlay}`;
}

/** The toast after the director answered. */
export function zoneToast(label: string, tool: ZoneTool, ack: StoryAck): string {
  const r: ZonePaintResult = {
    created: ack.created ?? [],
    merged: ack.merged ?? [],
    grown: ack.grown ?? [],
    shrunk: ack.shrunk ?? [],
    split: ack.split ?? [],
    deleted: ack.deleted ?? ack.removed ?? [],
  };
  return `${tool.op === 'remove' ? 'Erased' : label}: ${describeZonePaint(r)}`;
}
