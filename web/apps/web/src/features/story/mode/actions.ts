/**
 * Story Mode's actions (each takes the screen's `Story` object): bringing pawns online, retrying failed work, attack / gear /
 * formation orders, jumping to what an alert or a message names, founding a colony and moving its home, zone bills, a zone's
 * Preserve.
 */
import type { StoryAlert, StoryGearOp, StoryOnlineBlocked, StoryZone } from '@mineclaw/contracts';
import { sendDirective } from '@/features/world/directives';
import { ApiError, api, errorText } from '@/lib/api/client';
import { attackSpec, gearBody, isConfirmed, needsConfirm, type AttackWho } from '../assign/gear';
import type { GrowBillChange } from '../GrowingZones';
import { EMPTY_SELECTION, type Selection } from '../model';
import type { Condition } from '../notices';
import { storyWriteError } from '../optimistic';
import type { JumpTarget } from '../StoryNotices';
import type { FormationMove } from '../StoryMap';
import { failedCells, stalledIn } from '../tools';
import { preserveBody, preserveSaid, type PreserveChange } from '../zones/preserve';
import type { Story } from './commands';
import type { V3 } from './state';

type Xyz = { x: number; y: number; z: number };

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** The handler that shows a failed write as a warning toast (a 503 `busy` says the colony did not save it). */
export const warnOn =
  (c: Pick<Story, 'say'>) =>
  (e: unknown): void =>
    c.say(storyWriteError(e).text, 'warn');

/** "Bring online"'s toast. */
function onlineText(started: number, blocked: StoryOnlineBlocked[], overCap: boolean): [string, 'ok' | 'warn'] {
  if (blocked.length)
    return [
      `${started ? `${started} coming online; ` : ''}${blocked.length} can't: ${blocked[0]!.reason}`,
      started ? 'ok' : 'warn',
    ];
  if (!started) return ['Nobody is offline', 'ok'];
  return [`${plural(started, 'pawn')} coming online${overCap ? ' (on the regulars’ seats)' : ''}`, 'ok'];
}

/** "Bring online": these pawns (null = every offline one) are whitelisted again and started, the cap permitting. */
export async function bringOnline(c: Story, names: string[] | null, overCap = false): Promise<StoryOnlineBlocked[]> {
  if (!c.canWrite) {
    c.say('Needs the story role', 'warn');
    return [];
  }
  try {
    const out = await c.write('story.online', { pawns: names, ...(overCap ? { overCap: true } : {}) });
    const asked = names ?? c.offlineNames;
    c.setBlocked((cur) => [...cur.filter((b) => !asked.includes(b.pawn)), ...out.blocked]);
    c.say(...onlineText(out.started.length, out.blocked, overCap));
    return out.blocked;
  } catch (e) {
    c.say(errorText(e), 'warn');
    return [];
  }
}

/** The last "Bring online" refusals of pawns still offline. */
export const stillBlocked = (c: Story) => c.blocked.filter((b) => c.offlineNames.includes(b.pawn));

/** A pawn's shown name. */
export const displayOf = (c: Story, account: string) =>
  c.state?.pawns.find((p) => p.name === account)?.display ?? account;

type RetryBuild = { id: string | number; cells?: Array<[number, number, number]> };

/** "N designations and M build cells" (empty when neither). */
function retryParts(ids: number, cells: number): string {
  return [ids ? plural(ids, 'designation') : '', cells ? plural(cells, 'build cell') : '']
    .filter(Boolean)
    .join(' and ');
}

/** The retry routes: the designations, then every failed build (`'all'`) or these builds' cells. */
async function retryWrites(c: Story, ids: Array<string | number>, builds: RetryBuild[] | 'all') {
  if (ids.length) await c.write('story.designations', { op: 'retry', ids });
  if (builds === 'all') return [await c.write('story.builds', { op: 'retry_all' } as never)];
  return Promise.all(
    builds.map((b) =>
      c.write('story.builds', { op: 'retry', id: b.id, ...(b.cells ? { cells: b.cells } : {}) } as never),
    ),
  );
}

/**
 * Retry (docs/web/story-mode.md "Retrying failed build cells"): designations and failed build cells together; a build whose
 * reason still holds says it at once.
 */
export async function retryFailed(c: Story, ids: Array<string | number>, builds: RetryBuild[] | 'all') {
  try {
    const outs = await retryWrites(c, ids, builds);
    const cells = outs.reduce((n, o) => n + (o.reopened ?? 0), 0);
    const still = outs.flatMap((o) => o.reasons ?? []);
    const parts = retryParts(ids.length, cells);
    if (still.length)
      c.say(`${parts ? `Trying ${parts} again; ` : ''}build ${still[0]!.id} still can't: ${still[0]!.reason}`, 'warn');
    else c.say(parts ? `Trying ${parts} again now` : 'Nothing failed there');
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}

/** A player or another bot not yet confirmed this session (and not confirmed just now). */
const mustAsk = (who: AttackWho, confirmed: boolean) => needsConfirm(who) && !confirmed && !isConfirmed(who);

/**
 * An attack order for these pawns. A player or another bot needs the owner's confirm once per target per session: asked
 * here first, and again when the bot answers 409 (it insists on `confirm: true` too).
 */
export async function attack(
  c: Story,
  who: AttackWho,
  pawns: string[],
  queue: boolean,
  confirmed = false,
): Promise<void> {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  if (mustAsk(who, confirmed)) {
    c.setConfirmAttack({ who, pawns, queue });
    return;
  }
  try {
    await c.write('story.orders', {
      pawns,
      order: attackSpec(who, needsConfirm(who)),
      ...(queue ? { queue: true } : {}),
    });
    c.say(`${plural(pawns.length, 'pawn')} attacking ${who.name}`);
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) c.setConfirmAttack({ who, pawns, queue });
    else c.say(errorText(e), 'warn');
  }
}

/** One bulk gear op for the selected pawns; the director's summary is the toast. */
export async function gear(c: Story, op: StoryGearOp, pawns: string[], item?: [number, number], count?: number) {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  if (!pawns.length) return c.say('Select pawns first', 'warn');
  try {
    const out = await c.write('story.gear', gearBody(op, pawns, item, count));
    const none = out.results.length > 0 && !out.results.some((r) => r.got.length);
    c.say(out.summary || 'Done', none ? 'warn' : 'ok');
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}

/** A bot from before `moves` refuses them so (the formation then goes one order per pawn). */
const oldBotRefusal = (e: unknown) => e instanceof ApiError && e.status === 400 && /pawns required/.test(e.message);

/** A right-drag line: each drafted pawn moves to its own cell on it (drafted pawns then hold there). One request gives every
 * pawn its move, all or none, so the gesture is one write, one catch-up and one toast. */
export async function formation(c: Story, moves: FormationMove[], queue: boolean, ignored: number) {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  const note = ignored ? ` · ${plural(ignored, 'undrafted pawn')} not in it (draft with R)` : '';
  try {
    await c.write('story.orders', {
      moves: moves.map((mv) => ({ pawn: mv.pawn, target: { x: mv.target[0], y: mv.target[1], z: mv.target[2] } })),
      ...(queue ? { queue: true } : {}),
    });
  } catch (e) {
    if (oldBotRefusal(e)) return formationPerPawn(c, moves, queue, note);
    const why = storyWriteError(e);
    // An unknown outcome (a 502, a lost connection) may still have reached the colony: the pawns may walk to the line.
    if (why.kind === 'unknown')
      return c.say(`Could not confirm the formation (${why.text}); the pawns may still take it.`, 'warn');
    return c.say(`The formation was not given: ${why.text}`, 'warn');
  }
  c.say(`${queue ? 'Queued a formation' : 'Formation'}: ${plural(moves.length, 'pawn')} along the line${note}`);
}

async function formationPerPawn(c: Story, moves: FormationMove[], queue: boolean, note: string) {
  const res = await Promise.allSettled(
    moves.map((mv) =>
      c.write('story.orders', {
        pawns: [mv.pawn],
        order: { kind: 'move', target: { x: mv.target[0], y: mv.target[1], z: mv.target[2] } },
        ...(queue ? { queue: true } : {}),
      }),
    ),
  );
  const bad = res.filter((r) => r.status === 'rejected');
  if (bad.length)
    c.say(
      `${bad.length} of ${moves.length} formation orders failed: ${errorText((bad[0] as PromiseRejectedResult).reason)}`,
      'warn',
    );
  else c.say(`${queue ? 'Queued a formation' : 'Formation'}: ${plural(moves.length, 'pawn')} along the line${note}`);
}

/** A map selection; a chest picked while a bill waits for one answers it instead. */
export function selectOnMap(c: Story, sel: Selection) {
  if (c.picking.current && sel.chests.length) {
    const r = c.picking.current;
    c.picking.current = null;
    c.setPickHint(false);
    r(sel.chests[0]!);
    return;
  }
  c.setSelection(sel);
  c.setMenu(null);
}

/** Selects these bodies and centres the map on the first. */
function selectBodies(c: Story, names: string[]) {
  c.setSelection({ ...EMPTY_SELECTION, bodies: names });
  c.focusBody(names[0]!);
}

/** The storage fix: the first storage's chests, else the storage tool to make one. */
function openStorage(c: Story) {
  const s = c.state?.storages?.[0];
  if (s) c.setSelection({ bodies: [], chests: [s.chest], bench: null });
  else c.setTool({ kind: 'storage' });
}

/** An alert: its pawn, then what fixes it (the Work tab, storage, a workbench's bills). */
export function onAlert(c: Story, a: StoryAlert) {
  if (a.pawn) selectBodies(c, [a.pawn]);
  if (a.fix?.tab === 'work') c.setTab('work');
  if (a.fix?.tab === 'storage') openStorage(c);
  if (a.fix?.workbench) c.setSelection({ bodies: [], chests: [], bench: a.fix.workbench });
}

/** A message or log entry: its alert's fix, its pawn, or the place it names. */
export function onJump(c: Story, t: JumpTarget) {
  if (t.alert) onAlert(c, t.alert);
  else if (t.pawn) selectBodies(c, [t.pawn]);
  if (!t.pawn && t.pos) c.map.current?.focus(t.pos[0], t.pos[2], t.pos[1]);
}

/** An ongoing condition: select the pawns it is about, or open what fixes it. */
export function onCondition(c: Story, cond: Condition) {
  if (cond.pawns.length) selectBodies(c, cond.pawns);
  if (cond.tab === 'storage') openStorage(c);
}

/** A column as a home: the surface there (the working level when its chunk is not loaded). */
async function homeAt(c: Story, x: number, z: number): Promise<Xyz> {
  const y = (await c.map.current?.surface(x, z).catch(() => null)) ?? c.lv;
  return { x, y, z };
}

/** The map centre as a home. */
async function centreHome(c: Story): Promise<Xyz | null> {
  const at = c.map.current?.center();
  return at ? homeAt(c, at[0], at[2]) : null;
}

/** Founds a colony at the map centre and opens it. */
export async function createColony(c: Story, name: string, n: number) {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  const home = await centreHome(c);
  try {
    const out = await api('story.create', { body: { name, pawns: n, ...(home ? { home } : {}) } });
    await c.qc.invalidateQueries({ queryKey: ['story.list'] });
    c.router.replace(`/story?colony=${encodeURIComponent(out.id)}`);
    c.setTab(null);
    c.say(`${name} founded with ${plural(n, 'pawn')}`);
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}

/** Migrates home to a column (after the confirm): the bot brings far pawns over. */
export async function moveHome(c: Story, at: V3) {
  c.setMigrate(null);
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  const home = await homeAt(c, at[0], at[2]);
  try {
    const out = await c.write('story.home', { home });
    c.setLevel(null);
    c.say(`Home moved to ${out.home[0]}, ${out.home[2]}: pawns far from it are brought here`);
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}

/** A body from the top bar: focus it when it is off the map's picture (a double-click always does). */
export function showBody(c: Story, name: string) {
  const b = c.bodies.find((x) => x.name === name);
  if (b?.pos && !c.map.current?.onScreen(b.pos[0]!, b.pos[1]!, b.pos[2]!)) c.focusBody(name);
}

/** The toast for a write that may come back with warnings. */
export function sayWarnings(c: Story, warnings: string[] | null | undefined, ok: string) {
  c.say(warnings?.length ? warnings.join('; ') : ok, warnings?.length ? 'warn' : 'ok');
}

/** A zone's bills (planting or fishing), on the zones route. */
export function zoneBill(c: Story, z: StoryZone, body: GrowBillChange) {
  void c
    .write('story.zones', { ...body, id: z.id })
    .then((out) => sayWarnings(c, out.warnings, 'Bills updated'), warnOn(c));
}

/** A zone's Preserve (zones/PreservePanel.tsx): switched, its range set, or a new snapshot; the answer's warnings are said. */
export function preserveZone(c: Story, z: StoryZone, change: PreserveChange) {
  void c
    .write('story.zones', preserveBody(z, change))
    .then((out) => sayWarnings(c, out.warnings, preserveSaid(change, out.preserve)), warnOn(c));
}

/** Centres the map on a zone. */
export function focusZone(c: Story, z: StoryZone) {
  c.map.current?.focus(Math.floor((z.area[0] + z.area[3]) / 2), Math.floor((z.area[2] + z.area[5]) / 2), z.area[1]);
}

/** "+": pawns join (the seats permitting: the rest wait, and the toast says why). */
export function addPawns(c: Story, n: number) {
  void c
    .write('story.pawns', { op: 'add', count: n })
    .then((out) => {
      // The pawns still to come are one entry per reason with a count (no upper limit: never one entry per pawn).
      const waiting = Math.min(n, out.pending.reduce((sum, p) => sum + p.count, 0));
      c.say(
        out.blocked.length
          ? `${n - waiting} joining now; ${waiting} wait: ${out.blocked[0]!.reason}`
          : `${plural(n, 'pawn')} joining`,
        out.blocked.length ? 'warn' : 'ok',
      );
    }, warnOn(c));
}

/** A pawn leaves the colony (its items go to storage). */
export function removePawn(c: Story, name: string) {
  void c.write('story.pawns', { op: 'remove', pawn: name }).then(() => {
    c.say(`${displayOf(c, name)} is leaving: their items go to storage`);
    c.setSelection(EMPTY_SELECTION);
  }, warnOn(c));
}

/** Retry all: every failed or resting designation and every failed build cell. */
export function retryAll(c: Story) {
  const ids = stalledIn(c.state?.designations ?? [], null);
  const cells = failedCells(c.state?.ghosts?.blocks ?? []).length;
  if (!ids.length && !cells) return c.say('No failed or resting designations or build cells');
  void retryFailed(c, ids, cells ? 'all' : []);
}

/** How many designations and build cells Retry all would retry. */
export const stalledCount = (c: Story) =>
  stalledIn(c.state?.designations ?? [], null).length + failedCells(c.state?.ghosts?.blocks ?? []).length;

/** The chat line goes to the selected AI bodies as a directive. */
export function sendChat(c: Story, text: string) {
  void sendDirective(c.split.ai, text).then((res) => {
    const bad = res.filter((r) => !r.ok);
    c.say(
      bad.length ? `Not sent to ${bad.map((b) => b.bot).join(', ')}` : `Said to ${c.split.ai.join(', ')}`,
      bad.length ? 'warn' : 'ok',
    );
  });
}

/** The chat's addressees: the selected AI bodies (any selected body that is one). */
export function chatTargets(c: Story): string[] {
  if (c.split.ai.length) return c.split.ai;
  return c.selection.bodies.filter((n) => c.state?.ai?.some((a) => a.name === n));
}
