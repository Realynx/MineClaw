/**
 * The open bottom tab's window (Architect, Work, Schedule, Queue, Directives, Assign, Colony) and, beside the Colony tab, the
 * growing / fishing / quarry / other zone panels: each wired to the colony's write routes (every zone's "Preserve blocks"
 * section through `preserveWriter`).
 */
import { isWaterTool, STORY_FURNITURE, type StoryArchitectEntry, type StoryPolicy } from '@mineclaw/contracts';
import type { ReactElement, ReactNode } from 'react';
import { AssignPanel } from '../assign/AssignPanel';
import { GrowingZonesPanel } from '../GrowingZones';
import { InventoryPanel } from '../InventoryPanel';
import { DirectivesChat } from '../DirectivesChat';
import { materialsFor, materialToolOf } from '../materials';
import type { MapTool } from '../StoryMap';
import {
  ArchitectPanel,
  ColonyPanel,
  QueuePanel,
  SchedulePanel,
  WorkPanel,
  type TabId,
} from '../StoryPanels';
import { failedByBuild, failedCells } from '../tools';
import { FishingZonesPanel } from '../zones/FishingZones';
import { OtherZonesPanel } from '../zones/OtherZones';
import type { PreserveWriter } from '../zones/PreservePanel';
import { QuarryZonesPanel } from '../zones/quarry';
import {
  bringOnline,
  createColony,
  displayOf,
  focusZone,
  moveHome,
  preserveZone,
  retryFailed,
  sayWarnings,
  stillBlocked,
  warnOn,
  zoneBill,
} from './actions';
import type { Story } from './commands';
import { pickUpDesign } from './designParam';
import { irrigate } from './water';

/** A placeable from the Architect: its default build tool, with it as the material (the build's auto bills craft what's missing). */
function pickPlaceable(c: Story, e: StoryArchitectEntry) {
  if (isWaterTool(e.kind)) {
    c.setTool({ kind: 'build', id: e.kind });
    c.say(`${e.label}: ${e.kind === 'water_channel' ? 'drag a line or an area' : 'click a cell'} on the map`);
    return;
  }
  const furniture = (STORY_FURNITURE as readonly string[]).includes(e.kind);
  c.setTool({ kind: 'build', id: furniture ? `furniture:${e.kind}` : e.kind } as MapTool);
  if (!furniture) chooseEntryMaterial(c, e);
  c.say(`${e.label}: drag on the map to place`);
}

/** The entry's material, through the selector when its tool has one (remembered), else straight into the build. */
function chooseEntryMaterial(c: Story, e: StoryArchitectEntry) {
  const t = materialToolOf({ kind: 'build', id: e.kind } as MapTool);
  const o = t
    ? materialsFor(t, c.architect.data?.entries, c.stock).find((x) => x.key === `${e.material[0]}:${e.material[1]}`)
    : null;
  if (o) c.chooseMaterial(o, t);
  else c.setBuild((b) => ({ ...b, material: [e.material[0], e.material[1]] }));
}

function architectPanel(c: Story) {
  return (
    <ArchitectPanel
      tool={c.tool}
      onTool={c.setTool}
      plants={c.state?.plants}
      zones={c.state?.zones}
      palette={c.architect.data}
      onPick={(e) => pickPlaceable(c, e)}
      onDesignPick={(id) => void pickUpDesign(c, id)}
    />
  );
}

function workPanel(c: Story) {
  return (
    <WorkPanel
      pawns={c.pawns}
      ai={c.ai}
      canWrite={c.canWrite}
      onSet={(names, work) => void c.write('story.priorities', { pawns: names, work }).catch(warnOn(c))}
      roles={c.state?.roles ?? []}
      selected={c.selection.bodies}
      onRole={(names, role) => void c.write('story.priorities', { pawns: names, role }).catch(warnOn(c))}
      onRoleOp={(body) => void c.write('story.roles', body).catch(warnOn(c))}
      craftTasks={c.state?.craftTasks}
      onCancelTask={(t) => void c.write('story.bills', { op: 'remove', id: t.id } as never).catch(warnOn(c))}
    />
  );
}

function schedulePanel(c: Story) {
  return (
    <SchedulePanel
      clock={c.clock}
      pawns={c.pawns}
      ai={c.ai}
      canWrite={c.canWrite}
      onPaint={(reqs) => reqs.forEach((r) => void c.write('story.schedule', r).catch(warnOn(c)))}
    />
  );
}

function queuePanel(c: Story) {
  const failed = failedByBuild(failedCells(c.state?.ghosts?.blocks ?? []));
  return (
    <QueuePanel
      builds={c.state?.builds ?? []}
      canWrite={c.canWrite}
      failed={new Map([...failed].map(([k, v]) => [k, v.cells.length]))}
      onRetry={(bid) => void retryFailed(c, [], [{ id: bid }])}
      onOp={(op, ids) => void c.write('story.builds', { op, ids }).catch(warnOn(c))}
    />
  );
}

/** A policy as the policies route takes it (a new one has no id yet). */
function policyBody(pol: Partial<StoryPolicy> & { name: string }) {
  return {
    ...(pol.id ? { id: pol.id } : {}),
    name: pol.name,
    toolsByWork: pol.toolsByWork,
    tools: pol.tools as never,
    weapon: pol.weapon as never,
    armor: pol.armor as never,
    food: pol.food,
  };
}

function assignPanel(c: Story) {
  return (
    <AssignPanel
      state={c.state}
      pawns={c.pawns}
      canWrite={c.canWrite}
      writes={{
        assign: (policy, names) =>
          void c.write('story.policies', { op: 'assign', id: policy, pawns: names }).catch(warnOn(c)),
        setPolicy: (pol) =>
          void c
            .write('story.policies', { op: 'set', policy: policyBody(pol) })
            .then(() => c.say(`Policy “${pol.name}” saved`), warnOn(c)),
        removePolicy: (pid) => void c.write('story.policies', { op: 'remove', id: pid }).catch(warnOn(c)),
        rule: {
          set: (rule) => void c.write('story.materialRules', { op: 'set', rule }).catch(warnOn(c)),
          remove: (material) => void c.write('story.materialRules', { op: 'remove', material }).catch(warnOn(c)),
        },
        options: (o) => void c.write('story.options', o as never).catch(warnOn(c)),
      }}
    />
  );
}

function colonyPanel(c: Story) {
  return (
    <ColonyPanel
      state={c.state}
      colonies={c.list}
      current={c.id}
      canWrite={c.canWrite}
      onSwitch={(id) => c.router.replace(`/story?colony=${encodeURIComponent(id)}`)}
      onCreate={(name, n) => void createColony(c, name, n)}
      centre={c.centre}
      migrate={c.migrate}
      onMoveHome={() => c.setMigrate(c.map.current?.center() ?? c.centre)}
      onConfirmMove={() => c.migrate && void moveHome(c, c.migrate)}
      onCancelMove={() => c.setMigrate(null)}
      onPickHome={() => {
        c.setMigrate(null);
        c.setTool({ kind: 'home' });
      }}
      onDrawHouse={() => c.setTool({ kind: 'zone', id: 'home', op: 'add' })}
      onShowZone={(z) => focusZone(c, z)}
      onRenameZone={(z, name) =>
        void c
          .write('story.zones', { op: 'rename', id: z.id, name })
          .then(() => c.say(name ? `Renamed to ${name}` : 'Name cleared'), warnOn(c))
      }
      onDeleteZone={(z) =>
        void c
          .write('story.zones', { op: 'remove', id: z.id })
          .then(() => c.say(`${z.name || z.label || 'House'} removed`), warnOn(c))
      }
      onPause={c.togglePause}
      onOptions={(o) => void c.write('story.options', o).catch(warnOn(c))}
      onRelease={() => void c.write('story.release', {}).then(() => c.say('Pawns released; the colony is saved'))}
      onResume={() => void c.write('story.resume', {}).then(() => c.say('The pawns are coming back'), warnOn(c))}
      blocked={stillBlocked(c)}
      onBringAll={(overCap) => void bringOnline(c, null, overCap)}
      displayOf={(account) => displayOf(c, account)}
    />
  );
}

/** Each tab's window. */
const TAB_PANELS: Record<TabId, (c: Story, visible?: boolean) => ReactElement> = {
  architect: architectPanel,
  work: workPanel,
  schedule: schedulePanel,
  queue: queuePanel,
  directives: (c, visible) => <DirectivesChat state={c.state} canWrite={c.canWrite} visible={visible} />,
  assign: assignPanel,
  colony: colonyPanel,
  inventory: (c) => <InventoryPanel storages={c.state?.storages} />,
};

/** The open tab's window (null when no tab is open). */
export function tabPanel(c: Story, visible = true) {
  return c.tab ? TAB_PANELS[c.tab](c, visible) : null;
}

/** Growing zones (GrowingZones.tsx), beside the Colony tab: plant, seeds, status, seek seeds. */
function growingZones(c: Story) {
  return (
    <GrowingZonesPanel
      state={c.state}
      canWrite={c.canWrite}
      onIrrigate={(z) => void irrigate(c, z)}
      onShow={(z) => focusZone(c, z)}
      onUpdate={(z, change) =>
        void c
          .write('story.zones', { op: 'update', id: z.id, ...change })
          .then((out) => sayWarnings(c, out.warnings, 'Growing zone updated'), warnOn(c))
      }
      onBill={(z, body) => zoneBill(c, z, body)}
      onExtend={(z) => {
        c.setTool({ kind: 'zone', id: z.kind, op: 'add', into: z.id });
        c.setTab('architect');
      }}
      onSeek={(z) =>
        void c
          .write('story.zones', { op: 'seek_seeds', id: z.id })
          .then(
            (out) =>
              c.say(
                out.kind === 'bill' ? 'Seeds are queued for crafting' : `${out.count ?? 0} tall grass designated to cut`,
              ),
            warnOn(c),
          )
      }
    />
  );
}

/** The zone panels under the Colony tab: growing zones, fishing zones (water, fishers, catches, bills), quarries. */
export function colonyZones(c: Story): ReactElement<{ children: ReactNode }> | null {
  if (c.tab !== 'colony') return null;
  return (
    <>
      {growingZones(c)}
      <FishingZonesPanel
        state={c.state}
        canWrite={c.canWrite}
        onBill={(z, body) => zoneBill(c, z, body)}
        onShow={(z) => focusZone(c, z)}
      />
      <QuarryZonesPanel
        state={c.state}
        canWrite={c.canWrite}
        onShow={(z) => focusZone(c, z)}
        onMode={(z, mode) =>
          void c
            .write('story.zones', { op: 'update', id: z.id, mode })
            .then(
              () => c.say(mode === 'quarry' ? 'Quarry: digging everything to bedrock' : 'Quarry: ores only'),
              warnOn(c),
            )
        }
      />
      <OtherZonesPanel
        state={c.state}
        canWrite={c.canWrite}
        onShow={(z) => focusZone(c, z)}
        onRemove={(z) =>
          void c
            .write('story.zones', { op: 'remove', id: z.id })
            .then(() => c.say(`${z.name || 'Preserve zone'} removed: its blocks are no longer kept`), warnOn(c))
        }
      />
    </>
  );
}

/** What every zone's "Preserve blocks" section writes through (zones/PreservePanel.tsx). */
export function preserveWriter(c: Story): PreserveWriter {
  return { canWrite: c.canWrite, onPreserve: (z, change) => preserveZone(c, z, change) };
}
