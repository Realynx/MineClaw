import { STORY_MATERIALS, type StoryArchitectEntry, type StoryColonyState } from '@mineclaw/contracts';
import type { AimRay, AimTarget } from '@mineclaw/world-viewer';
import type { HoloCell } from '@mineclaw/world-viewer/settings';
import { familyOf, firstHalfOf, partnerOf, facingOf, hasFacing, type Version } from '../studio/facing';
import type { PaletteEntry } from '../studio/palette';

export type EditCell = [number, number, number, number, number];
export interface EditReply {
  buildId?: string | number | null;
  ids?: Array<string | number> | null;
  rev?: number | null;
}
export type EditSend = (cells: EditCell[], buildId?: string) => Promise<EditReply>;
export interface PlanOwner {
  id?: string;
  token: string;
}
interface EditOperation {
  cells: EditCell[];
  owner: PlanOwner;
  state: 'queued' | 'sending' | 'failed' | 'saved';
  rev?: number;
}
export interface PendingCell {
  cell: EditCell;
  owner: PlanOwner;
  failed: boolean;
}
const key = (cell: readonly unknown[]) => cell.slice(0, 3).join(',');
const UNSUPPORTED_EXACT = new Set([
  8, 9, 10, 11, 27, 28, 43, 51, 62, 63, 66, 68, 74, 94, 106, 125, 127, 144, 150, 157, 175, 176, 177, 181, 204,
]);

/** Serial writes preserve the new plan id and retain failed edits and their dependent additions for explicit retry. */
export class FlyEditQueue {
  private operations: EditOperation[] = [];
  private sending = false;
  private sequence = 0;
  private readonly owners = new Map<string, PlanOwner>();
  private readonly aliases = new Map<string, string>();
  revision = 0;
  error: string | null = null;
  constructor(
    private send: EditSend,
    private readonly changed: () => void,
  ) {}
  setSend(send: EditSend): void {
    this.send = send;
  }
  resolve(id: string): string {
    const seen = new Set<string>();
    while (this.aliases.has(id) && !seen.has(id)) {
      seen.add(id);
      id = this.aliases.get(id)!;
    }
    return id;
  }
  private notify() {
    this.revision++;
    this.changed();
  }
  get pending() {
    return this.operations.filter((op) => op.state !== 'saved').length;
  }
  get overlays(): PendingCell[] {
    const cells = new Map<string, PendingCell>();
    for (const op of this.operations)
      for (const cell of op.cells) cells.set(key(cell), { cell, owner: op.owner, failed: op.state === 'failed' });
    return [...cells.values()];
  }
  owner(at: readonly number[]): PlanOwner | undefined {
    return this.overlays.find((p) => key(p.cell) === key(at))?.owner;
  }
  enqueue(cells: EditCell[], owner?: PlanOwner): void {
    if (!cells.length) return;
    owner ??= { token: `edit-${++this.sequence}` };
    owner = this.owners.get(owner.token) ?? owner;
    this.owners.set(owner.token, owner);
    this.operations.push({ cells, owner, state: 'queued' });
    this.notify();
    void this.drain();
  }
  retry(): void {
    this.error = null;
    for (const op of this.operations) if (op.state === 'failed') op.state = 'queued';
    this.notify();
    void this.drain();
  }
  discard(): void {
    // A successful request is never rolled back by dismissing a later failed edit.
    this.operations = this.operations.filter((op) => op.state === 'saved' || op.state === 'sending');
    this.error = null;
    this.notify();
  }
  reconcile(state: StoryColonyState | undefined): void {
    if (!state) return;
    const old = this.operations.length;
    this.operations = this.operations.filter(
      (op) =>
        op.state !== 'saved' ||
        !(op.rev !== undefined
          ? state.rev >= op.rev
          : op.cells.every((c) =>
              state.ghosts?.blocks.some((g) => key(g) === key(c) && g[3] === c[3] && g[4] === c[4]),
            )),
    );
    if (old !== this.operations.length) this.notify();
  }
  private async drain(): Promise<void> {
    if (this.sending || this.error) return;
    this.sending = true;
    try {
      for (;;) {
        const op = this.operations.find((o) => o.state === 'queued');
        if (!op) break;
        op.state = 'sending';
        this.notify();
        try {
          const before = op.owner.id ? this.resolve(op.owner.id) : undefined;
          const reply = await this.send(op.cells, before);
          const id = reply.buildId ?? reply.ids?.[0];
          if (id !== undefined && id !== null) {
            if (before && before !== String(id)) this.aliases.set(before, String(id));
            op.owner.id = String(id);
          }
          op.state = 'saved';
          op.rev = reply.rev ?? undefined;
        } catch (e) {
          op.state = 'failed';
          this.error = e instanceof Error ? e.message : 'Could not save the plan edit';
          break;
        } finally {
          this.notify();
        }
      }
    } finally {
      this.sending = false;
    }
  }
}

/** Architect materials share the designer palette UI, without creating a designer session. */
export function editPalette(entries: readonly StoryArchitectEntry[] | undefined, version: Version): PaletteEntry[] {
  const source = entries?.length
    ? entries.map((e) => ({ id: e.material[0], meta: e.material[1], name: e.label }))
    : STORY_MATERIALS.map((m) => ({ id: m.id, meta: m.meta, name: m.label }));
  const out = new Map<string, PaletteEntry>();
  for (const e of source) {
    const family = familyOf(e.id);
    if (e.id <= 0 || UNSUPPORTED_EXACT.has(e.id) || ['rail', 'poweredrail', 'door', 'bed', 'tall'].includes(family))
      continue;
    if (version === 'b1.8.1' && e.id > 109) continue;
    const meta =
      family === 'log' ? e.meta & 3 : family === 'stairs' ? e.meta & 3 : family === 'slab' ? e.meta & 7 : e.meta;
    if (version === 'b1.8.1' && e.id === 17 && meta > 2) continue;
    out.set(`${e.id}:${meta}`, { ...e, meta, facing: hasFacing(e.id), family, cat: 'Building' });
  }
  return [...out.values()];
}

/** Only placement states, never runtime powered/open bits or neighbor-derived rail shapes. */
export function editVariants(e: PaletteEntry, version: Version): Array<{ meta: number; label: string }> {
  const family = familyOf(e.id),
    modern = version === '1.12.2';
  const directions = (metas: number[], suffix = '') =>
    metas.map((meta) => ({ meta, label: `${facingOf(e.id, meta) ?? meta}${suffix}` }));
  if (family === 'stairs')
    return [...directions([0, 1, 2, 3], ' · lower'), ...(modern ? directions([4, 5, 6, 7], ' · upper') : [])];
  if (family === 'front' || family === 'wall') return directions([2, 3, 4, 5]);
  if (family === 'door' || family === 'bed' || family === 'gate') return directions([0, 1, 2, 3]);
  if (family === 'slab')
    return [
      { meta: e.meta & 7, label: 'Lower half' },
      ...(modern ? [{ meta: (e.meta & 7) | 8, label: 'Upper half' }] : []),
    ];
  if (family === 'log')
    return [
      { meta: e.meta & 3, label: 'Vertical' },
      ...(modern
        ? [
            { meta: (e.meta & 3) | 4, label: 'East–west' },
            { meta: (e.meta & 3) | 8, label: 'North–south' },
          ]
        : []),
    ];
  if (family === 'torch')
    return [
      { meta: 5, label: 'Floor' },
      ...[1, 2, 3, 4].map((meta) => ({
        meta,
        label: `Attached to ${['west', 'east', 'north', 'south'][meta - 1]} wall`,
      })),
    ];
  return [{ meta: e.meta & 15, label: 'Default' }];
}

/** Creative pick: keep the material variant and its supported placement state, without copying runtime flags. */
export function pickEditMaterial(
  target: AimTarget | null,
  entries: readonly PaletteEntry[],
  version: Version,
): { entry: PaletteEntry; meta: number } | null {
  if (target?.kind !== 'block' || target.id <= 0) return null;
  const id = target.id === 62 ? 61 : target.id === 74 ? 73 : target.id;
  const family = familyOf(id);
  const meta = family === 'gate' || id === 18 || id === 161 ? target.meta & 3 : target.meta & 15;
  const choices = entries.filter((e) => e.id === id);
  const entry = choices.find((e) => e.meta === meta)
    ?? choices.find((e) => editVariants(e, version).some((v) => v.meta === meta))
    ?? choices.find((e) => e.meta === (meta & 3))
    ?? choices[0];
  if (!entry) return null;
  const variants = editVariants(entry, version);
  return { entry, meta: variants.find((v) => v.meta === meta)?.meta ?? variants[0]?.meta ?? entry.meta };
}

/** Partner cells are edited together, so removing the top half of a door cannot leave its lower plan behind. */
export function editCells(
  target: Extract<AimTarget, { kind: 'block' }>,
  button: number,
  entry: PaletteEntry,
  meta: number,
): EditCell[] {
  if (button === 0) {
    const other = firstHalfOf(target.id, target.meta) ?? partnerOf(target.id, target.meta);
    return [
      [target.x, target.y, target.z, 0, 0],
      ...(other ? [[target.x + other[0], target.y + other[1], target.z + other[2], 0, 0] as EditCell] : []),
    ];
  }
  const normal = (
    [
      [0, -1, 0],
      [0, 1, 0],
      [0, 0, -1],
      [0, 0, 1],
      [-1, 0, 0],
      [1, 0, 0],
    ] as const
  )[target.face];
  if (button !== 2 || !normal) return [];
  const cell: EditCell = [target.x + normal[0], target.y + normal[1], target.z + normal[2], entry.id, meta];
  const other = partnerOf(entry.id, meta);
  return [
    cell,
    ...(other ? [[cell[0] + other[0], cell[1] + other[1], cell[2] + other[2], entry.id, other[3]] as EditCell] : []),
  ];
}

export function editHolograms(base: readonly HoloCell[], pending: readonly PendingCell[]): HoloCell[] {
  const cells = new Map(base.map((c) => [`${c.x},${c.y},${c.z}`, c]));
  for (const p of pending) {
    const [x, y, z, id, meta] = p.cell;
    cells.set(key(p.cell), {
      x,
      y,
      z,
      id,
      meta,
      build: p.owner.id ?? p.owner.token,
      state: p.failed ? 'blocked' : 'todo',
    });
  }
  return [...cells.values()];
}

/** Full-cell plan raycast, independent of whether the physical block has been built yet. */
export function aimEdit(physical: AimTarget | null, ray: AimRay, cells: readonly HoloCell[]): AimTarget | null {
  let best = physical;
  // Pending removals remain physical occluders until dug; never aim through an unseen block behind them.
  let distance = best?.distance ?? ray.max;
  for (const c of cells) {
    if (!c.id) continue;
    let enter = 0,
      leave = ray.max,
      face = 1;
    const mins = [c.x, c.y, c.z];
    for (let axis = 0; axis < 3; axis++) {
      const o = ray.origin[axis]!,
        d = ray.direction[axis]!,
        min = mins[axis]!;
      if (Math.abs(d) < 1e-9) {
        if (o < min || o > min + 1) leave = -1;
        continue;
      }
      let a = (min - o) / d,
        b = (min + 1 - o) / d;
      const f = (d > 0 ? [4, 0, 2][axis] : [5, 1, 3][axis])!;
      if (a > b) [a, b] = [b, a];
      if (a > enter) {
        enter = a;
        face = f;
      }
      leave = Math.min(leave, b);
    }
    if (enter <= leave && enter <= distance + 0.001) {
      distance = enter;
      best = {
        kind: 'block',
        x: c.x,
        y: c.y,
        z: c.z,
        id: c.id,
        meta: c.meta,
        face,
        name: 'Plan block',
        distance: enter,
        plan: c.build,
      };
    }
  }
  return best;
}
