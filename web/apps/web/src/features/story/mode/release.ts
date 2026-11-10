/**
 * A tool's drag released on the map → the request it makes: designations (by area or, for Mine / Deconstruct / Cut /
 * Harvest, exactly the previewed cells), Retry, a zone painted, storage registered, a design or structure queued, water
 * placed, a new home picked.
 */
import { isWaterTool, materialKey, normArea } from '@mineclaw/contracts';
import { errorText } from '@/lib/api/client';
import { growPlantNow } from '../GrowingZones';
import { designationInRect } from '../mapDragDraw';
import { dispatch } from '../model';
import { answerRev, storyWriteError, writeCatchUp } from '../optimistic';
import { holoOf, treeTops, type PendingMark, type PendingOp, type PendingOutcome } from '../pending';
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
import { catchUp } from '../useStory';
import { zoneBody, zoneInto, zonePreview, zoneToast } from '../zones/zonePaint';
import { retryFailed } from './actions';
import type { RunResult, Story } from './commands';
import { releaseMine } from './releaseMine';
import { placeWater } from './water';

type ToolOf<K extends MapTool['kind']> = Extract<MapTool, { kind: K }>;
type Designate = ToolOf<'designate'>;
type HeightCells = NonNullable<DragResult['height']>['cells'];
type V3 = [number, number, number];

const designateTool = (id: DesignateId) => DESIGNATE_TOOLS.find((x) => x.id === id)!;
const countOf = (out: unknown, fallback: number) => (out as { count?: number } | null)?.count ?? fallback;
/** An add asks the colony to leave its targets out of the answer: the page reads only the count and the warnings. */
const quiet = (body: Record<string, unknown>) => (body.op === 'add' ? { ...body, includeTargets: false } : body);

/** What `run` says when an action went through: the head (`after`), who was told, and the answer's warnings. */
export function runText(
  plan: { summary: string },
  out: unknown,
  after?: (out: unknown) => string | null,
): [string, 'warn' | undefined] {
  const head = after?.(out);
  // A designation's or a build's answer may warn (e.g. targets in a preserved zone): said with it.
  const warnings = (out as { warnings?: string[] | null } | null)?.warnings ?? [];
  return [[head, plan.summary, ...warnings].filter(Boolean).join(' · '), warnings.length ? 'warn' : undefined];
}

// ------------------------------------------------------------------ drawn at once (pending.ts)

/** Draws `op` on the map from now on, before the request goes (a Story without `pending`, as in tests, draws nothing). */
function show(c: Story, op: PendingOp | null): string | undefined {
  return op && c.canWrite ? c.pending?.add(op, Date.now()) : undefined;
}

/** The request ended: the colony's answer (an answer without `rev` waits for the catch-up started after it), or its error. */
function settle(c: Story, id: string | undefined, res: RunResult | void): void {
  if (!id || !c.pending) return;
  let outcome: PendingOutcome;
  if (res && !res.ok) outcome = { ok: false, error: res.error };
  else {
    const out = res?.out;
    // The write already started that read (catchUpAfterWrite): settle by it, not by a second one.
    const caughtUp = answerRev(out) === null && c.qc && c.id ? (writeCatchUp(out) ?? catchUp(c.qc, c.id)) : undefined;
    outcome = { ok: true, out, ...(caughtUp ? { caughtUp } : {}) };
  }
  c.pending.settle(id, outcome, Date.now());
}

/**
 * `c.run` with what the command draws shown at once, and its toast said at once from the preview's count. `run` says it
 * again from the answer, which changes it only when the colony counted otherwise, warned, or refused (no retry).
 */
async function runShown(
  c: Story,
  op: PendingOp | null,
  ...[director, directive, verb, after]: Parameters<Story['run']>
) {
  const id = show(c, op);
  if (c.canWrite && c.split) c.say(...runText(dispatch(c.split, director, directive, '', verb), null, after));
  settle(c, id, await c.run(director, directive, verb, after));
}

const marksOf = (kind: string, cells: readonly V3[]): PendingMark[] =>
  cells.map((cell) => ({ cell: [cell[0], cell[1], cell[2]], kind }));

/** The marks a designate drag draws until the colony answers: the previewed cells (Chop: one per tree, on its top log;
 * Haul, Hunt and Forbid: the picked entities). Allow undoes forbids, so it draws nothing. */
function designateOp(tool: DesignateId, r: DragResult): PendingOp | null {
  if (tool === 'allow') return null;
  const t = designateTool(tool);
  const cells: V3[] =
    t.entities && r.entities.length
      ? r.entities.map((e) => [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)])
      : tool === 'chop'
        ? treeTops(r.cells)
        : r.cells;
  return cells.length ? { marks: marksOf(tool, cells) } : null;
}

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
  await runShown(
    c,
    { marks: marksOf(id, cells) },
    { route: 'story.designations', body: quiet(heightBody(id, cells, c.mineFilter)) },
    designationDirective(id, area, cells, c.mineFilter),
    'designated',
    (out) => countLabel(designateTool(id), countOf(out, r.count)),
  );
}

/** Any other designation: the box over the tool's y span (with the entities a hunting / taming tool picked); Cut and
 * Harvest exactly the previewed cells when the preview holds them all. */
async function designateArea(c: Story, tool: Designate, r: DragResult) {
  const t = designateTool(tool.id);
  const area = toolArea(r.rect, toolYRange(tool.id, c.lv, c.build.height, c.cutY));
  const body = designationBody(tool.id, area, c.mineFilter, r);
  if (t.entities && r.entities.length) Object.assign(body, { entities: r.entities.map((e) => e.id) });
  const cancel = tool.id === 'cancel';
  await runShown(
    c,
    designateOp(tool.id, r),
    { route: 'story.designations', body: quiet(body) },
    designationDirective(tool.id, area, r.cells, c.mineFilter),
    cancel ? 'cancelled' : 'designated',
    (out) => (cancel ? `Cancelled ${countOf(out, r.count)}` : countLabel(t, countOf(out, r.count))),
  );
}

async function releaseDesignate(c: Story, tool: Designate, r: DragResult) {
  if (tool.id === 'cancel') {
    const ids = (c.state?.designations ?? []).filter((d) => designationInRect(d, r.rect)).map((d) => d.id);
    // The cancelled marks go at once (only confirmed designations: a pending mark cannot be cancelled yet).
    return runShown(
      c,
      ids.length ? { hide: ids } : null,
      { route: 'story.designations', body: { op: 'cancel', ids } },
      null,
      'cancelled',
      (out) => `Cancelled ${countOf(out, ids.length)}`,
    );
  }
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
  const plant = growPlantNow();
  const body = zoneBody(tool, r.rect, c.lv, plant, into, quarryModeNow(), preservePaintRange());
  // The paint is drawn at once as the colony will make it (a fishing zone waits: its still water is the colony's to find).
  const lv = c.lv;
  const id = show(
    c,
    tool.id === 'fish' && tool.op === 'add'
      ? null
      : { zones: (zones) => zonePreview(zones, tool, r.rect, lv, plant, into)?.zones ?? zones },
  );
  await c.write('story.zones', body).then(
    (out) => {
      settle(c, id, { ok: true, out });
      const warned = !!out.warnings?.length;
      c.say(zoneToast(label, tool, out) + (warned ? ` (${out.warnings!.join('; ')})` : ''), warned ? 'warn' : 'ok');
    },
    (e) => {
      settle(c, id, { ok: false, error: e });
      c.say(storyWriteError(e).text, 'warn');
    },
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
  const placed = placeDesign(tool.blocks, r.a, c.build.facing, !!tool.mine, c.state?.version);
  let items;
  try {
    items = tool.mine ? mineItems(placed, tool.name) : designItems(placed, tool.name);
  } catch (e) {
    c.say(errorText(e), 'warn');
    return;
  }
  // A mine goes one item per request (all in one would pass the 64 KiB body cap); a build's items arrive together.
  if (tool.mine) {
    await releaseMine(c, items, tool.name, placed.length);
    return;
  }
  await runShown(
    c,
    { holo: holoOf(placed) },
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
  await runShown(
    c,
    { holo: holoOf(pv.blocks) },
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
