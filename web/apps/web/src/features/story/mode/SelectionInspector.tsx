/**
 * The inspector for what is selected: a designation (with its cluster), a workbench's bills, a bed, storage chests, one pawn
 * or AI body, or a group of bodies.
 */
import { errorText } from '@/lib/api/client';
import { EquipmentView, GearActions } from '../assign/GearActions';
import { EMPTY_SELECTION } from '../model';
import { storyWriteError } from '../optimistic';
import {
  AiInspector,
  BedInspector,
  BillsInspector,
  DesignationInspector,
  GroupInspector,
  PawnInspector,
  StorageInspector,
  UndesignatedBench,
} from '../StoryInspector';
import { bringOnline, gear, stillBlocked } from './actions';
import type { Story } from './commands';

const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

type Designation = NonNullable<Story['selection']['designation']>;

/** What the storage, bills, bench and bed inspectors get: their changes shown at once, and a toast when one fails. */
const tracked = (c: Story) => ({
  track: c.track,
  onError: (e: unknown) => c.say(storyWriteError(e).text, 'warn'),
});

/** A designation and the live members of its cluster: retry or cancel them. */
function designationInspector(c: Story, sel: Designation, close: () => void) {
  const all = c.state?.designations ?? [];
  const d = all.find((x) => String(x.id) === String(sel.id));
  if (!d) return null;
  const live = new Set(all.map((x) => String(x.id)));
  const cluster = sel.cluster.filter((x) => live.has(String(x)));
  const designations = (op: 'retry' | 'cancel', ids: Array<string | number>) =>
    void c.write('story.designations', { op, ids }).then(
      () => {
        c.say(
          op === 'retry'
            ? `Trying ${ids.length === 1 ? 'it' : `${ids.length}`} again now`
            : `${ids.length} designation${ids.length === 1 ? '' : 's'} cancelled`,
        );
        if (op === 'cancel' && ids.some((x) => String(x) === String(d.id))) close();
      },
      (e) => c.say(errorText(e), 'warn'),
    );
  return (
    <DesignationInspector
      designation={d}
      cluster={cluster.length ? cluster : [d.id]}
      canWrite={c.canWrite}
      onRetry={(ids) => designations('retry', ids)}
      onCancel={(ids) => designations('cancel', ids)}
      onClose={close}
    />
  );
}

/** A workbench: its bills, or the offer to make it one. */
function benchInspector(c: Story, pos: [number, number, number], close: () => void) {
  const bench = (c.state?.workbenches ?? []).find((w) => k3(w.pos) === k3(pos));
  return bench ? (
    <BillsInspector
      key={k3(pos)}
      bench={bench}
      state={c.state}
      canWrite={c.canWrite}
      write={c.write}
      {...tracked(c)}
      onPickChest={c.pickChest}
      onClose={close}
    />
  ) : (
    <UndesignatedBench key={k3(pos)} pos={pos} canWrite={c.canWrite} write={c.write} onClose={close} />
  );
}

function bedInspector(c: Story, pos: [number, number, number], close: () => void) {
  const bed = (c.state?.beds ?? []).find((b) => k3(b.pos) === k3(pos));
  return bed ? (
    <BedInspector bed={bed} state={c.state} canWrite={c.canWrite} write={c.write} {...tracked(c)} onClose={close} />
  ) : null;
}

/** One colony pawn: needs, bed, gear, draft, its queue, bringing it online. */
function pawnInspector(c: Story, name: string, close: () => void) {
  const p = c.state?.pawns.find((x) => x.name === name);
  if (!p) return null;
  const bed = (c.state?.beds ?? []).find((b) => b.pawn === p.name);
  return (
    <PawnInspector
      pawn={p}
      bed={bed}
      onShowBed={bed ? () => c.map.current?.focus(bed.pos[0], bed.pos[2], bed.pos[1]) : undefined}
      canWrite={c.canWrite}
      onDraft={(d) => void c.draft([p.name], d)}
      onClearQueue={() => void c.write('story.orders', { pawns: [p.name], order: { kind: 'stop' } })}
      onClose={close}
      extra={
        <>
          <EquipmentView pawn={p} policies={c.state?.policies ?? []} />
          <GearActions
            state={c.state}
            pawns={[p.name]}
            canWrite={c.canWrite}
            onGear={(op, it, n) => void gear(c, op, [p.name], it, n)}
          />
        </>
      }
      blocked={stillBlocked(c).filter((b) => b.pawn === p.name)}
      onBringOnline={c.state?.active === false ? undefined : (overCap) => void bringOnline(c, [p.name], overCap)}
    />
  );
}

/** One body: a colony pawn, else an AI body. */
function bodyInspector(c: Story, name: string, close: () => void) {
  const pawn = pawnInspector(c, name, close);
  if (pawn) return pawn;
  const a = c.state?.ai?.find((x) => x.name === name);
  return a ? <AiInspector body={a} state={c.state} onChat={() => c.setChat(true)} onClose={close} /> : null;
}

/** Several bodies: draft them and their bulk gear. */
function groupInspector(c: Story, close: () => void) {
  return (
    <GroupInspector
      names={c.selection.bodies}
      state={c.state}
      canWrite={c.canWrite}
      onDraft={(d) => void c.draft(c.split.pawns, d)}
      onClose={close}
      extra={
        <GearActions
          state={c.state}
          pawns={c.split.pawns}
          canWrite={c.canWrite}
          onGear={(op, it, n) => void gear(c, op, c.split.pawns, it, n)}
        />
      }
    />
  );
}

/** The inspector element for the selection (null when nothing is selected or it is gone). */
export function selectionInspector(c: Story) {
  const { selection } = c;
  const close = () => c.setSelection(EMPTY_SELECTION);
  if (selection.designation) return designationInspector(c, selection.designation, close);
  if (selection.bench) return benchInspector(c, selection.bench, close);
  if (selection.bed) return bedInspector(c, selection.bed, close);
  if (selection.chests.length)
    return (
      <StorageInspector
        chests={selection.chests}
        state={c.state}
        canWrite={c.canWrite}
        write={c.write}
        {...tracked(c)}
        clip={c.clip}
        onClip={(clip) => (c.setClip(clip), c.say('Storage settings copied'))}
        onClose={close}
      />
    );
  if (selection.bodies.length === 1) return bodyInspector(c, selection.bodies[0]!, close);
  if (selection.bodies.length > 1) return groupInspector(c, close);
  return null;
}
