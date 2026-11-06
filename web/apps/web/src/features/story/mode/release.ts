/**
 * A tool's drag released on the map → the request it makes: designations (by area or, for Mine / Deconstruct, exactly the
 * previewed cells), Retry, a zone painted, storage registered, a design or structure queued, water placed, a new home picked.
 */
import { isWaterTool, materialKey, normArea } from '@mineclaw/contracts';
import { errorText } from '@/lib/api/client';
import { growPlantNow } from '../GrowingZones';
import type { DragResult, MapTool } from '../StoryMap';
import {
  buildItem,
  buildItems,
  cellsArea,
  countLabel,
  DESIGNATE_TOOLS,
  type DesignateId,
  designationBody,
  designationDirective,
  designItems,
  failedByBuild,
  failedCells,
  hasHeight,
  heightBody,
  MAX_DRAG_CELLS,
  mineItems,
  placeDesign,
  preview,
  releaseIsNoop,
  stalledIn,
  toolArea,
  toolYRange,
  WATER_TOOLS,
  ZONE_TOOLS,
} from '../tools';
import { preservePaintRange } from '../zones/preserve';
import { quarryModeNow } from '../zones/quarry';
import { zoneBody, zoneInto, zoneToast } from '../zones/zonePaint';
import { retryFailed } from './actions';
import type { Story } from './commands';
import { placeWater } from './water';

type ToolOf<K extends MapTool['kind']> = Extract<MapTool, { kind: K }>;
type Designate = ToolOf<'designate'>;
type HeightCells = NonNullable<DragResult['height']>['cells'];

const designateTool = (id: DesignateId) => DESIGNATE_TOOLS.find((x) => x.id === id)!;
const countOf = (out: unknown, fallback: number) => (out as { count?: number } | null)?.count ?? fallback;

/** A click (under the drag threshold) paints no zone, and a box with nothing in it designates nothing: no request. */
function isNoop(c: Story, tool: MapTool, r: DragResult): boolean {
  const kind = tool.kind === 'designate' || tool.kind === 'zone' || tool.kind === 'storage' ? tool.kind : 'other';
  if (!releaseIsNoop(kind, r.moved ?? true, r.count)) return false;
  if (tool.kind === 'designate' && r.moved)
    c.say(`${designateTool(tool.id).label}: nothing ${hasHeight(tool.id) ? 'at that height ' : ''}in the box`, 'warn');
  return true;
}

/** Failed or resting designations and failed build cells under the box (any height) are tried again now; nothing new. */
async function retryBox(c: Story, r: DragResult) {
  const rect = [r.rect[0], r.rect[1], r.rect[2], r.rect[3]] as const;
  const ids = stalledIn(c.state?.designations ?? [], rect);
  const cells = failedByBuild(failedCells(c.state?.ghosts?.blocks ?? [], rect));
  if (!ids.length && !cells.size) return c.say('No failed or resting designations or build cells there');
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  await retryFailed(c, ids, [...cells.values()]);
}

/** Mine / Deconstruct: exactly the cells the preview showed (its height mode), as blocks; never expanded deeper. */
async function designateCells(c: Story, id: 'mine' | 'deconstruct', r: DragResult, cells: HeightCells) {
  const area = cellsArea(cells, toolArea(r.rect, [c.lv, c.lv]));
  await c.run(
    { route: 'story.designations', body: heightBody(id, cells, c.mineFilter) },
    designationDirective(id, area, cells, c.mineFilter),
    'designated',
    (out) => countLabel(designateTool(id), countOf(out, r.count)),
  );
}

/** Any other designation: the box over the tool's y span (with the entities a hunting / taming tool picked). */
async function designateArea(c: Story, tool: Designate, r: DragResult) {
  const t = designateTool(tool.id);
  const area = toolArea(r.rect, toolYRange(tool.id, c.lv, c.build.height, c.cutY));
  const body = designationBody(tool.id, area, c.mineFilter);
  if (t.entities && r.entities.length) Object.assign(body, { entities: r.entities.map((e) => e.id) });
  const cancel = tool.id === 'cancel';
  await c.run(
    { route: 'story.designations', body },
    designationDirective(tool.id, area, r.cells, c.mineFilter),
    cancel ? 'cancelled' : 'designated',
    (out) => (cancel ? `Cancelled ${countOf(out, r.count)}` : countLabel(t, countOf(out, r.count))),
  );
}

async function releaseDesignate(c: Story, tool: Designate, r: DragResult) {
  if (tool.id === 'retry') return retryBox(c, r);
  if (hasHeight(tool.id) && r.height) return designateCells(c, tool.id, r, r.height.cells);
  return designateArea(c, tool, r);
}

/** RimWorld painting (zones/zonePaint.ts): erase sends the kind (or none: any); Shift extends the zone the drag started in. */
async function releaseZone(c: Story, tool: ToolOf<'zone'>, r: DragResult, shift: boolean) {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  const into = zoneInto(tool, c.state?.zones ?? [], r.a, shift);
  const label =
    into !== undefined ? 'Zone extended' : `${ZONE_TOOLS.find((z) => z.id === tool.id)?.label ?? 'Zone'} painted`;
  const body = zoneBody(tool, r.rect, c.lv, growPlantNow(), into, quarryModeNow(), preservePaintRange());
  await c.write('story.zones', body).then(
    (out) => {
      const warned = !!out.warnings?.length;
      c.say(zoneToast(label, tool, out) + (warned ? ` (${out.warnings!.join('; ')})` : ''), warned ? 'warn' : 'ok');
    },
    (e) => c.say(errorText(e), 'warn'),
  );
}

/** Every chest in the box (a few blocks below the level to well above it) becomes storage. */
async function releaseStorage(c: Story, r: DragResult) {
  const area = normArea([r.rect[0], c.lv - 4, r.rect[1], r.rect[2], c.lv + 8, r.rect[3]]);
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  await c.write('story.storage', { op: 'assign_area', area }).then(
    (out) => {
      const n = out.count ?? out.chests?.length ?? 0;
      c.say(`${n} chest${n === 1 ? '' : 's'} registered as storage`);
    },
    (e) => c.say(errorText(e), 'warn'),
  );
}

/** A design (or a mine design) queued where it was clicked, turned to the build facing. */
async function releaseDesign(c: Story, tool: ToolOf<'design'>, r: DragResult) {
  const placed = placeDesign(tool.blocks, r.a, c.build.facing, !!tool.mine);
  const items = tool.mine ? mineItems(placed, tool.name) : designItems(placed, tool.name);
  await c.run(
    { route: 'story.builds', body: { op: 'add', items } },
    { kind: 'build', build: items },
    'queued',
    () => `Queued ${tool.name}: ${placed.length} blocks`,
  );
}

/** A structure (checked against the stock first), or water (checked by the director). */
async function releaseBuild(c: Story, tool: ToolOf<'build'>, r: DragResult) {
  if (isWaterTool(tool.id))
    return placeWater(
      c,
      buildItems(tool.id, r.a, r.b, c.build),
      WATER_TOOLS.find((t) => t.id === tool.id)?.label ?? 'Water',
    );
  const item = buildItem(tool.id, r.a, r.b, c.build);
  // A turned single block keeps its stock key (the item, not the facing meta).
  const pv = preview(
    item,
    c.stock.get(materialKey(item.kind === 'block' ? c.build.material : item.material).join(':')),
  );
  if (!pv.valid) return c.say(pv.reason ?? 'cannot place that', 'warn');
  await c.run(
    { route: 'story.builds', body: { op: 'add', items: [item] } },
    { kind: 'build', build: [item] },
    'queued',
    () => `Queued: ${pv.blocks.length} block${pv.blocks.length === 1 ? '' : 's'}`,
  );
}

/** "Move home here": the clicked column waits for the confirm in the Colony tab. */
function releaseHome(c: Story, r: DragResult) {
  c.setMigrate([r.a[0], c.lv, r.a[2]]);
  c.setTab('colony');
}

/** What each tool does with a released drag (the select tool: nothing). */
function releaseFor(c: Story, tool: MapTool, r: DragResult, shift: boolean): Promise<unknown> | void {
  switch (tool.kind) {
    case 'designate':
      return releaseDesignate(c, tool, r);
    case 'home':
      return releaseHome(c, r);
    case 'zone':
      return releaseZone(c, tool, r, shift);
    case 'storage':
      return releaseStorage(c, r);
    case 'design':
      return releaseDesign(c, tool, r);
    case 'build':
      return releaseBuild(c, tool, r);
  }
}

/** A drag released with the tool in hand (Shift keeps the tool; zone tools always stay in hand, Shift extends a zone). */
export async function releaseTool(c: Story, r: DragResult, shift: boolean) {
  const { tool } = c;
  if (isNoop(c, tool, r)) return;
  if (tool.kind === 'designate' && r.height?.tooMany)
    return c.say(`Too many blocks (max ${MAX_DRAG_CELLS}): drag a smaller box or a shallower mode`, 'warn');
  if (!shift && tool.kind !== 'zone') c.setTool({ kind: 'select' });
  await releaseFor(c, tool, r, shift);
}
