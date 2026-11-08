/**
 * The map's right-click: drafted pawns attack the entity clicked (the genre's way, no menu); anything else opens the context
 * menu, whose picks become orders, gear ops, blueprint changes or a storage / bills selection.
 */
import { errorText } from '@/lib/api/client';
import { draftedOf, type AttackWho } from '../assign/gear';
import { contextMenu, orderBody, type MenuItem, type Target } from '../tools';
import { attack, gear } from './actions';
import type { Story } from './commands';
import type { V3 } from './state';

type EntityTarget = Extract<Target, { kind: 'mob' | 'player' }>;
type GhostTarget = Extract<Target, { kind: 'ghost' }>;
type BlockTarget = Extract<Target, { kind: 'block' }>;

/** Who a right-clicked entity is, for an attack order. */
function attackWho(target: EntityTarget): AttackWho {
  return target.kind === 'mob'
    ? { name: target.name, kind: target.animal ? 'animal' : 'mob', entity: target.entity }
    : { name: target.name, kind: target.bot ? 'bot' : 'player', entity: target.entity };
}

/** The context menu's title: the block / entity / item's name, "Blueprint" for a ghost, else "Here". */
function menuTitle(target: Target): string {
  switch (target.kind) {
    case 'block':
    case 'mob':
    case 'item':
    case 'player':
      return target.name;
    case 'ghost':
      return 'Blueprint';
    default:
      return 'Here';
  }
}

/** A right-click on the map. */
export function openContext(c: Story, target: Target, at: { x: number; y: number }) {
  const drafted = draftedOf(c.state, c.split.pawns);
  if (drafted.length && (target.kind === 'mob' || target.kind === 'player')) {
    void attack(c, attackWho(target), drafted, c.keys.current.has('ShiftLeft') || c.keys.current.has('ShiftRight'));
    return;
  }
  const items = contextMenu(target, c.split.pawns.length > 0, c.split.ai.length > 0);
  c.setMenu({ at, title: menuTitle(target), items, target });
}

/** A blueprint's menu pick: cancel it, or move it to the front of the queue. */
async function pickBlueprint(c: Story, item: MenuItem, build: string | number) {
  const sent =
    item.action === 'cancel-build'
      ? c.write('story.builds', { op: 'remove', ids: [build] }).then(() => 'Blueprint cancelled')
      : c.write('story.builds', { op: 'reorder', ids: [build] }).then(() => 'Moved to the front of the queue');
  await sent.then(
    (done) => c.say(done),
    (e) => c.say(errorText(e), 'warn'),
  );
}

/** A chest's or a workbench's menu pick: select it (its storage settings or its bills). */
function pickBlock(c: Story, item: MenuItem, target: BlockTarget) {
  const pos: V3 = [target.x, target.y, target.z];
  c.setSelection(
    item.action === 'storage' ? { bodies: [], chests: [pos], bench: null } : { bodies: [], chests: [], bench: pos },
  );
}

/** A confirmed attack from the menu; the selected AI bodies get the item's directive too. */
async function pickAttack(c: Story, item: MenuItem, confirm: NonNullable<MenuItem['confirm']>, queue: boolean) {
  await attack(c, { name: confirm.name, kind: confirm.kind, entity: confirm.entity }, c.split.pawns, queue);
  if (!c.split.ai.length || !item.directive) return;
  return void c.run(null, item.directive, 'ordered', () => item.label);
}

/** The menu pick for the pawns' gear: pick-up opens the chooser, the rest go at once. */
function pickGear(c: Story, op: NonNullable<MenuItem['gear']>) {
  if (op === 'pickup') c.setPickupFor(c.split.pawns);
  else void gear(c, op, c.split.pawns);
}

const isBlueprintPick = (item: MenuItem, target: Target | undefined): target is GhostTarget =>
  (item.action === 'cancel-build' || item.action === 'first-build') && target?.kind === 'ghost';
const isBlockPick = (item: MenuItem, target: Target | undefined): target is BlockTarget =>
  (item.action === 'storage' || item.action === 'bills') && target?.kind === 'block';

/** A context-menu pick (Shift queues it). */
export async function pickMenu(c: Story, item: MenuItem, queue: boolean) {
  const target = c.menu?.target;
  c.setMenu(null);
  if (item.action === 'unprotect' && (target?.kind === 'block' || target?.kind === 'ghost')) {
    if (!c.canWrite) return c.say('Needs the story role', 'warn');
    const at = [Math.floor(target.x), Math.floor(target.y), Math.floor(target.z)] as [number, number, number];
    await c.write('story.builds', { op: 'unprotect', cells: [at] }).then(
      () => c.say(`Stopped rebuilding at ${at.join(', ')}`),
      (e) => c.say(errorText(e), 'warn'),
    );
    return;
  }
  if (item.action === 'gear' && item.gear) return pickGear(c, item.gear);
  if (item.confirm && c.split.pawns.length) return pickAttack(c, item, item.confirm, queue);
  if (isBlueprintPick(item, target)) return pickBlueprint(c, item, target.build);
  if (isBlockPick(item, target)) return pickBlock(c, item, target);
  await pickOrder(c, item, queue);
}

/** An order pick: the pawns' order and / or the AI bodies' directive. */
async function pickOrder(c: Story, item: MenuItem, queue: boolean) {
  const body = orderBody(c.split.pawns, item, queue);
  await c.run(
    body ? { route: 'story.orders', body } : null,
    item.directive ?? null,
    queue ? 'queued' : 'ordered',
    () => item.label,
  );
}
